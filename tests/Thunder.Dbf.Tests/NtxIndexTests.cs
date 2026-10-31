//       ______
//      / ____/ |   ProjectThunder :: Thunder.Dbf tests
//     / /    | |   Clipper .NTX files written by X#, every key rebuilt from its
//    / /___  | |   table; then hand-built pages for descending trees, negative numbers
//    \____/  |_|   stored as text, and the damage a reader must survive.

using System.Buffers.Binary;
using System.Text;
using static Thunder.Dbf.Tests.IndexTestSupport;

namespace Thunder.Dbf.Tests;

public class NtxIndexTests
{
    // Each real .ntx and the table it indexes. 10kName.xxx is an .ntx under another extension.
    public static TheoryData<string, string> RealIndexes() => new()
    {
        { "xsharp/explorer/Custname.ntx", "xsharp/explorer/Customer.dbf" },
        { "xsharp/explorer/Custnum.ntx", "xsharp/explorer/Customer.dbf" },
        { "xsharp/explorer/Ordcust.ntx", "xsharp/explorer/Orders.dbf" },
        { "xsharp/explorer/Ordernum.ntx", "xsharp/explorer/Orders.dbf" },
        { "xsharp/rdd-tests/10kDate.ntx", "xsharp/rdd-tests/TEST10K.DBF" },
        { "xsharp/rdd-tests/10kName.xxx", "xsharp/rdd-tests/TEST10K.DBF" },
        { "xsharp/rdd-tests/10kSalary.ntx", "xsharp/rdd-tests/TEST10K.DBF" },
        { "xsharp/rdd-tests/TESTNTX2.NTX", "xsharp/rdd-tests/TESTNTX2.DBF" },
    };

    // Orders.dbf gained two blank records after its indexes were last rebuilt. Clipper only indexes
    // what's appended while the index is open, so the files themselves disagree about those two.
    private static readonly Dictionary<string, int[]> NotIndexed = new()
    {
        ["xsharp/explorer/Orders.dbf"] = [94, 95],
    };

    [Theory]
    [MemberData(nameof(RealIndexes))]
    public void Every_key_matches_the_key_computed_from_its_table(string indexFixture, string tableFixture)
    {
        using var table = DbfTable.Open(Fixture(tableFixture));
        using var index = NtxIndex.Open(Fixture(indexFixture), table: table);

        AssertTagMatchesTable(TagView.Of(index.Tag), table, NotIndexed.GetValueOrDefault(tableFixture));
    }

    // 10kState.ntx was built when every STATE in its table was "IL" (X#'s own dump of an older
    // copy shows the mixed states the table holds now). It must still read as a sound tree.
    [Fact]
    public void A_stale_real_index_still_reads_as_a_whole_tree()
    {
        using var table = DbfTable.Open(Fixture("xsharp/rdd-tests/TEST10K.DBF"));
        using var index = NtxIndex.Open(Fixture("xsharp/rdd-tests/10kState.ntx"), table: table);

        var entries = index.Tag.Entries().ToList();
        Assert.All(entries, e => Assert.Equal("IL", e.Key));
        Assert.Equal(Enumerable.Range(1, 10_000), entries.Select(e => e.RecordNumber));
        Assert.Equal(10_000, index.Tag.SeekExact("IL").Count());
        Assert.Empty(index.Tag.SeekExact("CA"));
    }

    [Theory]
    [MemberData(nameof(RealIndexes))]
    public void Every_key_can_be_sought(string indexFixture, string tableFixture)
    {
        using var table = DbfTable.Open(Fixture(tableFixture));
        using var index = NtxIndex.Open(Fixture(indexFixture), table: table);

        AssertEveryKeyCanBeFound(TagView.Of(index.Tag), table.Encoding);
    }

