//      Thunder.Dbf  --writes-->  .dbf + .cdx  --VFP 9 reads every tag in order-->  same order?
//      Thunder.Dbf  <--reads--   .dbf + .cdx  <--VFP 9 appends, edits, deletes--   same keys as a rebuild?
//
//      ProjectThunder :: Thunder.Dbf tests
//      The real thing: Visual FoxPro 9 itself checks the .cdx files Thunder.Dbf keeps, and Thunder.Dbf
//      checks the ones VFP keeps. Runs only where VFP is installed (Windows), pointed to by
//
//        set THUNDER_VFP=C:\Program Files (x86)\Microsoft Visual FoxPro 9\vfp9.exe
//        dotnet test --filter VisualFoxPro
//
//      Without the variable these tests do nothing. VFP isn't redistributable, so it never lives in the repo.

using System.Diagnostics;
using System.Text;

namespace Thunder.Dbf.Tests;

public sealed class VisualFoxProRoundTripTests : IDisposable
{
    private static readonly string? Vfp = Environment.GetEnvironmentVariable("THUNDER_VFP");
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-vfp-");

    public void Dispose() => _dir.Delete(recursive: true);

    public static TheoryData<string, string[]> Tables() => new()
    {
        // Written by Visual FoxPro itself. (contacts.dbf indexes long field names from a database
        // container, which Thunder.Dbf refuses to maintain, so it can't be used here.)
        { "calls.dbf", ["infused-dbf/foxprodb/calls.dbf", "infused-dbf/foxprodb/calls.CDX", "infused-dbf/foxprodb/calls.FPT"] },
        { "TEST10K.DBF", ["xsharp/rdd-tests/TEST10K.DBF", "xsharp/rdd-tests/TEST10K.CDX"] },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void VisualFoxPro_reads_the_cdx_Thunder_keeps_and_Thunder_reads_the_one_VFP_keeps(string table, string[] files)
    {
        if (Vfp is null)
            return;
        Assert.True(File.Exists(Vfp), $"THUNDER_VFP points at {Vfp}, which doesn't exist.");

        foreach (var file in files)
            File.Copy(Path.Combine(Fixtures.Root, file), Path.Combine(_dir.FullName, Path.GetFileName(file)));
        var path = Path.Combine(_dir.FullName, table);
        FreeFromMissingDatabase(Vfp, path);

        // 1. Thunder.Dbf writes: enough appends to split pages, plus updates, deletes and recalls.
        using (var dbf = OpenForWriting(path))
        {
            Assert.True(dbf.MaintainsIndex, "The table's .cdx isn't being kept up to date, so there's nothing to check.");
            var random = new Random(9);
            var count = dbf.RecordCount;
            for (var i = 0; i < 400; i++)
                dbf.Append(WithNewIds(dbf, Mutated(dbf, dbf.Read(random.Next(count)).Values.ToArray(), $"T{i:D4}"), 900_000 + i));
            for (var i = 0; i < 60; i++)
            {
                var index = random.Next(dbf.RecordCount);
                dbf.Update(index, Mutated(dbf, dbf.Read(index).Values.ToArray(), $"U{i:D4}"));
            }
            for (var i = 0; i < 40; i++)
                dbf.Delete(random.Next(dbf.RecordCount));
            dbf.Recall(dbf.RecordCount - 1);
        }

        // 2. VFP walks every tag in order. Its order must be the order Thunder.Dbf's index holds.
        var ours = TagOrders(path);
        var theirs = RunVfp(path, edit: false);
        Assert.Equal(ours.Keys.Order(), theirs.Keys.Order());
        foreach (var (tag, records) in ours)
            Assert.True(records.SequenceEqual(theirs[tag]), $"Tag {tag}: VFP walks {Show(theirs[tag])} but the index holds {Show(records)}.");

        // 3. VFP writes through its own index; Thunder.Dbf must find exactly what a rebuild gives.
        RunVfp(path, edit: true);
        var afterVfp = TagOrders(path);
        var rebuilt = Path.Combine(_dir.FullName, "rebuilt");
        Directory.CreateDirectory(rebuilt);
        foreach (var file in Directory.GetFiles(_dir.FullName))
            File.Copy(file, Path.Combine(rebuilt, Path.GetFileName(file)));
        using (var copy = OpenForWriting(Path.Combine(rebuilt, table)))
            copy.RebuildIndex();
        var expected = TagOrders(Path.Combine(rebuilt, table));
        foreach (var (tag, records) in expected)
        {
            // A UNIQUE tag keeps whichever record got the key first until FoxPro reindexes, so it
            // matches a rebuild only right after one. VFP's walk in step 2 already checked it.
            if (UniqueTags(path).Contains(tag))
                continue;
            Assert.True(records.SequenceEqual(afterVfp[tag]), $"Tag {tag} after VFP's edits: the file holds {Show(afterVfp[tag])}, a rebuild gives {Show(records)}.");
        }
    }

    // Opens with the .cdx preview on, taking over an index sorted case-blind (X#'s TEST10K) by
    // rebuilding it in byte order first, as MaintainIndex(path, rebuild: true) does.
    private static DbfTable OpenForWriting(string path)
    {
        var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true);
        if (!table.MaintainsIndex)
            table.MaintainIndex(Path.ChangeExtension(path, Path.GetExtension(path).Any(char.IsUpper) ? ".CDX" : ".cdx"), rebuild: true);
        return table;
    }

