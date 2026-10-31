//     _____________
//    |  dBASE III  |     ProjectThunder :: Thunder.Dbf tests
//    |  PLUS  1.1  |     .NDX files written by dBASE III Plus itself (in DOSBox-X),
//    |_____________|     every key rebuilt from its table; then hand-built pages for
//      |_|     |_|       the shapes dBASE leaves behind and the damage it doesn't.

using System.Buffers.Binary;
using System.Text;
using static Thunder.Dbf.Tests.IndexTestSupport;

namespace Thunder.Dbf.Tests;

public class NdxIndexTests
{
    // Each real .ndx and the table it indexes.
    public static TheoryData<string, string> RealIndexes() => new()
    {
        { "retroinv/BIG1.NDX", "retroinv/BIG.DBF" },
        { "retroinv/BIG2.NDX", "retroinv/BIG.DBF" },
        { "retroinv/BIG3.NDX", "retroinv/BIG.DBF" },
        { "retroinv/ITEMS1.NDX", "retroinv/ITEMS.DBF" },
        { "retroinv/NUM2000.NDX", "retroinv/NUM2000.DBF" },
        { "retroinv/NUM993.NDX", "retroinv/NUM993.DBF" },
        { "retroinv/WIDE1.NDX", "retroinv/WIDE.DBF" },
        { "retroinv/WIDED1.NDX", "retroinv/WIDED.DBF" },
        { "ndbfreaderex/test_ix.NDX", "ndbfreaderex/test_ix.DBF" },
    };

    [Theory]
    [MemberData(nameof(RealIndexes))]
    public void Every_key_matches_the_key_computed_from_its_table(string indexFixture, string tableFixture)
    {
        using var table = DbfTable.Open(Fixture(tableFixture));
        using var index = NdxIndex.Open(Fixture(indexFixture), table: table);

        AssertTagMatchesTable(TagView.Of(index.Tag), table);
    }

    [Theory]
    [MemberData(nameof(RealIndexes))]
    public void Every_key_can_be_sought(string indexFixture, string tableFixture)
    {
        using var table = DbfTable.Open(Fixture(tableFixture));
        using var index = NdxIndex.Open(Fixture(indexFixture), table: table);

        AssertEveryKeyCanBeFound(TagView.Of(index.Tag), table.Encoding);
    }

    [Fact]
    public void Header_fields_read_like_dBASE_wrote_them()
    {
        using var index = NdxIndex.Open(Fixture("retroinv/BIG1.NDX"));
        var tag = index.Tag;

        Assert.Equal("BIG1", tag.Name);
        Assert.Same(tag, index["big1"]);
        Assert.Equal([tag], index.Tags);
        Assert.Equal("STR(ID,8)", tag.Expression); // dBASE leaves stray memory after the NUL
        Assert.Equal('C', tag.KeyType);
        Assert.Equal(8, tag.KeyLength);
        Assert.False(tag.IsDescending);
        Assert.False(tag.IsUnique);
        Assert.Null(tag.ForExpression);
        Assert.Throws<KeyNotFoundException>(() => index["OTHER"]);
    }

    [Fact]
    public void Date_keys_need_the_table_to_read_as_dates()
    {
        using (var bare = NdxIndex.Open(Fixture("retroinv/BIG3.NDX")))
        {
            Assert.Equal('N', bare.Tag.KeyType);
            Assert.Equal(2444250m, bare.Tag.Entries().First().Key); // a Julian day number: 1980-01-11
        }

        using var table = DbfTable.Open(Fixture("retroinv/BIG.DBF"));
        using var index = NdxIndex.Open(Fixture("retroinv/BIG3.NDX"), table: table);
        Assert.Equal('D', index.Tag.KeyType);
        Assert.Equal(new DateOnly(1980, 1, 11), index.Tag.Entries().First().Key);
        Assert.Equal([1], index.Tag.SeekExact(new DateOnly(1980, 1, 11)).Select(e => e.RecordNumber));
        Assert.Equal(new DateOnly(1980, 1, 21), index.Tag.Seek(new DateOnly(1980, 1, 12)).First().Key);
    }

