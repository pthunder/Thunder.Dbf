//     _____________
//    | a,"b,c",d   |     ProjectThunder :: Thunder.Dbf tests
//    | "say ""hi"""|     Every CSV quirk Excel, Access and a decade of
//    | 007,1/2/1997|     hand-edited exports can throw at the importer.
//    |_____________|

using System.Text;

namespace Thunder.Dbf.Tests;

public sealed class DbfImportTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-import-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Csv(string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir.FullName, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private string Dbf(string name = "out") => Path.Combine(_dir.FullName, name + ".dbf");

    [Fact]
    public void Types_and_widths_come_from_the_data()
    {
        var plan = DbfImport.Plan(Csv("Name,Qty,Price,Joined,Active,Zip\nSmith,3,1.5,1997-04-12,Y,02134\nJones,-12,10.25,2001-01-02,n,90210\n"));

        Assert.Equal(["C(5)", "N(3,0)", "N(5,2)", "D", "L", "C(5)"], plan.Columns.Select(c => Shape(c.Field)));
        Assert.Equal(2, plan.RowCount);
    }

    [Fact]
    public void Quoted_commas_quotes_and_newlines_survive()
    {
        var csv = Csv("Name,Note\r\n\"Smith, Jane\",\"She said \"\"hi\"\"\r\nthen left\"\r\nJones,plain\r\n");
        DbfImport.FromCsv(csv, Dbf());

        using var table = DbfTable.Open(Dbf());
        Assert.Equal("Smith, Jane", table.Read(0)["NAME"]);
        Assert.Equal("She said \"hi\"\r\nthen left", table.Read(0)["NOTE"]);
        Assert.Equal("plain", table.Read(1)["NOTE"]);
    }

    [Theory]
    [InlineData("a;b\n1;2\n", ';')]
    [InlineData("a\tb\n1\t2\n", '\t')]
    [InlineData("a|b\n1|2\n", '|')]
    [InlineData("a,b\n1,2\n", ',')]
    public void Delimiters_are_detected(string content, char expected)
    {
        Assert.Equal(expected, DbfImport.Plan(Csv(content)).Delimiter);
    }

    [Fact]
    public void Byte_order_marks_and_accents_are_handled()
    {
        var csv = Csv("City\nMontréal\nZürich\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        DbfImport.FromCsv(csv, Dbf());

        using var table = DbfTable.Open(Dbf());
        Assert.Equal("CITY", table.Fields[0].Name); // the BOM didn't leak into the name
        Assert.Equal(["Montréal", "Zürich"], table.ReadAll().Select(r => r["CITY"]));
    }

    [Fact]
    public void Header_names_become_valid_unique_field_names()
    {
        var plan = DbfImport.Plan(Csv("Customer Name,customer-name,2nd Address,,Prénom\nx,y,z,w,v\n"));

        Assert.Equal(["CUSTOMER_N", "CUSTOMER_2", "F2ND_ADDRE", "F4", "PRENOM"], plan.Columns.Select(c => c.Field.Name));
        Assert.Contains(plan.Warnings, w => w.Contains("CUSTOMER_N"));
    }

    [Fact]
    public void Long_text_becomes_a_memo()
    {
        var longText = new string('x', 300);
        var csv = Csv($"Id,Story\n1,{longText}\n2,short\n");
        var plan = DbfImport.FromCsv(csv, Dbf());

        Assert.Equal('M', plan.Columns[1].Field.Type);
        using var table = DbfTable.Open(Dbf());
        Assert.Equal(longText, table.Read(0)["STORY"]);
    }

    [Fact]
    public void Empty_cells_become_blanks_and_empty_columns_stay_text()
    {
        var csv = Csv("Qty,Joined,Nothing\n5,,\n,2001-01-02,\n");
        var plan = DbfImport.FromCsv(csv, Dbf());

        Assert.Equal(["N(1,0)", "D", "C(1)"], plan.Columns.Select(c => Shape(c.Field)));
        using var table = DbfTable.Open(Dbf());
        Assert.Equal([5m, null, ""], table.Read(0).Values); // character fields read back as ""
        Assert.Equal([null, new DateOnly(2001, 1, 2), ""], table.Read(1).Values);
    }

    [Fact]
    public void US_dates_and_custom_formats_are_recognised()
    {
        Assert.Equal('D', DbfImport.Plan(Csv("D\n4/12/1997\n12/31/2001\n")).Columns[0].Field.Type);

        var european = new DbfImportOptions { DateFormats = ["dd.MM.yyyy"] };
        Assert.Equal('D', DbfImport.Plan(Csv("D\n12.04.1997\n"), european).Columns[0].Field.Type);
    }

    [Fact]
    public void Shape_only_creates_an_empty_table()
    {
        DbfImport.FromCsv(Csv("Name,Qty\nSmith,3\n"), Dbf(), new DbfImportOptions { ShapeOnly = true });

        using var table = DbfTable.Open(Dbf());
        Assert.Equal(0, table.RecordCount);
        Assert.Equal(["NAME", "QTY"], table.Fields.Select(f => f.Name));
    }

    [Fact]
    public void Overrides_force_a_column_type()
    {
        var options = new DbfImportOptions
        {
            Overrides = new Dictionary<string, DbfFieldDefinition> { ["Code"] = DbfFieldDefinition.Character("X", 10) },
        };
        var plan = DbfImport.FromCsv(Csv("Code\n123\n456\n"), Dbf(), options);

        Assert.Equal("C(10)", Shape(plan.Columns[0].Field));
        Assert.Equal("CODE", plan.Columns[0].Field.Name);
        using var table = DbfTable.Open(Dbf());
        Assert.Equal("123", table.Read(0)["CODE"]);
    }

    [Fact]
    public void Ragged_rows_are_padded_and_flagged()
    {
        var plan = DbfImport.FromCsv(Csv("A,B\n1\n2,3,4\n"), Dbf());

        Assert.Contains(plan.Warnings, w => w.Contains("more values"));
        using var table = DbfTable.Open(Dbf());
        Assert.Equal([1m, null], table.Read(0).Values);
        Assert.Equal([2m, 3m], table.Read(1).Values);
    }

    [Fact]
    public void Unclosed_quotes_are_explained()
    {
        var error = Assert.Throws<DbfException>(() => DbfImport.Plan(Csv("A\n\"never closed\n")));
        Assert.Contains("never closes", error.Message);
    }

    [Fact]
    public void Values_that_do_not_fit_name_the_row_and_column()
    {
        var options = new DbfImportOptions
        {
            Overrides = new Dictionary<string, DbfFieldDefinition> { ["Qty"] = DbfFieldDefinition.Numeric("Q", 2) },
        };
        var error = Assert.Throws<DbfException>(() => DbfImport.FromCsv(Csv("Qty\n5\n12345\n"), Dbf(), options));

        Assert.Contains("Row 3", error.Message);
        Assert.Contains("'Qty'", error.Message);
    }

    [Fact]
    public void Missing_csv_files_are_explained()
    {
        Assert.Equal(DbfErrorCode.TableNotFound,
            Assert.Throws<DbfException>(() => DbfImport.Plan(Path.Combine(_dir.FullName, "nope.csv"))).Code);
    }

    [Fact]
    public void Export_and_import_round_trip()
    {
        var csv = Csv("Name,Qty,Joined,Active,Note\n\"Smith, Jane\",3,1997-04-12,T,\"two\nlines\"\nJones,,,,\n");
        DbfImport.FromCsv(csv, Dbf("first"));

        var exported = Path.Combine(_dir.FullName, "exported.csv");
        using (var table = DbfTable.Open(Dbf("first")))
            Assert.Equal(2, DbfExport.ToCsv(table, exported));

        DbfImport.FromCsv(exported, Dbf("second"));
        using var first = DbfTable.Open(Dbf("first"));
        using var second = DbfTable.Open(Dbf("second"));
        Assert.Equal(first.ReadAll().Select(r => r.Values), second.ReadAll().Select(r => r.Values));
    }

    private static string Shape(DbfFieldDefinition f) => f.Type switch
    {
        'N' => $"N({f.Length},{f.DecimalCount})",
        'C' => $"C({f.Length})",
        _ => f.Type.ToString(),
    };
}
