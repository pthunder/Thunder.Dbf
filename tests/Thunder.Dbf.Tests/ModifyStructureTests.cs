//    NAME C 10                 NAME C 30
//    AGE  N 3     ------>      AGE  N 3      ProjectThunder :: Thunder.Dbf tests
//    NOTE M                    NOTE M        MODIFY STRUCTURE in all four formats: every record and
//                              CITY C 20     memo carried across, deleted ones still deleted, tags
//                                            rebuilt, and a change killed at any step leaving either
//                                            the old table or the new one, never a mix.

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class ModifyStructureTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-restructure-");

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class Killed : Exception;

    // A kill in this process: the hook throws at the step, and nothing that catches only file errors runs.
    internal static void KillingAt(string step, Action action)
    {
        DbfTable.RestructurePoint = point => { if (point == step) throw new Killed(); };
        try
        {
            Assert.Throws<Killed>(action);
        }
        finally
        {
            DbfTable.RestructurePoint = null;
        }
    }

    public static TheoryData<DbfTableFormat> Formats() =>
        [DbfTableFormat.DbaseIII, DbfTableFormat.DbaseIV, DbfTableFormat.FoxPro2, DbfTableFormat.VisualFoxPro];

    private static readonly (string Name, decimal Age, string Note, DateOnly Hired)[] People =
    [
        ("Ada", 36, "Wrote the first program", new DateOnly(1843, 7, 1)),
        ("Grace", 45, "", new DateOnly(1944, 1, 2)),
        ("Hedy", 41, "Frequency hopping", new DateOnly(1941, 6, 10)),
        ("Joan", 30, "Long memo " + new string('x', 700), new DateOnly(1951, 3, 4)),
    ];

    private static bool IsFoxPro(DbfTableFormat format) => format is DbfTableFormat.FoxPro2 or DbfTableFormat.VisualFoxPro;

    // PEOPLE with a memo, one deleted record, and a tag over NAME (dBASE III has no .mdx).
    private string Table(DbfTableFormat format, bool tag = true)
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        using (var table = DbfTable.Create(path, format,
            [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Numeric("AGE", 3), DbfFieldDefinition.Memo("NOTE"), DbfFieldDefinition.Date("HIRED")]))
        {
            foreach (var (name, age, note, hired) in People)
                table.Append(name, age, note, hired);
            table.Delete(1);
        }
        if (tag && format != DbfTableFormat.DbaseIII)
        {
            using var table = Open(path);
            table.CreateIndexTag("NAME", "NAME");
        }
        return path;
    }

    private static DbfTable Open(string path, bool exclusive = true) =>
        DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: exclusive);

    private static List<(int Record, string Key)> TagEntries(DbfTable table, string tag)
    {
        using var index = (IDbfIndex?)table.OpenProductionIndex() ?? table.OpenStructuralIndex()!;
        return index.Tag(tag).Entries().Select(e => (e.RecordNumber, ((string)e.Key!).Trim())).ToList();
    }

    private List<string> Leftovers() => Directory.EnumerateDirectories(_dir.FullName)
        .Select(Path.GetFileName).Where(n => !n!.Contains(".restructured-", StringComparison.Ordinal)).Select(n => n!).ToList();

    [Theory]
    [MemberData(nameof(Formats))]
    public void Adding_widening_and_dropping_fields_keeps_every_record_and_memo(DbfTableFormat format)
    {
        var path = Table(format);
        var before = File.ReadAllBytes(path)[0];

        using (var old = Open(path))
        {
            var fields = old.FieldDefinitions.ToList();
            fields[0] = DbfFieldDefinition.Character("NAME", 30);
            fields.RemoveAt(3); // HIRED
            fields.Add(DbfFieldDefinition.Character("CITY", 20));
            using var table = old.ModifyStructure(fields);

            Assert.Equal(["NAME", "AGE", "NOTE", "CITY"], table.Fields.Where(f => !f.IsSystem).Select(f => f.Name));
            Assert.Equal(30, table.Fields[0].Length);
            Assert.Equal(People.Length, table.RecordCount);
            for (var i = 0; i < People.Length; i++)
            {
                var record = table.Read(i);
                Assert.Equal(People[i].Name, record["NAME"]);
                Assert.Equal(People[i].Age, Convert.ToDecimal(record["AGE"]));
                Assert.Equal(People[i].Note.Length == 0 ? null : People[i].Note, record["NOTE"] as string is "" ? null : record["NOTE"]);
                Assert.Equal(i == 1, record.IsDeleted);
                Assert.True(record["CITY"] is null or "");
            }
            Assert.NotNull(table.StructureBackupPath);
            Assert.True(File.Exists(Path.Combine(table.StructureBackupPath!, "PEOPLE.DBF")));

            // The new table keeps working, tags included.
            table.Append("Margaret", 33m, "Apollo", "Boston");
            if (format != DbfTableFormat.DbaseIII)
                Assert.Equal(["Ada", "Grace", "Hedy", "Joan", "Margaret"], TagEntries(table, "NAME").Select(e => e.Key));
        }
        Assert.Equal(before, File.ReadAllBytes(path)[0]); // same dialect
        Assert.Empty(Leftovers());

        // Reopened from disk, as the legacy app would.
        using var reopened = DbfTable.Open(path);
        Assert.Equal("Frequency hopping", reopened.Read(2)["NOTE"]);
        Assert.Equal(People[3].Note, reopened.Read(3)["NOTE"]);
        Assert.Equal("Boston", reopened.Read(4)["CITY"]);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_change_that_would_cut_values_short_is_refused_and_leaves_the_table_alone(DbfTableFormat format)
    {
        var path = Table(format);
        var files = Directory.GetFiles(_dir.FullName).ToDictionary(f => f, File.ReadAllBytes);

        using (var table = Open(path))
        {
            var ex = Assert.Throws<DbfException>(() => table.AlterField("NAME", DbfFieldDefinition.Character("NAME", 4)));
            Assert.Equal(DbfErrorCode.InvalidValue, ex.Code);
            Assert.Contains("Record 2", ex.Message); // Grace
            Assert.Equal("Grace", table.Read(1)["NAME"]); // still open and usable
        }
        Assert.Empty(Directory.EnumerateDirectories(_dir.FullName));
        foreach (var (file, bytes) in files)
            Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void With_allowLoss_values_are_shortened_and_counted(DbfTableFormat format)
    {
        var path = Table(format);
        using var old = Open(path);
        using var table = old.AlterField("NAME", DbfFieldDefinition.Character("NAME", 4), allowLoss: true);
        Assert.Equal(["Ada", "Grac", "Hedy", "Joan"], table.ReadAll().Select(r => (string)r["NAME"]!));
        Assert.Equal(1, table.StructureValuesChanged);
        if (format != DbfTableFormat.DbaseIII)
            Assert.Equal(["Ada", "Grac", "Hedy", "Joan"], TagEntries(table, "NAME").Select(e => e.Key));
    }

    [Fact]
    public void Values_convert_between_types()
    {
        var path = Table(DbfTableFormat.DbaseIV, tag: false);
        using var old = Open(path);
        using var table = old.ModifyStructure(
            [DbfFieldDefinition.Memo("NAME"), DbfFieldDefinition.Character("AGE", 5), DbfFieldDefinition.Character("NOTE", 254), DbfFieldDefinition.Character("HIRED", 8)],
            allowLoss: true);
        Assert.Equal(1, table.StructureValuesChanged); // Joan's long memo, cut to 254
        Assert.Equal(254, ((string)table.Read(3)["NOTE"]!).Length);
        var ada = table.Read(0);
        Assert.Equal("Ada", ada["NAME"]); // text to memo
        Assert.Equal("36", ada["AGE"]); // number to text
        Assert.Equal("Wrote the first program", ada["NOTE"]); // memo to text
        Assert.Equal("18430701", ada["HIRED"]); // date to text, as DTOS()

        using var back = table.ModifyStructure(
            [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Numeric("AGE", 5, 1), DbfFieldDefinition.Memo("NOTE"), DbfFieldDefinition.Date("HIRED")]);
        Assert.Equal(36m, back.Read(0)["AGE"]);
        Assert.Equal(new DateOnly(1843, 7, 1), back.Read(0)["HIRED"]);
        Assert.Equal("Wrote the first program", back.Read(0)["NOTE"]);
    }

    [Fact]
    public void Text_that_isnt_a_number_is_refused_as_a_loss()
    {
        var path = Table(DbfTableFormat.DbaseIV, tag: false);
        using var table = Open(path);
        Assert.Throws<DbfException>(() => table.AlterField("NAME", DbfFieldDefinition.Numeric("NAME", 5)));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Renaming_a_field_keeps_its_values(DbfTableFormat format)
    {
        var path = Table(format, tag: false);
        using var old = Open(path);
        using var table = old.AlterField("AGE", DbfFieldDefinition.Numeric("YEARS", 3));
        Assert.Equal(45m, Convert.ToDecimal(table.Read(1)["YEARS"]));
    }

    [Theory]
    [InlineData(DbfTableFormat.DbaseIV)]
    [InlineData(DbfTableFormat.FoxPro2)]
    [InlineData(DbfTableFormat.VisualFoxPro)]
    public void A_tag_over_a_dropped_or_renamed_field_is_refused_until_it_is_dropped(DbfTableFormat format)
    {
        var path = Table(format);
        using var old = Open(path);
        var dropped = Assert.Throws<DbfException>(() => old.DropField("NAME"));
        Assert.Contains("Tag NAME uses field NAME, which this change drops", dropped.Message);
        var renamed = Assert.Throws<DbfException>(() => old.AlterField("NAME", DbfFieldDefinition.Character("FULLNAME", 10)));
        Assert.Contains("renames", renamed.Message);
        Assert.Empty(Directory.EnumerateDirectories(_dir.FullName));

        old.DropIndexTag("NAME");
        using var table = old.DropField("NAME");
        Assert.Equal(["AGE", "NOTE", "HIRED"], table.Fields.Where(f => !f.IsSystem).Select(f => f.Name));
    }

    [Fact]
    public void A_shared_open_is_refused()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using var table = Open(path, exclusive: false);
        Assert.Equal(DbfErrorCode.ExclusiveUseRequired, Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK"))).Code);
    }

    // ---- killed partway -------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Formats))]
    public void Killed_while_copying_records_the_original_is_untouched(DbfTableFormat format)
    {
        var path = Table(format);
        var files = Directory.GetFiles(_dir.FullName).ToDictionary(f => f, File.ReadAllBytes);
        using (var table = Open(path))
        {
            KillingAt("copying", () => table.AddField(DbfFieldDefinition.Character("CITY", 20)));
        }
        foreach (var (file, bytes) in files)
            Assert.Equal(bytes, File.ReadAllBytes(file));
        using var reopened = Open(path);
        Assert.Equal(4, reopened.Fields.Count(f => !f.IsSystem));
    }

    [Fact]
    public void A_partial_folder_left_by_a_killed_change_is_cleared_by_the_next()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        var partial = path + ".restructure-partial";
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "PEOPLE.DBF"), "half written");
        using (var shared = Open(path, exclusive: false))
            shared.Append("Kay", 50m, "", new DateOnly(1950, 1, 1)); // it doesn't stop writes: nothing uses it

        using var old = Open(path);
        using var table = old.AddField(DbfFieldDefinition.Logical("OK"));
        Assert.Equal(5, table.RecordCount);
        Assert.Empty(Leftovers());
    }

    [Theory]
    [InlineData(DbfTableFormat.DbaseIII, "staged")]
    [InlineData(DbfTableFormat.DbaseIV, "staged")]
    [InlineData(DbfTableFormat.DbaseIV, "memo moved in")]
    [InlineData(DbfTableFormat.FoxPro2, "memo moved aside")]
    [InlineData(DbfTableFormat.VisualFoxPro, "staged")]
    [InlineData(DbfTableFormat.VisualFoxPro, "closed")]
    public void Killed_before_the_table_is_swapped_the_next_exclusive_open_puts_the_old_table_back(DbfTableFormat format, string step)
    {
        var path = Table(format);
        KilledAt(path, step);

        // Until an exclusive open settles it, shared writers are turned away; readers see the old table.
        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => Open(path, exclusive: false)).Code);
        if (step == "memo moved aside") // killed between the memo file's two renames: readers are told why it's missing
            Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => DbfTable.Open(path)).Code);
        else
        {
            using var reader = DbfTable.Open(path);
            Assert.Equal(4, reader.RecordCount);
        }

        using (var table = Open(path))
        {
            Assert.Null(table.StructureBackupPath);
            Assert.Equal(["NAME", "AGE", "NOTE", "HIRED"], table.Fields.Where(f => !f.IsSystem).Select(f => f.Name));
            Assert.Equal(People.Select(p => p.Note.Length == 0 ? null : p.Note), table.ReadAll().Select(r => r["NOTE"] as string is "" ? null : r["NOTE"]));
            table.Append("Kay", 50m, "Wrote COBOL", new DateOnly(1950, 1, 1));
            if (format != DbfTableFormat.DbaseIII)
                Assert.Equal(5, TagEntries(table, "NAME").Count);
        }
        Assert.Empty(Leftovers());
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("memo moved aside")]
    [InlineData("memo moved in")]
    public void Killed_before_the_decision_the_table_comes_back_byte_for_byte(string step)
    {
        var path = Table(DbfTableFormat.VisualFoxPro);
        var before = Directory.GetFiles(_dir.FullName).ToDictionary(Path.GetFileName, File.ReadAllBytes);
        KilledAt(path, step);
        Assert.Equal(before[Path.GetFileName(path)], File.ReadAllBytes(path)); // the .dbf never moves before the decision
        using (Open(path))
        {
        }
        // The memo file too: the old one comes back from the backup over the copy.
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_dir.FullName).Select(Path.GetFileName).Order());
        foreach (var (file, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_dir.FullName, file!)));
        Assert.Empty(Directory.EnumerateDirectories(_dir.FullName));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Killed_after_the_table_is_swapped_the_next_exclusive_open_finishes_the_change(DbfTableFormat format)
    {
        var path = Table(format);
        KilledAt(path, "table moved in");

        using (var table = Open(path))
        {
            Assert.NotNull(table.StructureBackupPath);
            Assert.Equal(["NAME", "AGE", "NOTE", "HIRED", "CITY"], table.Fields.Where(f => !f.IsSystem).Select(f => f.Name));
            Assert.Equal(People[0].Note, table.Read(0)["NOTE"]);
            Assert.Equal(20, table.Fields[0].Length);
            table.Append("Katherine Johnson", 50m, "", new DateOnly(1950, 1, 1), "Dayton");
            if (format != DbfTableFormat.DbaseIII)
            {
                Assert.Equal(["Ada", "Grace", "Hedy", "Joan", "Katherine Johnson"], TagEntries(table, "NAME").Select(e => e.Key));
                using var index = (IDbfIndex?)table.OpenProductionIndex() ?? table.OpenStructuralIndex()!;
                Assert.Equal(20, index.Tag("NAME").KeyLength); // the new index went in, not the old one's 10
            }
        }
        Assert.Empty(Leftovers());
    }

    private static void KilledAt(string path, string step)
    {
        using var table = Open(path);
        var fields = table.FieldDefinitions.ToList();
        fields[0] = DbfFieldDefinition.Character("NAME", 20); // the tag's keys widen too
        fields.Add(DbfFieldDefinition.Character("CITY", 20));
        KillingAt(step, () => table.ModifyStructure(fields));
    }

    // ---- Visual FoxPro --------------------------------------------------------------------

    [Fact]
    public void Visual_FoxPro_nulls_and_autoincrement_counters_carry_across()
    {
        var path = Path.Combine(_dir.FullName, "ITEMS.DBF");
        using (var created = DbfTable.Create(path, DbfTableFormat.VisualFoxPro,
            [DbfFieldDefinition.Integer("ID") with { AutoIncrement = true }, DbfFieldDefinition.Character("NAME", 10) with { Nullable = true },
             DbfFieldDefinition.Currency("PRICE"), DbfFieldDefinition.Memo("DATA") with { Binary = true }]))
        {
            created.Append(null, "bolt", 1.25m, new byte[] { 0, 1, 2, 0x81 });
            created.Append(null, null, 2.5m, null);
            created.Append(null, "nut", 0.1m, null);
        }

        using var old = Open(path);
        Assert.Equal(4, old.Header.NextAutoIncrement(0));
        var fields = old.FieldDefinitions.ToList();
        Assert.True(fields[3].Binary);
        fields.Insert(2, DbfFieldDefinition.Date("ADDED") with { Nullable = true });
        fields[1] = DbfFieldDefinition.Character("NAME", 20) with { Nullable = true };
        using var table = old.ModifyStructure(fields);

        Assert.Equal(new byte[] { 0, 1, 2, 0x81 }, table.Read(0)["DATA"]);
        Assert.True(table.Read(1).IsNull(1));
        Assert.False(table.Read(0).IsNull(1));
        Assert.True(table.Read(0).IsNull(2)); // the new nullable field starts as .NULL.
        Assert.Equal(2.5m, table.Read(1)["PRICE"]);
        Assert.Equal(4, table.Header.NextAutoIncrement(0));
        Assert.True(table.Fields[4].IsBinary);
        table.Append(null, "washer", null, 0.05m, null);
        Assert.Equal(4, table.Read(3)["ID"]);

        // A nullable field made NOT NULL would lose the null: refused.
        Assert.Throws<DbfException>(() => table.AlterField("NAME", DbfFieldDefinition.Character("NAME", 20)));
    }

    [Fact]
    public void A_new_autoincrement_field_numbers_the_records_already_there()
    {
        var path = Table(DbfTableFormat.VisualFoxPro, tag: false);
        using var old = Open(path);
        using var table = old.ModifyStructure([DbfFieldDefinition.Integer("ID") with { AutoIncrement = true, AutoIncrementNext = 10, AutoIncrementStep = 5 }, .. old.FieldDefinitions]);
        Assert.Equal([10, 15, 20, 25], table.ReadAll().Select(r => (int)r["ID"]!));
        Assert.Equal(30, table.Header.NextAutoIncrement(0));
        Assert.Equal(0x31, table.Header.Version); // Visual FoxPro's mark for a table with autoincrement
    }

    [Fact]
    public void A_binary_character_field_keeps_its_bytes_when_resized()
    {
        var path = Path.Combine(_dir.FullName, "KEYS.DBF");
        using (var created = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Character("HASH", 6) with { Binary = true }]))
        {
            created.Append("abcdef");
            created.Append("ab");
        }
        // Bytes no code page round-trips: 0x81 is unassigned in Windows-1252, and a NUL.
        var bytes = File.ReadAllBytes(path);
        var start = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)) + 1;
        new byte[] { 0x81, 0x00, 0xFF, (byte)'x', 0x90, 0x9D }.CopyTo(bytes, start);
        File.WriteAllBytes(path, bytes);

        using var old = Open(path);
        using var wider = old.AlterField("HASH", DbfFieldDefinition.Character("HASH", 10) with { Binary = true });
        Assert.Equal(new byte[] { 0x81, 0x00, 0xFF, (byte)'x', 0x90, 0x9D, 32, 32, 32, 32 }, wider.Read(0).Raw.AsSpan(1, 10).ToArray());
        Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => wider.AlterField("HASH", DbfFieldDefinition.Character("HASH", 3) with { Binary = true })).Code);
        using var narrower = wider.AlterField("HASH", DbfFieldDefinition.Character("HASH", 2) with { Binary = true }, allowLoss: true);
        Assert.Equal(new byte[] { 0x81, 0x00 }, narrower.Read(0).Raw.AsSpan(1, 2).ToArray());
        Assert.Equal("ab", narrower.Read(1)["HASH"]);
        Assert.Equal(1, narrower.StructureValuesChanged);
    }

    [Fact]
    public void Killed_before_the_table_is_swapped_a_memo_file_the_table_never_had_isnt_left_beside_it()
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        using (var created = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Character("NAME", 10)]))
            created.Append("Ada");
        using (var table = Open(path))
        {
            KillingAt("closed", () => table.AddField(DbfFieldDefinition.Memo("NOTE")));
        }
        using (Open(path))
        {
        }
        Assert.Equal(["PEOPLE.DBF"], Directory.GetFiles(_dir.FullName).Select(Path.GetFileName));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void A_change_that_adds_a_memo_file_finishes_with_it_in_place()
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        using (var created = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 10)]))
            created.Append("Ada");
        using (var table = Open(path))
        {
            KillingAt("table moved in", () => table.AddField(DbfFieldDefinition.Memo("NOTE")));
        }
        using var reopened = Open(path);
        reopened.Update(0, "Ada", "Countess of Lovelace");
        Assert.Equal("Countess of Lovelace", reopened.Read(0)["NOTE"]);
    }

    [Fact]
    public void An_index_another_reader_holds_open_stops_the_change_before_anything_moves()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        var dbf = File.ReadAllBytes(path);
        using var old = Open(path);
        using (old.OpenProductionIndex())
            Assert.Equal(DbfErrorCode.FileInUse, Assert.Throws<DbfException>(() => old.AddField(DbfFieldDefinition.Logical("OK"))).Code);
        Assert.Equal(dbf, File.ReadAllBytes(path));
        Assert.Empty(Leftovers());
        using var table = Open(path);
        Assert.Equal(4, table.Fields.Count);
    }

    [Fact]
    public void A_table_in_a_Visual_FoxPro_database_is_refused()
    {
        var path = Table(DbfTableFormat.VisualFoxPro, tag: false);
        var bytes = File.ReadAllBytes(path);
        bytes[28] |= 0x04;
        File.WriteAllBytes(path, bytes);
        using var table = Open(path);
        Assert.Equal(DbfErrorCode.UnsupportedFormat, Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK"))).Code);
    }

    // kinds.DBF and .FPT as Visual FoxPro 9 wrote them, with tags made here as kinds.prg made them (its
    // own .cdx has a GENERAL-collation tag, which stays refused), CdxCreateTests proving them the same.
    private string Kinds()
    {
        foreach (var file in new[] { "kinds.DBF", "kinds.FPT" })
            File.Copy(IndexTestSupport.Fixture("vfp9/" + file), Path.Combine(_dir.FullName, file));
        var path = Path.Combine(_dir.FullName, "kinds.DBF");
        var bytes = File.ReadAllBytes(path);
        bytes[28] &= unchecked((byte)~0x01);
        File.WriteAllBytes(path, bytes);
        using var table = Open(path);
        table.CreateIndexTag("CODE", "code");
        table.CreateIndexTag("UNAME", "UPPER(name)");
        table.CreateIndexTag("STAMP", "stamp");
        table.CreateIndexTag("CODEWHEN", "code+DTOS(when)");
        table.CreateIndexTag("LIVE", "id", "status=\"A\"", unique: false, descending: false);
        table.CreateIndexTag("ID", "id", null, unique: false, descending: false, candidate: true);
        return path;
    }

    [Fact]
    public void A_table_Visual_FoxPro_made_keeps_its_tags_through_a_change()
    {
        var path = Kinds();
        using var old = Open(path);
        List<string> tagsBefore;
        using (var before = old.OpenStructuralIndex()!)
            tagsBefore = before.Tags.Select(t => t.Name).Order().ToList();
        var notes = old.ReadAll().Select(r => r["NOTE"]).ToList();

        using var table = old.AlterField("NAME", DbfFieldDefinition.Character("NAME", 40));
        using var index = table.OpenStructuralIndex()!;
        Assert.Equal(tagsBefore, index.Tags.Select(t => t.Name).Order());
        Assert.Equal(notes, table.ReadAll().Select(r => r["NOTE"]));
        Assert.True(index["ID"].IsCandidate);
        Assert.Equal(table.ReadAll().Count(r => (string?)r["STATUS"] == "A"), index["LIVE"].Entries().Count());
        Assert.Equal(table.ReadAll().OrderBy(r => ((string)r["NAME"]!).ToUpperInvariant(), StringComparer.Ordinal).ThenBy(r => r.RecordNumber).Select(r => r.RecordNumber),
            index["UNAME"].Entries().Select(e => e.RecordNumber));
    }

    // ---- numbers that don't survive the trip ----------------------------------------------

    private DbfTable VfpTable(string name, params DbfFieldDefinition[] fields)
    {
        var path = Path.Combine(_dir.FullName, name + ".DBF");
        DbfTable.Create(path, DbfTableFormat.VisualFoxPro, fields).Dispose();
        return Open(path);
    }

    [Fact]
    public void A_double_too_small_for_an_integer_is_a_loss_not_zero()
    {
        using (var table = VfpTable("TINY", DbfFieldDefinition.Double("X")))
        {
            table.Append(1e-100);
            Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => table.AlterField("X", DbfFieldDefinition.Integer("X"))).Code);
            using var lossy = table.AlterField("X", DbfFieldDefinition.Integer("X"), allowLoss: true);
            Assert.Equal(0, lossy.Read(0)["X"]);
            Assert.Equal(1, lossy.StructureValuesChanged);
        }
    }

    [Fact]
    public void Text_holding_1E_100_keeps_its_value_in_a_numeric_field()
    {
        using var table = VfpTable("TEXT", DbfFieldDefinition.Character("X", 10));
        table.Append("1e-100");
        using var number = table.AlterField("X", DbfFieldDefinition.Numeric("X", 10, 2));
        Assert.Equal(1e-100, Convert.ToDouble(number.Read(0)["X"]));
        Assert.Equal(0, number.StructureValuesChanged);
    }

    [Theory]
    [InlineData(0.12345678901234567, 20, 18)] // 17 significant digits: decimal keeps only 15
    [InlineData(1234567.8901234567, 20, 10)]
    [InlineData(0.1 + 0.2, 20, 18)]
    public void A_double_with_more_digits_than_the_field_writes_is_a_loss(double value, int length, int decimals)
    {
        using var table = VfpTable("DIGITS", DbfFieldDefinition.Double("X"));
        table.Append(value);
        Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => table.AlterField("X", DbfFieldDefinition.Numeric("X", length, decimals))).Code);
    }

    [Fact]
    public void A_double_the_field_holds_exactly_is_carried()
    {
        using var table = VfpTable("EXACT", DbfFieldDefinition.Double("X"));
        table.Append(0.1);
        table.Append(-2.5);
        using var number = table.AlterField("X", DbfFieldDefinition.Numeric("X", 10, 4));
        Assert.Equal([0.1m, -2.5m], number.ReadAll().Select(r => Convert.ToDecimal(r["X"])));
    }

    [Theory]
    [InlineData("1.005", 5, 2, true)]   // rounds at the boundary: a loss
    [InlineData("1.004", 5, 2, true)]
    [InlineData("1.000", 5, 2, false)]  // trailing zeros only
    [InlineData("99.99", 4, 1, true)]   // rounds up and no longer fits
    public void Rounding_to_fewer_decimals_is_a_loss(string text, int length, int decimals, bool lost)
    {
        using var table = VfpTable("ROUND", DbfFieldDefinition.Numeric("X", 8, 3));
        table.Append(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        var narrower = DbfFieldDefinition.Numeric("X", length, decimals);
        if (lost)
        {
            Assert.Throws<DbfException>(() => table.AlterField("X", narrower));
            using var lossy = table.AlterField("X", narrower, allowLoss: true);
            Assert.Equal(1, lossy.StructureValuesChanged);
        }
        else
        {
            using var exact = table.AlterField("X", narrower);
            Assert.Equal(0, exact.StructureValuesChanged);
        }
    }

    [Fact]
    public void A_17_digit_number_is_a_loss_in_a_double_and_a_16_digit_one_isnt()
    {
        using var table = VfpTable("BIG", DbfFieldDefinition.Numeric("X", 20));
        table.Append(1234567890123456m);
        using (var exact = table.AlterField("X", DbfFieldDefinition.Double("X")))
            Assert.Equal(1234567890123456d, exact.Read(0)["X"]);
        using var again = VfpTable("BIG2", DbfFieldDefinition.Numeric("X", 20));
        again.Append(12345678901234567m);
        Assert.Throws<DbfException>(() => again.AlterField("X", DbfFieldDefinition.Double("X")));
    }

    // ---- Visual FoxPro nulls --------------------------------------------------------------

    [Fact]
    public void Resizing_a_nullable_number_keeps_blank_blank_and_null_null()
    {
        var path = Path.Combine(_dir.FullName, "NULLS.DBF");
        using (var created = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Numeric("X", 5, 2) with { Nullable = true }]))
        {
            created.Append(1.5m);
            created.Append((object?)null);
            created.Append((object?)null);
        }
        // Record 3: blank but not .NULL. (its null bit cleared, its digits left as spaces).
        var bytes = File.ReadAllBytes(path);
        var header = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        bytes[header + recordLength * 2 + recordLength - 1] = 0;
        File.WriteAllBytes(path, bytes);

        using var old = Open(path);
        Assert.False(old.Read(2).IsNull(0));
        using var table = old.AlterField("X", DbfFieldDefinition.Numeric("X", 8, 2) with { Nullable = true });
        Assert.Equal(1.5m, table.Read(0)["X"]);
        Assert.True(table.Read(1).IsNull(0));
        Assert.False(table.Read(2).IsNull(0)); // still blank, not made .NULL.
        Assert.Null(table.Read(2)["X"]);
        Assert.Equal(0, table.StructureValuesChanged);
    }

    [Fact]
    public void Null_bits_follow_their_fields_when_fields_move_across_bytes()
    {
        var fields = Enumerable.Range(0, 10).Select(i => DbfFieldDefinition.Character($"F{i}", 2) with { Nullable = true }).ToArray();
        var path = Path.Combine(_dir.FullName, "BITS.DBF");
        var rows = new[] { new[] { 0, 3, 8, 9 }, new[] { 1, 2, 7 }, Array.Empty<int>() }; // which fields are .NULL.
        using (var created = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, fields))
        {
            foreach (var nulls in rows)
                created.Append(Enumerable.Range(0, 10).Select(i => nulls.Contains(i) ? null : (object)$"v{i}").ToArray());
        }
        using var old = Open(path);
        // F9 to the front, F0 and F1 dropped, a new nullable field last: bits shift across the byte boundary.
        var changed = old.FieldDefinitions.Where(f => f.Name is not ("F0" or "F1")).ToList();
        var f9 = changed.Single(f => f.Name == "F9");
        changed.Remove(f9);
        changed.Insert(0, f9);
        changed.Add(DbfFieldDefinition.Character("NEW", 2) with { Nullable = true });
        using var table = old.ModifyStructure(changed);
        for (var r = 0; r < rows.Length; r++)
        {
            var record = table.Read(r);
            foreach (var field in changed.Where(f => f.Name != "NEW"))
            {
                var i = int.Parse(field.Name[1..], System.Globalization.CultureInfo.InvariantCulture);
                var at = table.Fields.ToList().FindIndex(f => f.Name == field.Name);
                Assert.Equal(rows[r].Contains(i), record.IsNull(at));
                if (!rows[r].Contains(i))
                    Assert.Equal($"v{i}", record[at]);
            }
            Assert.True(record.IsNull(table.Fields.ToList().FindIndex(f => f.Name == "NEW")));
        }
    }

    // ---- autoincrement --------------------------------------------------------------------

    [Fact]
    public void A_new_autoincrement_field_that_would_run_out_of_numbers_is_refused_with_nothing_changed()
    {
        var path = Table(DbfTableFormat.VisualFoxPro, tag: false);
        var before = File.ReadAllBytes(path);
        using (var old = Open(path))
        {
            var ex = Assert.Throws<DbfException>(() => old.ModifyStructure(
                [DbfFieldDefinition.Integer("ID") with { AutoIncrement = true, AutoIncrementNext = int.MaxValue - 150, AutoIncrementStep = 100 }, .. old.FieldDefinitions]));
            Assert.Equal(DbfErrorCode.InvalidValue, ex.Code);
            Assert.Contains("Record 2", ex.Message);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateDirectories(_dir.FullName));
    }

    [Fact]
    public void An_autoincrement_field_with_a_step_keeps_counting_by_it()
    {
        var path = Path.Combine(_dir.FullName, "STEP.DBF");
        using (var created = DbfTable.Create(path, DbfTableFormat.VisualFoxPro,
            [DbfFieldDefinition.Integer("ID") with { AutoIncrement = true, AutoIncrementNext = 7, AutoIncrementStep = 3 }, DbfFieldDefinition.Character("NAME", 5)]))
        {
            created.Append(null, "a");
            created.Append(null, "b");
        }
        using var old = Open(path);
        using var table = old.AlterField("NAME", DbfFieldDefinition.Character("NAME", 10));
        Assert.Equal(13, table.Header.NextAutoIncrement(0));
        Assert.Equal(3, table.Fields[0].AutoIncrementStep);
        table.Append(null, "c");
        Assert.Equal([7, 10, 13], table.ReadAll().Select(r => (int)r["ID"]!));
    }

    // ---- memo files coming and going ------------------------------------------------------

    [Theory]
    [InlineData(DbfTableFormat.DbaseIII, 0x03, 0x83)]
    [InlineData(DbfTableFormat.DbaseIV, 0x03, 0x83)] // no .mdx: dBASE III's memo file, which every dialect opens
    [InlineData(DbfTableFormat.FoxPro2, 0x03, 0x83)] // without memos or a .cdx nothing in the file says FoxPro 2: it's read as dBASE
    [InlineData(DbfTableFormat.VisualFoxPro, 0x30, 0x30)]
    public void Adding_the_first_memo_field_and_dropping_the_last(DbfTableFormat format, byte without, byte with)
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        using (var created = DbfTable.Create(path, format, [DbfFieldDefinition.Character("NAME", 10)]))
            created.Append("Ada");

        using (var old = Open(path))
        using (var memo = old.AddField(DbfFieldDefinition.Memo("NOTE")))
        {
            Assert.Equal(with, memo.Header.Version);
            Assert.True(memo.Header.HasMemo);
            memo.Update(0, "Ada", "Countess of Lovelace");
            Assert.Single(Directory.GetFiles(_dir.FullName, "PEOPLE.*").Where(f => Path.GetExtension(f).ToLowerInvariant() is ".dbt" or ".fpt"));

            using var none = memo.DropField("NOTE");
            Assert.Equal(without, none.Header.Version);
            Assert.False(none.Header.HasMemo);
            Assert.Equal("Ada", none.Read(0)["NAME"]);
            // No memo file is left beside it; the old one, with its text, went to the backup.
            Assert.DoesNotContain(Directory.GetFiles(_dir.FullName), f => Path.GetExtension(f).ToLowerInvariant() is ".dbt" or ".fpt");
            Assert.Contains(Directory.GetFiles(none.StructureBackupPath!), f => Path.GetExtension(f).ToLowerInvariant() is ".dbt" or ".fpt");
        }
    }

    [Fact]
    public void A_visual_foxpro_table_gains_autoincrement_s_version_byte()
    {
        using var table = VfpTable("AUTO", DbfFieldDefinition.Character("NAME", 5));
        Assert.Equal(0x30, table.Header.Version);
        using var numbered = table.ModifyStructure([DbfFieldDefinition.Integer("ID") with { AutoIncrement = true }, .. table.FieldDefinitions]);
        Assert.Equal(0x31, numbered.Header.Version);
        using var plain = numbered.DropField("ID");
        Assert.Equal(0x30, plain.Header.Version);
    }

    [Fact]
    public void A_backlink_naming_a_database_is_refused_even_without_the_database_bit()
    {
        var path = Table(DbfTableFormat.VisualFoxPro, tag: false);
        var bytes = File.ReadAllBytes(path);
        var header = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        "people.dbc"u8.CopyTo(bytes.AsSpan(header - 263));
        Assert.Equal(0, bytes[28] & 0x04);
        File.WriteAllBytes(path, bytes);
        using var table = Open(path);
        Assert.Equal(DbfErrorCode.UnsupportedFormat, Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK"))).Code);
    }

    [Fact]
    public void A_varchar_table_is_refused()
    {
        var path = Table(DbfTableFormat.VisualFoxPro, tag: false);
        var bytes = File.ReadAllBytes(path);
        bytes[0] = 0x32;
        File.WriteAllBytes(path, bytes);
        using var table = Open(path);
        Assert.Equal(DbfErrorCode.UnsupportedFormat, Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK"))).Code);
    }

    // ---- filesystem failures, explained without paths -------------------------------------

    [Fact]
    public void A_folder_that_cant_be_written_is_explained_without_its_path()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return; // the permission bits below are Unix's, and root ignores them
        var path = Table(DbfTableFormat.DbaseIV);
        using var table = Open(path);
        File.SetUnixFileMode(_dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var ex = Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK")));
            Assert.Equal(DbfErrorCode.AccessDenied, ex.Code);
            Assert.DoesNotContain(_dir.FullName, ex.Message);
            Assert.DoesNotContain(_dir.FullName, ex.Hint);
            Assert.Equal("Ada", table.Read(0)["NAME"]); // still open, still the old table
        }
        finally
        {
            File.SetUnixFileMode(_dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_leftover_folder_that_cant_be_cleared_is_explained_without_its_path()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;
        var path = Table(DbfTableFormat.DbaseIV);
        var stuck = Path.Combine(path + ".restructure-partial", "inner");
        Directory.CreateDirectory(stuck);
        File.WriteAllText(Path.Combine(stuck, "x"), "x");
        File.SetUnixFileMode(stuck, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var table = Open(path);
            var ex = Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK")));
            Assert.Equal(DbfErrorCode.AccessDenied, ex.Code);
            Assert.DoesNotContain(_dir.FullName, ex.Message);
        }
        finally
        {
            File.SetUnixFileMode(stuck, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void An_index_held_open_is_explained_as_in_use_without_its_path()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using var old = Open(path);
        using (old.OpenProductionIndex())
        {
            var ex = Assert.Throws<DbfException>(() => old.AddField(DbfFieldDefinition.Logical("OK")));
            Assert.Equal(DbfErrorCode.FileInUse, ex.Code);
            Assert.DoesNotContain(_dir.FullName, ex.Message);
        }
    }

    // ---- DELETE TAG -----------------------------------------------------------------------

    [Theory]
    [InlineData(DbfTableFormat.DbaseIV)]
    [InlineData(DbfTableFormat.FoxPro2)]
    [InlineData(DbfTableFormat.VisualFoxPro)]
    public void DropIndexTag_removes_one_tag_and_keeps_the_rest_working(DbfTableFormat format)
    {
        var path = Table(format);
        using (var table = Open(path))
        {
            table.CreateIndexTag("AGE", "AGE");
            table.CreateIndexTag("HIRED", "HIRED");
            table.DropIndexTag("age");
            table.Append("Kay", 50m, "", new DateOnly(1950, 1, 1));
            Assert.Equal(DbfErrorCode.InvalidValue, Assert.Throws<DbfException>(() => table.DropIndexTag("AGE")).Code);
        }
        using var reopened = Open(path);
        using IDbfIndex index = (IDbfIndex?)reopened.OpenProductionIndex() ?? reopened.OpenStructuralIndex()!;
        Assert.Equal(["HIRED", "NAME"], index.Tags.Select(t => t.Name).Order());
        Assert.Equal(5, index.Tag("HIRED").Entries().Count());
        Assert.Equal(5, index.Tag("NAME").Entries().Count());
        reopened.RebuildIndex();
    }

    [Fact]
    public void DropIndexTag_takes_tags_out_of_a_cdx_and_frees_their_pages()
    {
        var path = Kinds();
        var cdxLength = new FileInfo(Path.ChangeExtension(path, ".CDX")).Length;
        using (var table = Open(path))
        {
            table.DropIndexTag("CODE");
            table.DropIndexTag("STAMP");
        }
        // The two tags' pages went on the free list, for FoxPro to reuse; nothing was cut from the file.
        var cdx = File.ReadAllBytes(Path.ChangeExtension(path, ".CDX"));
        Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(cdx.AsSpan(4)));
        Assert.Equal(cdxLength, cdx.Length);

        using var reopened = Open(path); // the .cdx is checked again, every page with one owner
        using var index = reopened.OpenStructuralIndex()!;
        Assert.DoesNotContain(index.Tags, t => t.Name is "CODE" or "STAMP");
        Assert.Contains(index.Tags, t => t.Name == "CODEWHEN");
        reopened.Append(201, "C00001", "Zed", 1m, 1m, new DateOnly(2026, 1, 1), new DateTime(2026, 1, 1), true, 1d, 1m, "A", "", "x");
        Assert.Equal(201, index["CODEWHEN"].Entries().Count());
    }

    // dBASE finds a tag by walking the name tree from the anchor (byte 17 of the slot before the
    // table), comparing names; it never scans the table. Every tag listed must be found that way.
    private static bool NameTreeFinds(byte[] mdx, string name)
    {
        var entryLength = mdx[26] == 0 ? 32 : mdx[26];
        var count = BinaryPrimitives.ReadUInt16LittleEndian(mdx.AsSpan(28));
        var slot = (int)mdx[544 - entryLength + 17];
        var seen = new HashSet<int>();
        while (slot != 0)
        {
            if (slot > count || !seen.Add(slot))
                return false;
            var entry = 544 + entryLength * (slot - 1);
            var here = System.Text.Encoding.ASCII.GetString(mdx, entry + 4, 11).TrimEnd('\0', ' ');
            var order = string.CompareOrdinal(name.ToUpperInvariant(), here.ToUpperInvariant());
            if (order == 0)
                return true;
            slot = order < 0 ? mdx[entry + 16] : mdx[entry + 17];
        }
        return false;
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("count")]
    [InlineData("cleared")]
    [InlineData(null)]
    public void After_every_write_of_a_DELETE_TAG_dBASE_s_name_tree_finds_every_tag_left(string? killedAfter)
    {
        var path = Table(DbfTableFormat.DbaseIV, tag: false);
        using (var table = Open(path))
        {
            table.CreateIndexTag("B", "NAME");
            table.CreateIndexTag("A", "AGE");
            table.CreateIndexTag("C", "HIRED");
            MdxIndex.DropTagPoint = point => { if (point == killedAfter) throw new Killed(); };
            try
            {
                if (killedAfter is null)
                    table.DropIndexTag("B");
                else
                    Assert.Throws<Killed>(() => table.DropIndexTag("B"));
            }
            finally
            {
                MdxIndex.DropTagPoint = null;
            }
        }
        var mdx = File.ReadAllBytes(MdxIndex.FindFor(path)!);
        Assert.True(NameTreeFinds(mdx, "A"));
        Assert.True(NameTreeFinds(mdx, "C"));
        Assert.False(NameTreeFinds(mdx, "B"));
        // Cut off, the drop is undone by the next exclusive open, from the before-image beside the .mdx.
        using var reopened = Open(path);
        using var index = reopened.OpenProductionIndex()!;
        Assert.Equal(killedAfter is null ? ["A", "C"] : ["B", "A", "C"], index.Tags.Select(t => t.Name));
        Assert.False(File.Exists(MdxIndex.FindFor(path)! + ".droptag"));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("M")]
    [InlineData("Z")]
    public void Dropping_any_tag_of_several_leaves_a_name_tree_that_finds_the_rest(string dropped)
    {
        var path = Table(DbfTableFormat.DbaseIV, tag: false);
        var names = new[] { "M", "D", "T", "A", "G", "Z", "P" };
        using (var table = Open(path))
        {
            foreach (var name in names)
                table.CreateIndexTag(name, "NAME");
            table.DropIndexTag(dropped);
        }
        var mdx = File.ReadAllBytes(MdxIndex.FindFor(path)!);
        foreach (var name in names)
            Assert.Equal(name != dropped, NameTreeFinds(mdx, name));
    }

    [Fact]
    public void Dropping_the_only_tag_leaves_an_empty_name_tree()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
            table.DropIndexTag("NAME");
        var mdx = File.ReadAllBytes(MdxIndex.FindFor(path)!);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(mdx.AsSpan(28)));
        Assert.Equal(0, mdx[544 - 32 + 17]);
    }

    [Fact]
    public void An_mdx_tag_table_cut_off_between_moving_entries_and_the_count_still_lists_each_tag_once()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
        {
            table.CreateIndexTag("AGE", "AGE");
            table.CreateIndexTag("HIRED", "HIRED");
        }
        // What DropIndexTag("NAME") writes first: the entries after NAME's moved down a slot, the count not yet lowered.
        var mdx = MdxIndex.FindFor(path)!; // PEOPLE.MDX: Linux file names keep their case
        var bytes = File.ReadAllBytes(mdx);
        var entry = bytes[26];
        Array.Copy(bytes, 544 + entry, bytes, 544, entry * 2);
        File.WriteAllBytes(mdx, bytes);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)));

        using var reopened = Open(path);
        using var index = reopened.OpenProductionIndex()!;
        Assert.Equal(["AGE", "HIRED"], index.Tags.Select(t => t.Name));
        reopened.Append("Kay", 50m, "", new DateOnly(1950, 1, 1));
        Assert.Equal(5, index["HIRED"].Entries().Count());
    }
}
