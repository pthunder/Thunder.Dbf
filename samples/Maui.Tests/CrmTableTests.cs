//     ____________
//    | CONTACTS   |   ProjectThunder :: Thunder.Dbf CRM sample tests
//    | [x] guess  |   Which field is which, text to values and back, and a table opened
//    |____________|   without its index staying read-only.

using Thunder.Dbf;
using ThunderDbfMaui;

public sealed class CrmTableTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("crm-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private IReadOnlyList<DbfField> FieldsOf(params DbfFieldDefinition[] fields)
    {
        var path = Path.Combine(_folder, $"t{Guid.NewGuid():N}"[..8] + ".dbf");
        using (DbfTable.Create(path, DbfTableFormat.VisualFoxPro, fields)) { }
        using var table = DbfTable.Open(path);
        return [.. table.Fields];
    }

    [Fact]
    public void Guesses_a_FoxPro_customer_table()
    {
        var map = DbfFieldMap.Guess(FieldsOf(
            DbfFieldDefinition.Character("CUSTNO", 6), DbfFieldDefinition.Character("CONTACT", 30),
            DbfFieldDefinition.Character("COMPNAME", 40), DbfFieldDefinition.Character("TEL", 16),
            DbfFieldDefinition.Date("NEXTCALL"), DbfFieldDefinition.Memo("COMMENTS")));

        Assert.Equal(("CONTACT", "COMPNAME", "TEL", "NEXTCALL", "COMMENTS"), (map.Name, map.Company, map.Phone, map.FollowUp, map.Notes));
        Assert.Null(map.Email);
    }

    [Fact]
    public void Matches_the_start_of_a_name_not_the_middle()
    {
        var map = DbfFieldMap.Guess(FieldsOf(
            DbfFieldDefinition.Character("NAME", 30), DbfFieldDefinition.Character("HOTEL", 30),
            DbfFieldDefinition.Character("TELNO", 16), DbfFieldDefinition.Character("MAILADDR", 40)));

        Assert.Equal("TELNO", map.Phone); // TEL starts TELNO; it's only inside HOTEL
        Assert.Null(map.Email);           // a postal MAILADDR isn't an email
    }

    [Fact]
    public void A_company_only_table_uses_the_company_as_the_title_once()
    {
        var map = DbfFieldMap.Guess(FieldsOf(DbfFieldDefinition.Character("COMPNAME", 40), DbfFieldDefinition.Character("CITY", 20)));

        Assert.Equal("COMPNAME", map.Name);
        Assert.Null(map.Company);
    }

    [Fact]
    public void Each_field_plays_one_part()
    {
        var map = DbfFieldMap.Guess(FieldsOf(DbfFieldDefinition.Character("FIRSTNAME", 20), DbfFieldDefinition.Character("LASTNAME", 20)));

        Assert.Equal(("FIRSTNAME", "LASTNAME"), (map.First, map.Last));
        Assert.Null(map.Name); // NAME doesn't start either one, and neither is taken twice
    }

    [Fact]
    public void Text_turns_into_each_field_type_and_back()
    {
        var fields = FieldsOf(
            DbfFieldDefinition.Numeric("QTY", 8, 2), DbfFieldDefinition.Integer("N"), DbfFieldDefinition.Currency("BAL"),
            DbfFieldDefinition.Double("RATE", 3), DbfFieldDefinition.Date("D"), DbfFieldDefinition.DateTime("T"),
            DbfFieldDefinition.Logical("OK"), DbfFieldDefinition.Character("C", 10));
        var samples = new[] { "12.50", "42", "1204.5", "0.125", "2026-11-18", "2026-11-18 09:41", "Y", "Maria" };

        foreach (var (field, text) in fields.Zip(samples))
            Assert.Equal(text, DbfFieldMap.ToText(DbfFieldMap.FromText(field, text)));
        Assert.Null(DbfFieldMap.FromText(fields[0], "   "));
    }

    [Fact]
    public void Bad_text_is_refused_before_anything_is_written()
    {
        var fields = FieldsOf(DbfFieldDefinition.Date("D"), DbfFieldDefinition.Numeric("N", 6));

        Assert.Throws<FormatException>(() => DbfFieldMap.FromText(fields[0], "next Tuesday"));
        Assert.Throws<FormatException>(() => DbfFieldMap.FromText(fields[1], "a dozen"));
    }

    [Fact]
    public void The_sample_lists_saves_and_deletes()
    {
        var crm = CrmTable.CreateSample(Path.Combine(_folder, "sample"));
        Assert.Equal(60, crm.List(null, dueOnly: false).Count);
        Assert.False(crm.ReadOnly);

        var index = crm.Save(null, new Dictionary<string, string> { ["NAME"] = "Hanna Moos", ["FOLLOWUP"] = "2000-01-01" });
        Assert.Contains(crm.List("hanna", dueOnly: true), r => r.Index == index && r.Due);

        crm.Delete(index);
        Assert.DoesNotContain(crm.List("hanna", dueOnly: false), r => r.Index == index);
    }

    [Fact]
    public void A_table_opened_without_its_cdx_is_read_only()
    {
        var path = Path.Combine(_folder, "CUSTOMER.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Character("CONTACT", 30)]))
            table.Append("Maria Anders");
        using (var table = DbfTable.Open(path, writable: true, exclusive: true, maintainFoxProIndexes: true))
            table.CreateIndexTag("CONTACT", "UPPER(contact)");
        File.Delete(Path.ChangeExtension(path, ".CDX"));

        var crm = CrmTable.Open(path);

        Assert.Equal(".cdx", crm.MissingIndex);
        Assert.Single(crm.List(null, dueOnly: false));
        Assert.Throws<InvalidOperationException>(() => crm.Save(0, new Dictionary<string, string> { ["CONTACT"] = "Changed" }));
        Assert.Throws<InvalidOperationException>(() => crm.Delete(0));
    }
}
