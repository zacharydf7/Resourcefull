using System.Text;

namespace Resourcefull.ObjectStorage;

/// <summary>
/// Where things are kept in the object store, under the key prefix: each container's record, and under the container's
/// ID, each name's head and each version's content.
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
/// segments, and a <c>/</c> goes in once a segment nears <see cref="SegmentBytes"/>, to keep it within what a filesystem
/// allows. Whether a <c>/</c> goes in depends only on what came before it, so two names get their <c>/</c>s at the same
/// places up to where they differ, and the order holds. Each key ends with <c>!</c>, which an escape never does, so no
/// key followed by a <c>/</c> is the start of another key: such a store would keep the shorter one as a directory, and
/// couldn't store it as a file too.
/// </para>
/// </remarks>
internal sealed class ObjectKeys(string prefix)
{
    /// <summary>The most UTF-8 bytes between the <c>/</c>s put into an escaped name.</summary>
    internal const int SegmentBytes = 200;

    /// <summary>The longest unit a character escapes to: the escape of an unpaired surrogate.</summary>
    internal const int LongestUnit = 5;

    private const char End = '!';

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

    /// <summary>The prefix of every version's content in a container.</summary>
    public string Contents(string containerId) => Data(containerId) + "b/";

    /// <summary>The key of a version's content.</summary>
    public string Content(string containerId, string blob) => Contents(containerId) + blob;

    /// <summary>A name as it goes into a key, before the <c>!</c> that ends it, keeping the order of names.</summary>
    internal static string Escape(string name)
    {
        var escaped = new StringBuilder(name.Length + 8);
        int segment = 0;
        for (int i = 0; i < name.Length; i++)
        {
            // Decided before seeing the next character, which is what keeps the order.
            if (segment > SegmentBytes - LongestUnit)
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
