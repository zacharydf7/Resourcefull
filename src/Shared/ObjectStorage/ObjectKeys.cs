using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Resourcefull.ObjectStorage;

/// <summary>
/// Where things are kept in the object store, under the key prefix: each container's record, and under the container's
/// ID, each name's head, each version's content and the indexes of what's in its bin.
/// </summary>
/// <remarks>
/// <para>
/// Names go into keys escaped, keeping their order: every character below <c>0</c> (the space, the punctuation from
/// <c>!</c> to <c>/</c>, and the control characters) becomes <c>!</c> and its two hex digits; <c>[</c> and <c>\</c>,
/// which some stores drop or take as a separator, become <c>[A</c> and <c>[B</c>; and every other character stays as it
/// is. Each escape sorts against unescaped characters just as the character it replaces would, so keys sort as the
/// names' UTF-8 does, which is Unicode code point order. A UTF-16 surrogate without its pair, which no stored name has
/// but a lookup can be given, becomes <c>[S</c> and three hex digits, so it can't match a stored name.
/// </para>
/// <para>
/// No <c>.</c> or <c>/</c> is left in a name, so stores that keep keys as files see no <c>.</c>, <c>..</c> or empty
/// segments, and a <c>/</c> goes in once a segment nears <paramref name="segmentBytes"/>, to keep it within what a
/// filesystem allows. Whether a <c>/</c> goes in depends only on what came before it, so two names get their <c>/</c>s at the same
/// places up to where they differ, and the order holds. Each key ends with <c>!</c>, which an escape never does, so no
/// key followed by a <c>/</c> is the start of another key: such a store would keep the shorter one as a directory, and
/// couldn't store it as a file too.
/// </para>
/// </remarks>
/// <param name="prefix">What every key starts with.</param>
/// <param name="segmentBytes">The most UTF-8 bytes between the <c>/</c>s put into an escaped name.</param>
internal sealed class ObjectKeys(string prefix, int segmentBytes = ObjectKeys.SegmentBytes)
{
    /// <summary>The most UTF-8 bytes between the <c>/</c>s put into an escaped name, unless the store asks for fewer.</summary>
    internal const int SegmentBytes = 200;

    /// <summary>The longest unit a character escapes to: the escape of an unpaired surrogate.</summary>
    internal const int LongestUnit = 5;

    private const char End = '!';

    /// <summary>The prefix of the expiry entries, which sort by when their versions expire.</summary>
    public string Expiries { get; } = prefix + "x/";

    /// <summary>
    /// The key of a version's expiry entry: its expiry time, fixed-width so keys sort by it, then its version ID, so
    /// entries that expire at the same time sort as the contract lists them, then the token the version records for the
    /// entry, so no two writes of an entry share a key, then its container's ID and its blob.
    /// </summary>
    public string Expiry(DateTimeOffset expiresAt, string versionId, string token, string containerId, string blob) =>
        $"{Expiries}{expiresAt.UtcTicks:D19}/{Escape(versionId)}{End}/{token}/{containerId}/{blob}";

    /// <summary>
    /// What a listing of the entries after <paramref name="expiresAt"/> and <paramref name="versionId"/> starts after:
    /// past every entry with both, since <c>0</c> sorts after the <c>/</c> that follows them, and before the next
    /// version ID's, since an escape never ends with the <c>!</c> that ends a version ID.
    /// </summary>
    public string ExpiriesAfter(DateTimeOffset expiresAt, string versionId) =>
        $"{Expiries}{expiresAt.UtcTicks:D19}/{Escape(versionId)}{End}0";

    /// <summary>The prefix of the purge entries, which sort by when their versions leave a bin, as expiry entries sort.</summary>
    public string Purges { get; } = prefix + "t/";

    /// <summary>The key of a version's purge entry, in the form of an expiry entry's key, by its purge time.</summary>
    public string Purge(DateTimeOffset purgeAt, string versionId, string token, string containerId, string blob) =>
        $"{Purges}{purgeAt.UtcTicks:D19}/{Escape(versionId)}{End}/{token}/{containerId}/{blob}";

    /// <summary>What a listing of the purge entries after <paramref name="purgeAt"/> and <paramref name="versionId"/> starts after.</summary>
    public string PurgesAfter(DateTimeOffset purgeAt, string versionId) =>
        $"{Purges}{purgeAt.UtcTicks:D19}/{Escape(versionId)}{End}0";

