//     you:    [ NAME=Ann | CITY=Rome  ]  --.        ProjectThunder :: Thunder.Dbf tests
//     legacy: [ NAME=Ann | QTY=7      ]  --+-->    UpdateFields: only what you changed
//     disk:   [ NAME=Ann | CITY=Rome | QTY=7 ]     lands, so nobody's edit is lost.
//
//     Plus the locks that make it safe: each family's offsets, shared by every table
//     open in one process, and writes refused while a stray index would go stale.

namespace Thunder.Dbf.Tests;

public sealed class ConcurrentEditTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-edit-");
    private readonly string _path;

    public ConcurrentEditTests()
    {
        _path = Path.Combine(_dir.FullName, "people.dbf");
        using var table = DbfTable.Create(_path,
            [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Character("CITY", 10), DbfFieldDefinition.Numeric("QTY", 4)]);
        table.Append("Ann", "Oslo", 1m);
        table.Append("Bob", "Lima", 2m);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void UpdateFields_keeps_a_change_another_program_made_after_you_read()
    {
        using var web = DbfTable.Open(_path, writable: true);
        var seenByWeb = web.Read(0);

        using (var legacy = DbfTable.Open(_path, writable: true))
            legacy.Update(0, "Ann", "Oslo", 7m);

        var (before, after) = web.UpdateFields(0, new Dictionary<string, object?> { ["city"] = "Rome" });

        Assert.Equal(1m, seenByWeb["QTY"]);
        Assert.Equal(7m, before["QTY"]);   // what was on disk at the moment of the write
        Assert.Equal(7m, after["QTY"]);    // the legacy app's change survived
        Assert.Equal("Rome", after["CITY"]);
        using var check = DbfTable.Open(_path);
        Assert.Equal(("Rome", 7m), ((string?)check.Read(0)["CITY"], (decimal?)check.Read(0)["QTY"]));
    }

    [Fact]
    public void UpdateFields_refuses_an_unknown_field_without_writing()
    {
        using var table = DbfTable.Open(_path, writable: true);
        Assert.ThrowsAny<Exception>(() => table.UpdateFields(0, new Dictionary<string, object?> { ["CITY"] = "Rome", ["NOPE"] = 1 }));
        Assert.Equal("Oslo", table.Read(0)["CITY"]);
    }

    [Fact]
    public void Two_tables_in_one_process_see_each_others_locks()
    {
        // The operating system can't tell two handles in one process apart (and macOS has no
        // byte-range locks at all), so the library keeps its own registry of who holds what.
        using var first = DbfTable.Open(_path, writable: true);
        using var second = DbfTable.Open(_path, writable: true);
        second.LockTimeout = TimeSpan.FromMilliseconds(200);

        using (first.LockRecord(1))
        {
            var error = Assert.Throws<DbfException>(() => second.Update(1, "Blocked", "X", 0m));
            Assert.Equal(DbfErrorCode.RecordLocked, error.Code);
            second.Update(0, "Free", "Oslo", 1m); // a different record isn't blocked
        }
        second.Update(1, "Mine", "Lima", 2m); // released
        Assert.Equal("Mine", second.Read(1)["NAME"]);
    }

    [Theory]
    [InlineData(DbfLockScheme.Dbase, 3, 4_026_531_835L)]
    [InlineData(DbfLockScheme.Clipper, 3, 1_000_000_003L)]
    public void Record_locks_sit_where_each_family_expects(DbfLockScheme scheme, int recordNumber, long offset)
    {
        using var table = DbfTable.Open(_path, writable: true);
        table.LockScheme = scheme;
        Assert.Equal((offset, 1L), table.Locks.RecordRange(recordNumber));
    }

    [Fact]
    public void Visual_FoxPro_tables_pick_the_FoxPro_scheme_by_themselves()
    {
        var path = Path.Combine(_dir.FullName, "products.dbf");
        File.Copy(Path.Combine(Fixtures.Root, "infused-dbf/dbase_31.dbf"), path);
        using var table = DbfTable.Open(path);

        Assert.Equal(DbfLockScheme.VisualFoxPro, table.LockScheme);
        // Its header says it has a structural .cdx, so FoxPro counts down from 0x7FFFFFFE.
        Assert.True(table.Header.HasStructuralCdx);
        Assert.Equal((0x7FFFFFFEL - 1, 1L), table.Locks.RecordRange(1));

        // A FoxPro table without a .cdx locks 0x40000000 plus the record's byte offset instead.
        using var plain = DbfTable.Open(Path.Combine(Fixtures.Root, "infused-dbf/dbase_f5.dbf"));
        Assert.False(plain.Header.HasStructuralCdx);
        Assert.Equal((0x40000000L + plain.Header.HeaderLength + plain.Header.RecordLength, 1L), plain.Locks.RecordRange(2));
    }

    [Fact]
    public void The_scheme_can_be_set_in_the_connection_string_and_bad_values_are_explained()
    {
        using var db = DbfDatabase.Open($"Data Source={_dir.FullName};Lock Scheme=clipper");
        Assert.Equal(DbfLockScheme.Clipper, db.Options.LockScheme);
        Assert.Equal(DbfLockScheme.Clipper, db["people"].LockScheme);

        var error = Assert.Throws<DbfException>(() => DbfDatabase.Open($"Data Source={_dir.FullName};Lock Scheme=Paradox"));
        Assert.Equal(DbfErrorCode.InvalidConnectionString, error.Code);
    }

    [Fact]
    public void A_stray_index_that_cannot_be_kept_up_to_date_blocks_writes_until_it_is_gone()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir.FullName, "retro")).FullName;
        foreach (var file in new[] { "NUM2000.DBF", "NUM2000.NDX" })
            File.Copy(Path.Combine(Fixtures.Root, "retroinv", file), Path.Combine(folder, file));

        // A healthy .ndx is kept up to date now; a damaged one (no key length) can't be, and nobody
        // can tell it doesn't belong to the table.
        using (var damage = SharedFile.OpenWrite(Path.Combine(folder, "NUM2000.NDX")))
        {
            damage.Position = 12;
            damage.Write([0, 0]);
        }

        using (var table = DbfTable.Open(Path.Combine(folder, "NUM2000.DBF"), writable: true))
        {
            var error = Assert.Throws<DbfException>(() => table.Append(table.Read(0).Values.ToArray()));
            Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
            Assert.DoesNotContain(folder, error.Message); // the file name is for logs, not the message
        }

        File.Delete(Path.Combine(folder, "NUM2000.NDX"));
        using var unblocked = DbfTable.Open(Path.Combine(folder, "NUM2000.DBF"), writable: true);
        var count = unblocked.RecordCount;
        unblocked.Append(unblocked.Read(0).Values.ToArray());
        Assert.Equal(count + 1, unblocked.RecordCount);
    }
}

