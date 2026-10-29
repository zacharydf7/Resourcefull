using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.AspNetCore;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.AspNetCore;

/// <summary>
/// A minimal API over the in-memory provider, served by the test server, with endpoints built from the package the way
/// an application would: download, download inline under another name, download looked up by the result (which
/// answers ranges), upload, and one that throws a given exception.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    public const string Container = "files";

    private readonly WebApplication _app;

    private TestApp(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IResourceContainerClient Files => _app.Services.CreateScope().ServiceProvider
        .GetRequiredService<IResourceServiceClient>().GetResourceContainerClient(Container);

    public static async Task<TestApp> StartAsync(Exception? toThrow = null, bool versioning = true)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddResourcefull(options =>
        {
            options.UseInMemory();
            if (versioning) options.UseVersioning();
        });
        builder.Services.AddResourcefullExceptionHandler();

        WebApplication app = builder.Build();
        app.UseExceptionHandler();
        app.MapMethods("/files/{**name}", ["GET", "HEAD"], async (string name, IResourceServiceClient resources, CancellationToken cancellationToken) =>
        {
            ResourceDownloadResult? download = await resources.GetResourceContainerClient(Container).DownloadStreamingAsync(name, null, cancellationToken);
            return download is null ? Results.NotFound() : Results.Resource(download);
        });
        app.MapGet("/inline/{**name}", async (string name, IResourceServiceClient resources, CancellationToken cancellationToken) =>
        {
            ResourceDownloadResult? download = await resources.GetResourceContainerClient(Container).DownloadStreamingAsync(name, null, cancellationToken);
            return download is null ? Results.NotFound() : TypedResults.Resource(download, new ResourceResultOptions { Inline = true, FileName = "shown.txt" });
        });
        app.MapMethods("/ranged/{**name}", ["GET", "HEAD"], (string name, IResourceServiceClient resources) =>
            Results.Resource(resources.GetResourceContainerClient(Container), name));
        app.MapGet("/ranged-inline/{**name}", (string name, IResourceServiceClient resources) =>
            TypedResults.Resource(resources.GetResourceContainerClient(Container), name, new ResourceResultOptions { Inline = true, FileName = "shown.txt" }));
        app.MapPut("/files/{**name}", (string name, HttpRequest request, IResourceServiceClient resources, CancellationToken cancellationToken) =>
            resources.GetResourceContainerClient(Container).UploadFromRequestAsync(name, request, null, cancellationToken));
        app.MapGet("/throw", () => { throw toThrow!; });

        await app.StartAsync();
        await app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync(Container);
        return new TestApp(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
