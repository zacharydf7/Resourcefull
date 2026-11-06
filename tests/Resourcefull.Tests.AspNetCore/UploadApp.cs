using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Resourcefull.AspNetCore;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.AspNetCore;

/// <summary>
/// An app with the resumable upload endpoints, over the in-memory provider, on the test server or, for clients that
/// need a real one, on a port of its own.
/// <list type="bullet">
/// <item><c>/uploads</c> stores in <see cref="Container"/> under <c>tus/</c> and the <c>filename</c> the client gives. A
/// client can ask for what a real app would decide: <c>replace</c> replaces, <c>refuse</c> is refused, and
/// <c>fail-callback</c> makes <see cref="ResourceUploadEndpointOptions.OnCompletedAsync"/> throw.</item>
/// <item><c>/other-uploads</c> is a second set of endpoints that does the same, in the same store.</item>
/// <item><c>/archive-uploads</c> does the same in the store named <see cref="ArchiveStore"/>.</item>
/// <item><c>/flaky-uploads</c> does the same through a client whose completions fail while
/// <see cref="FailCompletions"/> is above zero, as a store that can't be reached fails them, and whose next completion,
/// once <see cref="TakeNameBeforeCompleting"/> is set, finds the name taken by another upload.</item>
/// <item><c>/upload-links</c> takes the upload links <see cref="Links"/> makes, and records completions and limits
/// sizes as <c>/uploads</c> does.</item>
/// <item><c>/links/uploads</c> takes the same links, mapped inside a <c>/links</c> group with the pattern string
/// <c>/uploads</c>, the same string <c>/uploads</c> is mapped with.</item>
/// </list>
/// A request with an <c>X-Test-User</c> header is signed in as that user.
/// </summary>
internal sealed class UploadApp : IAsyncDisposable
{
    public const string Container = "files";

    public const string ArchiveStore = "archive";

    private const string FlakyStore = "flaky";

    private readonly WebApplication _app;
    private readonly Flakiness _flakiness;

    private UploadApp(WebApplication app, Flakiness flakiness, HttpClient client, RecordingLoggerProvider logs)
    {
        _app = app;
        _flakiness = flakiness;
        Client = client;
        Logs = logs;
    }

    public HttpClient Client { get; }

    /// <summary>The app's address, when it runs on a port of its own.</summary>
    public Uri? Address { get; private init; }

    public RecordingLoggerProvider Logs { get; }

    /// <summary>The versions <see cref="ResourceUploadEndpointOptions.OnCompletedAsync"/> was called with.</summary>
    public ConcurrentQueue<ResourceInfo> Completed { get; } = new();

    /// <summary>How many more completions through <c>/flaky-uploads</c> fail.</summary>
    public int FailCompletions
    {
        get => _flakiness.FailCompletions;
        set => _flakiness.FailCompletions = value;
    }

    /// <summary>How many uploads were canceled through <c>/flaky-uploads</c>.</summary>
    public int Cancellations => _flakiness.Cancellations;

    /// <summary>Whether the next completion through <c>/flaky-uploads</c> finds its name taken just before.</summary>
    public bool TakeNameBeforeCompleting
    {
        get => _flakiness.TakeName;
        set => _flakiness.TakeName = value;
    }

    public IResourceContainerClient Files => _app.Services.CreateScope().ServiceProvider
        .GetRequiredService<IResourceServiceClient>().GetContainerClient(Container);

    public IResourceContainerClient ArchiveFiles => _app.Services.CreateScope().ServiceProvider
        .GetRequiredKeyedService<IResourceServiceClient>(ArchiveStore).GetContainerClient(Container);

    public IResourceLinkSigner Links => _app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceLinkSigner>();

