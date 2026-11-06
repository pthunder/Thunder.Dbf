//      DbfTable.Create  -->  .dbf + .fpt         --VFP 9 USEs, reads, appends, edits-->  same values?
//      CreateIndexTag   -->  .cdx                --VFP 9 walks every tag, edits-------->  same order? same as a rebuild?
//      Append (no _NullFlags value)              --VFP 9 reads----------------------->  .NULL. where it was given?
//      ModifyStructure + DropIndexTag            --VFP 9 reads memos, walks tags, edits->  still a table VFP keeps?
//
//      ProjectThunder :: Thunder.Dbf tests
//      Visual FoxPro 9 checks what Thunder.Dbf has learned to write since VFP last looked (0.2.0): new
//      tables in both FoxPro formats, a .cdx made from scratch, appends that leave _NullFlags to the
//      library, and the MODIFY STRUCTURE preview. Each test sets its table up first, so CI runs that
//      part, and stops there without THUNDER_VFP (see VisualFoxProHarness).

namespace Thunder.Dbf.Tests;

public sealed class VisualFoxProWritingTests : VisualFoxProHarness
{
    private static readonly DbfFieldDefinition[] VisualFoxProFields =
    [
        DbfFieldDefinition.Integer("ID"), DbfFieldDefinition.Character("NAME", 20), DbfFieldDefinition.Numeric("AMOUNT", 10, 2),
        DbfFieldDefinition.Currency("PRICE"), DbfFieldDefinition.Date("BORN"), DbfFieldDefinition.DateTime("STAMP"),
        DbfFieldDefinition.Logical("ACTIVE"), DbfFieldDefinition.Memo("NOTE"),
        DbfFieldDefinition.Character("NICK", 10) with { Nullable = true }, DbfFieldDefinition.Currency("RATE") with { Nullable = true },
        DbfFieldDefinition.DateTime("SEEN") with { Nullable = true }, DbfFieldDefinition.Logical("OK") with { Nullable = true },
    ];

    // FoxPro 2 has no integer, currency, datetime or null: a whole-number N field is the key instead.
    private static readonly DbfFieldDefinition[] FoxPro2Fields =
    [
        DbfFieldDefinition.Numeric("ID", 8), DbfFieldDefinition.Character("NAME", 20), DbfFieldDefinition.Numeric("AMOUNT", 10, 2),
        DbfFieldDefinition.Float("SCORE", 10, 3), DbfFieldDefinition.Date("BORN"), DbfFieldDefinition.Logical("ACTIVE"),
        DbfFieldDefinition.Memo("NOTE"),
    ];

    public static TheoryData<DbfTableFormat> Formats() => [DbfTableFormat.VisualFoxPro, DbfTableFormat.FoxPro2];

    private static DbfFieldDefinition[] FieldsFor(DbfTableFormat format) =>
        format == DbfTableFormat.VisualFoxPro ? VisualFoxProFields : FoxPro2Fields;

    // Mixed case and a letter outside ASCII, so UPPER() tags have something to fold.
    private static readonly string[] Names = ["ada", "Grace", "ZOË", "émile", "Bob", "alan", "Linus", "barbara", "Ken", "dennis"];

    // Record i's values, one per declared field: every kind of value, blanks, nulls in every third
    // record's nullable fields, and memos long enough to take several blocks.
    private static object?[] Row(IReadOnlyList<DbfFieldDefinition> fields, int i) => fields.Select<DbfFieldDefinition, object?>(field => field.Nullable && i % 3 == 1 ? null : field.Type switch
    {
        'I' => 1 + i,
        'N' when field.DecimalCount == 0 => 1 + i,
        'N' => (decimal?)Math.Round((i * 7919 % 100_000 - 40_000) / 7m, 2),
        'F' => (decimal?)Math.Round((i * 104_729 % 1_000_000 - 300_000) / 997m, 3),
        'C' when field.Name == "NAME" => $"{Names[i % Names.Length]} {i * 37 % 101}",
        'C' => $"n{i}",
        'Y' => i * 12.3456m - 100m,
        'D' => i % 7 == 0 ? null : new DateOnly(1950, 1, 1).AddDays(i * 397 % 30_000),
        'T' => i % 9 == 0 ? null : new DateTime(2026, 10, 31, 12, 0, 0).AddSeconds(-(i * 7_654_321L)),
        'L' => i % 2 == 0,
        'M' => i % 4 == 0 ? null : $"Memo {i}\r\nline two " + new string('m', i * 13),
        _ => throw new ArgumentException($"No sample value for a {field.Type} field."),
    }).ToArray();

    private string Create(DbfTableFormat format, string name, int records)
    {
        var path = Path.Combine(_dir.FullName, name + ".dbf");
        var fields = FieldsFor(format);
        using var table = DbfTable.Create(path, format, fields);
        table.AppendRange(Enumerable.Range(0, records).Select(i => Row(fields, i)));
        table.Delete(5);
        return path;
    }

