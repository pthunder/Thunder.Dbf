//      new key ----> record ----> old key
//         ^            ^            ^          ProjectThunder :: Thunder.Dbf tests
//       crash?       crash?       crash?       Kill a writer anywhere in a key change: the record
//                                              is always still found, and with the journal on, the
//                                              next writer leaves the index exactly right.

namespace Thunder.Dbf.Tests;

public sealed class CrashSafeUpdateTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-crash-");

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class Killed : Exception;

    private string Table()
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        using var table = DbfTable.Create(path, [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Numeric("AGE", 3)]);
        foreach (var (name, age) in new[] { ("Ada", 36m), ("Grace", 45m), ("Hedy", 41m), ("Joan", 30m) })
            table.Append(name, age);
        table.CreateIndexTag("NAME", "NAME");
        table.CreateIndexTag("AGE", "AGE");
        return path;
    }

    // Record 2 (Grace, 45) becomes (Zelda, 99), and the writer is killed at the given step.
    private static void KilledUpdate(string path, string step, bool journal)
    {
        using var table = DbfTable.Open(path, writable: true);
        table.UseJournal = journal;
        table.CrashPoint = point => { if (point == step) throw new Killed(); };
        Assert.Throws<Killed>(() => table.UpdateFields(1, new Dictionary<string, object?> { ["NAME"] = "Zelda", ["AGE"] = 99m }));
    }

    private static List<(string Tag, string Key, int Record)> Entries(string path)
    {
        using var table = DbfTable.Open(path);
        using var index = table.OpenProductionIndex()!;
        return index.Tags.SelectMany(t => t.Entries().Select(e => (t.Name, Convert.ToString(e.Key, System.Globalization.CultureInfo.InvariantCulture)!.Trim(), e.RecordNumber)))
            .OrderBy(e => e.Name).ThenBy(e => e.Item2).ThenBy(e => e.RecordNumber).ToList();
    }

    private static List<(string Tag, string Key, int Record)> Rebuilt(string path)
    {
        var copy = Path.Combine(Path.GetDirectoryName(path)!, "copy");
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, "PEOPLE.*"))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), overwrite: true);
        var copied = Path.Combine(copy, "PEOPLE.DBF");
        using (var table = DbfTable.Open(copied, writable: true, exclusive: true))
            table.RebuildIndex();
        return Entries(copied);
    }

    [Theory]
    [InlineData("new keys")]
    [InlineData("record")]
    public void Without_the_journal_a_killed_update_leaves_an_extra_key_never_a_missing_one(string step)
    {
        var path = Table();
        KilledUpdate(path, step, journal: false);

        using var table = DbfTable.Open(path);
        var name = (string)table.Read(1)["NAME"]!;
        using var index = table.OpenProductionIndex()!;
        // Whatever the record holds now, its tag finds it.
        Assert.Contains(index["NAME"].Entries(), e => e.RecordNumber == 2 && ((string)e.Key).Trim() == name);
        Assert.Contains(index["NAME"].Entries(), e => e.RecordNumber == 2 && ((string)e.Key).Trim() == "Zelda");
        Assert.Equal(step == "new keys" ? "Grace" : "Zelda", name);
    }

    [Theory]
    [InlineData("new keys")]
    [InlineData("record")]
    public void With_the_journal_the_next_write_finishes_the_change(string step)
    {
        var path = Table();
        KilledUpdate(path, step, journal: true);
        Assert.True(File.Exists(path + ".journal"));
        Assert.NotEqual(Rebuilt(path), Entries(path));

        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Tables.Single(t => t.Name.Equals("PEOPLE", StringComparison.OrdinalIgnoreCase)).Warnings, w => w.Contains("journal"));

        using (var table = DbfTable.Open(path, writable: true))
        {
            Assert.True(table.HasPendingJournal);   // opening alone doesn't touch it: locks may not be set up yet
            table.UpdateFields(3, new Dictionary<string, object?> { ["AGE"] = 31m });
            Assert.False(table.HasPendingJournal);
        }
        Assert.Equal(Rebuilt(path), Entries(path));
    }

    [Fact]
    public void A_shared_writer_finishes_the_note_before_its_own_write()
    {
        var path = Table();
        KilledUpdate(path, "record", journal: true);
        using var other = DbfTable.Open(path, writable: true);
        // A note that appears while another table is open is finished by that table's next write.
        other.UpdateFields(0, new Dictionary<string, object?> { ["AGE"] = 37m });
        Assert.False(other.HasPendingJournal);
        other.Dispose();
        Assert.Equal(Rebuilt(path), Entries(path));
    }

    [Fact]
    public void A_torn_note_was_cut_off_before_any_index_change_and_is_dropped()
    {
        var path = Table();
        var before = Entries(path);
        File.WriteAllBytes(path + ".journal", "TDJ2 torn"u8.ToArray());
        using (var table = DbfTable.Open(path, writable: true))
        {
            table.UpdateFields(0, new Dictionary<string, object?> { ["AGE"] = 36m });
            Assert.False(table.HasPendingJournal);
        }
        Assert.Equal(before, Entries(path));
    }

    [Fact]
    public void A_rebuild_clears_the_note()
    {
        var path = Table();
        KilledUpdate(path, "new keys", journal: true);
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.RebuildIndex();
        Assert.False(File.Exists(path + ".journal") && new FileInfo(path + ".journal").Length > 0);
    }

    [Fact]
    public void A_connection_string_turns_the_journal_on()
    {
        Table();
        using var db = DbfDatabase.Open($"Data Source={_dir.FullName};Read Only=false;Journal=true");
        Assert.True(db["PEOPLE"].UseJournal);
        db["PEOPLE"].UpdateFields(0, new Dictionary<string, object?> { ["NAME"] = "Ada L" });
        Assert.Equal(0, new FileInfo(Path.Combine(_dir.FullName, "PEOPLE.DBF.journal")).Length);
    }

    // A UNIQUE tag keeps one record per key, so its change isn't made ahead of the record: there,
    // handing the key over first would point it at a record that doesn't hold it yet.
    [Theory]
    [InlineData("new keys")]
    [InlineData("record")]
    public void A_UNIQUE_tag_changes_after_the_record_and_the_journal_finishes_it(string step)
    {
        var path = Table();
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("UAGE", "AGE", unique: true);
        KilledUpdate(path, step, journal: true);

        using (var table = DbfTable.Open(path, writable: true))
        {
            using (var index = table.OpenProductionIndex()!)
            {
                var holder = index["UAGE"].Entries().Where(e => Convert.ToDecimal(e.Key) == 45m).Select(e => e.RecordNumber);
                // Cut off at either step, the UNIQUE tag hasn't changed yet: record 2 still holds 45.
                Assert.Equal([2], holder.ToArray());
            }
            table.UpdateFields(3, new Dictionary<string, object?> { ["AGE"] = 31m });
        }
        Assert.Equal(Rebuilt(path), Entries(path));
    }

    // A note this table can't match (the cut-off writer kept an index this one doesn't) stops writes
    // rather than being written over and lost.
    [Fact]
    public void A_note_for_an_index_this_table_doesnt_keep_stops_writes()
    {
        var path = Table();
        KilledUpdate(path, "record", journal: true);
        var mdx = Path.ChangeExtension(path, ".MDX");
        File.Move(mdx, mdx + ".away");
        File.Move(path + ".journal", path + ".journal.keep");
        using (var fresh = DbfTable.Open(path, writable: true, exclusive: true, allowUnmaintainedIndexes: true))
            fresh.CreateIndexTag("OTHER", "NAME");   // a different .mdx now, without tags NAME and AGE
        File.Move(path + ".journal.keep", path + ".journal");

        using var table = DbfTable.Open(path, writable: true);
        var error = Assert.Throws<DbfException>(() => table.UpdateFields(0, new Dictionary<string, object?> { ["AGE"] = 37m }));
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        Assert.Contains("cut off", error.Message);
        Assert.True(table.HasPendingJournal);
    }

    [Fact]
    public void Replaying_never_drops_a_key_the_record_has()
    {
        var path = Table();
        KilledUpdate(path, "record", journal: true);
        using var table = DbfTable.Open(path, writable: true);
        table.UpdateFields(0, new Dictionary<string, object?> { ["AGE"] = 36m });   // finishes the note
        using var index = table.OpenProductionIndex()!;
        Assert.Contains(index["NAME"].Entries(), e => e.RecordNumber == 2 && ((string)e.Key).Trim() == "Zelda");
        Assert.DoesNotContain(index["NAME"].Entries(), e => e.RecordNumber == 2 && ((string)e.Key).Trim() == "Grace");
    }

    // A killed update leaves a .cdx tag with an extra key, which the open-time count check refuses.
    // With a note waiting, the index is attached on probation, the next write finishes the note,
    // and the index passes its checks again.
    [Fact]
    public void A_VFP_index_left_with_an_extra_key_is_put_right_by_the_journal()
    {
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(IndexTestSupport.Fixture("infused-dbf/foxprodb/calls.dbf"))!, "calls.*"))
            File.Copy(file, Path.Combine(_dir.FullName, Path.GetFileName(file)));
        var path = Path.Combine(_dir.FullName, "calls.dbf");
        int oldId;
        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true))
        {
            Assert.True(table.MaintainsIndex);
            table.UseJournal = true;
            oldId = (int)table.Read(3)["CALL_ID"]!;
            table.CrashPoint = point => { if (point == "record") throw new Killed(); };
            Assert.Throws<Killed>(() => table.UpdateFields(3, new Dictionary<string, object?> { ["CALL_ID"] = 70_000 }));
        }

        using (var shared = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true))
        {
            Assert.True(shared.MaintainsIndex);    // on probation while the note waits
            shared.UpdateFields(0, new Dictionary<string, object?> { ["SUBJECT"] = "after" });
            Assert.False(shared.HasPendingJournal);
        }

        using (var reopened = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true))
            Assert.True(reopened.MaintainsIndex);  // passes the count check on its own now
        using var check = DbfTable.Open(path);
        using var index = CdxIndex.Open(Path.ChangeExtension(path, ".CDX"), table: check);
        Assert.Equal(check.RecordCount, index["CALL_ID"].Entries().Count());
        Assert.Equal(4, Assert.Single(index["CALL_ID"].SeekExact(70_000)).RecordNumber);
        Assert.Empty(index["CALL_ID"].SeekExact(oldId));
    }

    // A deleted record leaves a UNIQUE ... FOR .NOT. DELETED() tag after its record is written; cut
    // off there, the note takes its key out.
    [Fact]
    public void A_record_leaving_a_UNIQUE_FOR_tag_is_finished_by_the_journal()
    {
        var path = Table();
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("LIVE", "NAME", forExpression: ".NOT. DELETED()", unique: true);
        using (var table = DbfTable.Open(path, writable: true))
        {
            table.UseJournal = true;
            table.CrashPoint = point => { if (point == "record") throw new Killed(); };
            Assert.Throws<Killed>(() => table.Delete(1));
        }
        using (var table = DbfTable.Open(path, writable: true))
            table.UpdateFields(0, new Dictionary<string, object?> { ["AGE"] = 37m });
        Assert.Equal(Rebuilt(path), Entries(path));
        using var check = DbfTable.Open(path);
        using var index = check.OpenProductionIndex()!;
        Assert.DoesNotContain(index["LIVE"].Entries(), e => e.RecordNumber == 2);
    }

    [Fact]
    public void A_whole_note_in_another_format_stops_writes_until_a_rebuild()
    {
        var path = Table();
        var body = "TDJ1"u8.ToArray().Concat(new byte[12]).ToArray();
        File.WriteAllBytes(path + ".journal", [.. body, .. System.Security.Cryptography.SHA256.HashData(body)]);
        using (var table = DbfTable.Open(path, writable: true))
            Assert.Equal(DbfErrorCode.IndexWouldGoStale,
                Assert.Throws<DbfException>(() => table.UpdateFields(0, new Dictionary<string, object?> { ["AGE"] = 37m })).Code);
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.RebuildIndex();
        Assert.Equal(0, new FileInfo(path + ".journal").Length);
    }

    // Found against Visual FoxPro 9: an append cut off after its record but before the header's count
    // left a record past the count (VFP error 2091), which only an exclusive open took off. Noted in
    // the journal, it's the next write's to finish, shared or not.
    [Theory]
    [InlineData("appended record")]
    [InlineData("counted")]
    public void A_journaled_append_cut_off_is_finished_by_the_next_shared_write(string step)
    {
        var path = Table();
        var committed = new FileInfo(path).Length;
        using (var table = DbfTable.Open(path, writable: true))
        {
            table.UseJournal = true;
            table.CrashPoint = point => { if (point == step) throw new Killed(); };
            Assert.Throws<Killed>(() => table.Append("Katherine", 50m));   // AppendRange would tidy up after an exception; a kill doesn't
        }
        Assert.True(File.Exists(path + ".journal") && new FileInfo(path + ".journal").Length > 0);

        using (var table = DbfTable.Open(path, writable: true))
        {
            table.UpdateFields(0, new Dictionary<string, object?> { ["AGE"] = 37m });
            Assert.False(table.HasPendingJournal);
            Assert.Equal(step == "counted" ? 5 : 4, table.RecordCount);
            Assert.Equal(0, table.RecordsPastCount());
        }
        if (step == "appended record")
            Assert.Equal(committed, new FileInfo(path).Length);
        Assert.Equal(Rebuilt(path), Entries(path));
    }

    [Fact]
    public void Without_the_journal_a_record_past_the_count_waits_for_an_exclusive_open()
    {
        var path = Table();
        using (var table = DbfTable.Open(path, writable: true))
        {
            table.CrashPoint = point => { if (point == "appended record") throw new Killed(); };
            Assert.Throws<Killed>(() => table.Append("Katherine", 50m));
        }
        using (var table = DbfTable.Open(path, writable: true))
        {
            table.UpdateFields(0, new Dictionary<string, object?> { ["AGE"] = 37m });
            Assert.Equal(1, table.RecordsPastCount());
        }
        using (DbfTable.Open(path, writable: true, exclusive: true)) { }
        using var check = DbfTable.Open(path);
        Assert.Equal(0, check.RecordsPastCount());
    }

    // A note's hash only says it's whole. One that's whole but makes no sense (damaged, or not
    // written by Thunder.Dbf) is refused with an explanation, never read past its end.
    public static TheoryData<string, byte[]> HostileNotes()
    {
        static byte[] Note(byte kind, int record, int count, params byte[] rest) =>
            [.. "TDJ3"u8, kind, .. BitConverter.GetBytes(record), .. BitConverter.GetBytes(count), .. rest];
        return new()
        {
            { "a count far past the note", Note((byte)'U', 2, int.MaxValue) },
            { "a negative count", Note((byte)'U', 2, -1) },
            { "a negative record", Note((byte)'U', -5, 0) },
            { "an unknown kind", Note((byte)'X', 2, 0) },
            { "a name running past the end", Note((byte)'U', 2, 1, 200, (byte)'A') },
            { "a key running past the end", Note((byte)'U', 2, 1, 0, 0, 0x10, 0x00, 1, 2) },
            { "a key longer than any index holds", Note((byte)'U', 2, 1, [0, 0, 0x00, 0x7F, .. new byte[0x7F00]]) },
            { "bytes left over", Note((byte)'U', 2, 0, 1, 2, 3) },
        };
    }

    [Theory]
    [MemberData(nameof(HostileNotes))]
    public void A_whole_note_that_makes_no_sense_is_refused_with_an_explanation(string what, byte[] body)
    {
        _ = what;
        var path = Table();
        File.WriteAllBytes(path + ".journal", [.. body, .. System.Security.Cryptography.SHA256.HashData(body)]);
        using var table = DbfTable.Open(path, writable: true);
        var error = Assert.Throws<DbfException>(() => table.UpdateFields(0, new Dictionary<string, object?> { ["AGE"] = 37m }));
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    // Clipper's .ntx keys run to 256 bytes, longer than any other format's: a note holding one reads
    // back whole (review of #17), and a longer key is refused when the note is written, not when it's read.
    [Fact]
    public void A_note_holds_keys_as_long_as_any_index_has()
    {
        var path = Table();
        var longest = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        DbfJournal.Write(path, new DbfJournalEntry(2, [new DbfJournalChange("PEOPLE.NTX", "PEOPLE", longest, [.. longest.Reverse()])]));
        var read = DbfJournal.Read(path)!;
        Assert.Equal(longest, read.Changes.Single().OldKey);
        Assert.Equal(longest.Reverse(), read.Changes.Single().NewKey);

        Assert.Throws<ArgumentException>(() => DbfJournal.Write(path, new DbfJournalEntry(2, [new DbfJournalChange("PEOPLE.NTX", "PEOPLE", null, new byte[257])])));
    }
}
