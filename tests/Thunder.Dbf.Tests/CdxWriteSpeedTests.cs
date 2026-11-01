//   write 1: lock, check everything, change, remember (length, counter)
//   write 2: lock, (length, counter) as we left them?  yes -> change   no -> check everything again
//
//   ProjectThunder :: Thunder.Dbf tests
//   The checks a write skips when nothing changed since the last one, and the flush setting. Each
//   shortcut must still notice another program's change before the next write.

using static Thunder.Dbf.Tests.IndexTestSupport;

namespace Thunder.Dbf.Tests;

public sealed partial class CdxWriteTests
{
    private static DbfTable OpenShared(string dbf) => DbfTable.Open(dbf, writable: true, maintainFoxProIndexes: true);

    [Fact]
    public void Writes_are_forced_to_disk_unless_that_is_turned_off()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using (var table = OpenWritable(dbf))
        {
            Assert.True(table.FlushWritesToDisk);
            table.FlushWritesToDisk = false;
            var template = table.Read(0).Values.ToArray();
            for (var id = 5000; id < 5300; id++)
                table.Append(Call(template, id, contact: id % 7, subject: $"fast {id}"));
            table.Update(3, Call(template, id: 6000, contact: 2, subject: "moved"));
            table.Delete(10);
        }
        AssertIndexMatchesTable(dbf, cdx);

        // The journal's note is emptied once what it covers is on disk, so it keeps every write forced.
        using (var journaled = OpenShared(dbf))
        {
            journaled.FlushWritesToDisk = false;
            Assert.False(journaled.ForcesWrites);
            journaled.UseJournal = true;
            Assert.True(journaled.ForcesWrites);
        }