public sealed class IndexSafetyTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-idxsafe-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Copy(string fixture, string? name = null)
    {
        var target = Path.Combine(_dir.FullName, name ?? Path.GetFileName(fixture));
        File.Copy(Path.Combine(Fixtures.Root, fixture), target);
        return target;
    }

    [Fact]
    public void Another_tables_index_with_the_same_field_names_is_never_written()
    {
        // COPY.DBF has BIG.DBF's fields, so BIG1.NDX (STR(ID,8)) looks like it could be COPY's index.
        var index = Copy("retroinv/BIG1.NDX");
        Copy("retroinv/BIG.DBF");
        var copy = Copy("retroinv/BIG.DBF", "COPY.DBF");
        var before = SharedFile.ReadAllBytes(index);

        using (var table = DbfTable.Open(copy, writable: true))
        {
            var error = Assert.Throws<DbfException>(() => table.Append(table.Read(0).Values.ToArray()));
            Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        }
        Assert.Equal(before, SharedFile.ReadAllBytes(index));

        // Named explicitly, it's kept up to date.
        using var owner = DbfTable.Open(Path.Combine(_dir.FullName, "BIG.DBF"), writable: true);
        owner.MaintainIndex(index);
        owner.Append(owner.Read(0).Values.ToArray());
        Assert.NotEqual(before, SharedFile.ReadAllBytes(index));
    }

    [Fact]
    public void An_index_created_after_the_table_was_opened_blocks_writes()
    {
        var path = Copy("retroinv/NUM2000.DBF");
        using var table = DbfTable.Open(path, writable: true);
        table.Append(table.Read(0).Values.ToArray()); // no index yet: fine

        Copy("retroinv/NUM2000.NDX"); // the legacy app runs INDEX ON meanwhile
        var error = Assert.Throws<DbfException>(() => table.Append(table.Read(0).Values.ToArray()));
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
    }

    [Fact]
    public void A_FoxPro_2_table_with_dBASEs_version_byte_is_known_by_its_cdx()
    {
        // X# wrote TEST10K as FoxPro 2 does without memos: version 0x03 with byte 28 set, which a
        // dBASE IV reader takes for "has a .mdx". The .cdx beside it, and no .mdx, says otherwise.
        Copy("xsharp/rdd-tests/TEST10K.CDX");
        var path = Copy("xsharp/rdd-tests/TEST10K.DBF");

        using var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true);
        Assert.True(table.Header.HasStructuralCdx);
        Assert.False(table.Header.HasProductionMdx);
        Assert.Equal(DbfLockScheme.VisualFoxPro, table.LockScheme);

        // Its case-blind tags keep writes blocked until the index is taken over and rebuilt.
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, Assert.Throws<DbfException>(() => table.Delete(0)).Code);
        table.MaintainIndex(Path.ChangeExtension(path, ".CDX"), rebuild: true);
        table.Delete(0);
        Assert.True(table.Read(0).IsDeleted);
    }

    [Fact]
    public void After_a_failed_index_update_every_write_waits_for_a_rebuild()
    {
        foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT", "CHANGELOG.MDX" })
            Copy(Path.Combine("siemens-step7/siprom", file));
        var path = Path.Combine(_dir.FullName, "CHANGELOG.DBF");
        using var table = DbfTable.Open(path, writable: true, exclusive: true);

        // Another program loses record 2's USER_TAG entry: the index is now out of step.
        using (var mdx = MdxIndex.Open(DbfException.OpenFile(Path.ChangeExtension(path, ".MDX"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)))
        {
            var tag = mdx["USER_TAG"];
            Assert.True(tag.Remove(tag.KeyFor(table.Read(1)), 2));
            mdx.Commit();
        }

        var values = table.Read(1).Values.ToArray();
        values[3] = "changed";
        Assert.Equal(DbfErrorCode.IndexDamaged, Assert.Throws<DbfException>(() => table.Update(1, values)).Code);
        Assert.Equal(DbfErrorCode.IndexDamaged, Assert.Throws<DbfException>(() => table.Delete(0)).Code);

        table.RebuildIndex();
        table.Delete(0); // writes work again
        using var check = table.OpenProductionIndex()!;
        Assert.Equal(2, table.Seek(check["USER_TAG"], "changed")!.RecordNumber);
    }
}

internal static class Fixtures
{
    public static readonly string Root = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find tests/fixtures above the test output folder.");
    }
}

internal static class IndexedTables
{
    /// <summary>
    /// Opens a table writable and keeps every index next to it that belongs to it, the way a legacy
    /// app's <c>USE TABLE INDEX A, B, C</c> would. Index files named after other tables need naming.
    /// </summary>
    public static DbfTable OpenWithIndexes(string path)
    {
        var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true);
        foreach (var index in DbfTable.FindCompanionIndexes(path, table.Header))
            table.MaintainIndex(index);
        return table;
    }
}
