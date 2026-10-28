using System.Runtime.CompilerServices;

namespace Resourcefull;

/// <summary>
/// The limits every container and resource name must stay within. The clients reject longer names, so a provider that
/// sizes its storage from these always has room. Like <see cref="IResourceRepository"/>, the limits change only in a
/// major release.
/// </summary>
public static class ResourceNames
{
    /// <summary>The longest container name, in characters.</summary>
    public static readonly int MaxContainerNameLength = 128;

    /// <summary>The longest resource name, in characters.</summary>
    public static readonly int MaxResourceNameLength = 256;

    /// <summary>The longest version ID a client generates, in characters.</summary>
    public static readonly int MaxVersionIdLength = 36;

    internal static void ThrowIfInvalidContainerName(string resourceContainerName, [CallerArgumentExpression(nameof(resourceContainerName))] string? paramName = null)
        => ThrowIfInvalid(resourceContainerName, MaxContainerNameLength, paramName);

    internal static void ThrowIfInvalidResourceName(string resourceName, [CallerArgumentExpression(nameof(resourceName))] string? paramName = null)
        => ThrowIfInvalid(resourceName, MaxResourceNameLength, paramName);

    private static void ThrowIfInvalid(string name, int maxLength, string? paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);

        if (name.Length > maxLength)
            throw new ArgumentException($"Name must be at most {maxLength} characters.", paramName);

        // SQL Server ignores trailing spaces when comparing strings, so "a" and "a " would be the same name there but
        // different names everywhere else.
        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
            throw new ArgumentException("Name must not start or end with whitespace.", paramName);
    }
}
