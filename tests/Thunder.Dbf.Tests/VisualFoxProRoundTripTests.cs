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
        { "contacts.dbf", ["infused-dbf/foxprodb/contacts.dbf", "infused-dbf/foxprodb/contacts.CDX", "infused-dbf/foxprodb/contacts.FPT"] },
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

        // 1. Thunder.Dbf writes: enough appends to split pages, plus updates, deletes and recalls.
        using (var dbf = IndexedTables.OpenWithIndexes(path))
        {
            Assert.True(dbf.MaintainsIndex, "The table's .cdx isn't being kept up to date, so there's nothing to check.");
            var random = new Random(9);
            var count = dbf.RecordCount;
            for (var i = 0; i < 400; i++)
                dbf.Append(Mutated(dbf, dbf.Read(random.Next(count)).Values.ToArray(), $"T{i:D4}"));
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
        using (var copy = IndexedTables.OpenWithIndexes(Path.Combine(rebuilt, table)))
            copy.RebuildIndex();
        var expected = TagOrders(Path.Combine(rebuilt, table));
        foreach (var (tag, records) in expected)
            Assert.True(records.SequenceEqual(afterVfp[tag]), $"Tag {tag} after VFP's edits: the file holds {Show(afterVfp[tag])}, a rebuild gives {Show(records)}.");
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
                        CASE laF[lnF, 2] $ "NFIBY"
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
}
