//        [ ID_TAG ]           ProjectThunder :: Thunder.Dbf tests
//         /      \            Every tag in every real .MDX, rebuilt from its
//      [1 2]   [3 13]         own table and compared key by key. If our keys
//                             match SIMATIC Manager's, our reader is right.

using System.Text;

namespace Thunder.Dbf.Tests;

public class MdxIndexTests
{
    private static readonly string FixtureRoot = FindFixtureRoot();

    public static TheoryData<string> AllIndexes()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(FixtureRoot, "siemens-step7"), "*.MDX", SearchOption.AllDirectories).Order())
            data.Add(Path.GetRelativePath(FixtureRoot, path).Replace('\\', '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(AllIndexes))]
    public void Every_tag_matches_keys_computed_from_its_table(string indexFixture)
    {
        var tablePath = Path.ChangeExtension(Path.Combine(FixtureRoot, indexFixture), ".DBF");
        using var table = DbfTable.Open(tablePath);
        using var index = table.OpenProductionIndex();

        Assert.NotNull(index);
        Assert.True(index.IsProduction);
        Assert.NotEmpty(index.Tags);

        foreach (var tag in index.Tags)
        {
            var key = DbfExpression.Parse(tag.Expression);
            var filter = tag.ForExpression is { } f ? DbfExpression.Parse(f) : null;

            var expected = table.ReadAll()
                .Where(r => filter is null || filter.Evaluate(r) is true)
                .Select(r => (Record: r.RecordNumber, Key: ExpectedKey(tag, key.Evaluate(r))))
                .OrderBy(e => e.Record)
                .ToList();

            var actual = tag.Entries().ToList();
            AssertSorted(tag, actual);

            if (tag.IsUnique)
            {
                // A unique tag keeps one record per key, so compare key sets, and check each kept
                // record really has the key it's filed under.
                var byRecord = expected.ToDictionary(e => e.Record, e => e.Key);
                Assert.Equal(actual.Count, actual.Select(e => e.Key).Distinct().Count());
                Assert.Equal(expected.Select(e => e.Key).Distinct().OrderBy(k => k), actual.Select(e => e.Key).OrderBy(k => k));
                Assert.All(actual, e => Assert.Equal(byRecord[e.RecordNumber], e.Key));
                continue;
            }

            Assert.Equal(expected.Select(e => e.Record), actual.Select(e => e.RecordNumber).Order());
            Assert.Equal(expected, actual.Select(e => (Record: e.RecordNumber, Key: e.Key)).OrderBy(e => e.Record).ToList());
        }
    }

    [Fact]
    public void Siemens_changelog_tags_read_in_key_order()
    {
        using var table = DbfTable.Open(Path.Combine(FixtureRoot, "siemens-step7/siprom/CHANGELOG.DBF"));
        using var index = table.OpenProductionIndex()!;

        Assert.Equal(["ID_TAG", "DATETIME_T", "USER_TAG", "ACTION_TAG", "OBJECTID_T", "LEVEL_TAG"], index.Tags.Select(t => t.Name));
        var idTag = index["id_tag"];
        Assert.Equal('N', idTag.KeyType);
        Assert.True(idTag.IsUnique);
        Assert.Equal(".NOT. DELETED()", idTag.ForExpression);

        Assert.Equal([1m, 2m, 3m], table.ReadInOrder(idTag).Select(r => r["ID"]));
    }

    [Fact]
    public void Seek_finds_records_by_numeric_and_character_keys()
    {
        using var table = DbfTable.Open(Path.Combine(FixtureRoot, "siemens-step7/ombstx/offline/00000001/BAUSTEIN.DBF"));
        using var index = table.OpenProductionIndex()!;

        foreach (var record in table.ReadAll().Where(r => !r.IsDeleted))
        {
            Assert.Equal(record.RecordNumber, table.Seek(index["ID_TAG"], record["ID"]!)!.RecordNumber);

            var typAndNumber = (string)DbfExpression.Parse("TYP + NUMMER").Evaluate(record)!;
            var found = index["TYPNUMBER_"].SeekExact(typAndNumber).Select(e => e.RecordNumber);
            Assert.Contains(record.RecordNumber, found);
        }

        Assert.Null(table.Seek(index["ID_TAG"], 999_999m));
    }

    [Fact]
    public void Seek_positions_on_the_next_key_when_there_is_no_exact_match()
    {
        using var table = DbfTable.Open(Path.Combine(FixtureRoot, "siemens-step7/siprom/CHANGELOG.DBF"));
        using var index = table.OpenProductionIndex()!;

        // ACTION keys are 1, 3 and 13 (two records share one); seeking 2 lands on the first 3-or-more key.
        var keys = index["ACTION_TAG"].Entries().Select(e => (decimal)e.Key!).ToList();
        var probe = keys.Min() + 0.5m;
        Assert.Equal(keys.Where(k => k >= probe), index["ACTION_TAG"].Seek(probe).Select(e => (decimal)e.Key!));
        Assert.Empty(index["ACTION_TAG"].SeekExact(probe));
    }

    [Theory]
    [InlineData("1", new byte[] { 0x35, 0x05, 0x10 })]
    [InlineData("3", new byte[] { 0x35, 0x05, 0x30 })]
    [InlineData("13", new byte[] { 0x36, 0x09, 0x13 })]
    [InlineData("0", new byte[] { 0x34, 0x01, 0x00 })]
    public void Numeric_keys_encode_like_SIMATIC_Manager(string value, byte[] prefix)
    {
        var key = MdxKeys.EncodeNumeric(decimal.Parse(value));
        Assert.Equal(prefix, key[..prefix.Length]);
        Assert.Equal(decimal.Parse(value), MdxKeys.DecodeNumeric(key));
    }

    [Theory]
    [InlineData("-42.5")]
    [InlineData("0.05")]
    [InlineData("123456789012.345678")]
    [InlineData("100")]
    public void Numeric_keys_round_trip(string value)
    {
        Assert.Equal(decimal.Parse(value), MdxKeys.DecodeNumeric(MdxKeys.EncodeNumeric(decimal.Parse(value))));
    }

    // None of the real files is big enough to need interior nodes, so build one by the spec:
    // a root over three leaves, with duplicate 5s straddling the first leaf boundary.
    //
    //                 [ 5 | 9 ]  (page 12)
    //               /     |     \
    //     [1 3 5 5] (6)  [5 7 9] (8)  [11 13] (10)
    private static readonly decimal[][] Leaves = [[1, 3, 5, 5], [5, 7, 9], [11, 13]];

    private static MemoryStream BuildThreeLeafIndex()
    {
        const int blockSize = 1024, itemLength = 16;
        var file = new byte[14 * 512];
        file[0] = 0x02;
        BitConverter.TryWriteBytes(file.AsSpan(20), (ushort)2);
        BitConverter.TryWriteBytes(file.AsSpan(22), (ushort)blockSize);
        file[24] = 1;
        file[26] = 32;
        BitConverter.TryWriteBytes(file.AsSpan(28), (ushort)1);

        BitConverter.TryWriteBytes(file.AsSpan(544), 4);   // tag header at page 4
        "NUM"u8.CopyTo(file.AsSpan(548));
        file[564] = (byte)'N';

        var tag = file.AsSpan(4 * 512, blockSize);
        BitConverter.TryWriteBytes(tag, 12);               // root at page 12
        tag[8] = 0x10;
        tag[9] = (byte)'N';
        BitConverter.TryWriteBytes(tag[12..], (ushort)12);
        BitConverter.TryWriteBytes(tag[14..], (ushort)((blockSize - 12) / itemLength));
        BitConverter.TryWriteBytes(tag[18..], (ushort)itemLength);
        "ID"u8.CopyTo(tag[24..]);

        var record = 1;
        int[] leafPages = [6, 8, 10];
        for (var leaf = 0; leaf < Leaves.Length; leaf++)
        {
            var node = file.AsSpan(leafPages[leaf] * 512, blockSize);
            BitConverter.TryWriteBytes(node, Leaves[leaf].Length);
            for (var i = 0; i < Leaves[leaf].Length; i++)
            {
                BitConverter.TryWriteBytes(node[(8 + i * itemLength)..], record++);
                MdxKeys.EncodeNumeric(Leaves[leaf][i]).CopyTo(node[(12 + i * itemLength)..]);
            }
        }

        var root = file.AsSpan(12 * 512, blockSize);
        BitConverter.TryWriteBytes(root, 2);
        for (var i = 0; i < 3; i++)
        {
            BitConverter.TryWriteBytes(root[(8 + i * itemLength)..], leafPages[i]);
            if (i < 2)
                MdxKeys.EncodeNumeric(Leaves[i][^1]).CopyTo(root[(12 + i * itemLength)..]);
        }

        return new MemoryStream(file);
    }

    [Fact]
    public void Interior_nodes_are_walked_in_order()
    {
        using var index = MdxIndex.Open(BuildThreeLeafIndex());
        Assert.Equal(Leaves.SelectMany(l => l), index["NUM"].Entries().Select(e => (decimal)e.Key!));
        Assert.Equal(Enumerable.Range(1, 9), index["NUM"].Entries().Select(e => e.RecordNumber));
    }

    [Fact]
    public void Exact_seeks_cross_leaf_boundaries()
    {
        using var index = MdxIndex.Open(BuildThreeLeafIndex());
        var tag = index["NUM"];

        Assert.Equal([3, 4, 5], tag.SeekExact(5m).Select(e => e.RecordNumber));
        Assert.Equal([9], tag.SeekExact(13m).Select(e => e.RecordNumber));
        Assert.Equal([7m, 9m, 11m, 13m], tag.Seek(6m).Select(e => (decimal)e.Key!));
        Assert.Empty(tag.Seek(14m));
        Assert.Empty(tag.SeekExact(6m));
    }

    // What the key bytes should decode to, given the expression's value for a record.
    private static object? ExpectedKey(MdxTag tag, object? value) => tag.KeyType switch
    {
        'C' => Fit((string)value!, tag.KeyLength).TrimEnd(' ', '\0'),
        'N' => value is decimal d ? d : 0m,
        _ => value as DateOnly?,
    };

    private static string Fit(string s, int length) => s.Length >= length ? s[..length] : s;

    private static void AssertSorted(MdxTag tag, List<MdxEntry> entries)
    {
        for (var i = 1; i < entries.Count; i++)
        {
            var order = tag.KeyType switch
            {
                'C' => string.CompareOrdinal((string?)entries[i - 1].Key, (string?)entries[i].Key),
                'N' => ((decimal)entries[i - 1].Key!).CompareTo((decimal)entries[i].Key!),
                _ => Nullable.Compare((DateOnly?)entries[i - 1].Key, (DateOnly?)entries[i].Key),
            };
            Assert.True(tag.IsDescending ? order >= 0 : order <= 0, $"Tag {tag.Name} is out of order at entry {i}.");
        }
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
