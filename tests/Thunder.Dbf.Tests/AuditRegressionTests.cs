//      LIKE '%50%'   ~  "50% off"        ProjectThunder :: Thunder.Dbf tests
//      TOTAL/QTY     ->  a number        What a read-through of the whole library found, one test
//      rebuild x 3   ->  same .mdx size  per defect. Each fails without its fix.

using System.Globalization;
using Thunder.Dbf.Data;

namespace Thunder.Dbf.Tests;

public sealed class AuditRegressionTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-audit-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string PathOf(string name) => Path.Combine(_dir.FullName, name);

    // ---- SQL ------------------------------------------------------------------------------

    [Theory]
    [InlineData("50% off", "%50%", true)]
    [InlineData("100% sure", "100%", true)]
    [InlineData("100% sure", "%sure", true)]
    [InlineData("a%b", "a%b", true)]
    [InlineData("a%b", "a_b", true)]
    [InlineData("%", "%", true)]
    [InlineData("50% off", "%60%", false)]
    [InlineData("half off", "%50%", false)]
    public void Like_takes_a_percent_in_the_pattern_as_a_wildcard_whatever_the_text_holds(string text, string pattern, bool expected) =>
        Assert.Equal(expected, DbfValues.Like(text, pattern));

    [Fact]
    public void A_reader_gives_nullable_dates_and_keeps_HasRows_after_the_last_row()
    {
        using (var table = DbfTable.Create(PathOf("sale.dbf"), DbfTableFormat.DbaseIV,
                   [DbfFieldDefinition.Character("NOTE", 20), DbfFieldDefinition.Date("SOLD")]))
        {
            table.Append("50% off", new DateOnly(2024, 1, 15));
            table.Append("half off", null);
        }

        using var connection = new DbfConnection($"Data Source={_dir.FullName}");
        connection.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT NOTE FROM sale WHERE NOTE LIKE '%50%'";
        Assert.Equal("50% off", command.ExecuteScalar());

        command.CommandText = "SELECT SOLD FROM sale";
        using var reader = command.ExecuteReader();
        Assert.True(reader.HasRows);
        Assert.True(reader.Read());
        Assert.Equal(new DateOnly(2024, 1, 15), reader.GetFieldValue<DateOnly?>(0));
        Assert.Equal(new DateOnly(2024, 1, 15), reader.GetFieldValue<DateOnly>(0));
        Assert.True(reader.Read());
        Assert.Null(reader.GetFieldValue<DateOnly?>(0));
        Assert.False(reader.Read());
        Assert.True(reader.HasRows); // the result had rows; other providers keep saying so
    }

    [Fact]
    public void HasRows_is_false_for_a_result_with_no_rows()
    {
        using (DbfTable.Create(PathOf("none.dbf"), DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NOTE", 5)]))
        {
        }
        using var connection = new DbfConnection($"Data Source={_dir.FullName}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NOTE FROM none";
        using var reader = command.ExecuteReader();
        Assert.False(reader.HasRows);
        Assert.False(reader.Read());
        Assert.False(reader.HasRows);
    }

    // ---- values ---------------------------------------------------------------------------

    [Theory]
    [InlineData("895")]   // Kamenicky: a number in range that .NET has no code page for
    [InlineData("1234")]
    [InlineData("nonsense")]
    public void A_cpg_file_naming_an_encoding_that_isnt_there_falls_back_to_the_header(string label)
    {
        var path = PathOf("t.dbf");
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 10)]))
            table.Append("x");
        File.WriteAllText(Path.ChangeExtension(path, ".cpg"), label);

        using var opened = DbfTable.Open(path);
        Assert.Equal(1252, opened.Encoding.CodePage);
        Assert.Equal("x", opened.Read(0)["NAME"]);
    }

    [Fact]
    public void A_double_keeps_every_digit_in_a_numeric_field()
    {
        using var table = DbfTable.Create(PathOf("n.dbf"), DbfTableFormat.DbaseIV,
            [DbfFieldDefinition.Numeric("BIG", 20), DbfFieldDefinition.Numeric("SMALL", 20, 17)]);
        table.Append(9007199254740992d, 0.1234567890123456d); // 2^53: sixteen digits, exact in a double
        table.Append(0.1f, 0.1d);
        table.Append(-123456789012345678d, 1e-17d);

        Assert.Equal(9007199254740992m, table.Read(0)["BIG"]);
        Assert.Equal(0.1234567890123456m, table.Read(0)["SMALL"]);
        Assert.Equal(0m, table.Read(1)["BIG"]); // 0.1 rounds to the field's no decimals
        Assert.Equal(0.1m, table.Read(1)["SMALL"]);
        Assert.Equal(decimal.Parse((-123456789012345678d).ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
            table.Read(2)["BIG"]);
        Assert.Equal(0.00000000000000001m, table.Read(2)["SMALL"]);
    }

    public sealed class Person
    {
        public string? Note { get; set; }
        public DateTime Hired { get; set; }
        public DateOnly Left { get; set; }
        public char Code { get; set; }
    }

    [Fact]
    public void Reading_a_record_and_updating_it_leaves_its_blank_fields_blank()
    {
        var path = PathOf("p.dbf");
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV,
                   [DbfFieldDefinition.Character("NOTE", 10), DbfFieldDefinition.Date("HIRED"), DbfFieldDefinition.Date("LEFT"), DbfFieldDefinition.Character("CODE", 1)]))
        {
            table.Append("a", null, null, "");
            var before = table.Read(0);
            var person = table.Read<Person>(0);
            Assert.Equal(default, person.Hired); // a blank reads as the type's default
            person.Note = "b";
            table.UpdateRecord(0, person);

            Assert.Equal("b", table.Read(0)["NOTE"]);
            Assert.Null(table.Read(0)["HIRED"]);
            Assert.Null(table.Read(0)["LEFT"]);

            // And a new record with the properties unset.
            table.AppendRecord(new Person { Note = "c" });
            Assert.Null(table.Read(1)["HIRED"]);
            _ = before;
        }

        // The stored bytes: spaces, not 00010101 and not a NUL.
        var bytes = File.ReadAllBytes(path);
        using var check = DbfTable.Open(path);
        var record = bytes.AsSpan(check.Header.HeaderLength, check.Header.RecordLength);
        Assert.Equal(" b                          "u8.ToArray(), record.ToArray());
    }

    // ---- index expressions ----------------------------------------------------------------

    [Fact]
    public void A_key_that_divides_by_a_field_still_has_a_type()
    {
        using var table = DbfTable.Create(PathOf("d.dbf"), DbfTableFormat.DbaseIV,
            [DbfFieldDefinition.Numeric("TOTAL", 8, 2), DbfFieldDefinition.Numeric("QTY", 5)]);

        // On a blank record QTY is 0. That used to escape as a DivideByZeroException and stop the
        // index, and a writable open of its table, from opening.
        Assert.Equal('N', IndexKeys.InferKeyType("TOTAL/QTY", table, fallback: 'C'));
        Assert.Equal('C', IndexKeys.InferKeyType("STR(TOTAL/QTY,10,2)", table, fallback: 'L'));
        Assert.Equal('C', IndexKeys.InferKeyType("100/(QTY-QTY)", table, fallback: 'C')); // zero whatever the numbers: the fallback
    }

    [Theory]
    [InlineData("QTY=1.AND.OK", true)]
    [InlineData("QTY=2.OR.OK", true)]
    [InlineData("QTY=2.AND..NOT.OK", false)]
    [InlineData("QTY>0.AND.OK", true)]
    [InlineData("QTY=1.0.AND.OK", true)]
    [InlineData("QTY<1.5", true)]
    [InlineData("QTY>.5", true)]
    [InlineData("QTY=1.", true)]
    public void A_number_right_before_a_dotted_operator_is_just_the_number(string expression, bool expected)
    {
        using var table = DbfTable.Create(PathOf("e.dbf"), DbfTableFormat.DbaseIV,
            [DbfFieldDefinition.Numeric("QTY", 5), DbfFieldDefinition.Logical("OK")]);
        table.Append(1, true);
        Assert.Equal(expected, DbfExpression.Parse(expression).Evaluate(table.Read(0)));
    }

    // ---- FoxPro .cdx, read ----------------------------------------------------------------

    private DbfTable OpenKinds()
    {
        foreach (var file in new[] { "kinds.DBF", "kinds.FPT", "kinds.CDX" })
            File.Copy(Path.Combine(Fixtures.Root, "vfp9", file), PathOf(file));
        return DbfTable.Open(PathOf("kinds.DBF"));
    }

    [Fact]
    public void A_currency_tag_Visual_FoxPro_made_is_read_and_sought_by_its_values()
    {
        using var table = OpenKinds();
        using var cdx = table.OpenStructuralIndex()!;
        var tag = cdx["PRICE"];
        var records = table.ReadAll().ToList();

        // Every key is the record's own price, in order.
        var entries = tag.Entries().ToList();
        Assert.Equal(records.Count, entries.Count);
        Assert.All(entries, e => Assert.Equal(records[e.RecordNumber - 1]["PRICE"], e.Key));
        Assert.Equal(entries.Select(e => (decimal)e.Key!).Order(), entries.Select(e => (decimal)e.Key!));

        // And each record is found by it.
        foreach (var record in records)
            Assert.Contains(tag.SeekExact(record["PRICE"]), e => e.RecordNumber == record.RecordNumber);
        Assert.Empty(tag.SeekExact(123456.7891m));
    }

    [Fact]
    public void A_tag_on_a_nullable_field_is_read_and_sought_by_its_values_and_by_null()
    {
        using var table = OpenKinds();
        using var cdx = table.OpenStructuralIndex()!;
        var tag = cdx["OPT"];
        var records = table.ReadAll().ToList();
        var opt = table.Fields.ToList().FindIndex(f => f.Name == "OPT");

        var entries = tag.Entries().ToList();
        Assert.Equal(records.Count, entries.Count);
        Assert.All(entries, e =>
        {
            var record = records[e.RecordNumber - 1];
            Assert.Equal(record.IsNull(opt) ? null : record["OPT"], e.Key);
        });

        var nulls = records.Where(r => r.IsNull(opt)).Select(r => r.RecordNumber).ToList();
        Assert.NotEmpty(nulls);
        Assert.Equal(nulls, tag.SeekExact(null).Select(e => e.RecordNumber).Order());

        foreach (var record in records.Where(r => !r.IsNull(opt)))
        {
            var hits = tag.SeekExact(record["OPT"]).ToList();
            Assert.Contains(hits, e => e.RecordNumber == record.RecordNumber);
            // A prefix match, as a character seek is: "O4" also finds "O41".
            Assert.All(hits, e => Assert.StartsWith((string)record["OPT"]!, (string)e.Key!));
        }
    }

    // ---- .mdx rebuild ---------------------------------------------------------------------

    [Fact]
    public void Rebuilding_an_mdx_reuses_its_pages_instead_of_growing_the_file()
    {
        var path = PathOf("big.dbf");
        const int count = 1500;
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV,
                   [DbfFieldDefinition.Character("NAME", 20), DbfFieldDefinition.Numeric("QTY", 8)]))
        {
            table.FlushWritesToDisk = false;
            table.AppendRange(Enumerable.Range(0, count).Select(i => new object?[] { "Name" + (i * 7919 % count), i }));
        }

        var mdx = Path.ChangeExtension(path, ".mdx");
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            table.FlushWritesToDisk = false;
            table.CreateIndexTag("NAME", "NAME");
            table.CreateIndexTag("QTY", "QTY");
            var built = new FileInfo(mdx).Length;

            for (var i = 0; i < 3; i++)
            {
                table.RebuildIndex();
                Assert.Equal(built, new FileInfo(mdx).Length);
            }

            // Fewer keys: the pages left over go on the free list, and the file is no bigger.
            for (var i = 0; i < count; i += 2)
                table.Delete(i);
            Assert.Equal(count / 2, table.Pack());
            Assert.Equal(built, new FileInfo(mdx).Length);
            table.RebuildIndex();
            Assert.Equal(built, new FileInfo(mdx).Length);
        }

        // The rebuilt index is whole: every record is found by both tags, in order.
        using var reopened = DbfTable.Open(path, writable: true);
        Assert.Equal(count / 2, reopened.RecordCount);
        using var index = reopened.OpenProductionIndex()!;
        Assert.Equal(reopened.ReadAll().Select(r => (string)r["NAME"]!).Order(StringComparer.Ordinal),
            index["NAME"].Entries().Select(e => (string)e.Key!));
        Assert.Equal(Enumerable.Range(0, count).Where(i => i % 2 == 1).Select(i => (decimal)i),
            index["QTY"].Entries().Select(e => Convert.ToDecimal(e.Key, CultureInfo.InvariantCulture)));
        foreach (var record in reopened.ReadAll())
            Assert.Equal(record.RecordNumber, reopened.Seek(index["NAME"], record["NAME"]!)!.RecordNumber);
        reopened.Append("Name-new", 99999); // and it takes writes
        using var after = reopened.OpenProductionIndex()!;
        Assert.NotNull(reopened.Seek(after["NAME"], "Name-new"));
    }
}
