//    change:  staged -> closed -> memo aside -> memo in -> COMMIT -> table aside -> table in -> index ... -> retired
//                 \________ undone by the next exclusive open ______/ \_______ finished by it _______________/
//
//   ProjectThunder :: Thunder.Dbf tests
//   A change of structure, and an open finishing one, killed for real at every step (another process,
//   so no catch or finally runs), a file move failing halfway, and another program opening the table
//   while its files are closed for the swap.

using System.Diagnostics;

namespace Thunder.Dbf.Tests;

public sealed class RestructureCrashTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-restructure-crash-");
    private readonly DirectoryInfo _markers = Directory.CreateTempSubdirectory("thunder-restructure-marker-");

    public void Dispose()
    {
        DbfTable.RestructurePoint = null;
        _dir.Delete(recursive: true);
        _markers.Delete(recursive: true);
    }

    private static readonly string[] Names = ["Ada", "Grace", "Hedy", "Joan"];

    private string Table(DbfTableFormat format, bool memo = true)
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        IReadOnlyList<DbfFieldDefinition> fields = memo
            ? [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Numeric("AGE", 3), DbfFieldDefinition.Memo("NOTE")]
            : [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Numeric("AGE", 3)];
        using (var table = DbfTable.Create(path, format, fields))
        {
            for (var i = 0; i < Names.Length; i++)
                table.Append(memo ? [Names[i], 30m + i, "note about " + Names[i]] : [Names[i], 30m + i]);
            table.Delete(1);
        }
        if (format != DbfTableFormat.DbaseIII)
        {
            using var table = Open(path);
            table.CreateIndexTag("NAME", "NAME");
        }
        return path;
    }

    private static DbfTable Open(string path, bool exclusive = true) =>
        DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: exclusive);

    private Dictionary<string, byte[]> Files() =>
        Directory.GetFiles(_dir.FullName).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);

    private List<string> Folders() => Directory.EnumerateDirectories(_dir.FullName).Select(d => Path.GetFileName(d)!).ToList();

    // Starts the crash child, waits until it reaches the step, and kills it there.
    private void KilledAt(string mode, string path, string step)
    {
        var marker = Path.Combine(_markers.FullName, Guid.NewGuid().ToString("N"));
        var child = Path.Combine(AppContext.BaseDirectory, "Thunder.Dbf.CrashChild.dll");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        using var process = Process.Start(new ProcessStartInfo(dotnet, [child, mode, path, step, marker])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        var clock = Stopwatch.StartNew();
        while (!File.Exists(marker) || new FileInfo(marker).Length == 0)
        {
            if (process.HasExited)
                Assert.Fail($"The crash child ended (exit {process.ExitCode}) before reaching '{step}': {process.StandardError.ReadToEnd()}");
            if (clock.Elapsed > TimeSpan.FromSeconds(60))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail($"The crash child didn't reach '{step}' within a minute.");
            }
            Thread.Sleep(10);
        }
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        Assert.Equal(step, File.ReadAllText(marker)); // killed there, not after finishing
    }

    public static TheoryData<DbfTableFormat, string> BeforeTheDecision() => Steps(["staged", "closed", "memo moved aside", "memo moved in"]);

    public static TheoryData<DbfTableFormat, string> AfterTheDecision() =>
        Steps(["committed", "table moved aside", "table moved in", "index moved aside", "index moved in", "retired", "deleted"]);

    private static TheoryData<DbfTableFormat, string> Steps(string[] steps)
    {
        var data = new TheoryData<DbfTableFormat, string>();
        foreach (var format in new[] { DbfTableFormat.DbaseIV, DbfTableFormat.VisualFoxPro })
        foreach (var step in steps)
            data.Add(format, step);
        data.Add(DbfTableFormat.DbaseIII, steps[^1]);
        data.Add(DbfTableFormat.FoxPro2, steps[0]);
        return data;
    }

    [Theory]
    [MemberData(nameof(BeforeTheDecision))]
    public void Killed_before_the_decision_the_next_exclusive_open_puts_back_every_byte(DbfTableFormat format, string step)
    {
        var path = Table(format);
        var before = Files();
        KilledAt("widen", path, step);

        using (var table = Open(path))
        {
            Assert.Null(table.StructureBackupPath);
            Assert.Equal(10, table.Fields[0].Length);
        }
        Assert.Equal(before.Keys.Order(), Files().Keys.Order());
        foreach (var (file, bytes) in Files())
            Assert.Equal(before[file], bytes);
        Assert.Empty(Folders()); // no staging, no retired folder, no empty backup
    }

    [Theory]
    [MemberData(nameof(AfterTheDecision))]
    public void Killed_after_the_decision_the_next_exclusive_open_finishes_the_change(DbfTableFormat format, string step)
    {
        if (format == DbfTableFormat.DbaseIII && step.StartsWith("index", StringComparison.Ordinal))
            return; // no index
        var path = Table(format);
        KilledAt("widen", path, step);

        using (var table = Open(path))
        {
            Assert.Equal(20, table.Fields[0].Length);
            Assert.Equal("CITY", table.Fields.Last(f => !f.IsSystem).Name);
            Assert.Equal("note about Hedy", table.Read(2)["NOTE"]);
            Assert.True(table.Read(1).IsDeleted);
            if (format != DbfTableFormat.DbaseIII)
            {
                using IDbfIndex index = (IDbfIndex?)table.OpenProductionIndex() ?? table.OpenStructuralIndex()!;
                Assert.Equal(20, index.Tag("NAME").KeyLength);
            }
        }
        var folders = Folders();
        Assert.Single(folders); // only the backup
        Assert.StartsWith("PEOPLE.DBF.restructured-", folders[0]);
        Assert.True(File.Exists(Path.Combine(_dir.FullName, folders[0], "PEOPLE.DBF")));
    }

    [Theory]
    [InlineData("table moved in")]
    [InlineData("memo moved in")]
    public void Killed_while_adding_a_first_memo_field_the_change_is_finished_with_its_memo_file(string step)
    {
        var path = Table(DbfTableFormat.DbaseIV, memo: false);
        KilledAt("add-memo", path, step);
        using var table = Open(path);
        table.Update(0, "Ada", 30m, "Countess of Lovelace");
        Assert.Equal("Countess of Lovelace", table.Read(0)["NOTE"]);
        Assert.Equal(0x8B, table.Header.Version);
    }

    // The audit's case: a narrowing (keys cut to 4) killed before the decision, then the open undoing
    // it killed at each step of the undoing. It must never mistake the half-undone folder for a
    // finished change and put the narrow index beside the old table.
    [Theory]
    [InlineData("put back")]
    [InlineData("retired")]
    [InlineData("deleted")]
    public void An_open_undoing_a_change_killed_partway_still_undoes_it(string recoveryStep)
    {
        var path = Table(DbfTableFormat.DbaseIV);
        var before = Files();
        KilledAt("narrow", path, "memo moved in");
        KilledAt("recover", path, recoveryStep);

        using (var table = Open(path))
        {
            Assert.Equal(10, table.Fields[0].Length);
            using var index = table.OpenProductionIndex()!;
            Assert.Equal(10, index["NAME"].KeyLength);
            Assert.Equal(["Ada", "Grace", "Hedy", "Joan"], index["NAME"].Entries().Select(e => ((string)e.Key!).Trim()));
        }
        foreach (var (file, bytes) in Files())
            Assert.Equal(before[file], bytes);
        Assert.Empty(Folders());
    }

    [Theory]
    [InlineData("index moved aside")]
    [InlineData("index moved in")]
    [InlineData("retired")]
    public void An_open_finishing_a_change_killed_partway_still_finishes_it(string recoveryStep)
    {
        var path = Table(DbfTableFormat.VisualFoxPro);
        KilledAt("widen", path, "table moved in");
        KilledAt("recover", path, recoveryStep);
        using var table = Open(path);
        Assert.Equal(20, table.Fields[0].Length);
        using var index = table.OpenStructuralIndex()!;
        Assert.Equal(20, index["NAME"].KeyLength);
    }

    // ---- a move failing halfway (Windows' ReplaceFile can leave a file moved aside and its
    // replacement not yet in) --------------------------------------------------------------------

    [Fact]
    public void A_memo_move_failing_halfway_is_undone_and_the_old_memo_comes_back()
    {
        var path = Table(DbfTableFormat.VisualFoxPro);
        var before = Files();
        using (var table = Open(path))
        {
            DbfTable.RestructurePoint = point => { if (point == "memo moved aside") throw new IOException("Unable to move the replacement file to the file to be replaced."); };
            var ex = Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK")));
            DbfTable.RestructurePoint = null;
            Assert.Equal(DbfErrorCode.WriteFailed, ex.Code);
            Assert.Contains("left as it was", ex.Message);
            Assert.DoesNotContain(_dir.FullName, ex.Message);
        }
        foreach (var (file, bytes) in Files())
            Assert.Equal(before[file], bytes);
        Assert.Empty(Folders());
    }

    [Fact]
    public void A_table_move_failing_halfway_after_the_decision_is_finished_with_the_dbf_missing()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
        {
            DbfTable.RestructurePoint = point => { if (point == "table moved aside") throw new IOException("Unable to move the replacement file to the file to be replaced."); };
            Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK"))).Code);
            DbfTable.RestructurePoint = null;
        }
        Assert.False(File.Exists(path)); // in the backup, its replacement still staged

        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => DbfTable.Open(path)).Code);
        using var finished = Open(path);
        Assert.Equal("OK", finished.Fields.Last().Name);
        Assert.Equal(4, finished.RecordCount);
    }

    // ---- another program opening the table while the change has its files closed ----------------

    [Fact]
    public void A_writer_opening_while_the_files_are_closed_for_the_swap_is_turned_away()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using var closed = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        var change = Task.Run(() =>
        {
            DbfTable.RestructurePoint = point => { if (point == "closed") { closed.Release(); go.Wait(); } };
            try
            {
                using var old = Open(path);
                using var table = old.AddField(DbfFieldDefinition.Logical("OK"));
            }
            finally
            {
                DbfTable.RestructurePoint = null;
            }
        });
        Assert.True(closed.Wait(TimeSpan.FromSeconds(30)));
        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => Open(path, exclusive: false)).Code);
        // On a filesystem that ignores case, another spelling of the name is the same table.
        var alias = Path.Combine(_dir.FullName, "people.dbf");
        if (File.Exists(alias))
            Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => Open(alias, exclusive: false)).Code);
        go.Release();
        change.Wait(TimeSpan.FromSeconds(30));

        using var after = Open(path, exclusive: false);
        Assert.Equal("OK", after.Fields.Last().Name);
    }

    // The window neither check sees on its own: the writer opens the old .dbf after the change has
    // tried every file unshared, and looks for the change only once it has finished. On Linux and
    // macOS its handle then points at the backup, so it must notice and open the file in place. On
    // Windows its handle makes the .dbf's move fail, so the change stops with its folder in place and
    // the writer is turned away; the next exclusive open finishes the change.
    [Theory]
    [InlineData("PEOPLE.DBF")]
    [InlineData("people.dbf")]
    public void A_writer_that_opened_the_old_dbf_just_before_the_swap_writes_to_the_new_one(string spelling)
    {
        var path = Table(DbfTableFormat.DbaseIV);
        var opener = Path.Combine(_dir.FullName, spelling);
        if (!File.Exists(opener))
            return; // a filesystem that keeps case: that spelling is another table
        using var decided = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        Exception? failed = null;
        var change = new Thread(() =>
        {
            DbfTable.RestructurePoint = point => { if (point == "committed") { decided.Release(); go.Wait(); } };
            try
            {
                using var old = Open(path);
                using var table = old.AddField(DbfFieldDefinition.Logical("OK"));
            }
            catch (Exception ex)
            {
                failed = ex;
            }
            finally
            {
                DbfTable.RestructurePoint = null;
            }
        });
        change.Start();
        Assert.True(decided.Wait(TimeSpan.FromSeconds(30)));

        // The writer's open: the old .dbf is opened, then the change runs to its end before the writer looks.
        DbfTable.RestructurePoint = point =>
        {
            if (point != "table file opened")
                return;
            DbfTable.RestructurePoint = null;
            go.Release();
            Assert.True(change.Join(TimeSpan.FromSeconds(30)));
        };
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => Open(opener, exclusive: false)).Code);
                Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.IsType<DbfException>(failed).Code);
                using var finished = Open(path);
                finished.Append("Kay", 50m, "Wrote COBOL", true);
            }
            else
            {
                Assert.Null(failed);
                using var writer = Open(opener, exclusive: false);
                Assert.Equal("OK", writer.Fields.Last().Name);
                writer.Append("Kay", 50m, "Wrote COBOL", true);
            }
        }
        finally
        {
            DbfTable.RestructurePoint = null;
        }

        using var table = DbfTable.Open(path);
        Assert.Equal(5, table.RecordCount);
        Assert.Equal("Kay", table.Read(4)["NAME"]);
        foreach (var backup in Directory.EnumerateDirectories(_dir.FullName))
        {
            using var old = DbfTable.Open(Path.Combine(backup, "PEOPLE.DBF"), ignoreMissingMemo: true);
            Assert.Equal(4, old.RecordCount); // nothing went into the backup
        }
    }

    // ---- the note naming the backup -------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("../elsewhere")]
    [InlineData(null)] // 600 bytes
    public void A_damaged_note_after_the_decision_stops_the_open_with_instructions(string? note)
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
            ModifyStructureTests.KillingAt("table moved in", () => table.AddField(DbfFieldDefinition.Logical("OK")));
        File.WriteAllText(Path.Combine(path + ".restructuring", "restructure.note"), note ?? new string('x', 600));

        var ex = Assert.Throws<DbfException>(() => Open(path));
        Assert.Equal(DbfErrorCode.IncompleteTransaction, ex.Code);
        Assert.DoesNotContain(_dir.FullName, ex.Message);
        Assert.True(Directory.Exists(path + ".restructuring")); // left for the person fixing it
    }

    [Fact]
    public void A_damaged_note_before_the_decision_still_undoes_the_change_when_the_old_table_is_whole()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        var before = Files();
        using (var table = Open(path))
            ModifyStructureTests.KillingAt("closed", () => table.AddField(DbfFieldDefinition.Logical("OK")));
        File.WriteAllText(Path.Combine(path + ".restructuring", "restructure.note"), new string('x', 600));

        using (Open(path))
        {
        }
        foreach (var (file, bytes) in Files())
            Assert.Equal(before[file], bytes);
        Assert.DoesNotContain(Folders(), f => f.EndsWith(".restructuring", StringComparison.Ordinal));
    }
}
