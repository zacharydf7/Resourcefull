using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace Resourcefull;

/// <summary>
/// The store-wide policy settings, and each container's effective policy, cached for every scope to share: a cache
/// that drops expired entries, so it doesn't grow with the number of containers ever seen.
/// </summary>
internal sealed class ContainerPolicySettings(ResourceContainerPolicy defaults, TimeSpan cacheFor) : IDisposable
{
    public ResourceContainerPolicy Defaults { get; } = defaults;

    public TimeSpan CacheFor { get; } = cacheFor;

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

    /// <summary>
    /// Checks a new version against the policy before its content is read: an upload, or a restore, which keeps its
    /// name and type and so isn't checked against <see cref="ResourceContainerPolicy.MaxResourceCount"/> or
    /// <see cref="ResourceContainerPolicy.AllowedContentTypes"/>.
    /// </summary>
    /// <returns>The content to store: <paramref name="content"/>, or a stream over it that stops at the size limits.</returns>
    public async Task<Stream> CheckNewVersionAsync(string containerName, string resourceName, bool restore, string? contentType,
        long? declaredLength, Stream content, ClientTelemetry telemetry, CancellationToken cancellationToken)
    {
        ResourceContainerPolicy policy = await GetAsync(containerName, telemetry, cancellationToken);
        ThrowIfReadOnly(containerName, policy);
        if (!restore && policy.AllowedContentTypes is { } allowed && !Allows(allowed, contentType))
        {
            throw new ResourcePolicyViolationException(contentType is null
                ? $"The container {containerName} accepts only uploads that declare a content type: {string.Join(", ", allowed)}."
                : $"The container {containerName} doesn't accept content of type {contentType}, only {string.Join(", ", allowed)}.",
                ResourcePolicyRule.AllowedContentTypes);
        }

        ResourceContainerUsage? usage = policy.MaxContainerSize is not null || (!restore && policy.MaxResourceCount is not null)
            ? await repository.GetContainerUsageAsync(containerName, cancellationToken)
            : null;
        if (!restore && policy.MaxResourceCount is { } maxCount && usage!.ResourceCount >= maxCount
            && await repository.GetResourceInfoAsync(containerName, resourceName, null, cancellationToken) is null)
        {
            throw new ResourcePolicyViolationException(
                $"The container {containerName} holds {usage.ResourceCount} resources, as many as it may.",
                ResourcePolicyRule.MaxResourceCount, maxCount);
        }

        ContentLimit? limit = null;
        if (policy.MaxResourceSize is { } maxSize)
        {
            limit = new ContentLimit(maxSize.Bytes, ResourcePolicyRule.MaxResourceSize, maxSize.Bytes,
                $"The content is larger than the container {containerName} allows for one resource, {maxSize}.");
        }

        if (policy.MaxContainerSize is { } maxContainerSize)
        {
            long room = Math.Max(0, maxContainerSize.Bytes - usage!.StoredSize.Bytes);
            if (limit is null || room < limit.Bytes)
            {
                limit = new ContentLimit(room, ResourcePolicyRule.MaxContainerSize, maxContainerSize.Bytes,
                    $"The container {containerName} has no room for the content: it holds {usage.StoredSize} of its {maxContainerSize}.");
            }
        }

        if (limit is null) return content;
        if (declaredLength > limit.Bytes) throw limit.Exception();
        return new LimitedContentStream(content, limit);
    }

    private static void ThrowIfReadOnly(string containerName, ResourceContainerPolicy policy)
    {
        if (policy.ReadOnly == true)
            throw new ResourcePolicyViolationException($"The container {containerName} is read-only.", ResourcePolicyRule.ReadOnly);
    }

    private async ValueTask<ResourceContainerPolicy> GetAsync(string containerName, ClientTelemetry telemetry,
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
                throw;
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
        return effective;
    }

    /// <summary>
    /// Whether <paramref name="contentType"/> is one of <paramref name="allowed"/>, compared without parameters and
    /// ignoring case, where <c>type/*</c> covers every subtype and <c>*/*</c> every type.
    /// </summary>
    private static bool Allows(IReadOnlyCollection<string> allowed, string? contentType)
    {
        if (contentType is null) return false;
        string type = MediaType(contentType);
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