    private static HashSet<string> UniqueTags(string path)
    {
        using var table = DbfTable.Open(path);
        using var index = table.OpenStructuralIndex()!;
        return index.Tags.Where(t => ((IDbfIndexTag)t).IsUnique).Select(t => t.Name.ToUpperInvariant()).ToHashSet();
    }

    // New records need their own values in whole-number fields, which is where candidate (primary)
    // keys like CALL_ID live: a copy would repeat the key, and FoxPro and Thunder.Dbf both refuse that.
    private static object?[] WithNewIds(DbfTable table, object?[] values, int serial)
    {
        for (var i = 0; i < table.Fields.Count; i++)
        {
            var field = table.Fields[i];
            if (field.IsSystem || field.IsAutoIncrement)
                continue;
            if (field.Type == 'I')
                values[i] = serial;
            else if (field.Type == 'N' && field.DecimalCount == 0 && field.Length >= 7)
                values[i] = (decimal)serial;
        }
        return values;
    }

    // A copy of a record with its character fields changed, so new keys land all over each tag.
    private static object?[] Mutated(DbfTable table, object?[] values, string stamp)
    {
        for (var i = 0; i < table.Fields.Count; i++)
        {
            var field = table.Fields[i];
            if (field.Type == 'C' && !field.IsSystem && field.Length > 0)
                values[i] = (stamp + values[i])[..Math.Min(field.Length, stamp.Length + ((string?)values[i])?.Length ?? 0)];
        }
        return values;
    }

    private static Dictionary<string, List<int>> TagOrders(string path)
    {
        using var table = DbfTable.Open(path);
        using var index = table.OpenStructuralIndex()!;
        return index.Tags.ToDictionary(t => t.Name.ToUpperInvariant(), t => ((IDbfIndexTag)t).Entries().Select(e => e.RecordNumber).ToList());
    }

    private static string Show(List<int> records) =>
        records.Count <= 12 ? string.Join(",", records) : string.Join(",", records.Take(12)) + $",... ({records.Count})";