    [Fact]
    public void Numeric_seeks_land_on_the_next_key()
    {
        var index = NdxIndex.Open(new NdxBuilder(keyLength: 8, numeric: true)
            .Leaf(1, (N(-2.5), 1), (N(0), 2), (N(0.1), 3), (N(7), 4), (N(7), 5), (N(1e300), 6)).Build());
        var tag = index.Tag;

        Assert.Equal([(object)-2.5m, 0m, 0.1m, 7m, 7m, 1e300], tag.Entries().Select(e => e.Key));
        Assert.Equal([4, 5], tag.SeekExact(7).Select(e => e.RecordNumber));
        Assert.Equal([4, 5], tag.SeekExact(7.0).Select(e => e.RecordNumber));
        Assert.Equal([3], tag.SeekExact(0.1m).Select(e => e.RecordNumber));
        Assert.Equal([4, 5, 6], tag.Seek(1).Select(e => e.RecordNumber));
        Assert.Empty(tag.SeekExact(1));
        Assert.Empty(tag.Seek(double.MaxValue));
    }

    // The shapes dBASE leaves behind: leaves at two depths (INDEX ON hangs a last page directly
    // under an upper level), an inner page with no keys and its one child in slot 0 (what deletes
    // leave), and a run of equal keys that crosses a page boundary.
    //
    //                  root [ "B" | "D" | -> ]                 (page 1)
    //                 /           |          \
    //        [ -> ] (page 2)  ["C" "D" "D"] (5)  ["D" "E" "F"] (6)
    //           |
    //      ["A" "B"] (3)
    [Fact]
    public void Inner_pages_with_no_keys_and_uneven_depths_are_walked_in_order()
    {
        var index = NdxIndex.Open(new NdxBuilder(keyLength: 1)
            .Inner(1, [C("B"), C("D")], [2, 5, 6])
            .Inner(2, [], [3])
            .Leaf(3, (C("A"), 1), (C("B"), 2))
            .Leaf(5, (C("C"), 3), (C("D"), 4), (C("D"), 5))
            .Leaf(6, (C("D"), 6), (C("E"), 7), (C("F"), 8))
            .Build());
        var tag = index.Tag;

        Assert.Equal(["A", "B", "C", "D", "D", "D", "E", "F"], tag.Entries().Select(e => e.Key));
        Assert.Equal([4, 5, 6], tag.SeekExact("D").Select(e => e.RecordNumber));
        Assert.Equal([2], tag.SeekExact("B").Select(e => e.RecordNumber));
        Assert.Equal(["C", "D", "D", "D", "E", "F"], tag.Seek("BB").Select(e => e.Key));
        Assert.Empty(tag.Seek("G"));
    }

    [Fact]
    public void Cyclic_and_shared_pages_are_rejected_instead_of_walked_forever()
    {
        var cycle = NdxIndex.Open(new NdxBuilder(keyLength: 1).Inner(1, [C("A")], [1, 1]).Build());
        Assert.Throws<InvalidDataException>(() => cycle.Tag.Entries().ToList());

        var shared = NdxIndex.Open(new NdxBuilder(keyLength: 1).Inner(1, [C("A")], [2, 2]).Leaf(2, (C("A"), 1)).Build());
        Assert.Throws<InvalidDataException>(() => shared.Tag.Entries().ToList());
    }

    [Fact]
    public void Damaged_pages_are_reported_as_corrupt()
    {
        var tooMany = new NdxBuilder(keyLength: 1).Leaf(1, (C("A"), 1)).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(tooMany.GetBuffer().AsSpan(512), 999);
        Assert.Throws<InvalidDataException>(() => NdxIndex.Open(tooMany).Tag.Entries().ToList());

        var outside = NdxIndex.Open(new NdxBuilder(keyLength: 1).Inner(1, [C("A")], [2, 5000]).Leaf(2, (C("A"), 1)).Build());
        Assert.Throws<InvalidDataException>(() => outside.Tag.Entries().ToList());

        var recordZero = NdxIndex.Open(new NdxBuilder(keyLength: 1).Leaf(1, (C("A"), 0)).Build());
        Assert.Throws<InvalidDataException>(() => recordZero.Tag.Entries().ToList());
    }

