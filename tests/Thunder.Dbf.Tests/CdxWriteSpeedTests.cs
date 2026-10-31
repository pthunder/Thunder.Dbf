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
        var error = Assert.Throws<DbfException>(() => table.Append(Call(template, 5002, contact: 1, subject: "third")));
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        Assert.Contains("appeared after it was opened", error.Message);
    }
}
