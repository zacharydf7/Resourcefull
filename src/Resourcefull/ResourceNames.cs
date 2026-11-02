using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Resourcefull;

/// <summary>
/// The limits every container and resource name must stay within, and checks against all the name rules. The clients
/// reject longer names, so a provider that sizes its storage from these always has room. Like
/// <see cref="ResourceRepository"/>, the limits change only in a major release.
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
    /// most <see cref="MaxContainerNameLength"/> characters, with no whitespace at either end, and with no control
    /// characters, bidirectional controls or unpaired surrogates. If this returns <see langword="false"/> for a non-null
    /// name, creating or importing a container with that name throws <see cref="ResourceNameException"/>.
    /// </summary>
    /// <param name="name">The container name to check.</param>
    /// <returns><see langword="true"/> if a write can create a container named <paramref name="name"/>.</returns>
    public static bool IsValidContainerName([NotNullWhen(true)] string? name)
        => !string.IsNullOrWhiteSpace(name) && FindProblem(name, MaxContainerNameLength, strict: true) is null;

    /// <summary>
    /// Whether <paramref name="name"/> is a valid resource name: not <see langword="null"/>, empty or whitespace, at
    /// most <see cref="MaxResourceNameLength"/> characters, with no whitespace at either end, and with no control
    /// characters, bidirectional controls or unpaired surrogates. If this returns <see langword="false"/> for a non-null
    /// name, uploading, copying, moving or renaming to that name throws <see cref="ResourceNameException"/>.
    /// </summary>
    /// <param name="name">The resource name to check.</param>
    /// <returns><see langword="true"/> if a write can create a resource named <paramref name="name"/>.</returns>
    public static bool IsValidResourceName([NotNullWhen(true)] string? name)
        => !string.IsNullOrWhiteSpace(name) && FindProblem(name, MaxResourceNameLength, strict: true) is null;

    // The checks for a name that must already exist keep the rules from before 4.0, so a name stored then that the
    // stricter rules refuse can still be read, deleted, and moved or renamed to a name they accept.

    /// <summary>
    /// Whether <paramref name="name"/> is a resource name a store could hold: the rules from before 4.0, which an
    /// export's names are checked against, so an export of a name stored then can still be imported.
    /// </summary>
    internal static bool IsValidStoredResourceName([NotNullWhen(true)] string? name)
        => !string.IsNullOrWhiteSpace(name) && FindProblem(name, MaxResourceNameLength, strict: false) is null;

    internal static void ThrowIfInvalidContainerName(string resourceContainerName, [CallerArgumentExpression(nameof(resourceContainerName))] string? paramName = null)
        => ThrowIfInvalid(resourceContainerName, MaxContainerNameLength, paramName, strict: false);

    internal static void ThrowIfInvalidResourceName(string resourceName, [CallerArgumentExpression(nameof(resourceName))] string? paramName = null)
        => ThrowIfInvalid(resourceName, MaxResourceNameLength, paramName, strict: false);

    /// <summary>Checks a container name that a write may create.</summary>
    internal static void ThrowIfInvalidNewContainerName(string resourceContainerName,
        [CallerArgumentExpression(nameof(resourceContainerName))] string? paramName = null)
        => ThrowIfInvalid(resourceContainerName, MaxContainerNameLength, paramName, strict: true);

    /// <summary>Checks a resource name that a write may create.</summary>
    internal static void ThrowIfInvalidNewResourceName(string resourceName,
        [CallerArgumentExpression(nameof(resourceName))] string? paramName = null)
        => ThrowIfInvalid(resourceName, MaxResourceNameLength, paramName, strict: true);

    private static void ThrowIfInvalid(string name, int maxLength, string? paramName, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name, paramName);

        if (string.IsNullOrWhiteSpace(name))
            throw new ResourceNameException($"The name \"{name}\" is empty or only whitespace.", paramName, name);
        if (FindProblem(name, maxLength, strict) is { } problem)
            throw new ResourceNameException(problem, paramName, name);
    }

    /// <summary>What's wrong with a name that isn't blank, or <see langword="null"/> if nothing is.</summary>
    private static string? FindProblem(string name, int maxLength, bool strict)
    {
        if (name.Length > maxLength)
        {
            // Names that long are at least 129 characters, so the first 40 are enough to recognize one by.
            return $"The name \"{name[..40]}…\" is {name.Length} characters long, more than the {maxLength} a " +
                "name may have.";
        }

        // SQL Server ignores trailing spaces when comparing strings, so "a" and "a " would be the same name there but
        // different names everywhere else.
        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
            return $"The name \"{name}\" starts or ends with whitespace, which a name mustn't.";

        return strict ? FindForbiddenCharacter(name) : null;
    }

    /// <summary>
    /// The first character a new name mustn't have, described by its position rather than shown, since it could change
    /// how the message itself displays; or <see langword="null"/> if there's none. Control characters break lines and
    /// headers, bidirectional controls can make a name display as another (a name made of "invoice", U+202E and
    /// "fdp.exe" displays as "invoiceexe.pdf"), and an unpaired surrogate isn't text at all: JSON and HTTP replace it,
    /// so an export would change the name. Joiners and the other format characters stay allowed, as some scripts and
    /// emoji need them.
    /// </summary>
    private static string? FindForbiddenCharacter(string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                i++;
                continue;
            }

            string? kind = char.IsSurrogate(c) ? "an unpaired surrogate"
                : char.IsControl(c) ? "a control character"
                : IsDirectionControl(c) ? "a bidirectional control"
                : null;
            if (kind is not null)
                return $"The name has {kind}, U+{(int)c:X4}, at index {i}, which a name mustn't.";
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="c"/> is one of Unicode's bidirectional controls. The same list is in the ASP.NET Core
    /// package's ResourceHttpResult, which leaves them out of download file names.
    /// </summary>
    private static bool IsDirectionControl(char c) =>
        c is '\u061C' or (>= '\u200E' and <= '\u200F') or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
}
