//     _______
//    | o   o |     ProjectThunder :: Thunder.Dbf tests
//    |   ^   |     Round three: the review of connections, errors, import
//    |  '-'  |     and export (5209ce3). Safety first: the goal is web apps.
//    |_______|

using System.Buffers.Binary;
using System.Text;

namespace Thunder.Dbf.Tests;

public sealed class ConnectionRegressionTests : IDisposable
{
    private static readonly string FixtureRoot = FindFixtureRoot();
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("thunder-review-");
    private readonly string _data;
    private readonly string _outside;

    public ConnectionRegressionTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root.FullName, "data")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_root.FullName, "private")).FullName;
        foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT", "CHANGELOG.MDX" })
            File.Copy(Path.Combine(FixtureRoot, "siemens-step7/siprom", file), Path.Combine(_data, file));
        File.Copy(Path.Combine(FixtureRoot, "infused-dbf/dbase_03.dbf"), Path.Combine(_data, "plain.dbf"));
        File.Copy(Path.Combine(FixtureRoot, "infused-dbf/dbase_03.dbf"), Path.Combine(_outside, "secret.dbf"));
    }

    public void Dispose()
    {
        foreach (var file in _root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows() && file.LinkTarget is null)
                File.SetUnixFileMode(file.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _root.Delete(recursive: true);
    }

    // #1, #2: links of every kind are refused, not followed and judged.
    [Fact]
    public void Links_through_a_parent_folder_are_refused()
    {
        Directory.CreateSymbolicLink(Path.Combine(_data, "bridge"), _outside);
        File.CreateSymbolicLink(Path.Combine(_data, "clients.dbf"), Path.Combine(_data, "bridge", "secret.dbf"));
        using var db = DbfDatabase.Open(_data);

        Assert.Equal(DbfErrorCode.PathEscapesFolder, Assert.Throws<DbfException>(() => db["clients"]).Code);
    }

    [Fact]
    public void Linked_companion_files_are_refused()
    {
        File.Move(Path.Combine(_data, "CHANGELOG.DBT"), Path.Combine(_outside, "CHANGELOG.DBT"));
        File.CreateSymbolicLink(Path.Combine(_data, "CHANGELOG.DBT"), Path.Combine(_outside, "CHANGELOG.DBT"));
        using var db = DbfDatabase.Open(_data);

        Assert.Equal(DbfErrorCode.PathEscapesFolder, Assert.Throws<DbfException>(() => db["changelog"]).Code);
    }

    [Fact]
    public void An_index_swapped_for_a_link_after_opening_is_refused()
    {
        using var db = DbfDatabase.Open(_data);
        var table = db["changelog"];

        File.Move(Path.Combine(_data, "CHANGELOG.MDX"), Path.Combine(_outside, "CHANGELOG.MDX"));
        File.CreateSymbolicLink(Path.Combine(_data, "CHANGELOG.MDX"), Path.Combine(_outside, "CHANGELOG.MDX"));

        Assert.Equal(DbfErrorCode.PathEscapesFolder, Assert.Throws<DbfException>(() => table.OpenProductionIndex()).Code);
    }

    [Fact]
    public void A_data_folder_reached_through_a_link_still_works()
    {
        var alias = Path.Combine(_root.FullName, "alias");
        Directory.CreateSymbolicLink(alias, _data);
        using var db = DbfDatabase.Open(alias);

        Assert.Equal(2, db.TableNames.Count);
        Assert.NotNull(db["plain"].Read(0));
    }

    // #3: export never overwrites table files.
    [Theory]
    [InlineData("plain.dbf")]
    [InlineData("PLAIN.DBF")]
    [InlineData("CHANGELOG.DBT")]
    [InlineData("CHANGELOG.mdx")]
    public void Export_refuses_to_overwrite_table_files(string destination)
    {
        using var table = DbfTable.Open(Path.Combine(_data, "plain.dbf"));
        var before = SharedFile.ReadAllBytes(Path.Combine(_data, "plain.dbf"));

        Assert.Throws<DbfException>(() => DbfExport.ToCsv(table, Path.Combine(_data, destination)));
        Assert.Equal(before, SharedFile.ReadAllBytes(Path.Combine(_data, "plain.dbf")));
    }

    [Fact]
    public void Export_leaves_no_temporary_files()
    {
        using var table = DbfTable.Open(Path.Combine(_data, "plain.dbf"));
        DbfExport.ToCsv(table, Path.Combine(_root.FullName, "out.csv"));

        Assert.Equal(["out.csv"], _root.EnumerateFiles().Select(f => f.Name));
    }

    // #4: spreadsheet formula injection.
    [Fact]
    public void Export_neutralises_formulas_but_not_numbers()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Character("NOTE", 20), DbfFieldDefinition.Numeric("AMT", 6, 2)]);
        table.Append("=HYPERLINK(\"x\")", -20m);
        table.Append("@SUM(A1)", 5m);

        var safe = new StringWriter();
        DbfExport.ToCsv(table, safe);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\",-20.00", safe.ToString());
        Assert.Contains("'@SUM(A1),5.00", safe.ToString());

        var raw = new StringWriter();
        DbfExport.ToCsv(table, raw, new DbfExportOptions { SpreadsheetSafe = false });
        Assert.Contains("@SUM(A1),5.00", raw.ToString());
        Assert.DoesNotContain("'@", raw.ToString());
    }

    // #6: partial decimal underflow.
    [Fact]
    public void Floats_decimal_would_round_come_back_as_double()
    {
        using var stream = Copy("infused-dbf/dbase_8b.dbf");
        using var memo = Copy("infused-dbf/dbase_8b.dbt");
        using var table = DbfTable.Open(stream, leaveOpen: true, memoStream: memo);
        var field = table.Fields.Single(f => f.Name == "FLOAT");
        var target = stream.GetBuffer().AsSpan(table.Header.HeaderLength + field.Offset, field.Length);
        target.Fill((byte)' ');
        "1.5E-28"u8.CopyTo(target[^7..]);

        Assert.Equal(1.5e-28, table.Read(0)["FLOAT"]);
    }

    // #7: lazy evaluation.
    [Theory]
    [InlineData("IIF(.T., 1, 1 / 0) = 1")]
    [InlineData(".NOT. (.F. .AND. (1 / 0 = 0))")]
    [InlineData(".T. .OR. (1 / 0 = 0)")]
    public void Guards_keep_unused_branches_from_running(string expression)
    {
        Assert.Equal(true, DbfExpression.Parse(expression).Evaluate(AnyRecord()));
    }

    // Clipper and Harbour programs write index keys like INDEX ON FIELD->ID: FIELD-> (or _FIELD->)
    // means this work area's field. Found by a Harbour-built .ntx on Windows.
    [Fact]
    public void FIELD_arrow_names_this_tables_own_field()
    {
        using var stream = Copy("infused-dbf/dbase_31.dbf");
        using var table = DbfTable.Open(stream, leaveOpen: true);
        var row = table.Read(0);
        Assert.Equal(DbfExpression.Parse("PRODUCTID").Evaluate(row), DbfExpression.Parse("FIELD->PRODUCTID").Evaluate(row));
        Assert.Equal(DbfExpression.Parse("STR(PRODUCTID, 5) + QUANTITYPE").Evaluate(row),
            DbfExpression.Parse("STR(_field->ProductID, 5) + Field->QUANTITYPE").Evaluate(row));
        Assert.Equal(["PRODUCTID", "QUANTITYPE"], DbfExpression.FieldNamesIn("FIELD->PRODUCTID + FIELD->QUANTITYPE").Select(n => n.ToUpperInvariant()).Order());
        Assert.Throws<NotSupportedException>(() => DbfExpression.Parse("M->PRODUCTID"));
        Assert.Throws<NotSupportedException>(() => DbfExpression.Parse("CUSTOMER->NAME"));
    }

    // #8: nullable is not the same as null.
    [Fact]
    public void Blank_nullable_fields_are_blank_not_null()
    {
        using var stream = Copy("infused-dbf/dbase_31.dbf");
        using var table = DbfTable.Open(stream, leaveOpen: true);
        var quantity = table.Fields.Single(f => f.Name == "QUANTITYPE");
        var flags = table.Fields.Single(f => f.Type == '0');
        var row = stream.GetBuffer().AsSpan(table.Header.HeaderLength);
        row.Slice(quantity.Offset, quantity.Length).Fill((byte)' ');

        Assert.True(quantity.IsNullable);
        Assert.Equal(new string(' ', 20), DbfExpression.Parse("QUANTITYPE").Evaluate(table.Read(0)));
        Assert.False(table.Read(0).IsNull(4));

        row[flags.Offset] |= (byte)(1 << quantity.NullBit!.Value);
        Assert.Null(DbfExpression.Parse("QUANTITYPE").Evaluate(table.Read(0)));
        Assert.True(table.Read(0).IsNull(4));
    }

    // #9: .cpg sidecars count as companions.
    [Fact]
    public void Create_refuses_a_stale_code_page_sidecar()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "people.cpg"), "UTF-8");
        var error = Assert.Throws<DbfException>(() => DbfTable.Create(Path.Combine(_root.FullName, "people.dbf"), [DbfFieldDefinition.Character("N", 5)]));
        Assert.Equal(DbfErrorCode.TableAlreadyExists, error.Code);
    }

    // #10: unrepresentable and undecodable text is an error, never '?'.
    [Fact]
    public void Characters_the_code_page_lacks_are_reported()
    {
        var csv = WriteCsv("Name\nSmith\n東京\n");
        var error = Assert.Throws<DbfException>(() => DbfImport.Plan(csv));

        Assert.Equal(DbfErrorCode.InvalidValue, error.Code);
        Assert.Equal(422, error.HttpStatus);
        Assert.Contains("Row 3", error.Message);
        Assert.Contains("'Name'", error.Message);
    }

    [Fact]
    public void Invalid_UTF8_in_a_CSV_is_reported()
    {
        var csv = Path.Combine(_root.FullName, "bad.csv");
        SharedFile.WriteAllBytes(csv, [.. "Name\nCaf"u8, 0xE9, (byte)'\n']); // Windows-1252 é in a "UTF-8" file
        var error = Assert.Throws<DbfException>(() => DbfImport.Plan(csv));

        Assert.Equal(DbfErrorCode.InvalidCsv, error.Code);
        Assert.Equal(400, error.HttpStatus);
        Assert.Contains("CsvEncoding", error.Hint);
        Assert.Equal('C', DbfImport.Plan(csv, new DbfImportOptions { CsvEncoding = DbfEncoding.GetEncoding(1252) }).Columns[0].Field.Type);
    }

    [Fact]
    public void Writing_text_the_table_cannot_hold_fails_instead_of_storing_question_marks()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Character("NAME", 10)]);

        Assert.Throws<ArgumentException>(() => table.Append("東京"));
        Assert.Throws<EncoderFallbackException>(() => DbfEncoding.Strict(MazoviaEncoding.Instance).GetBytes("東"));
    }

    // #11, #12, #13, #14: CSV details.
    [Fact]
    public void Delimiters_inside_quoted_headers_are_ignored()
    {
        var plan = DbfImport.Plan(WriteCsv("\"Last, First, Middle\";Age\n\"Doe, Jane, Q\";42\n"));
        Assert.Equal(';', plan.Delimiter);
        Assert.Equal(2, plan.Columns.Count);
    }

    [Theory]
    [InlineData("A\n\"\"\n")]
    [InlineData("A\n\"\"")]
    public void Quoted_empty_records_are_rows(string content)
    {
        Assert.Equal(1, DbfImport.Plan(WriteCsv(content)).RowCount);
    }

    [Fact]
    public void Text_keeps_tabs_and_trailing_line_breaks()
    {
        var csv = WriteCsv($"Note,Story\n\"x\t\",\"{new string('y', 300)}\n\"\n");
        var dbf = Path.Combine(_root.FullName, "text.dbf");
        DbfImport.FromCsv(csv, dbf);

        using var table = DbfTable.Open(dbf);
        Assert.Equal("x\t", table.Read(0)["NOTE"]);
        Assert.Equal(new string('y', 300) + "\n", table.Read(0)["STORY"]);
    }

    [Fact]
    public void Plus_signed_codes_with_leading_zeros_stay_text()
    {
        var plan = DbfImport.Plan(WriteCsv("Code\n+0007\n+0100\n"));
        Assert.Equal('C', plan.Columns[0].Field.Type);
    }

    // #17: failed imports leave nothing behind.
    [Fact]
    public void A_failed_import_leaves_no_table_and_can_be_retried()
    {
        var csv = WriteCsv("Qty\n5\n12345\n");
        var dbf = Path.Combine(_root.FullName, "qty.dbf");
        var tight = new DbfImportOptions { Overrides = new Dictionary<string, DbfFieldDefinition> { ["Qty"] = DbfFieldDefinition.Numeric("Q", 2) } };

        var error = Assert.Throws<DbfException>(() => DbfImport.FromCsv(csv, dbf, tight));
        Assert.Equal(DbfErrorCode.InvalidValue, error.Code);
        Assert.Equal([Path.GetFileName(csv)], _root.EnumerateFiles().Select(f => f.Name));

        DbfImport.FromCsv(csv, dbf);
        using var table = DbfTable.Open(dbf);
        Assert.Equal(2, table.RecordCount);
    }

    [Fact]
    public void AppendRange_reports_the_original_error_and_keeps_earlier_rows()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Numeric("Q", 2)]);
        object?[][] rows = [[1m], [2m], [12345m], [4m]];

        Assert.Throws<OverflowException>(() => table.AppendRange(rows));
        Assert.Equal(2, table.RecordCount);
        Assert.Equal([1m, 2m], table.ReadAll().Select(r => r["Q"]));
    }

    // #19: one unreadable companion doesn't stop the handshake.
    [Fact]
    public void An_unreadable_index_is_reported_and_the_rest_still_checked()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;
        File.SetUnixFileMode(Path.Combine(_data, "CHANGELOG.MDX"), UnixFileMode.None);
        using var db = DbfDatabase.Open(_data);

        var report = db.Check();

        Assert.Equal(DbfErrorCode.AccessDenied, report.Tables.Single(t => t.Name == "CHANGELOG").Problems.Single().Code);
        Assert.True(report.Tables.Single(t => t.Name == "plain").CanRead);
    }

    [Fact]
    public void A_tag_header_too_short_for_its_FOR_expression_is_not_a_crash()
    {
        // 512-byte blocks: the FOR expression's slot (byte 762) lies past the end of the tag header.
        var file = new byte[16 * 512];
        file[0] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), 512);
        file[26] = 32;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(28), 1);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(544), 4);
        "K"u8.CopyTo(file.AsSpan(548));
        var tag = file.AsSpan(4 * 512);
        BinaryPrimitives.WriteInt32LittleEndian(tag, 5);
        tag[9] = (byte)'N';
        BinaryPrimitives.WriteUInt16LittleEndian(tag[12..], 12);
        BinaryPrimitives.WriteUInt16LittleEndian(tag[14..], 31);
        BinaryPrimitives.WriteUInt16LittleEndian(tag[18..], 16);
        tag[245] = 1;

        using var index = MdxIndex.Open(new MemoryStream(file));
        Assert.Null(index["K"].ForExpression);
    }

    // ---- helpers --------------------------------------------------------------------------

    private string WriteCsv(string content)
    {
        var path = Path.Combine(_root.FullName, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static DbfRecord AnyRecord()
    {
        var table = DbfTable.Create(new MemoryStream(), [DbfFieldDefinition.Character("X", 1)]);
        table.Append("x");
        return table.Read(0);
    }

    private static MemoryStream Copy(string fixture)
    {
        var stream = new MemoryStream();
        stream.Write(SharedFile.ReadAllBytes(Path.Combine(FixtureRoot, fixture)));
        return stream;
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
