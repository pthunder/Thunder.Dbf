//      ____                          ProjectThunder :: Thunder.Dbf tests
//     |    |  record 3 is mine       Locks that another program actually sees, and
//     | () |  ...wait your turn      honest answers on platforms that can't lock.
//     |____|

using System.Diagnostics;

namespace Thunder.Dbf.Tests;

public sealed class DbfLockTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-lock-");
    private readonly string _path;

    public DbfLockTests()
    {
        _path = Path.Combine(_dir.FullName, "people.dbf");
        using var table = DbfTable.Create(_path, [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Numeric("QTY", 4)]);
        table.AppendRange(Enumerable.Range(1, 5).Select(i => new object?[] { $"P{i}", (decimal)i }));
    }

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void Two_writers_see_each_others_appends()
    {
        using var first = DbfTable.Open(_path, writable: true);
        using var second = DbfTable.Open(_path, writable: true);

        first.Append("First", 1m);
        second.Append("Second", 2m); // must refresh the count first, not overwrite First's record

        using var check = DbfTable.Open(_path);
        Assert.Equal(7, check.RecordCount);
        Assert.Equal(["First", "Second"], check.ReadAll().Skip(5).Select(r => r["NAME"]));
    }

    [Fact]
    public void Refresh_sees_records_added_elsewhere()
    {
        using var reader = DbfTable.Open(_path);
        using (var writer = DbfTable.Open(_path, writable: true))
            writer.Append("Later", 9m);

        Assert.Equal(5, reader.RecordCount);
        reader.Refresh();
        Assert.Equal(6, reader.RecordCount);
    }

    [Fact]
    public void A_record_locked_by_another_program_blocks_writes_until_the_timeout()
    {
        if (!DbfLocks.PlatformSupportsLocks)
            return; // covered by Locks_are_reported_as_not_enforced_where_unsupported

        using var holder = HoldRecordLock(recordNumber: 3);
        using var table = DbfTable.Open(_path, writable: true);
        table.LockTimeout = TimeSpan.FromMilliseconds(300);

        var timer = Stopwatch.StartNew();
        var error = Assert.Throws<DbfException>(() => table.Update(2, "Blocked", 3m));
        Assert.Equal(DbfErrorCode.RecordLocked, error.Code);
        Assert.Equal(423, error.HttpStatus);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(250));

        table.Update(3, "Free", 4m); // other records aren't blocked
    }

    [Fact]
    public void Explicit_record_locks_nest_with_the_locks_writes_take()
    {
        using var table = DbfTable.Open(_path, writable: true);
        using (table.LockRecord(1))
        {
            table.Update(1, "Mine", 2m); // same record, same table: no deadlock with itself
            table.Update(1, "Still", 2m);
        }
        using (table.LockTable())
            table.Delete(0);

        Assert.Equal("Still", table.Read(1)["NAME"]);
        Assert.True(table.Read(0).IsDeleted);
    }

    [Fact]
    public void Locks_are_reported_as_not_enforced_where_unsupported()
    {
        using var table = DbfTable.Open(_path, writable: true);
        Assert.Equal(DbfLocks.PlatformSupportsLocks, table.LocksEnforced);

        if (!DbfLocks.PlatformSupportsLocks)
        {
            table.RequireLocks = true;
            Assert.Equal(DbfErrorCode.LockingUnsupported, Assert.Throws<DbfException>(() => table.Update(0, "X", 1m)).Code);
        }
    }

    [Fact]
    public void Memory_streams_never_claim_to_lock()
    {
        using var table = DbfTable.Create(new MemoryStream(), [DbfFieldDefinition.Character("N", 1)]);
        Assert.False(table.LocksEnforced);
        table.Append("x"); // and writes still work
    }

    // Another program holding dBASE's lock byte for a record: on Windows a second handle in this
    // process is enough (locks belong to handles); on Linux locks belong to processes, so use one.
    private IDisposable HoldRecordLock(int recordNumber)
    {
        var offset = DbfLocks.HeaderByte - recordNumber;
        if (OperatingSystem.IsWindows())
        {
            var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            stream.Lock(offset, 1);
            return stream;
        }

        var script = $"import fcntl,os,sys,time\nf=open(sys.argv[1],'r+b')\nfcntl.lockf(f,fcntl.LOCK_EX,1,{offset})\nprint('locked',flush=True)\ntime.sleep(30)";
        var process = Process.Start(new ProcessStartInfo("python3", ["-I", "-c", script, _path]) { RedirectStandardOutput = true })!;
        Assert.Equal("locked", process.StandardOutput.ReadLine());
        return new Killer(process);
    }

    private sealed class Killer(Process process) : IDisposable
    {
        public void Dispose()
        {
            process.Kill();
            process.WaitForExit();
            process.Dispose();
        }
    }
}
