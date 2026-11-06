using System.Text;
using Resourcefull.ObjectStorage;

namespace Resourcefull.Tests.S3;

public class ObjectKeysTests
{
    private static readonly ObjectKeys Keys = new("resourcefull/");

    /// <summary>A name's head key, past the part every head key in the container shares.</summary>
    private static string KeyOf(string name) => Keys.Head("c", name)[Keys.Heads("c").Length..];

    public static TheoryData<string> Names => new()
    {
        "report.pdf", "..", ".", "./b", "a/../b", "a//b", "/b", "b/", @"a\b", "[A]", "a b", "!", "~", "名前", "😀/😀",
        "a\uD800", "\uDC00b", new string('a', 256), new string('.', 256), new string('名', 256),
        string.Concat(Enumerable.Repeat("😀", 128))
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Head_ShouldGiveAKeyThatUnescapesToTheNameAndFits_WhenGivenAName(string name)
    {
        // Act
        string key = KeyOf(name);

        // Assert: no dots, backslashes or empty segments, and within what's left of S3's 1,024 bytes after the longest
        // key prefix and the container's part.
        Assert.Equal(name, ObjectKeys.Unescape(key));
        Assert.DoesNotContain('.', key);
        Assert.DoesNotContain('\\', key);
        Assert.All(key.Split('/'), segment => Assert.InRange(Encoding.UTF8.GetByteCount(segment), 1, ObjectKeys.SegmentBytes));
        Assert.InRange(Encoding.UTF8.GetByteCount(Keys.Head(new string('0', 32), name)), 1, 1024 - 190 + "resourcefull/".Length);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void SearchEntry_ShouldGiveAKeyThatFits_WhenGivenTheLongestKeyAndValue(string name)
    {
        // Act
        string key = Keys.SearchEntry(new string('0', 32), new string('k', ResourceMetadataMatch.MaxKeyLength),
            new string('名', ResourceMetadataMatch.MaxValueLength), name);

        // Assert: within what's left of S3's 1,024 bytes, and of Azure's 1,024 characters, after the longest key prefix, and
        // listed under its value's entries.
        Assert.InRange(Encoding.UTF8.GetByteCount(key), 1, 1024 - 190 + "resourcefull/".Length);
        Assert.InRange(key.Length, 1, 1024 - 190 + "resourcefull/".Length);
        Assert.StartsWith(Keys.SearchEntries(new string('0', 32), new string('k', ResourceMetadataMatch.MaxKeyLength),
            new string('名', ResourceMetadataMatch.MaxValueLength)), key);
    }

    [Fact]
    public void Expiry_ShouldSortByTimeThenVersionIdAndParseBack_WhenGivenVersionsOfAnyId()
    {
        // Arrange: version IDs of every kind an import can bring, each at two times a tick apart.
        var earlier = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string[] ids = ["a", "a\u0001", "a!", "a/", "a0", "A", "z", "01a1a141-72ab-7b09-9d8f-161ba268f7e3", "名", "!", "/"];
        List<(DateTimeOffset ExpiresAt, string VersionId)> entries =
            [.. ids.SelectMany(id => new[] { (earlier, id), (earlier.AddTicks(1), id) })];
        string KeyOf((DateTimeOffset ExpiresAt, string VersionId) entry) =>
            Keys.Expiry(entry.ExpiresAt, entry.VersionId, new string('t', 32), new string('c', 32), new string('b', 32));

        // Act
        List<(DateTimeOffset, string)> byKey = [.. entries.OrderBy(KeyOf, CodePointOrder.Instance)];

        // Assert: the order the contract lists expired versions in; every key parses back; and a listing from an entry
        // starts before it and past the one before.
        Assert.Equal([.. entries.OrderBy(e => e.ExpiresAt).ThenBy(e => e.VersionId, StringComparer.Ordinal)], byKey);
        Assert.All(entries, e => Assert.Equal((e.ExpiresAt, e.VersionId, new string('t', 32), new string('c', 32), new string('b', 32)),
            ObjectKeys.ParseExpiry(KeyOf(e)[Keys.Expiries.Length..])));
        for (int i = 0; i < byKey.Count; i++)
        {
            string from = Keys.ExpiriesFrom(byKey[i].Item1, byKey[i].Item2);
            Assert.True(CodePointOrder.Instance.Compare(from, KeyOf(byKey[i])) < 0);
            if (i > 0) Assert.True(CodePointOrder.Instance.Compare(KeyOf(byKey[i - 1]), from) < 0);
        }
    }

    [Fact]
    public void Head_ShouldGiveAnUnpairedSurrogateAKeyNoStoredNameHas_WhenGivenOne()
    {
        // Act & Assert: a lookup with a broken name mustn't find the name it would be read as.
        Assert.NotEqual(KeyOf("a�"), KeyOf("a\uD800"));
    }

    [Fact]
    public void Head_ShouldKeepTheNamesOrderWithNoKeyADirectoryOfAnother_WhenGivenNamesThatDifferWhereASegmentEnds()
    {
        // Arrange: names alike up to near the end of a segment, then differing in characters of every width.
        var random = new Random(1042);
        string[] tails = ["", "a", "!", ".", "/", "Z", "[", @"\", "]", "~", "é", "名", "😀", "", "�"];
        var names = new List<string>();
        for (int prefix = 190; prefix <= 200; prefix++)
            foreach (string first in tails)
                foreach (string second in tails)
                    names.Add(new string('x', prefix) + first + second);
        for (int i = 0; i < 2000; i++)
            names.Add(string.Concat(Enumerable.Range(0, random.Next(1, 120)).Select(_ => tails[random.Next(1, tails.Length)])));
        names = [.. names.Distinct()];

        // Act
        List<string> byKey = [.. names.OrderBy(KeyOf, CodePointOrder.Instance)];

        // Assert: the store lists keys in their UTF-8 order, which must be the names' own; and no key followed by a "/"
        // starts another, since a store that keeps keys as files would need the shorter one as a directory.
        Assert.Equal([.. names.OrderBy(n => n, CodePointOrder.Instance)], byKey);
        var keys = names.Select(KeyOf).ToHashSet();
        Assert.DoesNotContain(keys, key => Enumerable.Range(0, key.Length).Any(i => key[i] == '/' && keys.Contains(key[..i])));
    }
}
