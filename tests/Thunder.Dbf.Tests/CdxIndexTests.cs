//      _.-^-._        ProjectThunder :: Thunder.Dbf tests
//     /  CDX  \       Visual FoxPro and X# compound indexes, every tag rebuilt from its
//    | T A G S |      table; then hand-packed leaves (shared prefixes, dropped blanks)
//     \_______/       for descending tags, typed keys, compact .idx files and damage.

using System.Buffers.Binary;
using System.Text;
using static Thunder.Dbf.Tests.IndexTestSupport;

namespace Thunder.Dbf.Tests;

public class CdxIndexTests
{
    // Each real .cdx and the table it belongs to.
    public static TheoryData<string, string> RealIndexes() => new()
    {
        { "infused-dbf/foxprodb/calls.CDX", "infused-dbf/foxprodb/calls.dbf" },
        { "infused-dbf/foxprodb/contacts.CDX", "infused-dbf/foxprodb/contacts.dbf" },
        { "infused-dbf/foxprodb/setup.CDX", "infused-dbf/foxprodb/setup.dbf" },
        { "infused-dbf/foxprodb/types.CDX", "infused-dbf/foxprodb/types.dbf" },
        { "xsharp/rdd-tests/TEST10K.CDX", "xsharp/rdd-tests/TEST10K.DBF" },
    };

    [Theory]
    [MemberData(nameof(RealIndexes))]
    public void Every_tag_matches_keys_computed_from_its_table(string indexFixture, string tableFixture)
    {
        using var table = DbfTable.Open(Fixture(tableFixture));
        using var index = CdxIndex.Open(Fixture(indexFixture), table: table);

        Assert.True(index.IsCompound);
        Assert.NotEmpty(index.Tags);
        foreach (var tag in index.Tags)
        {
            if (!CanEvaluate(tag.Expression, table))
            {
                // Visual FoxPro tables in a database container use long field names the .dbf
                // itself doesn't know; such keys can't be typed and stay raw text.
                Assert.Equal('C', tag.KeyType);
                Assert.Equal(table.RecordCount, tag.Entries().Count());
                continue;
            }

            var byteOrdered = !Collated.Contains((indexFixture, tag.Name));
            AssertTagMatchesTable(TagView.Of(tag), table, byteOrdered: byteOrdered);
            if (byteOrdered)
                AssertEveryKeyCanBeFound(TagView.Of(tag), table.Encoding);
        }
    }

    // X# sorted these character tags with a Windows collation rather than by byte, so a handful of
    // neighbours ("Macedon" before "MacLeod") are out of byte order; see the test below.
    private static readonly HashSet<(string, string)> Collated =
    [
        ("xsharp/rdd-tests/TEST10K.CDX", "CITY"),
        ("xsharp/rdd-tests/TEST10K.CDX", "FIRST"),
        ("xsharp/rdd-tests/TEST10K.CDX", "LAST"),
    ];

    // Seeks compare bytes, which is how dBASE, Clipper and FoxPro's MACHINE collation sort. A tag
    // sorted by a language collation reads back in its own order, and seeks still find every key
    // whose path down the tree doesn't cross one of the spots where the two orders disagree.
    [Fact]
    public void Tags_sorted_by_a_language_collation_keep_their_order()
    {
        using var table = DbfTable.Open(Fixture("xsharp/rdd-tests/TEST10K.DBF"));
        using var index = CdxIndex.Open(Fixture("xsharp/rdd-tests/TEST10K.CDX"), table: table);
        var cities = index["CITY"].Entries().Select(e => (string)e.Key!).Distinct().ToList();

        Assert.True(cities.IndexOf("Macedon") < cities.IndexOf("MacLeod"));
        Assert.Equal(cities.Order(StringComparer.OrdinalIgnoreCase), cities);
        Assert.NotEmpty(index["CITY"].SeekExact("Adelaide"));
        Assert.NotEmpty(index["CITY"].SeekExact("Worcester"));
    }

