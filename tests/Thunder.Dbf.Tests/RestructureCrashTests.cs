//    change:  staged -> closed -> memo aside -> memo in -> COMMIT -> table aside -> table in -> index ... -> retired
//                 \________ undone by the next exclusive open ______/ \_______ finished by it _______________/
//
//   ProjectThunder :: Thunder.Dbf tests
//   A change of structure, and an open finishing one, killed for real at every step (another process,
//   so no catch or finally runs), a file move failing halfway, and another program opening the table
//   while its files are closed for the swap.

using System.Diagnostics;
using System.Runtime.InteropServices;

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

    private string Backup() => Directory.EnumerateDirectories(_dir.FullName, "PEOPLE.DBF.restructured-*").Single();

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

    // Starts the crash child, waits until it reaches the step, and leaves it waiting there; disposing
    // the result lets it carry on and waits for it to finish.
    private sealed class Paused(Process process, string marker) : IDisposable
    {
        public void Dispose()
        {
            File.WriteAllText(marker + ".go", "");
            if (!process.WaitForExit(60_000))
                process.Kill(entireProcessTree: true);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("finished", File.ReadAllText(marker));
            process.Dispose();
        }
    }

    private Paused PausedAt(string mode, string path, string step)
    {
        var marker = Path.Combine(_markers.FullName, Guid.NewGuid().ToString("N"));
        var child = Path.Combine(AppContext.BaseDirectory, "Thunder.Dbf.CrashChild.dll");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var process = Process.Start(new ProcessStartInfo(dotnet, [child, mode, path, step, marker]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
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
        return new Paused(process, marker);
    }

    // The review's case: another process's exclusive open, while a change is still writing its new
    // table, must leave the change's partial folder alone, and the change then finishes.
    [Theory]
    [InlineData("copying")]
    [InlineData("manifest")]
    public void An_exclusive_open_in_another_process_leaves_a_change_being_written_alone(string step)
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var change = PausedAt("widen", path, step))
        {
            Assert.True(Directory.Exists(path + ".restructure-partial"));
            Assert.Equal(DbfErrorCode.FileInUse, Assert.Throws<DbfException>(() => Open(path)).Code);
            Assert.True(Directory.Exists(path + ".restructure-partial"));
            Assert.Equal(DbfErrorCode.FileInUse, Assert.Throws<DbfException>(() => Open(path)).Code);
        }
        using var table = Open(path);
        Assert.Equal(20, table.Fields[0].Length);
        Assert.Equal("CITY", table.Fields.Last(f => !f.IsSystem).Name);
        Assert.Equal("note about Hedy", table.Read(2)["NOTE"]);
    }

    public static TheoryData<DbfTableFormat, string> BeforeTheDecision() =>
        Steps(["copying", "manifest", "staged", "closed", "memo moved aside", "memo moved in", "deciding"]);

    public static TheoryData<DbfTableFormat, string> AfterTheDecision() =>
        Steps(["decided", "committed", "table moved aside", "table moved in", "index moved aside", "index moved in", "retiring", "retired", "deleted", "reopened", "cleaned"]);

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
        // Holding the old files only: the change's lock, manifest and decision were cleared away.
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_dir.FullName, folders[0])), f => Path.GetFileName(f).StartsWith("restructure.", StringComparison.Ordinal));
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
    [InlineData("recovery read")]
    [InlineData("recovery locked")]
    [InlineData("put back")]
    [InlineData("retiring")]
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
    [InlineData("recovery locked")]
    [InlineData("index moved aside")]
    [InlineData("index moved in")]
    [InlineData("retired")]
    [InlineData("cleaned")]
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

    // ---- telling whether an open file is still the one at its path ------------------------------

    [Fact]
    public void A_handle_is_the_file_at_its_path_until_that_file_is_renamed_away()
    {
        var path = Path.Combine(_dir.FullName, "ID.DBF");
        File.WriteAllText(path, "old");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var same = FileIdentity.Same(stream, path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.True(same); // read from the device and inode, not left to the fallback
        else
            Assert.True(same ?? true);
        if (OperatingSystem.IsWindows())
            return; // a file .NET has open can't be renamed there, so the question doesn't arise

        // The same bytes and times copied over, as a swap could leave them: only the inode tells them apart.
        File.Move(path, path + ".aside");
        File.Copy(path + ".aside", path);
        File.SetCreationTimeUtc(path, File.GetCreationTimeUtc(path + ".aside"));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path + ".aside"));
        Assert.False(FileIdentity.Same(stream, path));
        File.Delete(path);
        Assert.False(FileIdentity.Same(stream, path)); // nothing at the path at all
    }

    [Fact]
    public void An_open_whose_dbf_never_matches_its_path_is_refused_not_taken_on_trust()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        var tries = 0;
        FileIdentity.Override = (_, _) => { tries++; return false; };
        try
        {
            Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => Open(path, exclusive: false)).Code);
            Assert.Equal(3, tries);
            Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => DbfTable.Open(path)).Code);
        }
        finally
        {
            FileIdentity.Override = null;
        }
        using var table = Open(path, exclusive: false); // and opens once the files settle
        Assert.Equal(4, table.RecordCount);
    }

    [Fact]
    public void Where_the_inode_cant_be_read_the_creation_time_stands_in()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        FileIdentity.Override = (_, _) => null;
        try
        {
            using var table = Open(path, exclusive: false);
            Assert.Equal(4, table.RecordCount);
        }
        finally
        {
            FileIdentity.Override = null;
        }
    }

    // ---- a decision that didn't reach the disk ---------------------------------------------------

    // The decision's contents are forced to disk, but .NET can't force the folder entry naming it.
    // After a power cut that lost the entry while keeping the moves after it, the change must be
    // undone whole: every old file comes back from the backup, not just the memo.
    [Theory]
    [InlineData("table moved in")]
    [InlineData("index moved in")]
    public void A_lost_decision_undoes_the_change_with_every_old_file_back(string step)
    {
        var path = Table(DbfTableFormat.DbaseIV);
        var before = Files();
        using (var table = Open(path))
            ModifyStructureTests.KillingAt(step, () => table.AddField(DbfFieldDefinition.Logical("OK")));
        File.Delete(Path.Combine(Backup(), "restructure.commit"));

        using (var table = Open(path))
            Assert.Equal(3, table.Fields.Count);
        Assert.Equal(before.Keys.Order(), Files().Keys.Order());
        foreach (var (file, bytes) in Files())
            Assert.Equal(before[file], bytes);
        Assert.Empty(Folders());
    }

    // ---- the note naming the backup -------------------------------------------------------------

    // The id in the staged folder names the transaction, and the manifest in its backup every file
    // that moves. Either one missing, cut short, oversized or naming another file stops the open with
    // instructions, before or after the decision alike: nothing moves on a record it can't trust.
    [Theory]
    [InlineData("id", "", "closed")]
    [InlineData("id", "../elsewhere", "table moved in")]
    [InlineData("id", null, "closed")]
    [InlineData("manifest", "", "table moved in")]
    [InlineData("manifest", "id=PEOPLE.DBF.restructured-x\ntable=OTHER.DBF", "closed")]
    [InlineData("manifest", null, "table moved in")]
    [InlineData("manifest", "memo=../../etc/passwd", "closed")]
    public void A_damaged_record_stops_the_open_with_instructions(string which, string? content, string step)
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
            ModifyStructureTests.KillingAt(step, () => table.AddField(DbfFieldDefinition.Logical("OK")));
        var file = which == "id" ? Path.Combine(path + ".restructuring", "restructure.id") : Path.Combine(Backup(), "restructure.manifest");
        if (content is not null && content.StartsWith("memo=", StringComparison.Ordinal))
            content = string.Join('\n', File.ReadAllText(file).Split('\n').Select(line => line.StartsWith("memo=", StringComparison.Ordinal) ? content : line));
        File.WriteAllText(file, content ?? new string('x', 5000));

        var ex = Assert.Throws<DbfException>(() => Open(path));
        Assert.Equal(DbfErrorCode.IncompleteTransaction, ex.Code);
        Assert.DoesNotContain(_dir.FullName, ex.Message);
        Assert.True(Directory.Exists(path + ".restructuring")); // left for the person fixing it
    }

    // ---- a file operation failing at each transition ---------------------------------------------

    private static readonly IOException MoveFailed = new("Unable to move the replacement file to the file to be replaced.");

    [Theory]
    [InlineData("closed")]
    [InlineData("memo moved aside")]
    [InlineData("memo moved in")]
    [InlineData("deciding")]          // the decision written but not yet published: undecided
    public void A_failure_before_the_decision_is_published_undoes_the_change(string step)
    {
        var path = Table(DbfTableFormat.VisualFoxPro);
        var before = Files();
        using (var table = Open(path))
        {
            DbfTable.RestructurePoint = point => { if (point == step) throw MoveFailed; };
            var ex = Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK")));
            DbfTable.RestructurePoint = null;
            Assert.Contains("left as it was", ex.Message);
        }
        foreach (var (file, bytes) in Files())
            Assert.Equal(before[file], bytes);
        Assert.Equal(before.Keys.Order(), Files().Keys.Order());
        Assert.Empty(Folders());
    }

    // The rename publishing the decision went through, and only then did something fail: the change
    // is decided, and it's finished, never undone.
    [Fact]
    public void A_failure_just_after_the_decision_is_published_still_finishes_the_change()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using var old = Open(path);
        DbfTable.RestructurePoint = point => { if (point == "decided") throw MoveFailed; };
        try
        {
            using var table = old.AddField(DbfFieldDefinition.Logical("OK"));
            Assert.Equal("OK", table.Fields.Last().Name);
        }
        finally
        {
            DbfTable.RestructurePoint = null;
        }
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("table moved aside")]
    [InlineData("table moved in")]
    [InlineData("index moved aside")]
    [InlineData("index moved in")]
    public void A_failure_after_the_decision_says_so_and_the_next_exclusive_open_finishes(string step)
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
        {
            DbfTable.RestructurePoint = point => { if (point == step) throw MoveFailed; };
            var ex = Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK")));
            DbfTable.RestructurePoint = null;
            Assert.Equal(DbfErrorCode.IncompleteTransaction, ex.Code);
            Assert.Contains("decided", ex.Message);
            Assert.DoesNotContain("left as it was", ex.Message);
        }
        using var finished = Open(path);
        Assert.Equal("OK", finished.Fields.Last().Name);
    }

    [Fact]
    public void Failing_only_to_clear_the_folder_away_says_the_new_structure_is_in_place()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
        {
            DbfTable.RestructurePoint = point => { if (point == "retiring") throw MoveFailed; };
            var ex = Assert.Throws<DbfException>(() => table.AddField(DbfFieldDefinition.Logical("OK")));
            DbfTable.RestructurePoint = null;
            Assert.Equal(DbfErrorCode.IncompleteTransaction, ex.Code);
            Assert.Contains("has its new structure", ex.Message);
        }
        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => Open(path, exclusive: false)).Code);
        using (var finished = Open(path))
            Assert.Equal("OK", finished.Fields.Last().Name);
        Assert.False(Directory.Exists(path + ".restructuring"));
    }

    // ---- two opens recovering at once ------------------------------------------------------------

    // The audit's case: one open reads the staged folder's id and pauses; another undoes that change,
    // and a new change begins. The paused one, resumed, must not touch the new change's folder.
    [Fact]
    public void An_open_paused_over_an_ended_change_never_touches_the_next_one()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
            ModifyStructureTests.KillingAt("closed", () => table.AddField(DbfFieldDefinition.Logical("OK")));

        using var read = new SemaphoreSlim(0);
        using var resume = new SemaphoreSlim(0);
        Exception? pausedResult = null;
        var paused = new Thread(() =>
        {
            DbfTable.RestructurePoint = point => { if (point == "recovery read") { DbfTable.RestructurePoint = null; read.Release(); resume.Wait(); } };
            try
            {
                using var table = Open(path);
            }
            catch (Exception ex)
            {
                pausedResult = ex;
            }
            finally
            {
                DbfTable.RestructurePoint = null;
            }
        });
        paused.Start();
        Assert.True(read.Wait(TimeSpan.FromSeconds(30)));

        // Meanwhile: this open undoes the old change, and a new change starts and stops with its files closed.
        using var closed = new SemaphoreSlim(0);
        using var finish = new SemaphoreSlim(0);
        var change = new Thread(() =>
        {
            DbfTable.RestructurePoint = point => { if (point == "closed") { closed.Release(); finish.Wait(); } };
            try
            {
                using var old = Open(path);
                using var table = old.AddField(DbfFieldDefinition.Character("CITY", 20));
            }
            finally
            {
                DbfTable.RestructurePoint = null;
            }
        });
        change.Start();
        Assert.True(closed.Wait(TimeSpan.FromSeconds(30)));
        var newReady = File.ReadAllText(Path.Combine(path + ".restructuring", "restructure.id"));

        resume.Release();
        Assert.True(paused.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(DbfErrorCode.FileInUse, Assert.IsType<DbfException>(pausedResult).Code); // the new change holds its lock
        Assert.Equal(newReady, File.ReadAllText(Path.Combine(path + ".restructuring", "restructure.id"))); // untouched

        finish.Release();
        Assert.True(change.Join(TimeSpan.FromSeconds(30)));
        using var after = Open(path);
        Assert.Equal("CITY", after.Fields.Last().Name);
    }

    // ---- the audit's other cases ----------------------------------------------------------------

    // A Visual FoxPro table with its .FPT and a leftover .DBT: the change replaces the .FPT, and the
    // .DBT is never touched.
    [Fact]
    public void A_stray_memo_file_of_the_other_kind_is_never_mistaken_for_the_table_s_own()
    {
        var path = Table(DbfTableFormat.VisualFoxPro);
        var stray = Path.Combine(_dir.FullName, "PEOPLE.DBT");
        File.WriteAllBytes(stray, [1, 2, 3]);
        using (var old = Open(path))
        using (var table = old.AlterField("AGE", DbfFieldDefinition.Memo("AGE"))) // a character field made memo, beside a memo kept
        {
            Assert.Equal("32", table.Read(2)["AGE"]);
            Assert.Equal("note about Hedy", table.Read(2)["NOTE"]);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(stray));
        using var reopened = DbfTable.Open(path);
        Assert.Equal("33", reopened.Read(3)["AGE"]); // its new memo blocks are in the .FPT the table opens
        Assert.Equal("note about Joan", reopened.Read(3)["NOTE"]);
    }

    [Fact]
    public void Dropping_the_last_memo_field_moves_the_table_s_own_memo_file_never_a_stray_one()
    {
        var path = Table(DbfTableFormat.VisualFoxPro);
        var stray = Path.Combine(_dir.FullName, "PEOPLE.DBT");
        File.WriteAllBytes(stray, [1, 2, 3]);
        using (var old = Open(path))
        using (var table = old.DropField("NOTE"))
            Assert.Contains(Directory.GetFiles(table.StructureBackupPath!), f => Path.GetExtension(f).Equals(".fpt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(stray));
        Assert.DoesNotContain(Directory.GetFiles(_dir.FullName), f => Path.GetExtension(f).Equals(".fpt", StringComparison.OrdinalIgnoreCase));
    }

    // As above, but the old change was finished (its backup stays, as the user's): the paused open
    // can take that backup's lock, and must then see the staged folder now names another change.
    [Fact]
    public void An_open_paused_over_a_finished_change_never_touches_the_next_one()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
            ModifyStructureTests.KillingAt("table moved in", () => table.AddField(DbfFieldDefinition.Logical("OK")));
        // The lock stays in the finished change's backup, as a kill while it was being cleared would leave it.
        using var read = new SemaphoreSlim(0);
        using var resume = new SemaphoreSlim(0);
        Exception? pausedResult = null;
        var paused = new Thread(() =>
        {
            DbfTable.RestructurePoint = point => { if (point == "recovery read") { DbfTable.RestructurePoint = null; read.Release(); resume.Wait(); } };
            try
            {
                using var table = Open(path);
            }
            catch (Exception ex)
            {
                pausedResult = ex;
            }
            finally
            {
                DbfTable.RestructurePoint = null;
            }
        });
        paused.Start();
        Assert.True(read.Wait(TimeSpan.FromSeconds(30)));
        var oldBackup = Backup();

        DbfTable.RestructurePoint = point => { if (point == "cleaned") throw new IOException("killed while clearing"); };
        try
        {
            Assert.ThrowsAny<Exception>(() => Open(path).Dispose()); // finishes it, then stops with the old lock left behind
        }
        finally
        {
            DbfTable.RestructurePoint = null;
        }
        File.WriteAllText(Path.Combine(oldBackup, "restructure.lock"), "");
        using var closed = new SemaphoreSlim(0);
        using var finish = new SemaphoreSlim(0);
        var change = new Thread(() =>
        {
            DbfTable.RestructurePoint = point => { if (point == "closed") { closed.Release(); finish.Wait(); } };
            try
            {
                using var old = Open(path);
                using var table = old.AddField(DbfFieldDefinition.Character("CITY", 20));
            }
            finally
            {
                DbfTable.RestructurePoint = null;
            }
        });
        change.Start();
        Assert.True(closed.Wait(TimeSpan.FromSeconds(30)));
        var newId = File.ReadAllText(Path.Combine(path + ".restructuring", "restructure.id"));

        resume.Release();
        Assert.True(paused.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(DbfErrorCode.FileInUse, Assert.IsType<DbfException>(pausedResult).Code);
        Assert.Equal(newId, File.ReadAllText(Path.Combine(path + ".restructuring", "restructure.id")));

        finish.Release();
        Assert.True(change.Join(TimeSpan.FromSeconds(30)));
        using var after = Open(path);
        Assert.Equal("CITY", after.Fields.Last().Name);
    }

    [Fact]
    public void A_table_with_another_extension_is_changed_and_recovered_by_its_own_name()
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DAT");
        using (var created = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 10)]))
            created.Append("Ada");
        using (var old = Open(path))
        using (var table = old.AddField(DbfFieldDefinition.Logical("OK")))
            Assert.Equal("OK", table.Fields.Last().Name);
        using (var old = Open(path))
            ModifyStructureTests.KillingAt("table moved aside", () => old.AddField(DbfFieldDefinition.Logical("OK2")));
        using var recovered = Open(path);
        Assert.Equal("OK2", recovered.Fields.Last().Name);
        Assert.Equal("Ada", recovered.Read(0)["NAME"]);
    }

    [Fact]
    public void A_database_finds_and_recovers_a_table_whose_dbf_is_between_its_renames()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        using (var table = Open(path))
            ModifyStructureTests.KillingAt("table moved aside", () => table.AddField(DbfFieldDefinition.Logical("OK")));
        Assert.False(File.Exists(path));

        using (var shared = DbfDatabase.Open(_dir.FullName, new DbfDatabaseOptions { ReadOnly = false }))
            Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => shared.OpenTable("PEOPLE")).Code);
        using var exclusive = DbfDatabase.Open(_dir.FullName, new DbfDatabaseOptions { ReadOnly = false, Exclusive = true });
        using var table2 = exclusive.OpenTable("PEOPLE");
        Assert.Equal("OK", table2.Fields.Last().Name);
    }

    [Fact]
    public void An_identity_check_that_fails_closes_the_file_and_names_no_path()
    {
        var path = Table(DbfTableFormat.DbaseIV);
        FileIdentity.Override = (_, _) => throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        try
        {
            var ex = Assert.Throws<DbfException>(() => Open(path));
            Assert.DoesNotContain(_dir.FullName, ex.Message);
        }
        finally
        {
            FileIdentity.Override = null;
        }
        using var table = Open(path); // the handle was closed: an exclusive open gets in
        Assert.Equal(4, table.RecordCount);
    }

    [Fact]
    public void A_lost_decision_after_adding_the_first_memo_leaves_no_memo_file_behind()
    {
        var path = Table(DbfTableFormat.DbaseIV, memo: false);
        var before = Files();
        using (var table = Open(path))
            ModifyStructureTests.KillingAt("memo moved in", () => table.AddField(DbfFieldDefinition.Memo("NOTE")));
        File.Delete(Path.Combine(Backup(), "restructure.commit"));
        using (Open(path))
        {
        }
        Assert.Equal(before.Keys.Order(), Files().Keys.Order());
        foreach (var (file, bytes) in Files())
            Assert.Equal(before[file], bytes);
    }

    // ---- reading inodes -------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, Architecture.X64, false, 8, 48)]
    [InlineData(true, false, Architecture.Arm64, false, 8, 48)]
    [InlineData(false, true, Architecture.Arm64, false, 4, 96)]
    [InlineData(false, true, Architecture.X64, true, 4, 96)]   // an Intel process, Rosetta's too
    public void The_stat_layout_follows_the_process_not_the_machine(bool linux, bool macOS, Architecture process, bool intelSymbols, int device, int size)
    {
        var abi = FileIdentity.AbiFor(linux, macOS, process)!;
        Assert.Equal((intelSymbols, device, 8, size), (abi.IntelMacSymbols, abi.DeviceBytes, abi.InodeAt, abi.SizeAt));
    }

    [Theory]
    [InlineData(true, false, Architecture.X86)]
    [InlineData(true, false, Architecture.Arm)]
    [InlineData(false, false, Architecture.X64)]
    public void Other_layouts_aren_t_read(bool linux, bool macOS, Architecture process) =>
        Assert.Null(FileIdentity.AbiFor(linux, macOS, process));

    [Fact]
    public void A_layout_that_doesn_t_match_this_process_is_caught_and_never_trusted()
    {
        if (!FileIdentity.Available)
            return; // Windows, or a process that doesn't read inodes at all
        var path = Path.Combine(_dir.FullName, "ID.DBF");
        File.WriteAllText(path, "old");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // Another system's layout, read here: the inode and size land at the wrong places.
        FileIdentity.AbiOverride = OperatingSystem.IsMacOS()
            ? FileIdentity.AbiFor(linux: true, macOS: false, Architecture.X64)
            : FileIdentity.AbiFor(linux: false, macOS: true, Architecture.Arm64);
        try
        {
            Assert.Null(FileIdentity.Same(stream, path));
        }
        finally
        {
            FileIdentity.AbiOverride = null;
        }
        Assert.True(FileIdentity.Same(stream, path));
    }

    [Fact]
    public void Inodes_are_read_on_linux_and_macos()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.True(FileIdentity.Available);
    }

    // ---- links where the change keeps its folders --------------------------------------------------

    [Theory]
    [InlineData(".restructure-retired")]
    [InlineData(".restructure-partial")]
    [InlineData(".restructuring")]
    public void A_link_in_place_of_a_change_s_folder_is_never_entered(string suffix)
    {
        if (OperatingSystem.IsWindows())
            return; // making a link there needs a privilege the test run may not have
        var path = Table(DbfTableFormat.DbaseIV);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_markers.FullName, "elsewhere"));
        File.WriteAllText(Path.Combine(elsewhere.FullName, "precious.txt"), "keep me");
        File.WriteAllText(Path.Combine(elsewhere.FullName, "restructure.id"), "PEOPLE.DBF.restructured-x");
        Directory.CreateSymbolicLink(path + suffix, elsewhere.FullName);

        var ex = Assert.Throws<DbfException>(() => Open(path));
        Assert.Equal(DbfErrorCode.PathEscapesFolder, ex.Code);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(elsewhere.FullName, "precious.txt")));
        Assert.True(File.Exists(Path.Combine(elsewhere.FullName, "restructure.id")));
    }

    // ---- an .mdx DELETE TAG torn by a power cut -----------------------------------------------------

    // Twenty tags, so the first write of dropping T00 spans two sectors (bytes 512 to 1151). A power
    // cut can keep the sector from 1024 and lose the one before: T15 would then be gone from the
    // table. The before-image beside the .mdx puts the whole tag table back.
    [Fact]
    public void While_a_DELETE_TAG_s_before_image_is_beside_the_mdx_readers_are_told_why_and_a_new_drop_waits()
    {
        var path = Path.Combine(_dir.FullName, "TAGS.DBF");
        using (var created = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 10)]))
            created.Append("Ada");
        using (var table = Open(path))
        {
            table.CreateIndexTag("A", "NAME");
            table.CreateIndexTag("B", "NAME");
            MdxIndex.DropTagPoint = point => { if (point == "entries") throw new IOException("power cut"); };
            try
            {
                Assert.ThrowsAny<IOException>(() => table.DropIndexTag("A"));
            }
            finally
            {
                MdxIndex.DropTagPoint = null;
            }
            // The same open, dropping again with the image still there: told what to do, not a raw IOException.
            var again = Assert.Throws<DbfException>(() => table.DropIndexTag("B"));
            Assert.Equal(DbfErrorCode.IncompleteTransaction, again.Code);
            Assert.DoesNotContain(_dir.FullName, again.Message);
        }
        using (var reader = DbfTable.Open(path))
            Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => reader.OpenProductionIndex()).Code);
        using (var table = Open(path))
        {
            using var index = table.OpenProductionIndex()!;
            Assert.Equal(["A", "B"], index.Tags.Select(t => t.Name));
        }
    }

    [Fact]
    public void A_DELETE_TAG_torn_across_sectors_is_put_back_from_its_before_image()
    {
        var path = Path.Combine(_dir.FullName, "TAGS.DBF");
        using (var created = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 10)]))
            created.Append("Ada");
        var names = Enumerable.Range(0, 20).Select(i => $"T{i:00}").ToList();
        using (var table = Open(path))
        {
            foreach (var name in names)
                table.CreateIndexTag(name, "NAME");
        }
        var mdx = MdxIndex.FindFor(path)!;
        var before = File.ReadAllBytes(mdx);

        using (var table = Open(path))
        {
            MdxIndex.DropTagPoint = point => { if (point == "entries") throw new IOException("power cut"); };
            try
            {
                Assert.ThrowsAny<IOException>(() => table.DropIndexTag("T00"));
            }
            finally
            {
                MdxIndex.DropTagPoint = null;
            }
        }
        // The cut: the sector from 512 reverts to what it held before, the one from 1024 keeps the new bytes.
        var torn = File.ReadAllBytes(mdx);
        Array.Copy(before, 512, torn, 512, 512);
        File.WriteAllBytes(mdx, torn);
        Assert.True(File.Exists(mdx + ".droptag"));

        Assert.Equal(DbfErrorCode.IncompleteTransaction, Assert.Throws<DbfException>(() => Open(path, exclusive: false)).Code);
        using (var table = Open(path))
        {
            using var index = table.OpenProductionIndex()!;
            Assert.Equal(names, index.Tags.Select(t => t.Name));
        }
        Assert.Equal(before.AsSpan(0, 2048).ToArray(), File.ReadAllBytes(mdx).AsSpan(0, 2048).ToArray());
        Assert.False(File.Exists(mdx + ".droptag"));
    }
}
