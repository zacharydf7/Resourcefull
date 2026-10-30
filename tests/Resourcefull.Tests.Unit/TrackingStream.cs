namespace Resourcefull.Tests.Unit;

/// <summary>Records which dispose path ran.</summary>
internal sealed class TrackingStream(byte[]? content = null) : MemoryStream(content ?? [])
{
    public bool Disposed { get; private set; }

    public bool DisposedAsync { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        DisposedAsync = true;
        return ValueTask.CompletedTask;
    }
}