    // Writes a .prg that either lists every tag's record order or edits the table through its
    // indexes, runs VFP 9 hidden, and reads back what it wrote.
    private Dictionary<string, List<int>> RunVfp(string table, bool edit)
    {
        var output = Path.Combine(_dir.FullName, edit ? "vfp-edit.txt" : "vfp-order.txt");
        var errors = Path.Combine(_dir.FullName, "vfp-errors.txt");
        var program = Path.Combine(_dir.FullName, edit ? "edit.prg" : "order.prg");
        File.Delete(output);
        File.Delete(errors);

        var script = new StringBuilder($"""
            _SCREEN.Visible = .F.
            SET SAFETY OFF
            SET DELETED OFF
            SET EXCLUSIVE OFF
            SET TALK OFF
            SET CPDIALOG OFF
            ON ERROR DO LogError WITH ERROR(), MESSAGE(), LINENO()
            USE "{table}" SHARED
            """);
        script.AppendLine();
        if (edit)
        {
            script.AppendLine($"""
                LOCAL lnI, lnF, laF[1], lnCount
                lnCount = AFIELDS(laF)
                FOR lnI = 1 TO 150
                    APPEND BLANK
                    FOR lnF = 1 TO lnCount
                        DO CASE
                        CASE laF[lnF, 2] = "C"
                            REPLACE (laF[lnF, 1]) WITH LEFT("V" + SYS(2015), laF[lnF, 3])
                        CASE laF[lnF, 2] = "I" OR (laF[lnF, 2] = "N" AND laF[lnF, 4] = 0 AND laF[lnF, 3] >= 7)
                            REPLACE (laF[lnF, 1]) WITH 800000 + lnI
                        CASE laF[lnF, 2] $ "NF"
                            REPLACE (laF[lnF, 1]) WITH INT(RAND() * 10 ^ MAX(MIN(laF[lnF, 3] - laF[lnF, 4] - 2, 3), 0))
                        CASE laF[lnF, 2] $ "BY"
                            REPLACE (laF[lnF, 1]) WITH INT(RAND() * 1000)
                        CASE laF[lnF, 2] $ "DT"
                            REPLACE (laF[lnF, 1]) WITH DATE() - INT(RAND() * 3000)
                        ENDCASE
                    ENDFOR
                ENDFOR
                FOR lnI = 1 TO 40
                    GO INT(RAND() * RECCOUNT()) + 1
                    IF RLOCK()
                        FOR lnF = 1 TO lnCount
                            IF laF[lnF, 2] = "C"
                                REPLACE (laF[lnF, 1]) WITH LEFT("E" + SYS(2015), laF[lnF, 3])
                            ENDIF
                        ENDFOR
                        IF lnI % 4 = 0
                            DELETE
                        ENDIF
                        UNLOCK
                    ENDIF
                ENDFOR
                STRTOFILE("done", "{output}")
                """);
        }
        else
        {
            script.AppendLine($"""
                LOCAL lnT, lcTag
                FOR lnT = 1 TO TAGCOUNT()
                    lcTag = TAG(lnT)
                    SET ORDER TO TAG (lcTag)
                    STRTOFILE("TAG " + UPPER(lcTag) + CHR(13) + CHR(10), "{output}", .T.)
                    SCAN
                        STRTOFILE(TRANSFORM(RECNO()) + CHR(13) + CHR(10), "{output}", .T.)
                    ENDSCAN
                ENDFOR
                """);
        }
        script.AppendLine($"""
            USE
            QUIT

            PROCEDURE LogError(tnError, tcMessage, tnLine)
                STRTOFILE(TRANSFORM(tnError) + " " + tcMessage + " at line " + TRANSFORM(tnLine) + CHR(13) + CHR(10), "{errors}", .T.)
                QUIT
            ENDPROC
            """);
        File.WriteAllText(program, script.ToString(), Encoding.Latin1);
        File.Delete(Path.ChangeExtension(program, ".fxp")); // VFP -T runs a stale compiled .fxp over a newer .prg

        using var process = Process.Start(new ProcessStartInfo(Vfp!, $"-T \"{program}\"") { UseShellExecute = false, WorkingDirectory = _dir.FullName })!;
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill();
            Assert.Fail("VFP didn't finish within 5 minutes.");
        }
        Assert.False(File.Exists(errors), File.Exists(errors) ? "VFP reported: " + File.ReadAllText(errors) : "");
        Assert.True(File.Exists(output), "VFP finished without writing its results.");

        var orders = new Dictionary<string, List<int>>();
        List<int>? current = null;
        foreach (var line in File.ReadLines(output))
        {
            if (line.StartsWith("TAG ", StringComparison.Ordinal))
                orders[line[4..].Trim()] = current = [];
            else if (int.TryParse(line, out var record))
                current!.Add(record);
        }
        return orders;
    }

    // calls.dbf belongs to a database container the fixtures don't carry, and VFP won't open it
    // without one. FREE TABLE drops the link, as it does after a .dbc has been deleted.
    internal static void FreeFromMissingDatabase(string vfp, string table)
    {
        if (BacklinkLength(table) == 0)
            return;
        var program = Path.Combine(Path.GetDirectoryName(table)!, "free.prg");
        File.WriteAllText(program, $"_SCREEN.Visible = .F.\r\nSET SAFETY OFF\r\nON ERROR QUIT\r\nFREE TABLE \"{table}\"\r\nQUIT\r\n", Encoding.Latin1);
        File.Delete(Path.ChangeExtension(program, ".fxp"));
        using (var process = Process.Start(new ProcessStartInfo(vfp, $"-T \"{program}\"") { UseShellExecute = false })!)
            Assert.True(process.WaitForExit(TimeSpan.FromMinutes(1)), "VFP didn't finish FREE TABLE within a minute.");
        Assert.True(BacklinkLength(table) == 0, $"VFP's FREE TABLE left {Path.GetFileName(table)} linked to its database.");
    }

    // Visual FoxPro tables end their header with 263 bytes naming the database they belong to.
    private static int BacklinkLength(string table)
    {
        var header = new byte[4096];
        using (var stream = File.OpenRead(table))
            stream.ReadExactly(header, 0, Math.Min(header.Length, (int)stream.Length));
        var length = BitConverter.ToUInt16(header, 8);
        if (header[0] is not (0x30 or 0x31 or 0x32) || length > header.Length)
            return 0;
        return header.AsSpan(length - 263, 263).IndexOf((byte)0) is var end and >= 0 ? end : 263;
    }

}
