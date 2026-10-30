//    ___________________
//   |  ___   ___   ___  |    ProjectThunder :: Thunder.Dbf tests
//   | |dbf| |fpt| |cdx| |    Real files from real apps. If these
//   | |___| |___| |___| |    open, the synthetic tests weren't lying.
//   |___________________|
//   [_] [_] [_] [_] [_] [_]

namespace Thunder.Dbf.Tests;

public class RealFileTests
{
    // dBASE II and dBASE 7 use different header layouts; we refuse them on purpose.
    private static readonly HashSet<string> Unsupported = ["infused-dbf/dbase_02.dbf", "infused-dbf/dbase_8c.dbf"];

    // These ship without their memo file on purpose.
    private static readonly HashSet<string> MissingMemo = ["infused-dbf/dbase_83_missing_memo.dbf", "dbfread/no_memofile.dbf"];

    private static readonly string FixtureRoot = FindFixtureRoot();

    public static TheoryData<string> AllTables()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories)
                     .Where(p => Path.GetExtension(p).Equals(".dbf", StringComparison.OrdinalIgnoreCase))
                     .Select(p => Path.GetRelativePath(FixtureRoot, p).Replace('\\', '/'))
                     .Order(StringComparer.Ordinal))
            data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTables))]
    public void Every_record_decodes(string fixture)
    {
        var path = Path.Combine(FixtureRoot, fixture);

        if (Unsupported.Contains(fixture))
        {
            Assert.Equal(DbfErrorCode.UnsupportedFormat, Assert.Throws<DbfException>(() => DbfTable.Open(path)).Code);
            return;
        }

        if (MissingMemo.Contains(fixture))
            Assert.Equal(DbfErrorCode.MemoFileMissing, Assert.Throws<DbfException>(() => DbfTable.Open(path)).Code);

        using var table = DbfTable.Open(path, ignoreMissingMemo: MissingMemo.Contains(fixture));
        var fileLength = new FileInfo(path).Length;

        Assert.True(table.Header.HeaderLength + (long)table.RecordCount * table.Header.RecordLength <= fileLength,
            $"{fixture}: {table.RecordCount} records of {table.Header.RecordLength} bytes don't fit in {fileLength} bytes.");

        var count = 0;
        foreach (var record in table.ReadAll())
        {
            Assert.Equal(table.Fields.Count, record.Values.Count);
            count++;
        }
        Assert.Equal(table.RecordCount, count);
    }

    // Expected values below come from the upstream projects' own tests (infused/dbf, DbfDataReader)
    // or an independent Python decode of the same bytes.

    private static DbfTable OpenFixture(string fixture, System.Text.Encoding? encoding = null) =>
        DbfTable.Open(Path.Combine(FixtureRoot, fixture), encoding: encoding);

    [Fact]
    public void dBASE_III_with_memo_record_zero_matches_upstream()
    {
        using var table = OpenFixture("infused-dbf/dbase_83.dbf");
        var record = table.Read(0);

        Assert.Equal(87m, record[0]);
        Assert.Equal("Assorted Petits Fours", record[6]);
        Assert.Equal("graphics/00000001/t_1.jpg", record[7]);
        Assert.Equal(
            "Our Original assortment...a little taste of heaven for everyone.  Let us\r\n" +
            "select a special assortment of our chocolate and pastel favorites for you.\r\n" +
            "Each petit four is its own special hand decorated creation. Multi-layers of\r\n" +
            "moist cake with combinations of specialty fillings create memorable cake\r\n" +
            "confections. Varietes include; Luscious Lemon, Strawberry Hearts, White\r\n" +
            "Chocolate, Mocha Bean, Roasted Almond, Triple Chocolate, Chocolate Hazelnut,\r\n" +
            "Grand Orange, Plum Squares, Milk chocolate squares, and Raspberry Blanc.",
            record[11]); // from dbase_83.dbt
        Assert.Equal(5.51m, record[12]);
        Assert.Equal(true, record[13]);
    }

    [Fact]
    public void dBASE_IV_with_memo_has_expected_schema()
    {
        using var table = OpenFixture("infused-dbf/dbase_8b.dbf");

        Assert.Equal(0x8B, table.Header.Version);
        Assert.True(table.Header.HasMemo);
        Assert.Equal(10, table.RecordCount);
        Assert.Equal(["CHARACTER", "NUMERICAL", "DATE", "LOGICAL", "FLOAT", "MEMO"], table.Fields.Select(f => f.Name));
        Assert.Equal("CNDLFM", string.Concat(table.Fields.Select(f => f.Type)));
    }

    [Fact]
    public void Visual_FoxPro_integer_and_currency_fields_decode()
    {
        using var table = OpenFixture("infused-dbf/dbase_31.dbf");
        var chai = table.Read(0);

        Assert.Equal(77, table.RecordCount);
        Assert.Equal(1, chai["PRODUCTID"]);
        Assert.Equal("Chai", chai["PRODUCTNAM"]);
        Assert.Equal("10 boxes x 20 bags", chai["QUANTITYPE"]);
        Assert.Equal(18m, chai["UNITPRICE"]); // 0x02BF20 / 10,000
    }

    [Theory]
    [InlineData("infused-dbf/dbase_31.dbf", null)]
    [InlineData("infused-dbf/dbase_30.dbf", "infused-dbf/dbase_30.fpt")]
    [InlineData("infused-dbf/dbase_83.dbf", "infused-dbf/dbase_83.dbt")]
    [InlineData("infused-dbf/dbase_8b.dbf", "infused-dbf/dbase_8b.dbt")]
    [InlineData("dbfdatareader/client.dbf", "dbfdatareader/client.dbt")]
    [InlineData("dbfdatareader/foxpro_currency_01.dbf", null)]
    public void Rewriting_records_with_their_own_values_changes_no_bytes(string fixture, string? memoFixture)
    {
        var original = File.ReadAllBytes(Path.Combine(FixtureRoot, fixture));
        var originalMemo = memoFixture is null ? null : File.ReadAllBytes(Path.Combine(FixtureRoot, memoFixture));
        using var stream = new MemoryStream();
        stream.Write(original);
        using var memoStream = new MemoryStream();
        if (originalMemo is not null)
            memoStream.Write(originalMemo);

        using (var table = DbfTable.Open(stream, leaveOpen: true, memoStream: originalMemo is null ? null : memoStream, allowUnmaintainedIndexes: true))
        {
            foreach (var record in table.ReadAll().ToList())
                table.Update(record.Index, [.. record.Values]);
        }

        var rewritten = stream.ToArray();
        var header = DbfHeader.Read(new MemoryStream(original));
        // Only the last-update date (bytes 1-3) may change, and unchanged memos must stay where they are.
        Assert.Equal(original.AsSpan(header.HeaderLength).ToArray(), rewritten.AsSpan(header.HeaderLength).ToArray());
        Assert.Equal(original.AsSpan(4, header.HeaderLength - 4).ToArray(), rewritten.AsSpan(4, header.HeaderLength - 4).ToArray());
        if (originalMemo is not null)
            Assert.Equal(originalMemo, memoStream.ToArray());
    }

    [Fact]
    public void Visual_FoxPro_currency_signs_match_upstream()
    {
        using var table = OpenFixture("dbfdatareader/foxpro_currency_01.dbf");
        Assert.Equal([20m, -20m], table.Read(0).Values);
    }

    [Fact]
    public void Visual_FoxPro_datetime_and_fpt_memo_fields_decode()
    {
        using var table = OpenFixture("infused-dbf/dbase_30.dbf");

        Assert.Equal(DbfMemoFormat.FoxPro, table.MemoFormat);
        Assert.Equal(34, table.RecordCount);
        Assert.Equal(145, table.Fields.Count);
        Assert.All(table.ReadAll(), r =>
        {
            Assert.True(r["CLASSES"] is null or string);
            Assert.True(r["FLAGDATE"] is null or DateTime);
        });
        // infused/dbf expects record 3's CLASSES memo to match /\AAgriculture.*Farming\r\n\Z/m
        Assert.Matches(@"\AAgriculture[\s\S]*Farming\r\n\z", (string)table.Read(3)["CLASSES"]!);
    }

    [Fact]
    public void dBASE_IV_memo_lengths_exclude_the_block_header()
    {
        using var table = OpenFixture("infused-dbf/dbase_8b.dbf");

        Assert.Equal(DbfMemoFormat.DbaseIV, table.MemoFormat);
        Assert.Equal("First memo\r\n", table.Read(0)["MEMO"]);
    }

    [Fact]
    public void dBASE_IV_client_history_memo_matches_upstream()
    {
        using var table = OpenFixture("dbfdatareader/client.dbf");
        var history = (string)table.Read(0)["CLIEN_HIST"]!;

        Assert.StartsWith("85-200  08/02/85\r\n    C-300-400 BOOK CASE                535.00 1", history);
    }

    [Theory]
    [InlineData("infused-dbf/dbase_83.dbf", "infused-dbf/dbase_83.dbt", DbfMemoFormat.DbaseIII)]
    [InlineData("infused-dbf/dbase_8b.dbf", "infused-dbf/dbase_8b.dbt", DbfMemoFormat.DbaseIV)]
    [InlineData("infused-dbf/dbase_30.dbf", "infused-dbf/dbase_30.fpt", DbfMemoFormat.FoxPro)]
    public void Memos_appended_to_real_memo_files_read_back(string fixture, string memoFixture, DbfMemoFormat format)
    {
        using var stream = new MemoryStream();
        stream.Write(File.ReadAllBytes(Path.Combine(FixtureRoot, fixture)));
        using var memoStream = new MemoryStream();
        memoStream.Write(File.ReadAllBytes(Path.Combine(FixtureRoot, memoFixture)));
        const string note = "Added by Thunder.Dbf.\r\nSecond line, still the same memo.";

        object?[] expected;
        using (var table = DbfTable.Open(stream, leaveOpen: true, memoStream: memoStream, allowUnmaintainedIndexes: true))
        {
            Assert.Equal(format, table.MemoFormat);
            expected = table.Read(0).Values.ToArray();
            var memoIndex = table.Fields.Select((f, i) => (f, i)).First(x => x.f.Type == 'M').i;
            expected[memoIndex] = note;
            table.Append(expected);
        }

        // Every value comes back, including record 0's other memos, now copied into new blocks.
        using var reopened = DbfTable.Open(stream, leaveOpen: true, memoStream: memoStream);
        Assert.Equal(expected, reopened.Read(reopened.RecordCount - 1).Values);
    }

    [Fact]
    public void Missing_memo_file_reads_memos_as_null_when_asked()
    {
        using var table = DbfTable.Open(Path.Combine(FixtureRoot, "dbfread/no_memofile.dbf"), ignoreMissingMemo: true);

        Assert.Null(table.MemoFormat);
        Assert.Null(table.Read(0)["MEMO"]);
    }

    [Fact]
    public void Windows_1251_text_decodes_from_the_language_driver()
    {
        using var table = OpenFixture("infused-dbf/cp1251.dbf");

        Assert.Equal(1251, table.Encoding.CodePage);
        Assert.Equal("амбулаторно-поликлиническое", table.Read(0)["NAME"]);
    }

    [Fact]
    public void Non_ASCII_field_names_decode_with_an_explicit_encoding()
    {
        // Byte 29 is 0xF0 (not a real driver); the names and values are UTF-8.
        using var table = OpenFixture("infused-dbf/dbase_03_cyrillic.dbf", System.Text.Encoding.UTF8);

        Assert.Equal("ШАР", table.Fields[0].Name);
        Assert.Equal("Номер", table.Read(0)[0]);
    }

    [Fact]
    public void Census_TIGER_file_uses_its_cpg_sidecar()
    {
        using var table = OpenFixture("census-tiger/tl_2024_11_place.dbf");
        var dc = table.Read(0);

        Assert.Equal(65001, table.Encoding.CodePage); // UTF-8, from the .cpg
        Assert.Equal("11", dc["STATEFP"]);
        Assert.Equal("1150000", dc["GEOID"]);
        Assert.Equal("Washington", dc["NAME"]);
    }

    [Fact]
    public void Attribute_less_shapefile_table_has_no_fields_and_one_record()
    {
        using var table = OpenFixture("infused-dbf/polygon.dbf");

        Assert.Empty(table.Fields);
        Assert.Equal(1, table.RecordCount);
        Assert.Empty(table.Read(0).Values);
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
