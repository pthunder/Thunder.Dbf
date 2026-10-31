//    ___________
//   |  KEEP OUT |     ProjectThunder :: Thunder.Dbf tests
//   |___________|     PACK and REINDEX want the table to themselves,
//        | |          as they always did in dBASE and FoxPro.

namespace Thunder.Dbf.Tests;

public sealed class ExclusiveUseTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-exclusive-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Table()
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        using var table = DbfTable.Create(path, [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Numeric("AGE", 3)]);
        table.Append("Ada", 36m);
        table.Append("Grace", 45m);
        table.CreateIndexTag("NAME", "NAME");
        table.Delete(1);
        return path;
    }

    // Found against Visual FoxPro 9: a PACK or REINDEX while VFP had the table open shared left VFP
    // reading freed index pages ("tag is corrupted", error 2066) and its record numbers renumbered.
    [Fact]
    public void Pack_rebuilds_and_INDEX_ON_refuse_a_table_opened_shared()
    {
        var path = Table();
        var before = SharedFile.ReadAllBytes(path);
        using var table = DbfTable.Open(path, writable: true);
        Assert.False(table.IsExclusive);

        Assert.Equal(DbfErrorCode.ExclusiveUseRequired, Assert.Throws<DbfException>(() => table.Pack()).Code);
        Assert.Equal(DbfErrorCode.ExclusiveUseRequired, Assert.Throws<DbfException>(table.RebuildIndex).Code);
        Assert.Equal(DbfErrorCode.ExclusiveUseRequired, Assert.Throws<DbfException>(() => table.CreateIndexTag("AGE", "AGE")).Code);
        Assert.Equal(DbfErrorCode.ExclusiveUseRequired,
            Assert.Throws<DbfException>(() => table.MaintainIndex(Path.ChangeExtension(path, ".MDX"), rebuild: true)).Code);

        var error = Assert.Throws<DbfException>(() => table.Pack());
        Assert.Contains("exclusive: true", error.Hint);
        Assert.Equal(409, error.HttpStatus);
        Assert.Equal(before, SharedFile.ReadAllBytes(path));
    }

    [Fact]
    public void An_exclusive_table_packs_and_rebuilds()
    {
        var path = Table();
        using var table = DbfTable.Open(path, writable: true, exclusive: true);
        Assert.True(table.IsExclusive);
        Assert.Equal(1, table.Pack());
        table.RebuildIndex();
        table.CreateIndexTag("AGE", "AGE");
        Assert.Equal(1, table.RecordCount);
    }

    [Fact]
    public void An_exclusive_open_fails_while_another_program_has_the_table_and_keeps_others_out()
    {
        var path = Table();
        using (DbfTable.Open(path))
            Assert.Equal(DbfErrorCode.FileInUse, Assert.Throws<DbfException>(() => DbfTable.Open(path, writable: true, exclusive: true)).Code);

        using var exclusive = DbfTable.Open(path, writable: true, exclusive: true);
        Assert.Equal(DbfErrorCode.FileInUse, Assert.Throws<DbfException>(() => DbfTable.Open(path)).Code);
    }

    [Fact]
    public void A_connection_string_can_ask_for_exclusive_use()
    {
        Table();
        using var db = DbfDatabase.Open($"Data Source={_dir.FullName};Read Only=false;Exclusive=true");
        Assert.True(db.Options.Exclusive);
        var table = db["PEOPLE"];
        Assert.True(table.IsExclusive);
        Assert.Equal(1, table.Pack());
    }

    [Fact]
    public void Check_looks_at_an_exclusive_table_already_open_and_reports_one_another_program_holds()
    {
        var path = Table();
        using (var db = DbfDatabase.Open(_dir.FullName, new DbfDatabaseOptions { ReadOnly = false, Exclusive = true }))
        {
            Assert.True(db.Check().Ok);              // the probe holds it exclusively, then lets go
            _ = db["PEOPLE"];                         // now this database holds it
            var report = db.Check();
            Assert.True(report.Ok, report.ToString());
            Assert.True(report.Tables.Single().CanWrite);
        }

        using var other = DbfTable.Open(path);
        using var blocked = DbfDatabase.Open(_dir.FullName, new DbfDatabaseOptions { ReadOnly = false, Exclusive = true });
        Assert.Contains(blocked.Check().Tables.Single().Problems, p => p.Code == DbfErrorCode.FileInUse);
    }
}

