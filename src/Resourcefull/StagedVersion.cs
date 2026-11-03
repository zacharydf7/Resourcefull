namespace Resourcefull.Providers;

/// <summary>
/// A version an upload session is storing, as <see cref="ResourceRepository.GetStagedVersionAsync"/> reports it.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Version">
/// The version as it was staged: its location, ID, content type, metadata, expiry and declared length.
/// </param>
/// <param name="StoredLength">How many bytes have been appended to it so far.</param>
public sealed record StagedVersion(NewResourceVersion Version, long StoredLength);
