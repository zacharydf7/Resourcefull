using System.Runtime.CompilerServices;

namespace Resourcefull;

/// <summary>
/// The rules every container and resource name must follow. Providers size their storage from the same limits, so a
/// name that passes here always fits.
/// </summary>
internal static class ResourceNames
{
    public const int MaxContainerNameLength = 128;
    public const int MaxResourceNameLength = 256;
    public const int MaxVersionIdLength = 36;

    public static void ThrowIfInvalidContainerName(string resourceContainerName, [CallerArgumentExpression(nameof(resourceContainerName))] string? paramName = null)
        => ThrowIfInvalid(resourceContainerName, MaxContainerNameLength, paramName);

    public static void ThrowIfInvalidResourceName(string resourceName, [CallerArgumentExpression(nameof(resourceName))] string? paramName = null)
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
