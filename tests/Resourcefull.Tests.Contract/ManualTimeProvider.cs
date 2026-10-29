namespace Resourcefull.Tests.Contract;

/// <summary>A clock that reads whatever it was last set to, so a test can decide when each write happens.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