    [Fact]
    public void Header_fields_and_key_types_read_like_Clipper_wrote_them()
    {
        using var table = DbfTable.Open(Fixture("xsharp/rdd-tests/TEST10K.DBF"));

        using var salary = NtxIndex.Open(Fixture("xsharp/rdd-tests/10kSalary.ntx"), table: table);
        Assert.Equal("10kSalary", salary.Tag.Name); // stored in the header, case and all
        Assert.Equal("-Salary", salary.Tag.Expression);
        Assert.Equal(('N', 13, 2), (salary.Tag.KeyType, salary.Tag.KeyLength, salary.Tag.KeyDecimals));
        Assert.True((decimal)salary.Tag.Entries().First().Key! < 0);

        using var hired = NtxIndex.Open(Fixture("xsharp/rdd-tests/10kDate.ntx"), table: table);
        Assert.Equal('D', hired.Tag.KeyType);
        Assert.Equal(new DateOnly(1981, 1, 1), hired.Tag.Entries().First().Key);

        // Without the table every key is the text Clipper stored.
        using var bare = NtxIndex.Open(Fixture("xsharp/rdd-tests/10kDate.ntx"));
        Assert.Equal('C', bare.Tag.KeyType);
        Assert.Equal("19810101", bare.Tag.Entries().First().Key);

        // A header without a tag name (byte 538 below 0x20) takes the file's name instead.
        var bytes = File.ReadAllBytes(Fixture("xsharp/rdd-tests/TESTNTX2.NTX"));
        bytes.AsSpan(538, 12).Clear();
        var unnamedPath = Path.Combine(Path.GetTempPath(), $"unnamed-{Guid.NewGuid():N}.ntx");
        File.WriteAllBytes(unnamedPath, bytes);
        try
        {
            using var unnamed = NtxIndex.Open(unnamedPath);
            Assert.Equal(Path.GetFileNameWithoutExtension(unnamedPath).ToUpperInvariant(), unnamed.Tag.Name);
            Assert.Same(unnamed.Tag, unnamed[Path.GetFileNameWithoutExtension(unnamedPath)]);
        }
        finally
        {
            File.Delete(unnamedPath);
        }
    }

    [Fact]
    public void Negative_numbers_decode_and_seek_by_value()
    {
        using var table = TableWith(DbfFieldDefinition.Numeric("K", 6, 2));
        var tag = NtxIndex.Open(new NtxBuilder(keyLength: 6, decimals: 2)
            .Page(1, [(Text("******"), 5, 0), (Text(",+*.',"), 1, 0), (Text("000.00"), 2, 0), (Text("012.50"), 3, 0), (Text("012.50"), 4, 0)])
            .Build(), table: table).Tag;

        // -12.50 is stored as "-12.50" with the sign zeroed and each digit d written as '0' - d - 4.
        // STR()'s overflow asterisks sort before everything and come back as text.
        Assert.Equal([(object)"******", -12.5m, 0m, 12.5m, 12.5m], tag.Entries().Select(e => e.Key));
        Assert.Equal([1], tag.SeekExact(-12.5).Select(e => e.RecordNumber));
        Assert.Equal([3, 4], tag.SeekExact(12.5m).Select(e => e.RecordNumber));
        Assert.Equal([3, 4], tag.Seek(0.01).Select(e => e.RecordNumber));
        Assert.Equal([2, 3, 4], tag.Seek(-1).Select(e => e.RecordNumber));
    }

    // A real B-tree: inner pages hold keys too, so the walk interleaves them with their children.
    //
    //                 root [ c ] -> right      (page 1)
    //                /              \
    //       [ a  b ] (page 2)    [ d  e ] (page 3)
    [Fact]
    public void Keys_in_inner_pages_come_between_their_children()
    {
        var tag = NtxIndex.Open(new NtxBuilder(keyLength: 1)
            .Page(1, [(Text("c"), 3, 2)], lastChild: 3)
            .Page(2, [(Text("a"), 1, 0), (Text("c"), 2, 0)])
            .Page(3, [(Text("c"), 4, 0), (Text("e"), 5, 0)])
            .Build()).Tag;

        Assert.Equal(["a", "c", "c", "c", "e"], tag.Entries().Select(e => e.Key));
        Assert.Equal([2, 3, 4], tag.SeekExact("c").Select(e => e.RecordNumber));
        Assert.Equal([5], tag.Seek("d").Select(e => e.RecordNumber));
        Assert.Equal([1, 2, 3, 4, 5], tag.Seek("").Select(e => e.RecordNumber));
    }