    [Fact]
    public void Tags_come_in_creation_order_with_their_header_fields()
    {
        using var table = DbfTable.Open(Fixture("xsharp/rdd-tests/TEST10K.DBF"));
        using var index = CdxIndex.Open(Fixture("xsharp/rdd-tests/TEST10K.CDX"), table: table);

        Assert.Equal(512, index.PageLength);
        Assert.Equal(["AGE", "CITY", "FIRST", "HIREDATE", "LAST", "SALARY", "STATE"], index.Tags.Select(t => t.Name).Order());
        Assert.Equal(index.Tags.Select(t => t.HeaderOffset).Order(), index.Tags.Select(t => t.HeaderOffset));

        var hired = index["hiredate"];
        Assert.Equal(('D', 8, "HIREDATE"), (hired.KeyType, hired.KeyLength, hired.Expression));
        Assert.Equal(new DateOnly(1981, 1, 1), hired.Entries().First().Key);
        Assert.Equal(('N', 8), (index["SALARY"].KeyType, index["SALARY"].KeyLength));
        Assert.Throws<KeyNotFoundException>(() => index["NOPE"]);

        Assert.Equal(Fixture("xsharp/rdd-tests/TEST10K.CDX"), CdxIndex.FindFor(Fixture("xsharp/rdd-tests/TEST10K.DBF")));
    }

    [Fact]
    public void Visual_FoxPro_integer_keys_are_four_bytes()
    {
        using var table = DbfTable.Open(Fixture("infused-dbf/foxprodb/calls.dbf"));
        using var index = CdxIndex.Open(Fixture("infused-dbf/foxprodb/calls.CDX"), table: table);
        var tag = index["CONTACT_ID"];

        Assert.Equal(('N', 4), (tag.KeyType, tag.KeyLength));
        Assert.Equal(table.ReadAll().Where(r => (int)r["CONTACT_ID"]! == 3).Select(r => r.RecordNumber),
            tag.SeekExact(3).Select(e => e.RecordNumber).Order());
    }

    [Fact]
    public void Without_the_table_keys_read_as_text()
    {
        using var index = CdxIndex.Open(Fixture("infused-dbf/foxprodb/setup.CDX"));

        Assert.Equal('C', index["KEY_NAME"].KeyType);
        Assert.Equal(["CALLS", "CONTACTS", "CONTACT_TYPES"], index["KEY_NAME"].Entries().Select(e => e.Key));
        Assert.Equal(["CONTACTS", "CONTACT_TYPES"], index["KEY_NAME"].SeekExact("CONTACT").Select(e => e.Key));
    }

    // Two levels, with a run of equal keys across the leaf boundary:
    //
    //            [ "bb" | "dd" ]                (root, offset 2048)
    //            /             \
    //   [aa bb bb] (2560)    [bb cc dd] (3072)
    [Fact]
    public void Interior_pages_are_walked_in_order_and_seeks_cross_leaves()
    {
        var tag = CdxIndex.Open(new CdxBuilder()
            .Tag("K", headerOffset: 1024, root: 2048, keyLength: 2)
            .Interior(2048, 2, (C("bb"), 3, 2560), (C("dd"), 6, 3072))
            .Leaf(2560, 2, (C("aa"), 1), (C("bb"), 2), (C("bb"), 3))
            .Leaf(3072, 2, (C("bb"), 4), (C("cc"), 5), (C("dd"), 6))
            .Build())["K"];

        Assert.Equal(["aa", "bb", "bb", "bb", "cc", "dd"], tag.Entries().Select(e => e.Key));
        Assert.Equal([2, 3, 4], tag.SeekExact("bb").Select(e => e.RecordNumber));
        Assert.Equal([2, 3, 4], tag.SeekExact("b").Select(e => e.RecordNumber));
        Assert.Equal(["cc", "dd"], tag.Seek("bc").Select(e => e.Key));
        Assert.Empty(tag.Seek("e"));
    }

