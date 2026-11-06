namespace Resourcefull;

/// <summary>
/// Hears about every version Resourcefull deletes by itself, before it's deleted: expired versions, which
/// <see cref="ResourcefullOptionsBuilder.UseExpiry"/> deletes, older versions pruned past
/// the container's <see cref="ContainerPolicy.MaxVersions"/>, and versions it purges from a bin. Use it to record them in an audit system. The application's own
/// deletes aren't included. Register one with <see cref="ResourcefullOptionsBuilder.UseCleanupHandler{THandler}"/>;
/// it's a scoped service, so it can depend on the application's own.
/// </summary>
/// <remarks>
/// A report means the version is about to be deleted, and it's deleted only after the handler returns. If the handler
/// throws, the version stays, and the next sweep or prune tries again, so no deletion goes unreported. A report isn't
/// proof of a deletion, though: a version can be reported more than once, by each instance or prune that finds it, or
/// by a later sweep when its delete failed, and occasionally isn't deleted after all, when its expiry was extended or
/// something else deleted it first. Record reports by version ID, and check
/// <see cref="IResourceContainerClient.ExistsAsync"/> if you need to know a version is gone.
/// </remarks>
public interface IResourceCleanupHandler
{
    /// <summary>Called when a version is about to be deleted.</summary>
    /// <param name="cleanup">The version, and why it's being deleted.</param>
    /// <param name="cancellationToken">Cancels the cleanup, which then deletes nothing more.</param>
    ValueTask OnCleanupAsync(ResourceCleanup cleanup, CancellationToken cancellationToken);
}

/// <summary>
/// A version Resourcefull is about to delete by itself.
/// </summary>
public sealed record ResourceCleanup
{
    /// <summary>The version, as stored.</summary>
    public required ResourceInfo Version { get; init; }

    /// <summary>Why it's being deleted.</summary>
    public required ResourceCleanupReason Reason { get; init; }

    /// <summary>When the cleanup that's deleting it started.</summary>
    public required DateTimeOffset At { get; init; }
}

/// <summary>
/// Why Resourcefull is deleting a version by itself.
/// </summary>
public enum ResourceCleanupReason
{
    /// <summary>Its <see cref="ResourceInfo.ExpiresAt"/> has passed.</summary>
    Expired,

    /// <summary>
    /// The name has more versions than its container's <see cref="ContainerPolicy.MaxVersions"/> keeps. With
    /// <see cref="ResourcefullOptionsBuilder.UseExpiry"/> and a provider that supports it, such a version is set to
    /// expire five minutes later instead, and
    /// is reported as <see cref="Expired"/> when the sweep deletes it, or as <see cref="Pruned"/> if, in a container
    /// that keeps one version, it's deleted sooner along with the version that replaced it. In a container with a
    /// <see cref="ContainerPolicy.KeepDeletedFor"/>, it goes to the bin straight away.
    /// </summary>
    Pruned,

    /// <summary>
    /// Its <see cref="ResourceInfo.PurgeAt"/> has passed, and the sweep of <see cref="ResourcefullOptionsBuilder.UseExpiry"/>
    /// is purging it from the bin for good. If it was pruned into the bin, it was reported then too, as
    /// <see cref="Pruned"/>; if the application deleted it, this is the first report.
    /// </summary>
    Purged
}
