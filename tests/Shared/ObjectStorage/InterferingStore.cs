using Resourcefull.ObjectStorage;

namespace Resourcefull.Tests.ObjectStorage;

/// <summary>
/// A store that can fail reads of some keys, and whose first join, once done, can wait for <see cref="Release"/>, so
/// a test can act meanwhile.
/// </summary>
internal sealed class InterferingStore(IObjectStore inner) : IObjectStore
{
    private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the first join waits for <see cref="Release"/> once it's done.</summary>
    public bool HoldFirstJoin { get; init; }

    /// <summary>The keys whose reads fail, as a store that can't be reached fails them.</summary>
    public Func<string, bool>? FailReads { get; set; }

    /// <summary>The keys whose writes and deletes fail, as a store that can't be reached fails them.</summary>
    public Func<string, bool>? FailWrites { get; set; }

    /// <summary>Called before each conditional write, with its key and body; it may throw, as a failed write does.</summary>
    public Func<string, byte[], Task>? BeforePut { get; set; }

    /// <summary>Called before each join.</summary>
    public Func<Task>? BeforeJoin { get; set; }

    /// <summary>Called after each join the store completes.</summary>
    public Func<Task>? AfterJoin { get; set; }

    /// <summary>How many bytes the joins were given to copy.</summary>
    public long JoinedLength { get; private set; }

    /// <summary>Completes when the first join is done.</summary>
    public Task Joined => _joined.Task;

    public void Release() => _released.TrySetResult();

    public string ConditionalWrites => inner.ConditionalWrites;

    /// <summary>The most parts a join takes, in place of the store's own limit.</summary>
    public int? MaxParts { get; init; }

    public int MaxJoinedParts => MaxParts ?? inner.MaxJoinedParts;

    public int KeySegmentBytes => inner.KeySegmentBytes;

    public long MaxUploadLength => inner.MaxUploadLength;

    /// <summary>The most an object holds, in place of the store's own limit.</summary>
    public long? MaxObject { get; init; }

    public long MaxObjectLength => MaxObject ?? inner.MaxObjectLength;

    /// <summary>Changes what a listing says of each object, as for objects written longer ago.</summary>
    public Func<ObjectListing, ObjectListing>? MapListing { get; set; }

    public async Task JoinAsync(string key, IReadOnlyList<ObjectRange> parts, CancellationToken cancellationToken)
    {
        if (BeforeJoin is not null) await BeforeJoin();
        ThrowIfFailing(key);
        JoinedLength += parts.Sum(p => p.Length);
        await inner.JoinAsync(key, parts, cancellationToken);
        if (AfterJoin is not null) await AfterJoin();
        if (HoldFirstJoin && _joined.TrySetResult()) await _released.Task.WaitAsync(cancellationToken);
    }

    public Task<(byte[]? Body, string? ETag)> GetAsync(string key, CancellationToken cancellationToken) =>
        FailReads?.Invoke(key) == true ? throw new IOException($"Reading {key} failed.") : inner.GetAsync(key, cancellationToken);

    public async Task<bool> TryPutAsync(string key, byte[] body, string? etag)
    {
        if (BeforePut is not null) await BeforePut(key, body);
        ThrowIfFailing(key);
        return await inner.TryPutAsync(key, body, etag);
    }

    public Task UploadAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        ThrowIfFailing(key);
        return inner.UploadAsync(key, content, cancellationToken);
    }

    public Task<ObjectRead?> OpenReadAsync(string key, long start, long end, long length, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(key, start, end, length, cancellationToken);

    public IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
        MapListing is { } map ? inner.ListAsync(prefix, startAfter, cancellationToken).Select(map)
            : inner.ListAsync(prefix, startAfter, cancellationToken);

    public Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        foreach (string key in keys) ThrowIfFailing(key);
        return inner.DeleteAsync(keys, cancellationToken);
    }

    public bool IsStoreFailure(Exception exception) => inner.IsStoreFailure(exception);

    private void ThrowIfFailing(string key)
    {
        if (FailWrites?.Invoke(key) == true) throw new IOException($"Writing {key} failed.");
    }
}
