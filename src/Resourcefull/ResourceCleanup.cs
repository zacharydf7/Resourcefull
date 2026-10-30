namespace Resourcefull;

/// <summary>
/// Hears about every version Resourcefull deletes by itself, before it's deleted: expired versions, which
/// <see cref="ResourcefullOptionsBuilder.UseExpiry"/> deletes, and older versions pruned past
/// <see cref="ResourcefullOptions.MaxVersions"/>. Use it to record them in an audit system. The application's own
/// deletes aren't included. Register one with <see cref="ResourcefullOptionsBuilder.UseCleanupHandler{THandler}"/>;
/// it's a scoped service, so it can depend on the application's own.
/// <para>Coming in 3.2.</para>
/// </summary>
/// <remarks>
/// A report means the version is about to be deleted, and it's deleted only after the handler returns. If the handler
/// throws, the version stays, and the next sweep or prune tries again, so no deletion goes unreported. A report isn't
/// proof of a deletion, though: a version can be reported more than once, by each instance or prune that finds it, or
/// by a later sweep when its delete failed, and occasionally isn't deleted after all, when its expiry was extended or
/// something else deleted it first. Record reports by version ID, and check
/// <see cref="IResourceContainerClient.ExistsAsync(string, string?)"/> if you need to know a version is gone.
/// </remarks>
public interface IResourceCleanupHandler
{
    /// <summary>Called when a version is about to be deleted.</summary>
    /// <param name="cleanup">The version, and why it's being deleted.</param>
    /// <param name="cancellationToken">Cancels the cleanup, which then deletes nothing more.</param>
    ValueTask OnCleanupAsync(ResourceCleanup cleanup, CancellationToken cancellationToken);
}

/// <summary>A version Resourcefull is about to delete by itself. <para>Coming in 3.2.</para></summary>
/// <param name="Resource">The version, as stored.</param>
/// <param name="Reason">Why it's being deleted.</param>
/// <param name="At">When the cleanup that's deleting it started.</param>
public sealed record ResourceCleanup(ResourceInfo Resource, ResourceCleanupReason Reason, DateTimeOffset At);

/// <summary>Why Resourcefull is deleting a version by itself. <para>Coming in 3.2.</para></summary>
public enum ResourceCleanupReason
{
    /// <summary>Its <see cref="ResourceInfo.ExpiresAt"/> has passed.</summary>
    Expired,

    /// <summary>The name has more versions than <see cref="ResourcefullOptions.MaxVersions"/> keeps.</summary>
    Pruned
}
