namespace Resourcefull.Tests.Integration;

/// <summary>
/// Cancels <paramref name="source"/> once <paramref name="readsBeforeCancel"/> reads have been served, so the next read
/// observes cancellation without any timing dependence.
/// </summary>
internal sealed class CancelAfterReadsStream(byte[] content, CancellationTokenSource source, int readsBeforeCancel) : MemoryStream(content)
{
    private int _reads;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (++_reads > readsBeforeCancel) source.Cancel();
        return base.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
