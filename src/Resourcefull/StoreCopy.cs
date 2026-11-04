namespace Resourcefull;

/// <summary>
/// A version to import, as <see cref="IResourceServiceClient.ImportContainerAsync(string, IAsyncEnumerable{ImportedVersion}, IReadOnlyDictionary{string, string}?, CancellationToken)"/>
/// takes it: the version as it was, and a way to open its content.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Version">
/// The version as another store holds it. Its <see cref="ResourceInfo.Name"/>, <see cref="ResourceInfo.VersionId"/>,
/// <see cref="ResourceInfo.LastModified"/>, content type, metadata, <see cref="ResourceInfo.CopiedFrom"/>, expiry,
/// retention date and legal hold are kept; its <see cref="ResourceInfo.ContentHash"/>, when it has one, is what the
/// content stored must hash to, and its <see cref="ResourceInfo.ContentLength"/>, when it has one, is checked against the
/// container's policy before any content is read. Its container name is ignored.
/// </param>
/// <param name="OpenContentAsync">
/// Opens the version's content, which the import reads to its end and then disposes; <see langword="null"/> when the
/// version is gone, and the import skips it.
/// </param>
public sealed record ImportedVersion(ResourceInfo Version, Func<CancellationToken, Task<Stream?>> OpenContentAsync);

/// <summary>
/// What <see cref="IResourceServiceClient.CopyContainerToAsync"/> copied.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="SourceContainerName">The container copied.</param>
/// <param name="DestinationContainerName">The container it was copied to.</param>
/// <param name="ResourceCount">How many resources (names) it copied.</param>
/// <param name="VersionCount">How many versions it copied, current and older.</param>
/// <param name="ContentSize">
/// The size of every version it copied: the sum of their lengths, whatever each store keeps for them.
/// </param>
public sealed record ContainerCopyResult(string SourceContainerName, string DestinationContainerName, long ResourceCount,
    long VersionCount, ResourceSize ContentSize);

/// <summary>
/// What <see cref="IResourceServiceClient.CopyStoreToAsync"/> does with a container the destination already has.
/// <para>Coming in 4.0.</para>
/// </summary>
public enum ExistingContainers
{
    /// <summary>The container fails, and the destination's is left as it is.</summary>
    Fail,

    /// <summary>
    /// The container is skipped when it matches the source's version for version, and fails otherwise, naming the first
    /// differences. A copy that comes back with nothing failed proves the destination matches.
    /// </summary>
    SkipMatching,

    /// <summary>
    /// The destination's container is brought up to date in place: versions it lacks are imported, changed metadata,
    /// content types, expiries, retention dates and holds are applied, and versions the source no longer has are
    /// deleted. The container itself is never deleted, so a failure leaves what was copied before, and running the copy
    /// again goes on from there.
    /// </summary>
    Update
}

/// <summary>
/// What <see cref="IResourceServiceClient.CopyStoreToAsync"/> does when a container fails.
/// <para>Coming in 4.0.</para>
/// </summary>
public enum StoreCopyFailures
{
    /// <summary>It stops, and throws <see cref="StoreCopyException"/>, which holds what it did before.</summary>
    Stop,

    /// <summary>It goes on with the other containers, and the result lists every failure.</summary>
    Continue
}

/// <summary>
/// How <see cref="IResourceServiceClient.CopyStoreToAsync"/> copies.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class StoreCopyOptions
{
    /// <summary>Only containers whose names start with this, ordinally; <see langword="null"/> or empty copies every container.</summary>
    public string? Prefix { get; init; }

    /// <summary>What to do with a container the destination already has. The default is <see cref="ExistingContainers.Fail"/>.</summary>
    public ExistingContainers Existing { get; init; } = ExistingContainers.Fail;

    /// <summary>What to do when a container fails. The default is <see cref="StoreCopyFailures.Stop"/>.</summary>
    public StoreCopyFailures OnFailure { get; init; } = StoreCopyFailures.Stop;

    /// <summary>
    /// Told about each container once it's done with. It's for showing progress only: an exception it throws stops the
    /// copy, as a failure with <see cref="StoreCopyFailures.Stop"/> does.
    /// </summary>
    public IProgress<ContainerCopied>? Progress { get; init; }
}

/// <summary>
/// One container <see cref="IResourceServiceClient.CopyStoreToAsync"/> is done with, for its
/// <see cref="StoreCopyOptions.Progress"/>.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Name">The container.</param>
/// <param name="Outcome">What the copy did with it.</param>
public sealed record ContainerCopied(string Name, ContainerCopyOutcome Outcome);

/// <summary>
/// What <see cref="IResourceServiceClient.CopyStoreToAsync"/> did with one container.
/// <para>Coming in 4.0.</para>
/// </summary>
public enum ContainerCopyOutcome
{
    /// <summary>The destination didn't have it, and it was copied.</summary>
    Copied,

    /// <summary>The destination had it, and it was brought up to date.</summary>
    Updated,

    /// <summary>The destination had it, and it matched the source's version for version.</summary>
    Matching,

    /// <summary>It failed.</summary>
    Failed
}

/// <summary>
/// What <see cref="ExistingContainers.Update"/> changed in one container.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Name">The container.</param>
/// <param name="Imported">How many versions it imported that the destination lacked.</param>
/// <param name="Changed">How many versions' metadata, content type, expiry, retention date or hold it changed.</param>
/// <param name="Deleted">How many versions it deleted that the source no longer has.</param>
public sealed record ContainerUpdateResult(string Name, long Imported, long Changed, long Deleted);

/// <summary>
/// A container <see cref="IResourceServiceClient.CopyStoreToAsync"/> couldn't copy, and why.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Name">The container.</param>
/// <param name="Exception">What went wrong.</param>
public sealed record ContainerCopyFailure(string Name, Exception Exception);

/// <summary>
/// What <see cref="IResourceServiceClient.CopyStoreToAsync"/> did, container by container.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Copied">The containers the destination didn't have, now copied.</param>
/// <param name="Updated">The containers <see cref="ExistingContainers.Update"/> brought up to date.</param>
/// <param name="Matching">The containers the destination had that matched the source's version for version.</param>
/// <param name="Failed">The containers that failed.</param>
/// <param name="DestinationOnly">
/// The destination's containers, within <see cref="StoreCopyOptions.Prefix"/>, that the source doesn't have, which the
/// copy leaves alone.
/// </param>
public sealed record StoreCopyResult(IReadOnlyList<ContainerCopyResult> Copied, IReadOnlyList<ContainerUpdateResult> Updated,
    IReadOnlyList<string> Matching, IReadOnlyList<ContainerCopyFailure> Failed, IReadOnlyList<string> DestinationOnly)
{
    /// <summary>Whether no container failed.</summary>
    public bool Succeeded => Failed.Count == 0;
}
