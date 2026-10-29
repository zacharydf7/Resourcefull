using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Resourcefull;

/// <summary>
/// The limits every container and resource name must stay within, and checks against all the name rules. The clients
/// reject longer names, so a provider that sizes its storage from these always has room. Like
/// <see cref="IResourceRepository"/>, the limits change only in a major release.
/// </summary>
public static class ResourceNames
{
    /// <summary>The longest container name, in characters.</summary>
    public static readonly int MaxContainerNameLength = 128;

    /// <summary>The longest resource name, in characters.</summary>
    public static readonly int MaxResourceNameLength = 256;

    /// <summary>The longest version ID a client generates, in characters.</summary>
    public static readonly int MaxVersionIdLength = 36;

    /// <summary>
    /// Whether <paramref name="name"/> is a valid container name: not <see langword="null"/>, empty or whitespace, at
    /// most <see cref="MaxContainerNameLength"/> characters, and with no whitespace at either end. The clients throw
    /// <see cref="ArgumentException"/> for any name this returns <see langword="false"/> for.
    /// </summary>
    /// <param name="name">The container name to check.</param>
    /// <returns><see langword="true"/> if the clients accept <paramref name="name"/> as a container name.</returns>
    public static bool IsValidContainerName([NotNullWhen(true)] string? name)
        => !string.IsNullOrWhiteSpace(name) && FindProblem(name, MaxContainerNameLength) is null;

    /// <summary>
    /// Whether <paramref name="name"/> is a valid resource name: not <see langword="null"/>, empty or whitespace, at
    /// most <see cref="MaxResourceNameLength"/> characters, and with no whitespace at either end. The clients throw
    /// <see cref="ArgumentException"/> for any name this returns <see langword="false"/> for.
    /// </summary>
    /// <param name="name">The resource name to check.</param>
    /// <returns><see langword="true"/> if the clients accept <paramref name="name"/> as a resource name.</returns>
    public static bool IsValidResourceName([NotNullWhen(true)] string? name)
        => !string.IsNullOrWhiteSpace(name) && FindProblem(name, MaxResourceNameLength) is null;

    internal static void ThrowIfInvalidContainerName(string resourceContainerName, [CallerArgumentExpression(nameof(resourceContainerName))] string? paramName = null)
        => ThrowIfInvalid(resourceContainerName, MaxContainerNameLength, paramName);

    internal static void ThrowIfInvalidResourceName(string resourceName, [CallerArgumentExpression(nameof(resourceName))] string? paramName = null)
        => ThrowIfInvalid(resourceName, MaxResourceNameLength, paramName);

    private static void ThrowIfInvalid(string name, int maxLength, string? paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);

        if (FindProblem(name, maxLength) is { } problem)
            throw new ArgumentException(problem, paramName);
    }

    /// <summary>What's wrong with a name that isn't blank, or <see langword="null"/> if nothing is.</summary>
    private static string? FindProblem(string name, int maxLength)
    {
        if (name.Length > maxLength)
            return $"Name must be at most {maxLength} characters.";

        // SQL Server ignores trailing spaces when comparing strings, so "a" and "a " would be the same name there but
        // different names everywhere else.
        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
            return "Name must not start or end with whitespace.";

        return null;
    }
}
