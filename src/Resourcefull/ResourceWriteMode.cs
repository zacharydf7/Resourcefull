namespace Resourcefull;

/// <summary>
/// What an upload or copy does when its name already has a current version.
/// <para>Coming in 4.0.</para>
/// </summary>
public enum ResourceWriteMode
{
    /// <summary>
    /// Store only if the name has no current version; otherwise throw <see cref="ResourceConflictException"/> and store
    /// nothing. The check is part of the write, so of several concurrent writes of one new name, exactly one succeeds.
    /// </summary>
    Create,

    /// <summary>
    /// Store as the name's new current version, whether or not it has one. The container's
    /// <see cref="ContainerPolicy.MaxVersions"/> decides what happens to the version it replaces: with a limit
    /// of one it's deleted, and with more it's kept as an older version.
    /// </summary>
    Replace
}
