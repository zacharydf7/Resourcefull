using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Resourcefull.Providers;

namespace Resourcefull;

/// <summary>
/// The names Resourcefull's traces and metrics are published under, for subscribing to them, for example with
/// OpenTelemetry's <c>AddSource(ResourcefullTelemetry.ActivitySourceName)</c> and
/// <c>AddMeter(ResourcefullTelemetry.MeterName)</c>. Nothing is measured while nothing subscribes.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> every operation's span comes from.</summary>
    public const string ActivitySourceName = "Resourcefull";

    /// <summary>The <see cref="Meter"/> Resourcefull's metrics come from.</summary>
    public const string MeterName = "Resourcefull";
}

/// <summary>
/// What Resourcefull's telemetry records, as configured through
/// <see cref="ResourcefullOptionsBuilder.UseTelemetry(Action{ResourcefullTelemetryOptions})"/>.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourcefullTelemetryOptions
{
    /// <summary>
    /// Record resource names on spans, as <c>resourcefull.resource</c>. Off by default, because resource names, such
    /// as file names, can hold personal data. It also decides whether a failed operation's exception message and
    /// full exception text go on its span, since they often name the resource; without it, the exception's type stands
    /// in for its message. Container names are always recorded on spans, and names never on metrics, where they would
    /// make too many distinct series.
    /// </summary>
    public bool RecordResourceNames { get; set; }
}

/// <summary>The source, meter and instruments the clients record to.</summary>
internal static class Telemetry
{
    private static readonly string? Version =
        typeof(Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public static readonly ActivitySource Source = new(ResourcefullTelemetry.ActivitySourceName, Version);

    private static readonly Meter Meter = new(ResourcefullTelemetry.MeterName, Version);

    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("resourcefull.client.operation.duration",
        unit: "s", description: "How long a client operation took, from its call to its result.");

    public static readonly Counter<long> Uploaded = Meter.CreateCounter<long>("resourcefull.client.uploaded",
        unit: "By", description: "Content bytes stored by uploads and restores.");

    public static readonly Counter<long> Downloaded = Meter.CreateCounter<long>("resourcefull.client.downloaded",
        unit: "By", description: "Content bytes read from download streams, counted as they're read.");

    public static readonly Counter<long> PruneFailures = Meter.CreateCounter<long>("resourcefull.client.prune.failures",
        unit: "{failure}", description: "Prunes of older versions that failed, after a write or in a container prune; the versions stay.");

    public static readonly Counter<long> Expired = Meter.CreateCounter<long>("resourcefull.client.expired",
        unit: "{version}", description: "Expired versions that sweeps deleted.");

    public static readonly Counter<long> ExpireFailures = Meter.CreateCounter<long>("resourcefull.client.expire.failures",
        unit: "{version}", description: "Expired versions a sweep failed to delete; they stay for the next sweep.");

    public static readonly Counter<long> LastKnownPolicyUses = Meter.CreateCounter<long>(
        "resourcefull.client.policy.last_known_uses", unit: "{use}",
        description: "Times a container's last known policy was used because the policy provider failed.");

    public static readonly Histogram<double> InspectionDuration = Meter.CreateHistogram<double>(
        "resourcefull.client.inspection.duration", unit: "s",
        description: "How long the upload inspector took to answer, from the start of the upload or imported version.");

    public static readonly Counter<long> CleanupHandlerFailures = Meter.CreateCounter<long>(
        "resourcefull.client.cleanup_handler.failures", unit: "{failure}",
        description: "Calls to the cleanup handler that threw; the version they reported stays.");

    /// <summary>
    /// <c>sqlserver</c> or <c>inmemory</c> for the built-in providers, otherwise the repository's type name: a
    /// low-cardinality tag that tells measurements from different stores apart.
    /// </summary>
    public static string ProviderName(ResourceRepository repository) => ProviderNames.GetOrAdd(repository.GetType(), static type =>
    {
        string? assembly = type.Assembly.GetName().Name;
        return assembly?.StartsWith("Resourcefull.", StringComparison.Ordinal) == true
            ? assembly["Resourcefull.".Length..].ToLowerInvariant()
            : type.FullName ?? type.Name;
    });

    private static readonly ConcurrentDictionary<Type, string> ProviderNames = new();
}

/// <summary>
/// Records one client operation: a span, when anything listens to <see cref="Telemetry.Source"/>, and its duration,
/// when anything listens to the histogram. When nothing listens, it skips the span, its tags and the timing; what's
/// left is two checks, the delegate each operation passes in, and an async wrapper around it.
/// </summary>
internal sealed class ClientTelemetry(string provider, ResourcefullOptions options)
{
    public string Provider { get; } = provider;

    /// <summary>The tags every measurement carries: the provider, and the store's name when it has one.</summary>
    private TagList StoreTags()
    {
        var tags = new TagList { { "resourcefull.provider", Provider } };
        if (options.StoreName is not null) tags.Add("resourcefull.store", options.StoreName);
        return tags;
    }

    public Task<T> RunAsync<T>(string operation, string? containerName, string? resourceName, Func<Task<T>> body,
        string? extraTag = null, object? extraValue = null) =>
        RunAsync(operation, containerName, resourceName, _ => body(), extraTag, extraValue);

