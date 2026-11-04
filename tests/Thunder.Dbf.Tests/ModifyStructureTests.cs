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
            table.CrashPoint = point => { if (point == "copying") throw new Killed(); };
            Assert.Throws<Killed>(() => table.AddField(DbfFieldDefinition.Character("CITY", 20)));
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
    [InlineData(DbfTableFormat.DbaseIV, "memo swapped")]
    [InlineData(DbfTableFormat.FoxPro2, "memo swapped")]
    [InlineData(DbfTableFormat.VisualFoxPro, "staged")]
    [InlineData(DbfTableFormat.VisualFoxPro, "memo swapped")]
    public void Killed_before_the_table_is_swapped_the_next_exclusive_open_puts_the_old_table_back(DbfTableFormat format, string step)
    {
        var path = Table(format);
        KilledAt(path, step);

        // Until an exclusive open settles it, shared writes refuse.
        using (var shared = Open(path, exclusive: false))
            Assert.Equal(DbfErrorCode.IncompleteTransaction,
                Assert.Throws<DbfException>(() => shared.Append("Kay", 50m, "", new DateOnly(1950, 1, 1))).Code);

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
    [InlineData("memo swapped")]
    public void Killed_before_the_table_is_swapped_the_dbf_is_byte_for_byte_the_old_one(string step)
    {
        var path = Table(DbfTableFormat.VisualFoxPro);
        var dbf = File.ReadAllBytes(path);
        KilledAt(path, step);
        Assert.Equal(dbf, File.ReadAllBytes(path));
        using (Open(path))
        {
        }
        Assert.Equal(dbf, File.ReadAllBytes(path));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Killed_after_the_table_is_swapped_the_next_exclusive_open_finishes_the_change(DbfTableFormat format)
    {
        var path = Table(format);
        KilledAt(path, "table swapped");

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
        table.CrashPoint = point => { if (point == step) throw new Killed(); };
        var fields = table.FieldDefinitions.ToList();
        fields[0] = DbfFieldDefinition.Character("NAME", 20); // the tag's keys widen too
        fields.Add(DbfFieldDefinition.Character("CITY", 20));
        Assert.Throws<Killed>(() => table.ModifyStructure(fields));
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
            table.CrashPoint = point => { if (point == "memo swapped") throw new Killed(); };
            Assert.Throws<Killed>(() => table.AddField(DbfFieldDefinition.Memo("NOTE")));
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
            table.CrashPoint = point => { if (point == "table swapped") throw new Killed(); };
            Assert.Throws<Killed>(() => table.AddField(DbfFieldDefinition.Memo("NOTE")));
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