    // 1. A table Thunder.Dbf creates: VFP USEs it and reads every value as written, appends and edits
    // through its own memo file, and Thunder.Dbf reads what VFP wrote; then once more the other way.
    [Theory]
    [MemberData(nameof(Formats))]
    public void VisualFoxPro_reads_and_edits_a_table_Thunder_creates(DbfTableFormat format)
    {
        var path = Create(format, format == DbfTableFormat.VisualFoxPro ? "created" : "created2", records: 40);
        if (!HaveVfp())
            return;

        AssertVfpReadsWhatThunderReads(path, "as created");
        Edit(path, nulls: format == DbfTableFormat.VisualFoxPro);
        AssertVfpReadsWhatThunderReads(path, "after VFP's edits");

        var fields = FieldsFor(format);
        using (var table = DbfTable.Open(path, writable: true))
        {
            for (var i = 1000; i < 1010; i++)
                table.Append(Row(fields, i));
            table.UpdateFields(2, new Dictionary<string, object?> { ["NAME"] = "Thunder again", ["NOTE"] = "Written after VFP's memos\r\n" + new string('t', 300) });
        }
        AssertVfpReadsWhatThunderReads(path, "after Thunder.Dbf's writes on top");
    }

    // 2. A structural .cdx Thunder.Dbf makes with CreateIndexTag on such a table, then keeps through
    // more writes: VFP walks every tag in the order the index holds, edits through it, and Thunder.Dbf
    // finds each tag as a rebuild makes it.
    [Theory]
    [MemberData(nameof(Formats))]
    public void VisualFoxPro_walks_and_keeps_a_cdx_Thunder_creates(DbfTableFormat format)
    {
        var path = Create(format, format == DbfTableFormat.VisualFoxPro ? "indexed" : "indexed2", records: 300);
        var fields = FieldsFor(format);
        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true))
        {
            var visualFoxPro = format == DbfTableFormat.VisualFoxPro;
            table.CreateIndexTag("ID", "id", null, unique: false, descending: false, candidate: visualFoxPro);
            table.CreateIndexTag("UNAME", "UPPER(name)");
            table.CreateIndexTag("NAMEBORN", "name+DTOS(born)");
            table.CreateIndexTag("BORN", "born");
            table.CreateIndexTag("AMTDESC", "amount", descending: true);
            table.CreateIndexTag("LIVE", "id", "active");
            if (visualFoxPro)
            {
                table.CreateIndexTag("PRICE", "price");
                table.CreateIndexTag("STAMP", "stamp");
                table.CreateIndexTag("NICK", "nick");
            }
            else
            {
                table.CreateIndexTag("SCORE", "score");
            }
            Assert.True(table.MaintainsIndex);

            // Writes after the tags exist go through the index writer: enough to split pages.
            for (var i = 300; i < 450; i++)
                table.Append(Row(fields, i));
            for (var i = 0; i < 40; i++)
                table.UpdateFields(i * 7 % 400, new Dictionary<string, object?> { ["NAME"] = $"{Names[(i + 3) % Names.Length]} upd {i}", ["AMOUNT"] = i * 3.5m });
            for (var i = 0; i < 20; i++)
                table.Delete(i * 13 % 420);
        }
        if (!HaveVfp())
            return;

        AssertVfpReadsWhatThunderReads(path, "with Thunder.Dbf's tags");
        AssertVfpWalksEveryTagAsTheIndexHolds(path);
        Edit(path, nulls: format == DbfTableFormat.VisualFoxPro);
        AssertEveryTagMatchesARebuild(path);
        AssertVfpReadsWhatThunderReads(path, "after VFP's edits");
    }

    // kinds.DBF and .FPT as Visual FoxPro 9 wrote them (OPT is nullable), with tags made here as kinds.prg
    // made them: its own .cdx has a GENERAL-collation tag, which Thunder.Dbf won't keep.
    private string Kinds(params string[] tags)
    {
        foreach (var file in new[] { "kinds.DBF", "kinds.FPT" })
            File.Copy(IndexTestSupport.Fixture("vfp9/" + file), Path.Combine(_dir.FullName, file));
        var path = Path.Combine(_dir.FullName, "kinds.DBF");
        var bytes = File.ReadAllBytes(path);
        bytes[28] &= unchecked((byte)~0x01);
        File.WriteAllBytes(path, bytes);
        using var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true);
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case "LIVE": table.CreateIndexTag("LIVE", "id", "status=\"A\""); break;
                case "ID": table.CreateIndexTag("ID", "id", null, unique: false, descending: false, candidate: true); break;
                case "UNAME": table.CreateIndexTag("UNAME", "UPPER(name)"); break;
                case "CODEWHEN": table.CreateIndexTag("CODEWHEN", "code+DTOS(when)"); break;
                default: table.CreateIndexTag(tag, tag.ToLowerInvariant()); break;
            }
        }
        return path;
    }

    // kinds.DBF's 13 declared fields; ID 1000 and up, so the candidate tag takes them.
    private static object?[] KindsRow(int i, bool nullOpt) =>
    [
        1000 + i, $"K{i:D5}", $"Kind {Names[i % Names.Length]}", i * 11.25m, i * 3.5m, new DateOnly(2000, 1, 1).AddDays(i),
        new DateTime(2026, 10, 31, 8, 30, 0).AddMinutes(i), i % 2 == 0, i * 0.5, (decimal)(i % 50), i % 3 == 0 ? "A" : "B",
        i % 4 == 0 ? null : $"kinds memo {i}", nullOpt ? null : $"opt{i}",
    ];

    // 3. Values for the declared fields only, as #37 allows: Thunder.Dbf works out _NullFlags. VFP must
    // find .NULL. exactly where null was given, through Append, AppendRange and Update alike, and the
    // tag on the nullable field in the right order.
    [Fact]
    public void VisualFoxPro_sees_the_nulls_of_records_written_without_NullFlags()
    {
        var path = Kinds("OPT", "ID", "UNAME");
        var expected = new Dictionary<int, bool>(); // record number -> OPT should be .NULL.
        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true))
        {
            Assert.Equal(13, table.FieldDefinitions.Count);
            for (var i = 0; i < 10; i++)
                expected[table.Append(KindsRow(i, nullOpt: i % 3 == 0)) + 1] = i % 3 == 0;
            var first = table.RecordCount + 1;
            table.AppendRange(Enumerable.Range(10, 10).Select(i => KindsRow(i, nullOpt: i % 3 == 0)));
            for (var i = 10; i < 20; i++)
                expected[first + i - 10] = i % 3 == 0;
            for (var i = 20; i < 30; i++)
            {
                // Records 1-10 were written by VFP; overwrite them whole, nulls set and cleared.
                table.Update(i - 20, KindsRow(i, nullOpt: i % 2 == 0));
                expected[i - 19] = i % 2 == 0;
            }
            foreach (var (record, isNull) in expected)
                Assert.Equal(isNull, table.Read(record - 1).IsNull(table.Fields.ToList().FindIndex(f => f.Name == "OPT")));
        }
        if (!HaveVfp())
            return;

        var vfp = AssertVfpReadsWhatThunderReads(path, "after appends without _NullFlags");
        foreach (var (record, isNull) in expected)
        {
            var opt = vfp[record - 1].Split('\t').Single(part => part.StartsWith("OPT=", StringComparison.Ordinal));
            Assert.True(isNull == (opt == "OPT=.NULL."), $"Record {record}: VFP reads {opt}, but {(isNull ? "null" : "a value")} was given.");
        }
        AssertVfpWalksEveryTagAsTheIndexHolds(path);
    }

    // 4. The MODIFY STRUCTURE preview on a table VFP made, with memos and a .cdx: a tag dropped, its
    // field dropped, a field in a tag widened, a nullable field and a memo field added. VFP must open the
    // result, read every memo, walk the tags left, and edit through them.
    [Fact]
    public void VisualFoxPro_opens_and_keeps_a_table_after_ModifyStructure()
    {
        var path = Kinds("CODE", "UNAME", "STAMP", "CODEWHEN", "LIVE", "ID", "OPT", "QTY");
        var before = Values(path);
        var kept = new HashSet<string> { "ID", "CODE", "NAME", "AMOUNT", "PRICE", "WHEN", "STAMP", "FLAG", "SCORE", "STATUS", "NOTE", "OPT" };
        using (var old = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true))
        {
            old.DropIndexTag("QTY");
            var fields = old.FieldDefinitions
                .Where(f => f.Name != "QTY")
                .Select(f => f.Name == "NAME" ? DbfFieldDefinition.Character("NAME", 40) : f)
                .Append(DbfFieldDefinition.Character("CITY", 20) with { Nullable = true })
                .Append(DbfFieldDefinition.Memo("REMARK"))
                .ToList();
            using var table = old.ModifyStructure(fields);
            for (var i = 0; i < 30; i++)
                table.UpdateFields(i * 5, new Dictionary<string, object?>
                {
                    ["CITY"] = i % 3 == 0 ? null : $"City {i}",
                    ["REMARK"] = $"Added after the change {i}\r\n" + new string('r', i * 17),
                    ["NAME"] = i % 2 == 0 ? $"A name longer than thirty characters {i}" : table.Read(i * 5)["NAME"],
                });
        }
        Assert.Equal(["CODE", "CODEWHEN", "ID", "LIVE", "OPT", "STAMP", "UNAME"], TagOrders(path).Keys.Order());
        var changed = Only(Values(path), kept);
        Assert.Equal(Only(before, kept).Where((_, i) => i % 5 != 0 || i >= 150), changed.Where((_, i) => i % 5 != 0 || i >= 150));
        if (!HaveVfp())
            return;

        var vfp = AssertVfpReadsWhatThunderReads(path, "after ModifyStructure");
        Assert.Equal(changed, Only(vfp, kept));
        AssertVfpWalksEveryTagAsTheIndexHolds(path);
        Edit(path, nulls: true);
        AssertEveryTagMatchesARebuild(path);
        AssertVfpReadsWhatThunderReads(path, "after VFP's edits");
    }
}