    [Fact]
    public void Descending_tags_seek_downwards()
    {
        var tag = NtxIndex.Open(new NtxBuilder(keyLength: 1, descending: true)
            .Page(1, [(Text("c"), 3, 2)], lastChild: 3)
            .Page(2, [(Text("e"), 1, 0), (Text("d"), 2, 0)])
            .Page(3, [(Text("b"), 4, 0), (Text("a"), 5, 0)])
            .Build()).Tag;

        Assert.True(tag.IsDescending);
        Assert.Equal(["e", "d", "c", "b", "a"], tag.Entries().Select(e => e.Key));
        Assert.Equal(["c", "b", "a"], tag.Seek("c").Select(e => e.Key));
        Assert.Equal(["b", "a"], tag.Seek("bz").Select(e => e.Key)); // the first key at or below "bz"
        Assert.Equal([4], tag.SeekExact("b").Select(e => e.RecordNumber));
        Assert.Empty(tag.SeekExact("f"));
    }

    [Fact]
    public void Logical_and_date_keys_decode_with_the_table()
    {
        using var table = TableWith(DbfFieldDefinition.Logical("L"), DbfFieldDefinition.Date("D"));

        var logical = NtxIndex.Open(new NtxBuilder(keyLength: 1, expression: "L")
            .Page(1, [(Text("F"), 2, 0), (Text("T"), 1, 0)]).Build(), table: table).Tag;
        Assert.Equal('L', logical.KeyType);
        Assert.Equal([(object)false, true], logical.Entries().Select(e => e.Key));
        Assert.Equal([1], logical.SeekExact(true).Select(e => e.RecordNumber));

        var dates = NtxIndex.Open(new NtxBuilder(keyLength: 8, expression: "D")
            .Page(1, [(Text("        "), 2, 0), (Text("19991231"), 1, 0)]).Build(), table: table).Tag;
        Assert.Equal('D', dates.KeyType);
        Assert.Equal([null, (object)new DateOnly(1999, 12, 31)], dates.Entries().Select(e => e.Key));
        Assert.Equal([2], dates.SeekExact(null).Select(e => e.RecordNumber));
        Assert.Equal([1], dates.SeekExact(new DateTime(1999, 12, 31)).Select(e => e.RecordNumber));
    }

    [Fact]
    public void Header_flags_give_the_FOR_filter_and_large_file_pointers()
    {
        var filtered = NtxIndex.Open(new NtxBuilder(keyLength: 1, flags: 0x0007, forExpression: "K > 'a'")
            .Page(1, [(Text("b"), 1, 0)]).Build()).Tag;
        Assert.Equal("K > 'a'", filtered.ForExpression);

        // With the large-file flag, child pointers are page numbers instead of byte offsets.
        var large = NtxIndex.Open(new NtxBuilder(keyLength: 1, flags: 0x0206, largeFile: true)
            .Page(1, [(Text("b"), 2, 2)], lastChild: 3)
            .Page(2, [(Text("a"), 1, 0)])
            .Page(3, [(Text("c"), 3, 0)])
            .Build()).Tag;
        Assert.Null(large.ForExpression);
        Assert.Equal(["a", "b", "c"], large.Entries().Select(e => e.Key));
    }

    [Fact]
    public void Cyclic_and_shared_pages_are_rejected_instead_of_walked_forever()
    {
        var cycle = NtxIndex.Open(new NtxBuilder(keyLength: 1).Page(1, [(Text("a"), 1, 1)]).Build());
        Assert.Throws<InvalidDataException>(() => cycle.Tag.Entries().ToList());

        var shared = NtxIndex.Open(new NtxBuilder(keyLength: 1)
            .Page(1, [(Text("b"), 1, 2)], lastChild: 2)
            .Page(2, [(Text("a"), 2, 0)]).Build());
        Assert.Throws<InvalidDataException>(() => shared.Tag.Entries().ToList());
    }

    [Fact]
    public void Damaged_pages_are_reported_as_corrupt()
    {
        var builder = new NtxBuilder(keyLength: 1).Page(1, [(Text("a"), 1, 0)]);
        var stream = builder.Build();
        BinaryPrimitives.WriteUInt16LittleEndian(stream.GetBuffer().AsSpan(NtxIndex.PageSize + 2), 1020); // slot 0 runs off the page
        Assert.Throws<InvalidDataException>(() => NtxIndex.Open(stream).Tag.Entries().ToList());

        stream = new NtxBuilder(keyLength: 1).Page(1, [(Text("a"), 1, 0)]).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(stream.GetBuffer().AsSpan(NtxIndex.PageSize), 999); // more keys than fit
        Assert.Throws<InvalidDataException>(() => NtxIndex.Open(stream).Tag.Entries().ToList());

        stream = new NtxBuilder(keyLength: 1).Page(1, [(Text("a"), 1, 0)]).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(stream.GetBuffer().AsSpan(4), 1000); // root not on a page boundary
        Assert.Throws<InvalidDataException>(() => NtxIndex.Open(stream).Tag.Entries().ToList());

        stream = new NtxBuilder(keyLength: 1).Page(1, [(Text("a"), 0, 0)]).Build(); // record 0
        Assert.Throws<InvalidDataException>(() => NtxIndex.Open(stream).Tag.Entries().ToList());
    }

