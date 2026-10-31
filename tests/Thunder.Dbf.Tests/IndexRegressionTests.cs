//     _______
//    | o   o |     ProjectThunder :: Thunder.Dbf tests
//    |   ^   |     Round two: an independent review of the MDX reader and the
//    |  '-'  |     expression evaluator (ae4fd4a), one test per finding.
//    |_______|

using System.Buffers.Binary;
using System.Text;

namespace Thunder.Dbf.Tests;

public class IndexRegressionTests
{
    private static readonly string FixtureRoot = FindFixtureRoot();

    // ---- a tiny MDX builder: header + tag table, then whatever pages a test lays out ----------

    private sealed class MdxBuilder
    {
        public const int BlockSize = 1024;
        private readonly byte[] _file = new byte[64 * 512];
        private int _tags;

        public MdxBuilder()
        {
            _file[0] = 0x02;
            BinaryPrimitives.WriteUInt16LittleEndian(_file.AsSpan(20), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(_file.AsSpan(22), BlockSize);
            _file[24] = 1;
            _file[26] = 32;
        }

        public MdxBuilder Tag(string name, char keyType, int keyLength, int headerPage, int rootPage, string expression = "K",
            bool descending = false, int? maxKeys = null, int? itemLength = null, byte[]? rawExpression = null)
        {
            var entry = _file.AsSpan(544 + 32 * _tags++);
            BinaryPrimitives.WriteInt32LittleEndian(entry, headerPage);
            Encoding.ASCII.GetBytes(name).CopyTo(entry[4..]);
            entry[20] = (byte)keyType;
            BinaryPrimitives.WriteUInt16LittleEndian(_file.AsSpan(28), (ushort)_tags);

            var item = itemLength ?? (keyLength + 4 + 3) / 4 * 4;
            var header = _file.AsSpan(headerPage * 512, BlockSize);
            BinaryPrimitives.WriteInt32LittleEndian(header, rootPage);
            header[8] = (byte)(0x10 | (descending ? 0x08 : 0));
            header[9] = (byte)keyType;
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)keyLength);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)(maxKeys ?? (BlockSize - 12) / item));
            BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)item);
            (rawExpression ?? Encoding.ASCII.GetBytes(expression)).CopyTo(header[24..]);
            _itemLength = item;
            return this;
        }

        private int _itemLength;

        public MdxBuilder Leaf(int page, params (byte[] Key, int Record)[] items)
        {
            var node = _file.AsSpan(page * 512, BlockSize);
            BinaryPrimitives.WriteInt32LittleEndian(node, items.Length);
            for (var i = 0; i < items.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(node[(8 + i * _itemLength)..], items[i].Record);
                items[i].Key.CopyTo(node[(12 + i * _itemLength)..]);
            }
            return this;
        }

        public MdxBuilder Interior(int page, byte[][] keys, int[] children)
        {
            var node = _file.AsSpan(page * 512, BlockSize);
            BinaryPrimitives.WriteInt32LittleEndian(node, keys.Length);
            for (var i = 0; i < children.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(node[(8 + i * _itemLength)..], children[i]);
                if (i < keys.Length)
                    keys[i].CopyTo(node[(12 + i * _itemLength)..]);
            }
            return this;
        }

        public MemoryStream Build() => new(_file);
    }

    private static byte[] N(decimal value) => MdxKeys.EncodeNumeric(value);
    private static byte[] Raw(params byte[] bytes) { var key = new byte[12]; bytes.CopyTo(key, 0); return key; }

    // #1 cycles and shared subtrees
    [Fact]
    public void Cyclic_and_shared_pages_are_rejected_instead_of_walked_forever()
    {
        var cycle = new MdxBuilder().Tag("K", 'N', 12, headerPage: 4, rootPage: 6)
            .Interior(6, [N(1)], [6, 6]).Build();
        Assert.Throws<InvalidDataException>(() => MdxIndex.Open(cycle)["K"].Entries().ToList());

        var shared = new MdxBuilder().Tag("K", 'N', 12, headerPage: 4, rootPage: 6)
            .Interior(6, [N(1)], [8, 8])
            .Leaf(8, (N(1), 1)).Build();
        Assert.Throws<InvalidDataException>(() => MdxIndex.Open(shared)["K"].Entries().ToList());
    }

    // #2 tiny scientific floats
    [Fact]
    public void Tiny_scientific_floats_survive_reading_and_writing()
    {
        using var stream = Copy("infused-dbf/dbase_8b.dbf");
        using var memo = Copy("infused-dbf/dbase_8b.dbt");
        using var table = DbfTable.Open(stream, leaveOpen: true, memoStream: memo);
        var field = table.Fields.Single(f => f.Name == "FLOAT");
        var target = stream.GetBuffer().AsSpan(table.Header.HeaderLength + field.Offset, field.Length);
        target.Fill((byte)' ');
        "1E-100"u8.CopyTo(target[^6..]);

        Assert.Equal(1e-100, table.Read(0)["FLOAT"]);

        var values = table.Read(1).Values.ToArray();
        values[4] = 2.5e-50;
        table.Update(1, values);
        Assert.Equal(2.5e-50, (double)table.Read(1)["FLOAT"]!, 1e-60);
    }

    // #3 numeric keys beyond decimal's range
    [Fact]
    public void Numeric_keys_beyond_decimal_range_decode_order_and_seek()
    {
        var huge = Raw(0x99, 0x7D, 0x10);  // 1E100
        var tiny = Raw(0x18, 0x75, 0x10);  // 1E-29
        var negative = N(-5);
        var index = MdxIndex.Open(new MdxBuilder().Tag("K", 'N', 12, 4, 6)
            .Leaf(6, (negative, 1), (tiny, 2), (N(1), 3), (huge, 4)).Build());

        var keys = index["K"].Entries().Select(e => e.Key).ToList();
        Assert.Equal(-5m, keys[0]);
        Assert.Equal(1e-29, Convert.ToDouble(keys[1]), 1e-40);
        Assert.Equal(1e100, keys[3]);

        Assert.True(MdxKeys.CompareNumeric(negative, tiny) < 0);
        Assert.True(MdxKeys.CompareNumeric(tiny, N(1)) < 0);
        Assert.True(MdxKeys.CompareNumeric(N(1), huge) < 0);
        Assert.True(MdxKeys.CompareNumeric(N(0), tiny) < 0);
        Assert.Equal([4], index["K"].SeekExact(1e100).Select(e => e.RecordNumber));
    }

    // #4 duplicates whose bytes don't survive decode/re-encode
    [Fact]
    public void Exact_seeks_compare_stored_bytes_across_leaves()
    {
        byte[] a0 = [0x41, 0x00];
        var index = MdxIndex.Open(new MdxBuilder().Tag("K", 'C', 2, 4, 10)
            .Leaf(6, (a0, 1)).Leaf(8, (a0, 2))
            .Interior(10, [a0], [6, 8]).Build());

        Assert.Equal([1, 2], index["K"].SeekExact("A\0").Select(e => e.RecordNumber));
    }

    // #8 overlong probes
    [Fact]
    public void Probes_longer_than_the_key_only_match_with_trailing_spaces()
    {
        var index = MdxIndex.Open(new MdxBuilder().Tag("K", 'C', 2, 4, 6).Leaf(6, ("AB"u8.ToArray(), 1)).Build());

        Assert.Empty(index["K"].SeekExact("ABC"));
        Assert.Single(index["K"].SeekExact("AB  "));
        Assert.Single(index["K"].SeekExact("A"));
    }

    // #10 expressions keep their code page
    [Fact]
    public void Key_expressions_decode_with_the_index_encoding()
    {
        var cp850 = DbfEncoding.GetEncoding(850);
        var index = MdxIndex.Open(new MdxBuilder().Tag("K", 'C', 10, 4, 6, rawExpression: cp850.GetBytes("NAME + 'é'")).Leaf(6).Build(), cp850);

        Assert.Equal("NAME + 'é'", index["K"].Expression);
    }

    // #16 short reads
    [Fact]
    public void Streams_that_return_a_few_bytes_at_a_time_still_open()
    {
        var bytes = SharedFile.ReadAllBytes(Path.Combine(FixtureRoot, "siemens-step7/siprom/CHANGELOG.MDX"));
        using var index = MdxIndex.Open(new TrickleStream(bytes, chunk: 32));

        Assert.Equal(6, index.Tags.Count);
        Assert.Equal(3, index["ID_TAG"].Entries().Count());
    }

    // #19 layouts that overflow
    [Fact]
    public void Impossible_tag_layouts_are_rejected_up_front()
    {
        var overflow = new MdxBuilder().Tag("K", 'C', 10, 4, 6, maxKeys: 65535, itemLength: 65535).Build();
        Assert.Throws<InvalidDataException>(() => MdxIndex.Open(overflow));

        var wrongNumericLength = new MdxBuilder().Tag("K", 'N', 8, 4, 6).Build();
        Assert.Throws<InvalidDataException>(() => MdxIndex.Open(wrongNumericLength));
    }

    // #20 blank dates
    [Fact]
    public void Blank_date_keys_can_be_sought_with_null()
    {
        var blank = new byte[8];
        var date = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(date, MdxKeys.JulianDay(new DateOnly(1997, 4, 12)));
        var index = MdxIndex.Open(new MdxBuilder().Tag("K", 'D', 8, 4, 6).Leaf(6, (blank, 1), (date, 2)).Build());

        Assert.Equal([1], index["K"].SeekExact(null).Select(e => e.RecordNumber));
        Assert.Equal([2], index["K"].SeekExact(new DateOnly(1997, 4, 12)).Select(e => e.RecordNumber));
        Assert.Equal([null, (object)new DateOnly(1997, 4, 12)], index["K"].Entries().Select(e => e.Key));
    }

    // #5 two memos, the second invalid
    [Fact]
    public void A_bad_second_memo_leaves_the_memo_file_untouched()
    {
        using var stream = new MemoryStream();
        using var memo = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Memo("A"), DbfFieldDefinition.Memo("B")], memoStream: memo);
        var before = memo.ToArray();

        Assert.ThrowsAny<ArgumentException>(() => table.Append("new memo", -1));

        Assert.Equal(before, memo.ToArray());
    }

    // #6 autoincrement exhaustion
    [Fact]
    public void Exhausted_autoincrement_counters_refuse_without_writing()
    {
        using var stream = Copy("infused-dbf/dbase_31.dbf");
        BinaryPrimitives.WriteInt32LittleEndian(stream.GetBuffer().AsSpan(32 + 19), int.MaxValue);
        using var table = DbfTable.Open(stream, leaveOpen: true, allowUnmaintainedIndexes: true);
        var values = table.Read(0).Values.ToArray();
        values[0] = null;
        var count = table.RecordCount;
        var before = stream.ToArray();

        Assert.Throws<InvalidOperationException>(() => table.Append(values));
        Assert.Equal(count, table.RecordCount);
        Assert.Equal(before, stream.ToArray());
    }

    // #7, #12, #13, #14, #15 expression semantics
    [Theory]
    [InlineData("'Smith' = 'Sm'", true)]
    [InlineData("'Smith' <> 'Sm'", false)]
    [InlineData("'Smith' > 'Sm'", false)]
    [InlineData("'Smith' == 'Sm'", false)]
    [InlineData("'Sm' = 'Smith'", false)]
    [InlineData("VAL('12.3.4') = 12.3", true)]
    [InlineData("VAL('-7abc') = -7", true)]
    public void Expressions_follow_dBASE_rules(string expression, bool expected)
    {
        Assert.Equal(expected, DbfExpression.Parse(expression).Evaluate(AnyRecord()));
    }

    [Theory]
    [InlineData("'X' + 1")]
    [InlineData("1 - 'junk'")]
    [InlineData(".NOT. 1")]
    [InlineData("1 .AND. .T.")]
    [InlineData("UPPER(5)")]
    public void Type_mismatches_are_errors_not_guesses(string expression)
    {
        Assert.Throws<InvalidOperationException>(() => DbfExpression.Parse(expression).Evaluate(AnyRecord()));
    }

    [Fact]
    public void Aliases_are_refused_rather_than_ignored()
    {
        Assert.Throws<NotSupportedException>(() => DbfExpression.Parse("OTHER->ID"));
        Assert.Throws<NotSupportedException>(() => DbfExpression.Parse("M->ID"));
    }

    [Fact]
    public void Trims_and_string_minus_only_move_spaces()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Character("F", 4)]);
        table.Append("A\t");
        var record = table.Read(0);

        Assert.Equal("A\t", DbfExpression.Parse("TRIM(F)").Evaluate(record));
        Assert.Equal("A\tB  ", DbfExpression.Parse("F - 'B'").Evaluate(record));
    }

    // #9 padding in bytes
    [Fact]
    public void Character_fields_pad_to_their_width_in_bytes()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Character("F", 4)], languageDriver: 0x13); // code page 932
        table.Append("あ"); // two bytes in CP932

        Assert.Equal("あ  X", DbfExpression.Parse("F + 'X'").Evaluate(table.Read(0)));
    }

    // #11 real nulls
    [Fact]
    public void Visual_FoxPro_nulls_flow_through_expressions()
    {
        using var stream = Copy("infused-dbf/dbase_31.dbf");
        using var table = DbfTable.Open(stream, leaveOpen: true);
        var flags = table.Fields.Single(f => f.Type == '0');
        stream.GetBuffer()[table.Header.HeaderLength + flags.Offset] |= 0x01; // SUPPLIERID is null
        var record = table.Read(0);

        Assert.Null(DbfExpression.Parse("SUPPLIERID = 0").Evaluate(record));
        Assert.Null(DbfExpression.Parse("SUPPLIERID + 1").Evaluate(record));
        Assert.Equal(false, DbfExpression.Parse("SUPPLIERID = 0 .AND. .F.").Evaluate(record));
        Assert.Equal(true, DbfExpression.Parse("SUPPLIERID = 0 .OR. .T.").Evaluate(record));
    }

    // #17 Mazovia
    [Fact]
    public void Mazovia_driver_decodes_Polish_letters_and_round_trips()
    {
        var mazovia = DbfEncoding.For(0x69);
        Assert.Equal("ąćęłńóśźż", mazovia.GetString([0x86, 0x8D, 0x91, 0x92, 0xA4, 0xA2, 0x9E, 0xA6, 0xA7]));

        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        Assert.Equal(all, mazovia.GetBytes(mazovia.GetString(all)));
    }

    // #18 companions are matched case-insensitively
    [Fact]
    public void Create_refuses_an_existing_companion_in_any_case()
    {
        var dir = Directory.CreateTempSubdirectory("thunder-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "PEOPLE.MDX"), "old index");
            var error = Assert.Throws<DbfException>(() => DbfTable.Create(Path.Combine(dir.FullName, "people.dbf"), [DbfFieldDefinition.Character("N", 5)]));

            Assert.Equal(DbfErrorCode.TableAlreadyExists, error.Code);
            Assert.False(File.Exists(Path.Combine(dir.FullName, "people.dbf")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- helpers --------------------------------------------------------------------------

    private static DbfRecord AnyRecord()
    {
        var stream = new MemoryStream();
        var table = DbfTable.Create(stream, [DbfFieldDefinition.Character("X", 1)]);
        table.Append("x");
        return table.Read(0);
    }

    private static MemoryStream Copy(string fixture)
    {
        var stream = new MemoryStream();
        stream.Write(SharedFile.ReadAllBytes(Path.Combine(FixtureRoot, fixture)));
        return stream;
    }

    /// <summary>A stream that hands out at most a few bytes per Read, like a slow network share.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
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
