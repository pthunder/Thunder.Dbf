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
        File.WriteAllBytes(path + ".journal", "TDJ1 torn"u8.ToArray());
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
}