    [Fact]
    public void Files_that_arent_single_ntx_indexes_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => NtxIndex.Open(new NtxBuilder(keyLength: 1, flags: 0x1234).Build()));
        Assert.Throws<NotSupportedException>(() => NtxIndex.Open(new NtxBuilder(keyLength: 1, flags: 0x8006).Build()));
        Assert.Throws<InvalidDataException>(() => NtxIndex.Open(new NtxBuilder(keyLength: 1, expression: "").Build()));
        Assert.Throws<InvalidDataException>(() => NtxIndex.Open(new NtxBuilder(keyLength: 300).Build()));
        Assert.Throws<InvalidDataException>(() => NtxIndex.Open(new MemoryStream(new byte[100])));
    }

    [Fact]
    public void Streams_that_return_a_few_bytes_at_a_time_still_open()
    {
        using var index = NtxIndex.Open(new TrickleStream(File.ReadAllBytes(Fixture("xsharp/rdd-tests/10kState.ntx")), chunk: 13));
        Assert.Equal(10_000, index.Tag.Entries().Count());
    }

    // ---- a tiny NTX builder ---------------------------------------------------------------

    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

    private sealed class NtxBuilder
    {
        private readonly byte[] _file = new byte[8 * NtxIndex.PageSize];
        private readonly int _keyLength;
        private readonly int _itemLength;
        private readonly int _maxKeys;
        private readonly bool _largeFile;

        public NtxBuilder(int keyLength, int decimals = 0, string expression = "K", bool descending = false,
            ushort flags = 0x0006, string? forExpression = null, bool largeFile = false)
        {
            _keyLength = keyLength;
            _itemLength = keyLength + 8;
            _maxKeys = Math.Max(1, (NtxIndex.PageSize - 4) / (_itemLength + 2) - 1);
            _largeFile = largeFile;

            var header = _file.AsSpan();
            BinaryPrimitives.WriteUInt16LittleEndian(header, flags);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], largeFile ? 1u : NtxIndex.PageSize); // root: page 1
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)_itemLength);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)keyLength);
            BinaryPrimitives.WriteUInt16LittleEndian(header[16..], (ushort)decimals);
            BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)_maxKeys);
            BinaryPrimitives.WriteUInt16LittleEndian(header[20..], (ushort)(_maxKeys / 2));
            Encoding.ASCII.GetBytes(expression).CopyTo(header[22..]);
            header[280] = (byte)(descending ? 1 : 0);
            if (forExpression is not null)
                Encoding.ASCII.GetBytes(forExpression).CopyTo(header[282..]);
        }

        /// <summary>Lays out page <paramref name="page"/>: items (key, record, child page), then the rightmost child.</summary>
        public NtxBuilder Page(int page, (byte[] Key, uint Record, uint Child)[] items, uint lastChild = 0)
        {
            var node = _file.AsSpan(page * NtxIndex.PageSize, NtxIndex.PageSize);
            BinaryPrimitives.WriteUInt16LittleEndian(node, (ushort)items.Length);
            var firstItem = 2 + (_maxKeys + 1) * 2;
            for (var slot = 0; slot <= _maxKeys; slot++)
                BinaryPrimitives.WriteUInt16LittleEndian(node[(2 + slot * 2)..], (ushort)(firstItem + slot * _itemLength));

            for (var i = 0; i <= items.Length; i++)
            {
                var item = node[(firstItem + i * _itemLength)..];
                var child = i < items.Length ? items[i].Child : lastChild;
                BinaryPrimitives.WriteUInt32LittleEndian(item, child == 0 || _largeFile ? child : child * NtxIndex.PageSize);
                if (i < items.Length)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(item[4..], items[i].Record);
                    items[i].Key.CopyTo(item[8..]);
                }
            }
            return this;
        }

        public MemoryStream Build() => new(_file, 0, _file.Length, writable: true, publiclyVisible: true);
    }
}