    public static async Task<UploadApp> StartAsync(bool expiry = false, long? maxSize = null, ContainerPolicy? policy = null,
        bool onPort = false, TimeProvider? clock = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        if (onPort) builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        var logs = new RecordingLoggerProvider();
        builder.Logging.AddProvider(logs);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddProblemDetails();
        if (clock is not null) builder.Services.AddSingleton(clock);
        builder.Services.AddResourcefullLinks();
        builder.Services.AddResourcefull(options =>
        {
            options.UseInMemory().UseVersioning();
            if (expiry) options.UseExpiry(TimeSpan.FromHours(1));
            if (policy is not null) options.UseContainerPolicies(policy);
        });
        builder.Services.AddKeyedResourcefull(ArchiveStore, options => options.UseInMemory());
        var flakiness = new Flakiness();
        builder.Services.AddKeyedScoped(FlakyStore, (services, _) =>
            FlakyClient.Wrap(services.GetRequiredService<IResourceServiceClient>(), flakiness));

        WebApplication app = builder.Build();
        app.Use((context, next) =>
        {
            if (context.Request.Headers["X-Test-User"] is [{ } user])
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "Test"));
            return next(context);
        });
        UploadApp? started = null;
        Task<ResourceUploadTarget?> Resolve(HttpContext context, ResourceUploadRequest request)
        {
            IReadOnlyDictionary<string, string> metadata = request.Metadata;
            if (metadata.ContainsKey("refuse")) return Task.FromResult<ResourceUploadTarget?>(null);
            var target = new ResourceUploadTarget(Container, "tus/" + metadata.GetValueOrDefault("filename", "file"))
            {
                Mode = metadata.ContainsKey("replace") ? ResourceWriteMode.Replace : ResourceWriteMode.Create,
                ContentType = metadata.GetValueOrDefault("filetype")
            };
            if (metadata.ContainsKey("fail-callback")) target.Metadata["fail-callback"] = "";
            return Task.FromResult<ResourceUploadTarget?>(target);
        }
        Task OnCompleted(HttpContext context, ResourceInfo version)
        {
            if (version.Metadata.ContainsKey("fail-callback")) throw new InvalidOperationException("The app's save failed.");
            started!.Completed.Enqueue(version);
            return Task.CompletedTask;
        }
        app.MapResourcefullUploads("/uploads", options =>
        {
            options.ResolveAsync = Resolve;
            options.OnCompletedAsync = OnCompleted;
            options.MaxSize = maxSize;
        });
        app.MapResourcefullUploads("/other-uploads", options => options.ResolveAsync = Resolve);
        app.MapResourcefullUploads("/archive-uploads", options =>
        {
            options.ResolveAsync = Resolve;
            options.StoreName = ArchiveStore;
        });
        app.MapResourcefullUploads("/flaky-uploads", options =>
        {
            options.ResolveAsync = Resolve;
            options.OnCompletedAsync = OnCompleted;
            options.StoreName = FlakyStore;
        });
        app.MapResourcefullUploadLinks("/upload-links", options =>
        {
            options.OnCompletedAsync = OnCompleted;
            options.MaxSize = maxSize;
        });
        app.MapGroup("/links").MapResourcefullUploadLinks("/uploads");

        await app.StartAsync();
        await app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync(Container);
        await app.Services.CreateScope().ServiceProvider.GetRequiredKeyedService<IResourceServiceClient>(ArchiveStore).CreateContainerAsync(Container);
        Uri? address = onPort ? new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) : null;
        HttpClient client = onPort ? new HttpClient { BaseAddress = address } : app.GetTestClient();
        started = new UploadApp(app, flakiness, client, logs) { Address = address };
        return started;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }

    internal sealed class Flakiness
    {
        public int FailCompletions { get; set; }

        public bool TakeName { get; set; }

        private int _cancellations;

        public int Cancellations => _cancellations;

        public void CountCancellation() => Interlocked.Increment(ref _cancellations);

        /// <summary>Whether this completion fails, counting it.</summary>
        public bool TakeFailure()
        {
            lock (this)
            {
                if (FailCompletions <= 0) return false;
                FailCompletions--;
                return true;
            }
        }
    }

    /// <summary>A service client whose container clients' completions fail while <see cref="Flakiness.FailCompletions"/> is above zero.</summary>
    public class FlakyClient : DispatchProxy
    {
        private object _inner = null!;
        private Flakiness _flakiness = null!;

        internal static IResourceServiceClient Wrap(IResourceServiceClient inner, Flakiness flakiness) => Wrap<IResourceServiceClient>(inner, flakiness);

        private static T Wrap<T>(T inner, Flakiness flakiness) where T : class
        {
            T wrapper = Create<T, FlakyClient>();
            var flaky = (FlakyClient)(object)wrapper;
            flaky._inner = inner;
            flaky._flakiness = flakiness;
            return wrapper;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IResourceContainerClient.CompleteUploadAsync))
            {
                if (_flakiness.TakeFailure()) return Task.FromException<ResourceInfo?>(new IOException("The store can't be reached."));
                if (_flakiness.TakeName)
                {
                    _flakiness.TakeName = false;
                    return TakeNameThenCompleteAsync(targetMethod, args!);
                }
            }
            if (targetMethod.Name == nameof(IResourceContainerClient.CancelUploadAsync)) _flakiness.CountCancellation();
            object? result = targetMethod.Invoke(_inner, args);
            return result is IResourceContainerClient container ? Wrap(container, _flakiness) : result;
        }

        private async Task<ResourceInfo?> TakeNameThenCompleteAsync(MethodInfo complete, object?[] args)
        {
            await ((IResourceContainerClient)_inner).UploadAsync((string)args[0]!, BinaryData.FromString("theirs"));
            return await (Task<ResourceInfo?>)complete.Invoke(_inner, args)!;
        }
    }
}

/// <summary>Builds tus requests, as a client sends them.</summary>
internal static class TusRequests
{
    public static HttpRequestMessage Tus(HttpMethod method, string url, string? user = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        if (user is not null) request.Headers.Add("X-Test-User", user);
        return request;
    }

    /// <summary>An <c>Upload-Metadata</c> header: each key and, when it has one, its value in Base64.</summary>
    public static string Metadata(params (string Key, string? Value)[] pairs) => string.Join(",",
        pairs.Select(p => p.Value is null ? p.Key : $"{p.Key} {Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Value))}"));

    public static ByteArrayContent Content(byte[] bytes) =>
        new(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/offset+octet-stream") } };

    public static HttpRequestMessage Create(long length, string metadata, string path = "/uploads", string? user = null,
        byte[]? content = null)
    {
        HttpRequestMessage request = Tus(HttpMethod.Post, path, user);
        request.Headers.Add("Upload-Length", length.ToString());
        request.Headers.Add("Upload-Metadata", metadata);
        if (content is not null) request.Content = Content(content);
        return request;
    }

    public static HttpRequestMessage Patch(string url, long offset, byte[] bytes, string? user = null)
    {
        HttpRequestMessage request = Tus(HttpMethod.Patch, url, user);
        request.Headers.Add("Upload-Offset", offset.ToString());
        request.Content = Content(bytes);
        return request;
    }
}
