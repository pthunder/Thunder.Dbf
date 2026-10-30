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
            Assert.Throws<NotSupportedException>(() => DbfTable.Open(path));
            return;
        }

        using var table = DbfTable.Open(path);
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
        Assert.IsType<int>(record[11]); // memo pointer into dbase_83.dbt
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
    [InlineData("infused-dbf/dbase_31.dbf")]
    [InlineData("infused-dbf/dbase_30.dbf")]
    [InlineData("infused-dbf/dbase_83.dbf")]
    [InlineData("dbfdatareader/foxpro_currency_01.dbf")]
    public void Rewriting_records_with_their_own_values_changes_no_bytes(string fixture)
    {
        var original = File.ReadAllBytes(Path.Combine(FixtureRoot, fixture));
        using var stream = new MemoryStream();
        stream.Write(original);

        using (var table = DbfTable.Open(stream, leaveOpen: true))
        {
            foreach (var record in table.ReadAll().ToList())
                table.Update(record.Index, [.. record.Values]);
        }

        var rewritten = stream.ToArray();
        var header = DbfHeader.Read(new MemoryStream(original));
        // Only the last-update date (bytes 1-3) may change.
        Assert.Equal(original.AsSpan(header.HeaderLength).ToArray(), rewritten.AsSpan(header.HeaderLength).ToArray());
        Assert.Equal(original.AsSpan(4, header.HeaderLength - 4).ToArray(), rewritten.AsSpan(4, header.HeaderLength - 4).ToArray());
    }

    [Fact]
    public void Visual_FoxPro_currency_signs_match_upstream()
    {
        using var table = OpenFixture("dbfdatareader/foxpro_currency_01.dbf");
        Assert.Equal([20m, -20m], table.Read(0).Values);
    }

    [Fact]
    public void Visual_FoxPro_datetime_and_binary_memo_fields_decode()
    {
        using var table = OpenFixture("infused-dbf/dbase_30.dbf");

        Assert.Equal(34, table.RecordCount);
        Assert.Equal(145, table.Fields.Count);
        Assert.All(table.ReadAll(), r =>
        {
            Assert.True(r["CLASSES"] is null or int);
            Assert.True(r["FLAGDATE"] is null or DateTime);
        });
        Assert.Contains(table.ReadAll(), r => r["CLASSES"] is int);
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
