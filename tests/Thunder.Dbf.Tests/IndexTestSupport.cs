//    table  --DbfExpression-->  keys we expect      ProjectThunder :: Thunder.Dbf tests
//    index  --the reader----->  keys on disk        Shared plumbing for the .ndx, .ntx and
//                    \       /                      .cdx tests: rebuild every key from the
//                     ( == ? )                      table and hold the index to it.

using System.Text;

namespace Thunder.Dbf.Tests;

/// <summary>What the tests need to know about a tag, whichever reader produced it.</summary>
internal sealed record TagView(string Name, char KeyType, int KeyLength, bool IsDescending, bool IsUnique,
    string Expression, string? ForExpression, Func<IEnumerable<IndexEntry>> Entries,
    Func<object?, IEnumerable<IndexEntry>> Seek, Func<object?, IEnumerable<IndexEntry>> SeekExact)
{
    public static TagView Of(NdxTag t) => new(t.Name, t.KeyType, t.KeyLength, t.IsDescending, t.IsUnique, t.Expression, t.ForExpression, t.Entries, t.Seek, t.SeekExact);
    public static TagView Of(NtxTag t) => new(t.Name, t.KeyType, t.KeyLength, t.IsDescending, t.IsUnique, t.Expression, t.ForExpression, t.Entries, t.Seek, t.SeekExact);
    public static TagView Of(CdxTag t) => new(t.Name, t.KeyType, t.KeyLength, t.IsDescending, t.IsUnique, t.Expression, t.ForExpression, t.Entries, t.Seek, t.SeekExact);
}

internal static class IndexTestSupport
{
    public static readonly string FixtureRoot = FindFixtureRoot();

    public static string Fixture(string relativePath) => Path.Combine(FixtureRoot, relativePath);

    /// <summary>
    /// Rebuilds every key of <paramref name="tag"/> from <paramref name="table"/> with DbfExpression and
    /// checks the index holds exactly those keys, for exactly those records, in sorted order.
    /// <paramref name="notIndexed"/> lists records the file's own app added after the index was last updated.
    /// <paramref name="byteOrdered"/> is false for tags their writer sorted with a language collation.
    /// </summary>
    public static void AssertTagMatchesTable(TagView tag, DbfTable table, IReadOnlyCollection<int>? notIndexed = null,
        bool byteOrdered = true)
    {
        var key = DbfExpression.Parse(tag.Expression);
        var filter = tag.ForExpression is { } f ? DbfExpression.Parse(f) : null;

        var expected = table.ReadAll()
            .Where(r => notIndexed is null || !notIndexed.Contains(r.RecordNumber))
            .Where(r => filter is null || filter.Evaluate(r) is true)
            .Select(r => (Record: r.RecordNumber, Key: ExpectedKey(tag, key.Evaluate(r), table.Encoding)))
            .ToList();

        var actual = tag.Entries().ToList();
        if (byteOrdered)
            AssertSorted(tag, actual, table.Encoding);

        if (tag.IsUnique)
        {
            // A unique tag keeps one record per key: compare key sets, and check each kept record
            // really has the key it's filed under.
            var byRecord = expected.ToDictionary(e => e.Record, e => e.Key);
            Assert.Equal(actual.Count, actual.Select(e => e.Key).Distinct().Count());
            Assert.Equal(expected.Select(e => e.Key).Distinct().Count(), actual.Count);
            Assert.All(actual, e => Assert.Equal(byRecord[e.RecordNumber], e.Key));
            return;
        }

        Assert.Equal(expected.Select(e => e.Record).Order(), actual.Select(e => e.RecordNumber).Order());
        Assert.Equal(expected.OrderBy(e => e.Record), actual.Select(e => (Record: e.RecordNumber, e.Key)).OrderBy(e => e.Record));
    }

    /// <summary>
    /// Seeks every distinct key of the tag and checks SeekExact returns exactly the entries that
    /// share it (for character keys: every entry it prefixes), and Seek starts at the same place.
    /// </summary>
    public static void AssertEveryKeyCanBeFound(TagView tag, Encoding encoding)
    {
        var all = tag.Entries().ToList();
        foreach (var group in all.GroupBy(e => e.Key).Take(400))
        {
            var expected = all.Where(e => Matches(tag, e.Key, group.Key, encoding)).Select(e => e.RecordNumber).ToList();
            var found = tag.SeekExact(group.Key).Select(e => e.RecordNumber).ToList();
            Assert.Equal(expected, found);

            var first = all.FindIndex(e => Matches(tag, e.Key, group.Key, encoding));
            Assert.Equal(all.Skip(first).Take(5), tag.Seek(group.Key).Take(5));
        }
    }

    private static bool Matches(TagView tag, object? stored, object? sought, Encoding encoding) => tag.KeyType switch
    {
        // A character seek is a prefix match on the stored bytes (keys come back with trailing blanks trimmed).
        'C' => encoding.GetBytes((string)stored!).AsSpan().StartsWith(encoding.GetBytes(((string)sought!).TrimEnd(' '))),
        _ => Equals(stored, sought),
    };

    /// <summary>What the key bytes should decode to, given the expression's value for a record.</summary>
    public static object? ExpectedKey(TagView tag, object? value, Encoding encoding) => tag.KeyType switch
    {
        'C' => Fit((string)value!, tag.KeyLength, encoding).TrimEnd(' ', '\0'),
        'N' => value is decimal d ? d : Convert.ToDecimal(value ?? 0m),
        'L' => value is true,
        _ => value as DateOnly?,
    };

    // Keys are cut to the key length in bytes, not characters.
    private static string Fit(string s, int length, Encoding encoding)
    {
        var bytes = encoding.GetBytes(s);
        return bytes.Length > length ? encoding.GetString(bytes, 0, length) : s;
    }

    public static void AssertSorted(TagView tag, List<IndexEntry> entries, Encoding encoding)
    {
        for (var i = 1; i < entries.Count; i++)
        {
            var order = tag.KeyType switch
            {
                'C' => encoding.GetBytes((string)entries[i - 1].Key!).AsSpan().SequenceCompareTo(encoding.GetBytes((string)entries[i].Key!)),
                'N' => Convert.ToDouble(entries[i - 1].Key).CompareTo(Convert.ToDouble(entries[i].Key)),
                'L' => ((bool)entries[i - 1].Key!).CompareTo((bool)entries[i].Key!),
                _ => Nullable.Compare((DateOnly?)entries[i - 1].Key, (DateOnly?)entries[i].Key),
            };
            Assert.True(tag.IsDescending ? order >= 0 : order <= 0, $"Tag {tag.Name} is out of order at entry {i}.");
        }
    }

    /// <summary>A small in-memory table, for hand-built indexes whose key types come from a table.</summary>
    public static DbfTable TableWith(params DbfFieldDefinition[] fields) => DbfTable.Create(new MemoryStream(), fields);

    /// <summary>A stream that hands out at most a few bytes per Read, like a slow network share.</summary>
    public sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);
    }

    private static string FindFixtureRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find tests/fixtures above the test output folder.");
    }
}