    /// <summary>What an expiry entry's key, after <see cref="Expiries"/>, or a purge entry's after <see cref="Purges"/>, says.</summary>
    internal static (DateTimeOffset ExpiresAt, string VersionId, string Token, string ContainerId, string Blob) ParseExpiry(string key)
    {
        // An escape of a character is ! and two hex digits, so the version ID ends at the first ! that a / follows.
        int versionEnd = key.IndexOf(End + "/", 20, StringComparison.Ordinal);
        string[] ids = key[(versionEnd + 2)..].Split('/');
        return (new DateTimeOffset(long.Parse(key[..19], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero),
            Unescape(key[20..versionEnd]), ids[0], ids[1], ids[2]);
    }

    /// <summary>The prefix of every container's record.</summary>
    public string Containers { get; } = prefix + "c/";

    /// <summary>The key of a container's record.</summary>
    public string Container(string containerName) => Containers + Escape(containerName) + End;

    /// <summary>The prefix of the keys of every container whose name starts with <paramref name="namePrefix"/>.</summary>
    public string ContainersStartingWith(string? namePrefix) => Containers + (namePrefix is null ? "" : Escape(namePrefix));

    /// <summary>The prefix of everything a container holds, under its ID.</summary>
    public string Data(string containerId) => $"{prefix}d/{containerId}/";

    /// <summary>The prefix of every name's head in a container.</summary>
    public string Heads(string containerId) => Data(containerId) + "h/";

    /// <summary>The key of a name's head.</summary>
    public string Head(string containerId, string resourceName) => Heads(containerId) + Escape(resourceName) + End;

    /// <summary>The prefix of the heads of every name in a container that starts with <paramref name="namePrefix"/>.</summary>
    public string HeadsStartingWith(string containerId, string? namePrefix) =>
        Heads(containerId) + (namePrefix is null ? "" : Escape(namePrefix));

    /// <summary>
    /// The prefix of a container's search entries for one metadata key and value, under a token of the two: a hash, so
    /// an entry's key is as long as a head's, plus 23 bytes, however long the value is.
    /// </summary>
    public string SearchEntries(string containerId, string key, string value) =>
        $"{Data(containerId)}m/{Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}\0{value}")))[..22]}/";

    /// <summary>The key of a name's search entry for one metadata key and value.</summary>
    public string SearchEntry(string containerId, string key, string value, string resourceName) =>
        SearchEntries(containerId, key, value) + Escape(resourceName) + End;

    /// <summary>
    /// The prefix of a container's protection entries: one for each version that's had a retention date or a legal
    /// hold.
    /// </summary>
    public string Protections(string containerId) => Data(containerId) + "r/";

    /// <summary>The key of a version's protection entry, by the token the version records for it.</summary>
    public string Protection(string containerId, string token) => Protections(containerId) + token;

    /// <summary>
    /// The prefix of a container's bin entries, one for each version in its bin, newest deletion first: the deletion
    /// time counts down from <see cref="DateTimeOffset.MaxValue"/>, so keys sort that way.
    /// </summary>
    public string Bin(string containerId) => Data(containerId) + "t/";

    /// <summary>
    /// The key of a version's bin entry: when it was deleted, then its version ID, so versions deleted at once sort as
    /// the contract lists them, then the token the version records for its entries, then its content's length, so the
    /// usage figures can add up the bin from a listing.
    /// </summary>
    public string BinEntry(string containerId, DateTimeOffset deletedAt, string versionId, string token, long contentLength) =>
        $"{Bin(containerId)}{DateTimeOffset.MaxValue.UtcTicks - deletedAt.UtcTicks:D19}/{Escape(versionId)}{End}/{token}/{contentLength}";

    /// <summary>
    /// What a listing of the bin entries after <paramref name="deletedAt"/> and <paramref name="versionId"/>, in the bin's
    /// order, starts after, as <see cref="ExpiriesAfter"/> does for expiry entries.
    /// </summary>
    public string BinAfter(string containerId, DateTimeOffset deletedAt, string versionId) =>
        $"{Bin(containerId)}{DateTimeOffset.MaxValue.UtcTicks - deletedAt.UtcTicks:D19}/{Escape(versionId)}{End}0";

    /// <summary>What a bin entry's key, after <see cref="Bin"/>, says.</summary>
    internal static (DateTimeOffset DeletedAt, string VersionId, string Token, long ContentLength) ParseBinEntry(string key)
    {
        int versionEnd = key.IndexOf(End + "/", 20, StringComparison.Ordinal);
        string[] rest = key[(versionEnd + 2)..].Split('/');
        long ticks = DateTimeOffset.MaxValue.UtcTicks - long.Parse(key[..19], System.Globalization.CultureInfo.InvariantCulture);
        return (new DateTimeOffset(ticks, TimeSpan.Zero), Unescape(key[20..versionEnd]), rest[0],
            long.Parse(rest[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The prefix of a container's marks of names left with only a bin, which the usage figures don't count.</summary>
    public string EmptiedNames(string containerId) => Data(containerId) + "e/";

    /// <summary>The key of the mark of the name whose head is at <paramref name="headKey"/>, beside its head.</summary>
    public string EmptiedFor(string headKey) => OfKind(headKey, 'e');

    /// <summary>The key of the head of the name whose mark is at <paramref name="markKey"/>.</summary>
    public string HeadForEmptied(string markKey) => OfKind(markKey, 'h');

    /// <summary>
    /// <paramref name="key"/>, a key under a container's data, with its kind letter (<c>h</c> for a head, <c>e</c> for a
    /// bin-only mark) replaced by <paramref name="kind"/>.
    /// </summary>
    private string OfKind(string key, char kind)
    {
        // A container's ID has no /, so the one after it starts the key's own part.
        int at = key.IndexOf('/', prefix.Length + 2) + 1;
        return string.Concat(key.AsSpan(0, at), [kind], key.AsSpan(at + 1));
    }

    /// <summary>The prefix of every version's content in a container.</summary>
    public string Contents(string containerId) => Data(containerId) + "b/";

    /// <summary>The key of a version's content.</summary>
    public string Content(string containerId, string blob) => Contents(containerId) + blob;

    /// <summary>
    /// The key of the record of the last scan for content no head names, which also lists what that scan found.
    /// </summary>
    public string Scan { get; } = prefix + "s";

    /// <summary>The prefix of the records of the uploads that go up in pieces, while they do.</summary>
    public string Pieces { get; } = prefix + "p/";

    /// <summary>The key of the record of one upload's pieces.</summary>
    public string PiecesOf(string token) => Pieces + token;

    /// <summary>
    /// The prefix of every move's entry, kept apart from the containers' data so deleting a container doesn't take the
    /// entry that decides a move into or out of it.
    /// </summary>
    public string Moves { get; } = prefix + "m/";

    /// <summary>The key of a move's entry.</summary>
    public string Move(string moveId) => Moves + moveId;

    /// <summary>
    /// The prefix of every upload session, kept apart from the containers' data so a session's content doesn't count
    /// toward its container's usage.
    /// </summary>
    public string Uploads { get; } = prefix + "u/";

    /// <summary>The prefix of everything an upload session holds: its record and its segments.</summary>
    public string Upload(string containerId, string versionId) => $"{Uploads}{containerId}/{Escape(versionId)}{End}/";

    /// <summary>The key of an upload session's record.</summary>
    public string UploadRecord(string containerId, string versionId) => Upload(containerId, versionId) + "r";

    /// <summary>The key of one segment of an upload session's content.</summary>
    public string UploadSegment(string containerId, string versionId, string token) => Upload(containerId, versionId) + "p/" + token;

    /// <summary>
    /// The prefix of the session a key under <see cref="Uploads"/> belongs to: up to the <c>!/</c> that ends its version
    /// ID, which an escape never has.
    /// </summary>
    internal string UploadOf(string key) => key[..(key.IndexOf(End + "/", Uploads.Length, StringComparison.Ordinal) + 2)];

    /// <summary>A name as it goes into a key, before the <c>!</c> that ends it, keeping the order of names.</summary>
    internal string Escape(string name)
    {
        var escaped = new StringBuilder(name.Length + 8);
        int segment = 0;
        for (int i = 0; i < name.Length; i++)
        {
            // Decided before seeing the next character, which is what keeps the order.
            if (segment > segmentBytes - LongestUnit)
            {
                escaped.Append('/');
                segment = 0;
            }
            char c = name[i];
            if (c < '0')
            {
                escaped.Append('!').Append(((int)c).ToString("X2"));
                segment += 3;
            }
            else if (c is '[' or '\\')
            {
                escaped.Append(c == '[' ? "[A" : "[B");
                segment += 2;
            }
            else if (char.IsSurrogatePair(name, i))
            {
                escaped.Append(c).Append(name[++i]);
                segment += 4;
            }
            else if (char.IsSurrogate(c))
            {
                escaped.Append("[S").Append((c - 0xD800).ToString("X3"));
                segment += LongestUnit;
            }
            else
            {
                escaped.Append(c);
                segment += Encoding.UTF8.GetByteCount([c]);
            }
        }
        return escaped.ToString();
    }

    /// <summary>The name a key's escaped part stands for, with or without the <c>!</c> that ends it.</summary>
    internal static string Unescape(string escaped)
    {
        var name = new StringBuilder(escaped.Length);
        for (int i = 0; i < escaped.Length; i++)
        {
            char c = escaped[i];
            if (c == '/') continue;
            if (c == '!')
            {
                if (i == escaped.Length - 1) break;
                name.Append((char)Convert.ToByte(escaped.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c == '[')
            {
                char kind = escaped[++i];
                if (kind == 'S')
                {
                    name.Append((char)(0xD800 + Convert.ToInt32(escaped.Substring(i + 1, 3), 16)));
                    i += 3;
                }
                else
                {
                    name.Append(kind == 'A' ? '[' : '\\');
                }
            }
            else
            {
                name.Append(c);
            }
        }
        return name.ToString();
    }
}