    [Theory]
    [InlineData(0, false, null, null)]          // no key
    [InlineData(101, false, null, null)]        // longer than dBASE allows
    [InlineData(4, true, null, null)]           // numeric keys are 8-byte doubles
    [InlineData(10, false, 3, null)]            // items shorter than key + 8
    [InlineData(10, false, null, 60)]           // 60 items of 20 bytes don't fit a page
    public void Impossible_headers_are_rejected_up_front(int keyLength, bool numeric, int? itemLength, int? maxKeys)
    {
        var file = new NdxBuilder(keyLength: Math.Max(keyLength, 1), numeric: numeric).Build();
        var header = file.GetBuffer().AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)keyLength);
        if (itemLength is { } item)
            BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)item);
        if (maxKeys is { } max)
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)max);

        Assert.Throws<InvalidDataException>(() => NdxIndex.Open(file));
    }

    [Fact]
    public void Probes_longer_than_the_key_only_match_with_trailing_spaces()
    {
        var tag = NdxIndex.Open(new NdxBuilder(keyLength: 2).Leaf(1, ("AB"u8.ToArray(), 1)).Build()).Tag;

        Assert.Empty(tag.SeekExact("ABC"));
        Assert.Single(tag.SeekExact("AB  "));
        Assert.Single(tag.SeekExact("A"));
    }

    [Fact]
    public void Blank_dates_read_and_seek_as_null()
    {
        using var table = TableWith(DbfFieldDefinition.Date("D"));
        var index = NdxIndex.Open(new NdxBuilder(keyLength: 8, numeric: true, expression: "D")
            .Leaf(1, (N(0), 1), (N(IndexKeys.JulianDay(new DateOnly(1999, 12, 31))), 2)).Build(), table: table);

        Assert.Equal([null, (object)new DateOnly(1999, 12, 31)], index.Tag.Entries().Select(e => e.Key));
        Assert.Equal([1], index.Tag.SeekExact(null).Select(e => e.RecordNumber));
    }

    [Fact]
    public void Streams_that_return_a_few_bytes_at_a_time_still_open()
    {
        using var index = NdxIndex.Open(new TrickleStream(File.ReadAllBytes(Fixture("retroinv/NUM993.NDX")), chunk: 7));
        Assert.Equal(993, index.Tag.Entries().Count());
    }

    // ---- a tiny NDX builder ---------------------------------------------------------------

    private static byte[] C(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] N(double value)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(key, value);
        return key;
    }

    private sealed class NdxBuilder
    {
        private readonly byte[] _file = new byte[16 * NdxIndex.PageSize];
        private readonly int _keyLength;
        private readonly int _itemLength;

        public NdxBuilder(int keyLength, bool numeric = false, string expression = "K")
        {
            _keyLength = keyLength;
            _itemLength = (keyLength + 8 + 3) / 4 * 4;
            var header = _file.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(header, 1); // root at page 1
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)keyLength);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)((NdxIndex.PageSize - 4) / _itemLength));
            BinaryPrimitives.WriteUInt16LittleEndian(header[16..], (ushort)(numeric ? 1 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)_itemLength);
            Encoding.ASCII.GetBytes(expression).CopyTo(header[24..]);
        }

        public NdxBuilder Leaf(int page, params (byte[] Key, uint Record)[] items)
        {
            var node = _file.AsSpan(page * NdxIndex.PageSize, NdxIndex.PageSize);
            BinaryPrimitives.WriteUInt16LittleEndian(node, (ushort)items.Length);
            for (var i = 0; i < items.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(node[(8 + i * _itemLength)..], items[i].Record);
                items[i].Key.AsSpan(0, Math.Min(_keyLength, items[i].Key.Length)).CopyTo(node[(12 + i * _itemLength)..]);
            }
            return this;
        }

        public NdxBuilder Inner(int page, byte[][] keys, uint[] children)
        {
            var node = _file.AsSpan(page * NdxIndex.PageSize, NdxIndex.PageSize);
            BinaryPrimitives.WriteUInt16LittleEndian(node, (ushort)keys.Length);
            for (var i = 0; i < children.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(node[(4 + i * _itemLength)..], children[i]);
                if (i < keys.Length)
                    keys[i].CopyTo(node[(12 + i * _itemLength)..]);
            }
            return this;
        }

        public MemoryStream Build() => new(_file, 0, _file.Length, writable: true, publiclyVisible: true);
    }
}
