using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Resourcefull.AspNetCore;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.AspNetCore;

/// <summary>
/// A minimal API over the in-memory provider, served by the test server, with endpoints built from the package the way
/// an application would: download, download inline under another name, download looked up by the result (which
/// answers ranges), download through a signed link, upload, upload through a signed link (a body put, or a form posted),
/// one that throws a given exception, a Razor page that downloads, and the dashboard, open to anyone, with Resourcefull's
/// health check: read-only at <c>/resourcefull</c>, and with changes and links at <c>/editable</c>. A second store, named
/// <see cref="ArchiveStore"/>, has link endpoints of its own under <c>/archive</c>. Each app has Data Protection keys of
/// its own, held in memory, so one app's links don't work in another, and records what it logs. A request with an
/// <c>X-Test-Body-Limit</c> header fails past that many bytes, as one past the server's size limit does.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    public const string Container = "files";

    public const string ArchiveStore = "archive";

    private readonly WebApplication _app;

    private TestApp(WebApplication app, RecordingLoggerProvider logs)
    {
        _app = app;
        Client = app.GetTestClient();
        Logs = logs;
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>What the app logged, as each message's category and text.</summary>
    public RecordingLoggerProvider Logs { get; }

    public IResourceContainerClient Files => _app.Services.CreateScope().ServiceProvider
        .GetRequiredService<IResourceServiceClient>().GetContainerClient(Container);

    /// <summary>Creates signed links in this app.</summary>
    public IResourceLinkSigner Links => _app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceLinkSigner>();

    /// <summary>The <see cref="Container"/> container in the store named <see cref="ArchiveStore"/>.</summary>
    public IResourceContainerClient ArchiveFiles => _app.Services.CreateScope().ServiceProvider
        .GetRequiredKeyedService<IResourceServiceClient>(ArchiveStore).GetContainerClient(Container);

    /// <summary>Creates signed links for the store named <see cref="ArchiveStore"/>.</summary>
    public IResourceLinkSigner ArchiveLinks =>
        _app.Services.CreateScope().ServiceProvider.GetRequiredKeyedService<IResourceLinkSigner>(ArchiveStore);

    public static async Task<TestApp> StartAsync(Exception? toThrow = null, bool versioning = true, TimeProvider? clock = null,
        ContainerPolicy? policy = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var logs = new RecordingLoggerProvider();
        builder.Logging.AddProvider(logs);
        if (clock is not null) builder.Services.AddSingleton(clock);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddResourcefullLinks();
        builder.Services.AddResourcefull(options =>
        {
            options.UseInMemory();
            if (versioning) options.UseVersioning();
            if (policy is not null) options.UseContainerPolicies(policy);
        });
        builder.Services.AddKeyedResourcefull(ArchiveStore, options => options.UseInMemory());
        builder.Services.AddResourcefullLinks(ArchiveStore);
        builder.Services.AddResourcefullExceptionHandler();
        builder.Services.AddHealthChecks().AddResourcefullCheck();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(TestApp).Assembly);

        WebApplication app = builder.Build();
        app.UseExceptionHandler();
        app.Use((context, next) =>
        {
            if (context.Request.Headers["X-Test-Body-Limit"] is [{ } limit])
                context.Request.Body = new LimitedBody(context.Request.Body, long.Parse(limit));
            return next(context);
        });
        app.MapMethods("/files/{**name}", ["GET", "HEAD"], async (string name, IResourceServiceClient resources, CancellationToken cancellationToken) =>
        {
            ResourceDownloadResult? download = await resources.GetContainerClient(Container).DownloadStreamingAsync(name, cancellationToken: cancellationToken);
            return download is null ? Results.NotFound() : Results.Resource(download);
        });
        app.MapGet("/inline/{**name}", async (string name, IResourceServiceClient resources, CancellationToken cancellationToken) =>
        {
            ResourceDownloadResult? download = await resources.GetContainerClient(Container).DownloadStreamingAsync(name, cancellationToken: cancellationToken);
            return download is null ? Results.NotFound() : TypedResults.Resource(download, new ResourceResultOptions { Inline = true, FileName = "shown.txt" });
        });
        app.MapMethods("/ranged/{**name}", ["GET", "HEAD"], (string name, string? versionId, IResourceServiceClient resources) =>
            Results.Resource(resources.GetContainerClient(Container), name, versionId));
        app.MapGet("/ranged-inline/{**name}", (string name, IResourceServiceClient resources) =>
            TypedResults.Resource(resources.GetContainerClient(Container), name, null, new ResourceResultOptions { Inline = true, FileName = "shown.txt" }));
        app.MapMethods("/shared/{token}", ["GET", "HEAD"], (string token, IResourceServiceClient resources) =>
            Results.SharedResource(resources, token));
        app.MapPut("/files/{**name}", (string name, HttpRequest request, IResourceServiceClient resources, CancellationToken cancellationToken) =>
            resources.GetContainerClient(Container).UploadFromRequestAsync(name, request, null, cancellationToken));
        app.MapMethods("/upload/{token}/{**name}", ["PUT", "POST"], (string token, string? name, IResourceServiceClient resources) =>
            Results.SharedUpload(resources, token, name));
        app.MapGet("/archive/shared/{token}", (string token, [FromKeyedServices(ArchiveStore)] IResourceServiceClient archive) =>
            Results.SharedResource(archive, token));
        app.MapPut("/archive/upload/{token}/{**name}", (string token, string? name, [FromKeyedServices(ArchiveStore)] IResourceServiceClient archive) =>
            Results.SharedUpload(archive, token, name));
        app.MapGet("/throw", () => { throw toThrow!; });
        app.MapRazorPages();
        app.MapResourcefullDashboard().AllowAnonymous();
        app.MapResourcefullDashboard("/editable", new ResourcefullDashboardOptions
        {
            AllowChanges = true, DownloadLinkPath = "/shared/{token}", UploadLinkPath = "/upload/{token}"
        }).AllowAnonymous();

        await app.StartAsync();
        await app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync(Container);
        await app.Services.CreateScope().ServiceProvider.GetRequiredKeyedService<IResourceServiceClient>(ArchiveStore).CreateContainerAsync(Container);
        return new TestApp(app, logs);
    }

    /// <summary>A request body that throws past <paramref name="limit"/> bytes, as Kestrel's does past its size limit.</summary>
    private sealed class LimitedBody(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            _read += read;
            if (_read > limit) throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
