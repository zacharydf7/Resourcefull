namespace Resourcefull.Providers;

/// <summary>
/// A version an upload session is storing, as <see cref="ResourceRepository.GetStagedVersionAsync"/> reports it.
/// </summary>
public sealed record StagedVersion
{
    /// <summary>
    /// The version as it was staged: its location, ID, content type, metadata, expiry and declared length.
    /// </summary>
    public required NewResourceVersion Version { get; init; }

    /// <summary>How many bytes have been appended to it so far.</summary>
    public required long StoredLength { get; init; }
}