public sealed class UnfinishedAppendTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-append-");

    public void Dispose() => _dir.Delete(recursive: true);

    private (string Path, int RecordLength, long Committed) TwoRecords()
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        using var table = DbfTable.Create(path, [DbfFieldDefinition.Character("NAME", 10)]);
        table.Append("Ada");
        table.Append("Grace");
        return (path, table.Header.RecordLength, table.Header.HeaderLength + 2L * table.Header.RecordLength);
    }

    // Found against Visual FoxPro 9: a writer killed after writing a record but before counting it
    // left the record past the header's count, and VFP refused the table (error 2091).
    [Fact]
    public void A_record_past_the_count_is_reported_and_taken_off_by_an_exclusive_open()
    {
        var (path, length, committed) = TwoRecords();
        using (var file = SharedFile.OpenWrite(path))
        {
            file.SetLength(committed);
            file.Position = committed;
            file.Write([(byte)' ', .. "Cut off   "u8, 0x1A]);
        }
        var cutOff = committed + length + 1;

        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Tables.Single().Warnings, w => w.Contains("2091"));

        // A shared writer can't tell a cut-off append from one in progress, so it leaves the bytes.
        using (var shared = DbfTable.Open(path, writable: true))
            shared.UpdateFields(0, new Dictionary<string, object?> { ["NAME"] = "Ada L" });
        Assert.Equal(cutOff, new FileInfo(path).Length);

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            Assert.Equal(2, table.RecordCount);
            // Reported, and kept: the removed record's bytes are beside the table.
            Assert.Equal(1, table.CutOffRecordsRemoved);
            Assert.NotNull(table.CutOffBackupPath);
            Assert.Equal([(byte)' ', .. "Cut off   "u8, 0x1A], File.ReadAllBytes(table.CutOffBackupPath!));
        }
        Assert.Equal(committed + 1, new FileInfo(path).Length);
        Assert.Equal(0x1A, SharedFile.ReadAllBytes(path)[^1]);
        using var reopened = DbfTable.Open(path);
        Assert.Equal(["Ada L", "Grace"], reopened.ReadAll().Select(r => (string)r["NAME"]!));
    }

    // Each removal keeps its own backup, however quickly they follow each other (review of #17).
    [Fact]
    public void Cut_off_records_taken_off_twice_in_a_row_get_a_backup_each()
    {
        var (path, length, committed) = TwoRecords();
        var backups = new List<string>();
        var stopped = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        DbfTable.CutOffClock = () => stopped; // all three in the same millisecond
        try
        {
            for (var round = 0; round < 3; round++)
            {
                using (var file = SharedFile.OpenWrite(path))
                {
                    file.SetLength(committed);
                    file.Position = committed;
                    file.Write([(byte)' ', .. "Cut off   "u8, 0x1A]);
                }
                using var table = DbfTable.Open(path, writable: true, exclusive: true);
                Assert.Equal(1, table.CutOffRecordsRemoved);
                backups.Add(table.CutOffBackupPath!);
            }
        }
        finally
        {
            DbfTable.CutOffClock = () => DateTime.UtcNow;
        }
        Assert.Equal(3, backups.Distinct().Count());
        Assert.All(backups, b => Assert.Equal(length + 1, new FileInfo(b).Length));
    }

    [Fact]
    public void Many_records_past_the_count_are_all_found()
    {
        var (path, _, committed) = TwoRecords();
        using (var file = SharedFile.OpenWrite(path))
        {
            file.SetLength(committed);
            file.Position = committed;
            for (var i = 0; i < 40; i++)
                file.Write([(byte)' ', .. "Batch     "u8]);
        }
        using (var table = DbfTable.Open(path))
            Assert.Equal(40, table.RecordsPastCount());
        using (DbfTable.Open(path, writable: true, exclusive: true)) { }
        Assert.Equal(committed + 1, new FileInfo(path).Length);
    }

    [Fact]
    public void A_repair_rebuilds_the_index_a_cut_off_writer_had_already_reached()
    {
        var (path, _, committed) = TwoRecords();
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("NAME", "NAME");
        using (var file = SharedFile.OpenWrite(path))
        {
            file.SetLength(committed);
            file.Position = committed;
            file.Write([(byte)' ', .. "Cut off   "u8, 0x1A]);
        }

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            Assert.True(table.MaintainsIndex);
            table.Append("Hedy");
        }
        using var reopened = DbfTable.Open(path);
        using var index = reopened.OpenProductionIndex()!;
        Assert.Equal(["Ada", "Grace", "Hedy"], reopened.ReadInOrder(index["NAME"]).Select(r => (string)r["NAME"]!));
    }

    [Fact]
    public void Bytes_that_dont_look_like_a_record_are_left_alone()
    {
        var (path, length, committed) = TwoRecords();
        var junk = new byte[length + 5];
        junk[0] = 0x1A;
        junk[1] = (byte)'?';
        using (var file = SharedFile.OpenWrite(path))
        {
            file.SetLength(committed);
            file.Position = committed;
            file.Write(junk);
        }

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            Assert.Equal(0, table.RecordsPastCount());
            table.Append("Hedy");
        }
        using var reopened = DbfTable.Open(path);
        Assert.Equal(3, reopened.RecordCount);
        Assert.Equal("Hedy", reopened.Read(2)["NAME"]);
    }
}
