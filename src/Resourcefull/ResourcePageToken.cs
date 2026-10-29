using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Resourcefull;

/// <summary>
/// Encodes where a listing continues: the last name returned, and a fingerprint of the prefix, so a token can't be
/// carried over to a listing of another prefix and silently skip or repeat names.
/// </summary>
internal static class ResourcePageToken
{
    private const string Version = "1";

    public static string Encode(string? prefix, string lastName) =>
        $"{Version}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(lastName))}.{Fingerprint(prefix)}";

    /// <summary>The last name the token's page returned.</summary>
    /// <exception cref="ArgumentException">The token is malformed, or belongs to a listing of another prefix.</exception>
    public static string Decode(string token, string? prefix, string paramName)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Version)
            throw new ArgumentException("The continuation token isn't one a listing returned.", paramName);
        if (parts[2] != Fingerprint(prefix))
            throw new ArgumentException("The continuation token belongs to a listing of another prefix.", paramName);

        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The continuation token isn't one a listing returned.", paramName, exception);
        }
    }

    private static string Fingerprint(string? prefix) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix ?? ""))[..8]);
}