    [Fact]
    public void Leaves_rebuild_shared_prefixes_and_dropped_blanks()
    {
        var tag = CdxIndex.Open(new CdxBuilder()
            .Tag("NAME", headerOffset: 1024, root: 2048, keyLength: 8)
            .Leaf(2048, 8, (C("SMITH   "), 1), (C("SMITHERS"), 2), (C("SMITS   "), 3), (C("SMITS   "), 4), (C("        "), 5))
            .Build())["NAME"];

        // (The blank key sorts last only because this hand-built page says so; the reader trusts the order.)
        Assert.Equal(["SMITH", "SMITHERS", "SMITS", "SMITS", ""], tag.Entries().Select(e => e.Key));
        Assert.Equal([1, 2, 3, 4, 5], tag.Entries().Select(e => e.RecordNumber));
    }

    [Fact]
    public void Descending_tags_seek_downwards()
    {
        var tag = CdxIndex.Open(new CdxBuilder()
            .Tag("K", headerOffset: 1024, root: 2048, keyLength: 1, descending: true)
            .Interior(2048, 1, (C("d"), 2, 2560), (C("a"), 5, 3072))
            .Leaf(2560, 1, (C("e"), 1), (C("d"), 2))
            .Leaf(3072, 1, (C("c"), 3), (C("b"), 4), (C("a"), 5))
            .Build())["K"];

        Assert.True(tag.IsDescending);
        Assert.Equal(["e", "d", "c", "b", "a"], tag.Entries().Select(e => e.Key));
        Assert.Equal(["c", "b", "a"], tag.Seek("cz").Select(e => e.Key));
        Assert.Equal([2], tag.SeekExact("d").Select(e => e.RecordNumber));
        Assert.Empty(tag.SeekExact("f"));
    }

    [Fact]
    public void Numbers_dates_and_logicals_decode_with_the_table()
    {
        using var table = TableWith(DbfFieldDefinition.Numeric("N", 10, 2), DbfFieldDefinition.Date("D"), DbfFieldDefinition.Logical("L"));
        var index = CdxIndex.Open(new CdxBuilder()
            .Tag("N", headerOffset: 1024, root: 3072, keyLength: 8, expression: "N")
            .Tag("D", headerOffset: 2048, root: 3584, keyLength: 8, expression: "D")
            .Leaf(3072, 8, trail: 0, (Num(-1e30), 1), (Num(-2.5), 2), (Num(0), 3), (Num(0.25), 4), (Num(1e100), 5))
            .Leaf(3584, 8, trail: 0, (Num(0), 1), (Num(IndexKeys.JulianDay(new DateOnly(2001, 2, 3))), 2))
            .Tag("L", headerOffset: 4096, root: 5120, keyLength: 1, expression: "L")
            .Leaf(5120, 1, trail: 0, (C("F"), 2), (C("T"), 1))
            .Build(), table: table);

        var numbers = index["N"];
        Assert.Equal([(object)-1e30, -2.5m, 0m, 0.25m, 1e100], numbers.Entries().Select(e => e.Key));
        Assert.Equal([4, 5], numbers.Seek(0.1m).Select(e => e.RecordNumber));
        Assert.Equal([2], numbers.SeekExact(-2.5).Select(e => e.RecordNumber));

        var dates = index["D"];
        Assert.Equal([null, (object)new DateOnly(2001, 2, 3)], dates.Entries().Select(e => e.Key));
        Assert.Equal([1], dates.SeekExact(null).Select(e => e.RecordNumber));

        Assert.Equal([(object)false, true], index["L"].Entries().Select(e => e.Key));
        Assert.Equal([1], index["L"].SeekExact(true).Select(e => e.RecordNumber));
    }

