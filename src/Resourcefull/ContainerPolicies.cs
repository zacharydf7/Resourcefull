using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace Resourcefull;

/// <summary>
/// The store-wide policy settings, and each container's effective policy, cached for every scope to share: a cache
/// that drops expired entries, so it doesn't grow with the number of containers ever seen.
/// </summary>
internal sealed class ContainerPolicySettings(ResourceContainerPolicy defaults, TimeSpan cacheFor,
    TimeSpan keepLastKnownFor) : IDisposable
{
    public ResourceContainerPolicy Defaults { get; } = defaults;

    public TimeSpan CacheFor { get; } = cacheFor;

    /// <summary>
    /// How long a container's policy stands in for its provider's answer after it was given, while the provider fails.
    /// </summary>
    public TimeSpan KeepLastKnownFor { get; } = keepLastKnownFor;

    public MemoryCache Cache { get; } = new(new MemoryCacheOptions());

    public void Dispose() => Cache.Dispose();
}

/// <summary>
/// Enforces container policies for one scope's clients: finds a container's effective policy, asking the application's
/// policy provider only when the cache misses, and throws for a write the policy refuses.
/// </summary>
internal sealed class ContainerPolicies(ContainerPolicySettings settings, IResourceRepository repository,
    IResourcePolicyProvider? provider = null)
{
    public async Task ThrowIfReadOnlyAsync(string containerName, ClientTelemetry telemetry, CancellationToken cancellationToken)
        => ThrowIfReadOnly(containerName, await GetAsync(containerName, telemetry, cancellationToken));

    public async Task<bool> IsReadOnlyAsync(string containerName, ClientTelemetry telemetry, CancellationToken cancellationToken)
        => (await GetAsync(containerName, telemetry, cancellationToken)).ReadOnly == true;

    /// <summary>
    /// Checks a new version against the policy before its content is read: an upload or a copy, or a restore, which
    /// keeps its name and type and so isn't checked against <see cref="ResourceContainerPolicy.MaxResourceCount"/> or
    /// <see cref="ResourceContainerPolicy.AllowedContentTypes"/>. A caller that keeps track of the container's usage, as an
    /// import does, passes it as <paramref name="knownUsage"/>; otherwise it's read from storage when a limit needs it.
    /// </summary>
    /// <returns>The size the content must stay within as it's read, or <see langword="null"/> for no limit.</returns>
    public async Task<ContentLimit?> CheckNewVersionAsync(string containerName, string resourceName, bool restore, string? contentType,
        long? declaredLength, ClientTelemetry telemetry, CancellationToken cancellationToken, ResourceContainerUsage? knownUsage = null)
    {
        ResourceContainerPolicy policy = await GetAsync(containerName, telemetry, cancellationToken);
        ThrowIfReadOnly(containerName, policy);
        if (!restore) ThrowIfTypeNotAllowed(containerName, policy, contentType);

        ResourceContainerUsage? usage = policy.MaxContainerSize is not null || (!restore && policy.MaxResourceCount is not null)
            ? knownUsage ?? await repository.GetContainerUsageAsync(containerName, cancellationToken)
            : null;
        if (!restore && policy.MaxResourceCount is { } maxCount && usage!.ResourceCount >= maxCount
            && await repository.GetResourceInfoAsync(containerName, resourceName, null, cancellationToken) is null)
        {
            throw CountExceeded(containerName, usage, maxCount);
        }

        ContentLimit? limit = null;
        if (policy.MaxResourceSize is { } maxSize)
        {
            limit = new ContentLimit(maxSize.Bytes, ResourcePolicyRule.MaxResourceSize, maxSize.Bytes, TooLargeMessage(containerName, maxSize));
        }

        if (policy.MaxContainerSize is { } maxContainerSize)
        {
            long room = Math.Max(0, maxContainerSize.Bytes - usage!.StoredSize.Bytes);
            if (limit is null || room < limit.Bytes)
            {
                limit = new ContentLimit(room, ResourcePolicyRule.MaxContainerSize, maxContainerSize.Bytes,
                    NoRoomMessage(containerName, usage, maxContainerSize));
            }
        }

        if (declaredLength > limit?.Bytes) throw limit.Exception();
        return limit;
    }

    /// <summary>
    /// Checks a move of a resource, every version of it in <paramref name="versions"/>, against both containers'
    /// policies: the source mustn't be read-only, and the destination is checked as for a new name holding those
    /// versions, unless the move stays within its container, which then holds what it did. Every version's
    /// <see cref="ResourceInfo.ContentLength"/> must be set.
    /// </summary>
    public async Task CheckMoveAsync(string sourceContainer, string destinationContainer, IReadOnlyList<ResourceInfo> versions,
        ClientTelemetry telemetry, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly(sourceContainer, await GetAsync(sourceContainer, telemetry, cancellationToken));
        if (destinationContainer == sourceContainer) return;

        ResourceContainerPolicy policy = await GetAsync(destinationContainer, telemetry, cancellationToken);
        ThrowIfReadOnly(destinationContainer, policy);
        foreach (string? contentType in versions.Select(v => v.ContentType).Distinct())
            ThrowIfTypeNotAllowed(destinationContainer, policy, contentType);
        ResourceContainerUsage? usage = policy.MaxContainerSize is not null || policy.MaxResourceCount is not null
            ? await repository.GetContainerUsageAsync(destinationContainer, cancellationToken)
            : null;
        if (policy.MaxResourceCount is { } maxCount && usage!.ResourceCount >= maxCount)
            throw CountExceeded(destinationContainer, usage, maxCount);
        if (policy.MaxResourceSize is { } maxSize && versions.Any(v => v.ContentLength > maxSize.Bytes))
            throw new ResourcePolicyViolationException(TooLargeMessage(destinationContainer, maxSize), ResourcePolicyRule.MaxResourceSize, maxSize.Bytes);
        if (policy.MaxContainerSize is { } maxContainerSize
            && usage!.StoredSize.Bytes + versions.Sum(v => v.ContentLength ?? 0) > maxContainerSize.Bytes)
        {
            throw new ResourcePolicyViolationException(NoRoomMessage(destinationContainer, usage, maxContainerSize),
                ResourcePolicyRule.MaxContainerSize, maxContainerSize.Bytes);
        }
    }

    private static void ThrowIfReadOnly(string containerName, ResourceContainerPolicy policy)
    {
        if (policy.ReadOnly == true)
            throw new ResourcePolicyViolationException($"The container {containerName} is read-only.", ResourcePolicyRule.ReadOnly);
    }

    private static void ThrowIfTypeNotAllowed(string containerName, ResourceContainerPolicy policy, string? contentType)
    {
        if (policy.AllowedContentTypes is { } allowed && !Allows(allowed, contentType))
        {
            throw new ResourcePolicyViolationException(contentType is null
                ? $"The container {containerName} accepts only content that declares a type: {string.Join(", ", allowed)}."
                : $"The container {containerName} doesn't accept content of type {contentType}, only {string.Join(", ", allowed)}.",
                ResourcePolicyRule.AllowedContentTypes);
        }
    }

    private static ResourcePolicyViolationException CountExceeded(string containerName, ResourceContainerUsage usage, long maxCount) =>
        new($"The container {containerName} holds {usage.ResourceCount} resources, as many as it may.", ResourcePolicyRule.MaxResourceCount, maxCount);

    private static string TooLargeMessage(string containerName, ResourceSize maxSize) =>
        $"The content is larger than the container {containerName} allows for one resource, {maxSize}.";

    private static string NoRoomMessage(string containerName, ResourceContainerUsage usage, ResourceSize maxContainerSize) =>
        $"The container {containerName} has no room for the content: it holds {usage.StoredSize} of its {maxContainerSize}.";

    /// <summary>
    /// The container's effective policy: the defaults, with the provider's policy for the container in place of each
    /// limit it sets.
    /// </summary>
    public async ValueTask<ResourceContainerPolicy> GetAsync(string containerName, ClientTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        if (settings.Cache.TryGetValue(containerName, out ResourceContainerPolicy? cached)) return cached!;

        ResourceContainerPolicy? own = null;
        if (provider is not null)
        {
            using Activity? activity = telemetry.StartSpan("get_policy", containerName);
            try
            {
                own = await provider.GetPolicyAsync(containerName, cancellationToken);
            }
            catch (Exception exception)
            {
                telemetry.RecordFailure(activity, exception);
                // An outage of the policy store mustn't fail every write: the policy it last gave stands in for a while.
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                    || !settings.Cache.TryGetValue(new LastKnown(containerName), out ResourceContainerPolicy? lastKnown))
                {
                    throw;
                }

                activity?.SetTag("resourcefull.policy_last_known", true);
                telemetry.CountFailure(Telemetry.LastKnownPolicyUses, exception);
                // Kept as an answer is, so writes during the outage don't each wait for the provider to fail.
                if (settings.CacheFor > TimeSpan.Zero) settings.Cache.Set(containerName, lastKnown, settings.CacheFor);
                return lastKnown!;
            }
        }

        ResourceContainerPolicy defaults = settings.Defaults;
        ResourceContainerPolicy effective = own is null ? defaults : new ResourceContainerPolicy
        {
            MaxResourceSize = own.MaxResourceSize ?? defaults.MaxResourceSize,
            MaxContainerSize = own.MaxContainerSize ?? defaults.MaxContainerSize,
            MaxResourceCount = own.MaxResourceCount ?? defaults.MaxResourceCount,
            AllowedContentTypes = own.AllowedContentTypes ?? defaults.AllowedContentTypes,
            ReadOnly = own.ReadOnly ?? defaults.ReadOnly
        };
        if (settings.CacheFor > TimeSpan.Zero) settings.Cache.Set(containerName, effective, settings.CacheFor);
        if (provider is not null && settings.KeepLastKnownFor > TimeSpan.Zero)
            settings.Cache.Set(new LastKnown(containerName), effective, settings.KeepLastKnownFor);
        return effective;
    }

    /// <summary>The cache key of a container's policy as the provider last gave it.</summary>
    private sealed record LastKnown(string ContainerName);

    /// <summary>
    /// Whether <paramref name="contentType"/> is one of <paramref name="allowed"/>, compared without parameters and
    /// ignoring case, where <c>type/*</c> covers every subtype and <c>*/*</c> every type. It's parsed strictly: a value
    /// holding more than one type, which browsers read as its last, isn't allowed because of its first.
    /// </summary>
    private static bool Allows(IReadOnlyCollection<string> allowed, string? contentType)
    {
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.MediaType is not { } type)
            return false;
        return allowed.Select(MediaType).Any(pattern => pattern == "*/*"
            || (pattern.EndsWith("/*", StringComparison.Ordinal)
                ? type.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(type, pattern, StringComparison.OrdinalIgnoreCase)));
    }

    private static string MediaType(string value) => value.Split(';', 2)[0].Trim();
}

/// <summary>How many bytes a new version may have, and the rule that refuses one with more.</summary>
internal sealed record ContentLimit(long Bytes, ResourcePolicyRule Rule, long RuleLimit, string Message)
{
    public ResourcePolicyViolationException Exception() => new(Message, Rule, RuleLimit);
}

/// <summary>
/// Content being stored, which throws as soon as more than <see cref="ContentLimit.Bytes"/> of it are read, so the
/// provider stores nothing. It seeks when the content does, so a provider can rewind it to retry, and counts from where
/// it's rewound to.
/// </summary>
internal sealed class LimitedContentStream(Stream content, ContentLimit limit) : Stream
{
    private readonly long _start = content.CanSeek ? content.Position : 0;
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => content.CanSeek;
    public override bool CanWrite => false;
    public override long Length => content.Length;

    public override long Position
    {
        get => content.Position;
        set
        {
            content.Position = value;
            _read = value - _start;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Counted(content.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Counted(content.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await content.ReadAsync(buffer, cancellationToken));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Counted(await content.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    private int Counted(int read)
    {
        _read += read;
        if (_read > limit.Bytes) throw limit.Exception();
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = content.Seek(offset, origin);
        _read = position - _start;
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