    /// <summary>As the other overload, passing the operation's span, if anything listens, to tag what it finds.</summary>
    public async Task<T> RunAsync<T>(string operation, string? containerName, string? resourceName, Func<Activity?, Task<T>> body,
        string? extraTag = null, object? extraValue = null)
    {
        bool timed = Telemetry.Duration.Enabled;
        long start = timed ? Stopwatch.GetTimestamp() : 0;
        using Activity? activity = StartSpan(operation, containerName);
        if (activity is not null)
        {
            if (resourceName is not null && options.Telemetry.RecordResourceNames) activity.SetTag("resourcefull.resource", resourceName);
            if (extraTag is not null) activity.SetTag(extraTag, extraValue);
        }

        string? errorType = null;
        try
        {
            return await body(activity).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errorType = exception.GetType().FullName;
            (exception as ResourcefullException)?.LocateIfUnknown(containerName, resourceName);
            RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            if (timed)
            {
                TagList tags = StoreTags();
                tags.Add("resourcefull.operation", operation);
                if (errorType is not null) tags.Add("error.type", errorType);
                Telemetry.Duration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, tags);
            }
        }
    }

    /// <summary>A span named and tagged like every Resourcefull span; null when nothing listens.</summary>
    public Activity? StartSpan(string operation, string? containerName)
    {
        Activity? activity = Telemetry.Source.StartActivity($"resourcefull {operation}");
        if (activity is null) return null;
        activity.SetTag("resourcefull.operation", operation);
        activity.SetTag("resourcefull.provider", Provider);
        if (options.StoreName is not null) activity.SetTag("resourcefull.store", options.StoreName);
        if (!string.IsNullOrEmpty(containerName)) activity.SetTag("resourcefull.container", containerName);
        return activity;
    }

    /// <summary>
    /// Marks the span failed, with the exception's type. Its message goes on the span only when resource names may:
    /// messages often name the resource, which can be personal data.
    /// </summary>
    public void RecordFailure(Activity? activity, Exception exception)
    {
        if (activity is null) return;
        string? errorType = exception.GetType().FullName;
        activity.SetTag("error.type", errorType);
        if (exception is ResourcePolicyViolationException violation)
        {
            activity.SetTag("resourcefull.policy_rule", violation.Rule switch
            {
                ResourcePolicyRule.MaxResourceSize => "max_resource_size",
                ResourcePolicyRule.MaxContainerSize => "max_container_size",
                ResourcePolicyRule.MaxResourceCount => "max_resource_count",
                ResourcePolicyRule.AllowedContentTypes => "allowed_content_types",
                ResourcePolicyRule.ReadOnly => "read_only",
                _ => violation.Rule.ToString()
            });
        }
        activity.SetStatus(ActivityStatusCode.Error, options.Telemetry.RecordResourceNames ? exception.Message : null);
        RecordException(activity, exception);
    }

    /// <summary>
    /// Adds the exception to the span as an event, without marking it failed, for a failure the operation recovered
    /// from. Its message goes on the span only when resource names may.
    /// </summary>
    public void RecordException(Activity? activity, Exception exception)
    {
        if (activity is null) return;
        if (options.Telemetry.RecordResourceNames)
        {
            activity.AddException(exception);
        }
        else
        {
            // The default stack trace is exception.ToString(), which starts with the message; StackTrace has only frames.
            activity.AddException(exception, new TagList
            {
                { "exception.message", exception.GetType().FullName },
                { "exception.stacktrace", exception.StackTrace }
            });
        }
    }

    /// <summary>Adds one to <paramref name="counter"/>, tagged with the provider and the exception's type.</summary>
    public void CountFailure(Counter<long> counter, Exception exception, string? extraTag = null, object? extraValue = null)
    {
        if (!counter.Enabled) return;
        TagList tags = StoreTags();
        tags.Add("error.type", exception.GetType().FullName);
        if (extraTag is not null) tags.Add(extraTag, extraValue);
        counter.Add(1, tags);
    }

    public void CountExpired()
    {
        if (Telemetry.Expired.Enabled) Telemetry.Expired.Add(1, StoreTags());
    }

    /// <summary>Tells the cleanup handler about a version, counting the call as failed if it throws.</summary>
    public async Task NotifyCleanupAsync(IResourceCleanupHandler handler, ResourceCleanup cleanup, CancellationToken cancellationToken)
    {
        try
        {
            await handler.OnCleanupAsync(cleanup, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            CountFailure(Telemetry.CleanupHandlerFailures, exception, "resourcefull.cleanup_reason",
                cleanup.Reason == ResourceCleanupReason.Expired ? "expired" : "pruned");
            throw;
        }
    }

    /// <summary>
    /// Records the upload inspector's <paramref name="outcome"/> (<c>accepted</c>, <c>refused</c>, <c>failed</c> or
    /// <c>canceled</c>) on its span, and how long it took.
    /// </summary>
    public void RecordInspection(Activity? activity, string operation, string outcome, Exception? failure, long start)
    {
        activity?.SetTag("resourcefull.inspection_outcome", outcome);
        if (!Telemetry.InspectionDuration.Enabled) return;
        TagList tags = StoreTags();
        tags.Add("resourcefull.operation", operation);
        tags.Add("resourcefull.inspection_outcome", outcome);
        if (failure is not null) tags.Add("error.type", failure.GetType().FullName);
        Telemetry.InspectionDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, tags);
    }

    public void RecordUploaded(string operation, long bytes)
    {
        if (!Telemetry.Uploaded.Enabled) return;
        TagList tags = StoreTags();
        tags.Add("resourcefull.operation", operation);
        Telemetry.Uploaded.Add(bytes, tags);
    }

    /// <summary>The stream, counting the bytes read from it when anything listens to the downloaded counter.</summary>
    public Stream CountDownloaded(Stream stream)
    {
        if (!Telemetry.Downloaded.Enabled) return stream;
        TagList tags = StoreTags();
        tags.Add("resourcefull.operation", "download");
        return new DownloadCountingStream(stream, tags);
    }
}

/// <summary>A download stream that adds the bytes read from it to the downloaded counter.</summary>
internal sealed class DownloadCountingStream(Stream inner, TagList tags) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    private int Counted(int read)
    {
        if (read > 0) Telemetry.Downloaded.Add(read, tags);
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