    [Fact]
    public void Compact_idx_files_hold_one_tag_named_after_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"thunder-{Guid.NewGuid():N}.idx");
        File.WriteAllBytes(path, new CdxBuilder(compound: false)
            .Tag("", headerOffset: 0, root: 1024, keyLength: 3, forExpression: "K <> 'zzz'", unique: true)
            .Leaf(1024, 3, (C("abc"), 2), (C("abd"), 1))
            .Build().ToArray());
        try
        {
            using var index = CdxIndex.Open(path);
            var tag = Assert.Single(index.Tags);
            Assert.False(index.IsCompound);
            Assert.Equal(Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), tag.Name);
            Assert.True(tag.IsUnique);
            Assert.Equal("K <> 'zzz'", tag.ForExpression);
            Assert.Equal(["abc", "abd"], tag.Entries().Select(e => e.Key));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Cyclic_and_shared_pages_are_rejected_instead_of_walked_forever()
    {
        var cycle = CdxIndex.Open(new CdxBuilder().Tag("K", 1024, 2048, 1).Interior(2048, 1, (C("a"), 1, 2048)).Build());
        Assert.Throws<InvalidDataException>(() => cycle["K"].Entries().ToList());

        var shared = CdxIndex.Open(new CdxBuilder().Tag("K", 1024, 2048, 1)
            .Interior(2048, 1, (C("a"), 1, 2560), (C("b"), 1, 2560))
            .Leaf(2560, 1, (C("a"), 1)).Build());
        Assert.Throws<InvalidDataException>(() => shared["K"].Entries().ToList());
    }

    [Fact]
    public void Damaged_leaves_and_pointers_are_reported_as_corrupt()
    {
        // Duplicate + trailing counts larger than the key.
        var stream = new CdxBuilder().Tag("K", 1024, 2048, 2).Leaf(2048, 2, (C("ab"), 1), (C("ac"), 2)).Build();
        stream.GetBuffer()[2048 + 24 + 4 + 2] = 0xFF; // second entry's duplicate count
        Assert.Throws<InvalidDataException>(() => CdxIndex.Open(stream)["K"].Entries().ToList());

        // More entries than the page holds.
        stream = new CdxBuilder().Tag("K", 1024, 2048, 2).Leaf(2048, 2, (C("ab"), 1)).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(stream.GetBuffer().AsSpan(2048 + 2), 500);
        Assert.Throws<InvalidDataException>(() => CdxIndex.Open(stream)["K"].Entries().ToList());

        // An interior child of -1, and a child outside the file.
        Assert.Throws<InvalidDataException>(() => CdxIndex.Open(new CdxBuilder().Tag("K", 1024, 2048, 1)
            .Interior(2048, 1, (C("a"), 1, 0xFFFFFFFF)).Build())["K"].Entries().ToList());
        Assert.Throws<InvalidDataException>(() => CdxIndex.Open(new CdxBuilder().Tag("K", 1024, 2048, 1)
            .Interior(2048, 1, (C("a"), 1, 1 << 30)).Build())["K"].Entries().ToList());

        // A pointer to a free page (type 0xFF).
        stream = new CdxBuilder().Tag("K", 1024, 2048, 1).Interior(2048, 1, (C("a"), 1, 2560)).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(stream.GetBuffer().AsSpan(2560), 0xFF);
        Assert.Throws<InvalidDataException>(() => CdxIndex.Open(stream)["K"].Entries().ToList());
    }

    [Fact]
    public void Broken_headers_are_rejected_up_front()
    {
        Assert.Throws<InvalidDataException>(() => CdxIndex.Open(new MemoryStream(new byte[600])));
        Assert.Throws<NotSupportedException>(() => CdxIndex.Open(new MemoryStream(new byte[2048]))); // no compact flag: FoxBASE .idx

        var shared = new CdxBuilder().Tag("A", 1024, 3072, 1).Tag("B", 1024, 3072, 1).Leaf(3072, 1).Build();
        Assert.Throws<InvalidDataException>(() => CdxIndex.Open(shared));

        var stream = new CdxBuilder().Tag("K", 1024, 2048, 1).Leaf(2048, 1).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(stream.GetBuffer().AsSpan(1024 + 508), 511); // key expression past the pool
        Assert.Throws<InvalidDataException>(() => CdxIndex.Open(stream));

        stream = new CdxBuilder().Tag("K", 1024, 2048, 1).Leaf(2048, 1).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(stream.GetBuffer().AsSpan(1024 + 12), 400); // keys too long for a page
        Assert.Throws<InvalidDataException>(() => CdxIndex.Open(stream));
    }

    [Fact]
    public void Ordered_doubles_sort_as_bytes_and_round_trip()
    {
        double[] values = [double.MinValue, -1e300, -1, -0.5, -double.Epsilon, 0, double.Epsilon, 0.5, 1, 1e300, double.MaxValue];
        var encoded = values.Select(IndexKeys.WriteOrderedDouble).ToList();

        for (var i = 1; i < encoded.Count; i++)
            Assert.True(encoded[i - 1].AsSpan().SequenceCompareTo(encoded[i]) < 0, $"{values[i - 1]} should sort before {values[i]}");
        Assert.Equal(values, encoded.Select(e => IndexKeys.ReadOrderedDouble(e)));
        Assert.Equal(IndexKeys.WriteOrderedDouble(0), IndexKeys.WriteOrderedDouble(-0.0));
    }

    [Fact]
    public void Streams_that_return_a_few_bytes_at_a_time_still_open()
    {
        using var index = CdxIndex.Open(new TrickleStream(File.ReadAllBytes(Fixture("xsharp/rdd-tests/TEST10K.CDX")), chunk: 29));
        Assert.Equal(7, index.Tags.Count);
        Assert.Equal(10_000, index["STATE"].Entries().Count());
    }

    private static bool CanEvaluate(string expression, DbfTable table)
    {
        try
        {
            DbfExpression.Parse(expression).Evaluate(table.Read(0));
            return true;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    // ---- a tiny CDX builder: tag headers, packed leaves, interior pages, and the tag directory ----

    private static byte[] C(string text) => Encoding.ASCII.GetBytes(text);
    private static byte[] Num(double value) => IndexKeys.WriteOrderedDouble(value);

    private sealed class CdxBuilder(bool compound = true)
    {
        private const int Page = CdxIndex.DefaultPageLength;
        private const int DirectoryRoot = 7 * 1024; // the directory's one leaf sits out of the way
        private readonly byte[] _file = new byte[16 * 1024];
        private readonly List<(string Name, int Offset)> _tags = [];

        public CdxBuilder Tag(string name, int headerOffset, int root, int keyLength, string expression = "K",
            string? forExpression = null, bool descending = false, bool unique = false)
        {
            _tags.Add((name, headerOffset));
            var header = _file.AsSpan(headerOffset, CdxIndex.HeaderLength);
            BinaryPrimitives.WriteInt32LittleEndian(header, root);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)keyLength);
            header[14] = (byte)(CdxIndex.OptionCompact | (compound ? CdxIndex.OptionCompound : 0)
                | (unique ? CdxIndex.OptionUnique : 0) | (forExpression is null ? 0 : CdxIndex.OptionForFilter));
            header[15] = 0x01;
            BinaryPrimitives.WriteUInt16LittleEndian(header[502..], (ushort)(descending ? 1 : 0));

            // FoxPro's layout: the key expression at 0, the FOR expression after it, lengths counting the NUL.
            var key = Encoding.ASCII.GetBytes(expression + "\0");
            var filter = Encoding.ASCII.GetBytes((forExpression ?? "") + "\0");
            BinaryPrimitives.WriteUInt16LittleEndian(header[504..], (ushort)key.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[506..], (ushort)filter.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[508..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[510..], (ushort)key.Length);
            key.CopyTo(header[512..]);
            filter.CopyTo(header[(512 + key.Length)..]);
            return this;
        }

        public CdxBuilder Leaf(int offset, int keyLength, params (byte[] Key, uint Record)[] items) =>
            Leaf(offset, keyLength, (byte)' ', items);

        /// <summary>
        /// Packs a leaf the way FoxPro does, with 4-byte entries: 16 bits of record number, then 8 bits
        /// each for the duplicate and trailing counts; key bytes fill the page from the end backwards.
        /// </summary>
        public CdxBuilder Leaf(int offset, int keyLength, byte trail, params (byte[] Key, uint Record)[] items)
        {
            const int entryLength = 4, recordBits = 16, countBits = 8;
            var page = _file.AsSpan(offset, Page);
            BinaryPrimitives.WriteUInt16LittleEndian(page, 0x03); // root + leaf
            BinaryPrimitives.WriteUInt16LittleEndian(page[2..], (ushort)items.Length);
            BinaryPrimitives.WriteInt32LittleEndian(page[4..], -1);
            BinaryPrimitives.WriteInt32LittleEndian(page[8..], -1);
            BinaryPrimitives.WriteUInt32LittleEndian(page[14..], 0xFFFF);
            page[18] = 0xFF;
            page[19] = 0xFF;
            (page[20], page[21], page[22], page[23]) = (recordBits, countBits, countBits, entryLength);

            var end = Page;
            byte[] previous = [];
            for (var i = 0; i < items.Length; i++)
            {
                var key = items[i].Key;
                var trailing = keyLength - key.AsSpan().TrimEnd(trail).Length;
                var duplicates = 0;
                while (i > 0 && duplicates < keyLength - trailing && key[duplicates] == previous[duplicates])
                    duplicates++;

                var stored = key.AsSpan(duplicates, keyLength - duplicates - trailing);
                end -= stored.Length;
                stored.CopyTo(page[end..]);

                var packed = items[i].Record | (uint)duplicates << recordBits | (uint)trailing << (recordBits + countBits);
                BinaryPrimitives.WriteUInt32LittleEndian(page[(24 + i * entryLength)..], packed);
                previous = key;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(page[12..], (ushort)(end - 24 - items.Length * entryLength));
            return this;
        }

        public CdxBuilder Interior(int offset, int keyLength, params (byte[] Key, uint Record, uint Child)[] items)
        {
            var page = _file.AsSpan(offset, Page);
            BinaryPrimitives.WriteUInt16LittleEndian(page, 0x01); // root
            BinaryPrimitives.WriteUInt16LittleEndian(page[2..], (ushort)items.Length);
            BinaryPrimitives.WriteInt32LittleEndian(page[4..], -1);
            BinaryPrimitives.WriteInt32LittleEndian(page[8..], -1);
            for (var i = 0; i < items.Length; i++)
            {
                var item = page[(12 + i * (keyLength + 8))..];
                items[i].Key.CopyTo(item);
                BinaryPrimitives.WriteUInt32BigEndian(item[keyLength..], items[i].Record);
                BinaryPrimitives.WriteUInt32BigEndian(item[(keyLength + 4)..], items[i].Child);
            }
            return this;
        }

        public MemoryStream Build()
        {
            if (compound)
            {
                // The directory: a tag at offset 0 whose keys are tag names and records are header offsets.
                var directory = _file.AsSpan(0, CdxIndex.HeaderLength);
                BinaryPrimitives.WriteInt32LittleEndian(directory, DirectoryRoot);
                BinaryPrimitives.WriteUInt16LittleEndian(directory[12..], 10);
                directory[14] = 0xE0;
                directory[15] = 0x01;
                Leaf(DirectoryRoot, 10, _tags.OrderBy(t => t.Name, StringComparer.Ordinal)
                    .Select(t => (Encoding.ASCII.GetBytes(t.Name.PadRight(10)), (uint)t.Offset)).ToArray());
            }
            return new MemoryStream(_file, 0, _file.Length, writable: true, publiclyVisible: true);
        }
    }
}
