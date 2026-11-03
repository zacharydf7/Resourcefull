namespace Resourcefull.Providers;

/// <summary>
/// What an <see cref="ResourceRepository.AddResourceAsync"/>
/// write requires of the name before it stores a version.
/// </summary>
public sealed class ResourceWriteCondition
{
    /// <summary>Store only if no version of the name exists.</summary>
    public bool CreateOnly { get; init; }

    /// <summary>Store only if the name's current version meets these.</summary>
    public ResourceConditions? Conditions { get; init; }
}