        Assert.True(new DbfDatabaseOptions().FlushWritesToDisk);
        using var db = DbfDatabase.Open($"Data Source={_dir.FullName};Read Only=false;Maintain FoxPro Indexes=true;Flush Writes To Disk=false");
        Assert.False(db.Options.FlushWritesToDisk);
    }

    // Two programs taking turns: each write finds the other's change by the update counter, reloads
    // what it had skipped reloading, and leaves every key in place.
    [Fact]
    public void Writes_taking_turns_with_another_writer_see_each_others_changes()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using (var first = OpenShared(dbf))
        using (var second = OpenShared(dbf))
        {
            var template = first.Read(0).Values.ToArray();
            for (var round = 0; round < 40; round++)
            {
                first.Append(Call(template, 5000 + round, contact: round % 5, subject: $"first {round}"));
                first.Append(Call(template, 6000 + round, contact: round % 3, subject: $"first again {round}"));
                second.Append(Call(template, 7000 + round, contact: round % 4, subject: $"second {round}"));
                if (round % 4 == 0)
                    second.Update(round, Call(template, 8000 + round, contact: 9, subject: "second's update"));
            }
        }
        AssertIndexMatchesTable(dbf, cdx);
    }

    // A tag rebuilt by another program between two writes bumps the counter: the next write
    // reloads the tags, and checks them, rather than trusting the ones it had.
    [Fact]
    public void A_tag_another_program_rebuilt_between_writes_is_reloaded_first()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using (var table = OpenShared(dbf))
        {
            var template = table.Read(0).Values.ToArray();
            table.Append(Call(template, 5000, contact: 1, subject: "before"));

            // What a REINDEX elsewhere does to this process's picture: a tag's header rewritten, at
            // another key length, and the counter bumped. Writing on would build wrong-length keys.
            var bytes = SharedFile.ReadAllBytes(cdx);
            var tag = CdxIndex.Open(new MemoryStream(bytes)).Tags[0];
            var header = (int)tag.HeaderOffset;
            BitConverter.TryWriteBytes(bytes.AsSpan(header + 12, 2), (ushort)(tag.KeyLength + 1));
            bytes[8 + 3]++;
            SharedFile.WriteAllBytes(cdx, bytes);

            var error = Assert.Throws<DbfException>(() => table.Append(Call(template, 5002, contact: 3, subject: "after")));
            Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
            Assert.Equal(17, table.RecordCount); // refused before the record was written
        }
    }

    // Visual FoxPro changes keys under its own index lock, not the table's, so it can change the .cdx
    // after this process reloaded its tags and before it takes the index lock. The pages it cached
    // from its last write are stale then: the moved counter has to keep them from being used.
    [Fact]
    public void Pages_another_program_changed_since_the_last_write_are_read_again()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using (var table = OpenShared(dbf))
        {
            var template = table.Read(0).Values.ToArray();
            table.Append(Call(template, 5000, contact: 1, subject: "fills the cache"));
            var contact = table.Fields.Single(f => f.Name == "CONTACT_ID");
            var before = table.Read(2);
            table.CrashPoint = point =>
            {
                if (point != "counted")
                    return;
                table.CrashPoint = null;

                // VFP's REPLACE contact_id WITH 77 on record 3: the field, then the key moved, counter bumped.
                using (var data = SharedFile.OpenWrite(dbf))
                {
                    data.Position = table.Header.HeaderLength + 2L * table.Header.RecordLength + contact.Offset;
                    Assert.Equal('I', contact.Type);
                    data.Write(BitConverter.GetBytes(77));
                }
                using var reader = DbfTable.Open(dbf);
                using var other = CdxIndex.Open(new FileStream(cdx, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite), reader.Encoding, table: reader);
                var tag = (IMaintainedTag)other.Tags.Single(t => t.Name == "CONTACT_ID");
                Assert.True(tag.Remove(tag.KeyFor(before), 3));
                tag.Insert(tag.KeyFor(reader.Read(2)), 3);
                other.Commit();
            };
            table.Append(Call(template, 5001, contact: 2, subject: "after VFP's change"));
            Assert.Equal(77m, Convert.ToDecimal(table.Read(2)["CONTACT_ID"]));
        }
        AssertIndexMatchesTable(dbf, cdx);
    }

    // The folder isn't listed again while its last-write time is unchanged; a new file changes it.
    [Fact]
    public void An_index_made_after_the_first_write_still_stops_the_next_one()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using var table = OpenWritable(dbf);
        var template = table.Read(0).Values.ToArray();
        table.Append(Call(template, 5000, contact: 1, subject: "first"));
        table.Append(Call(template, 5001, contact: 1, subject: "second"));

        File.Copy(cdx, Path.Combine(_dir.FullName, "calls.idx"));
        if (table.WatchesFolder) // Windows: the watcher's event comes a moment after the file
            Assert.True(SpinWait.SpinUntil(() => table.FolderLookPending, TimeSpan.FromSeconds(10)));
        var error = Assert.Throws<DbfException>(() => table.Append(Call(template, 5002, contact: 1, subject: "third")));
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        Assert.Contains("appeared after it was opened", error.Message);
    }

    // The watcher Windows uses (started by hand here, so it runs everywhere): quiet while nothing is
    // made next to the table, and a new index file makes the next write look and refuse.
    [Fact]
    public void A_watched_folder_reports_a_new_index_to_the_next_write()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using var table = OpenShared(dbf);
        table.WatchFolder();
        Assert.True(table.WatchesFolder);
        var template = table.Read(0).Values.ToArray();
        table.Append(Call(template, 5000, contact: 1, subject: "looks once"));
        table.Append(Call(template, 5001, contact: 1, subject: "nothing to look for"));
        Assert.False(table.FolderLookPending);

        File.Copy(cdx, Path.Combine(_dir.FullName, "calls.idx"));
        Assert.True(SpinWait.SpinUntil(() => table.FolderLookPending, TimeSpan.FromSeconds(10)), "the watcher never reported the new file");
        var error = Assert.Throws<DbfException>(() => table.Append(Call(template, 5002, contact: 1, subject: "refused")));
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        Assert.True(table.FolderLookPending); // refused, so the next write looks again
    }

    // DeferIndexWrites: a batch under LockTable() writes each changed .cdx page once, when the lock
    // goes. The index then holds exactly what per-record writes would have left.
    [Fact]
    public void A_deferred_batch_leaves_the_index_exactly_as_record_by_record_writes_would()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using (var table = OpenShared(dbf))
        {
            table.DeferIndexWrites = true;
            table.FlushWritesToDisk = false;
            var template = table.Read(0).Values.ToArray();
            using (table.LockTable())
            {
                for (var id = 5000; id < 5600; id++)
                    table.Append(Call(template, id, contact: id % 13, subject: $"batch {id}"));
                table.Update(4, Call(template, id: 9000, contact: 3, subject: "changed in the batch"));
                table.Delete(7);
                var onDisk = SharedFile.ReadAllBytes(cdx);
                Assert.Equal(new byte[0x200], onDisk[^0x200..]); // the batch's newest page: still blank on disk
            }
            table.Append(Call(template, 9100, contact: 1, subject: "after the batch"));
        }
        AssertIndexMatchesTable(dbf, cdx);
    }

    // Inside a batch the cache shares its nodes rather than copying them: thousands of mixed changes
    // across X#'s seven tags (splits, keys moving, records deleted and recalled) must still leave
    // exactly the keys record-by-record writes would.
    [Fact]
    public void A_long_mixed_batch_over_seven_tags_leaves_every_key_right()
    {
        var (dbf, cdx) = People();
        using (var table = TakeOver(dbf, cdx))
        {
            table.DeferIndexWrites = true;
            table.FlushWritesToDisk = false;
            using (table.LockTable())
                Mutate(table, new Random(23), steps: 3000);
            Assert.False(table.HasPendingJournal);
        }
        AssertIndexMatchesTable(dbf, cdx, rebuilt: true);
    }

    // What a process killed during a batch leaves on disk: the records, and an index without their
    // keys. It isn't silent: the next writable open refuses the index until it's rebuilt.
    [Fact]
    public void A_batch_cut_off_leaves_an_index_the_next_open_reports()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        var killed = Directory.CreateDirectory(Path.Combine(_dir.FullName, "killed")).FullName;
        using (var table = OpenShared(dbf))
        {
            table.DeferIndexWrites = true;
            var template = table.Read(0).Values.ToArray();
            using (table.LockTable())
            {
                for (var id = 5000; id < 5050; id++)
                    table.Append(Call(template, id, contact: 2, subject: "cut off"));
                foreach (var file in Directory.EnumerateFiles(_dir.FullName))
                    File.WriteAllBytes(Path.Combine(killed, Path.GetFileName(file)), SharedFile.ReadAllBytes(file));
            }
        }
        var error = Assert.Throws<DbfException>(() =>
        {
            using var reopened = DbfTable.Open(Path.Combine(killed, "calls.dbf"), writable: true, maintainFoxProIndexes: true);
            reopened.UpdateFields(0, new Dictionary<string, object?> { ["SUBJECT"] = "refused" });
        });
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        AssertIndexMatchesTable(dbf, cdx); // the batch that finished is whole

        // The documented way back: RebuildIndex() on an exclusive open.
        var cutOff = Path.Combine(killed, "calls.dbf");
        using (var exclusive = DbfTable.Open(cutOff, writable: true, maintainFoxProIndexes: true, exclusive: true))
        {
            exclusive.RebuildIndex();
            Assert.True(exclusive.MaintainsIndex);
            Assert.False(exclusive.HasPendingJournal);
        }
        AssertIndexMatchesTable(cutOff, Path.Combine(killed, "calls.CDX"), rebuilt: true);
    }

    // A structural .cdx refused when the table opened (short of the table's records here) is what
    // RebuildIndex() is for: it takes the index over and rebuilds it (found by the Visual FoxPro kill
    // tests, where it said "no index open" and only MaintainIndex(path, rebuild: true) would do).
    [Fact]
    public void RebuildIndex_takes_over_a_structural_cdx_that_was_refused_at_open()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using (var unindexed = DbfTable.Open(dbf, writable: true, allowUnmaintainedIndexes: true))
        {
            var template = unindexed.Read(0).Values.ToArray();
            unindexed.Append(Call(template, 5000, contact: 1, subject: "the index never heard of this"));
        }
        using (var exclusive = DbfTable.Open(dbf, writable: true, maintainFoxProIndexes: true, exclusive: true))
        {
            Assert.False(exclusive.MaintainsIndex); // refused: one key short
            exclusive.RebuildIndex();
            Assert.True(exclusive.MaintainsIndex);
        }
        AssertIndexMatchesTable(dbf, cdx, rebuilt: true);
    }

    // A batch of updates cut off leaves the key counts matching the table, so only the batch's note
    // in the journal tells: the next write refuses, until a rebuild clears it.
    [Fact]
    public void An_update_batch_cut_off_is_reported_by_its_note()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        var killed = Directory.CreateDirectory(Path.Combine(_dir.FullName, "killed")).FullName;
        using (var table = OpenShared(dbf))
        {
            table.DeferIndexWrites = true;
            var template = table.Read(0).Values.ToArray();
            using (table.LockTable())
            {
                for (var i = 0; i < 16; i += 3)
                    table.Update(i, Call(template, id: 7000 + i, contact: 9, subject: "moved in the batch"));
                foreach (var file in Directory.EnumerateFiles(_dir.FullName))
                    File.WriteAllBytes(Path.Combine(killed, Path.GetFileName(file)), SharedFile.ReadAllBytes(file));
            }
            Assert.False(table.HasPendingJournal); // the batch that finished cleared its note
        }
        AssertIndexMatchesTable(dbf, cdx);

        var copy = Path.Combine(killed, "calls.dbf");
        var note = DbfJournal.Read(copy)!;
        Assert.True(note.IsBatch);
        Assert.Equal(["calls.CDX"], note.Changes.Select(c => c.IndexFile)); // a rebuild clears it only after rebuilding these
        using (var reopened = DbfTable.Open(copy, writable: true, maintainFoxProIndexes: true))
        {
            Assert.True(reopened.HasPendingJournal);
            var error = Assert.Throws<DbfException>(() => reopened.UpdateFields(1, new Dictionary<string, object?> { ["SUBJECT"] = "refused" }));
            Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
            Assert.Contains("batch", error.Message);
        }
        using (var exclusive = DbfTable.Open(copy, writable: true, maintainFoxProIndexes: true, exclusive: true))
        {
            exclusive.RebuildIndex();
            Assert.False(exclusive.HasPendingJournal);
            exclusive.UpdateFields(1, new Dictionary<string, object?> { ["SUBJECT"] = "after the rebuild" });
        }
        AssertIndexMatchesTable(copy, Path.Combine(killed, "calls.CDX"), rebuilt: true);
    }

    // The journal promises each write's keys are finished on disk before its note goes, so a table
    // keeping one writes record by record whatever DeferIndexWrites says.
    [Fact]
    public void A_table_keeping_a_journal_doesnt_defer()
    {
        var (dbf, cdx) = Copy("infused-dbf/foxprodb", "calls");
        using var table = OpenShared(dbf);
        table.DeferIndexWrites = true;
        table.UseJournal = true;
        var template = table.Read(0).Values.ToArray();
        using (table.LockTable())
        {
            table.Append(Call(template, 5000, contact: 2, subject: "journaled"));
            using var reader = DbfTable.Open(dbf);
            using var index = CdxIndex.Open(new MemoryStream(SharedFile.ReadAllBytes(cdx)), table: reader);
            Assert.Contains(index["CALL_ID"].RawEntries(), e => e.Record == 17); // already on disk
        }
    }
}
