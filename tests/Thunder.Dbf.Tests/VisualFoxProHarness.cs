//      dotnet test  -->  order.prg / edit.prg / dump.prg  -->  vfp9.exe -T (hidden)  -->  vfp-*.txt  -->  Assert
//
//      ProjectThunder :: Thunder.Dbf tests
//      What every Visual FoxPro round trip shares: a temporary folder, a hidden VFP 9 run of a generated
//      .prg, and the three things VFP is asked to do with a table (walk its tags, edit it, list every
//      value), each with the matching view from Thunder.Dbf's side to compare against.
//
//        set THUNDER_VFP=C:\Program Files (x86)\Microsoft Visual FoxPro 9\vfp9.exe
//        dotnet test --filter VisualFoxPro
//
//      Without the variable the tests that use it do nothing past their own setup.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Thunder.Dbf.Tests;

public abstract class VisualFoxProHarness : IDisposable
{
    protected static readonly string? Vfp = Environment.GetEnvironmentVariable("THUNDER_VFP");
    protected readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-vfp-");

    public void Dispose()
    {
        _dir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    // True when the test should go on to VFP; false (the test then returns, passing) without THUNDER_VFP.
    protected static bool HaveVfp()
    {
        if (Vfp is null)
            return false;
        Assert.True(File.Exists(Vfp), $"THUNDER_VFP points at {Vfp}, which doesn't exist.");
        return true;
    }

    // Opens with the .cdx preview on, taking over an index sorted case-blind (X#'s TEST10K) by
    // rebuilding it in byte order first, as MaintainIndex(path, rebuild: true) does.
    protected static DbfTable OpenForWriting(string path)
    {
        var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true);
        if (!table.MaintainsIndex)
            table.MaintainIndex(Path.ChangeExtension(path, Path.GetExtension(path).Any(char.IsUpper) ? ".CDX" : ".cdx"), rebuild: true);
        return table;
    }

    // ---- tags ------------------------------------------------------------------------------

    // VFP walks every tag in order. Its order must be the order Thunder.Dbf's index holds.
    protected void AssertVfpWalksEveryTagAsTheIndexHolds(string path)
    {
        var ours = TagOrders(path);
        var theirs = RunVfp(path, "order", WalkTagsScript);
        Assert.Equal(ours.Keys.Order(), theirs.Keys.Order());
        foreach (var (tag, records) in ours)
            Assert.True(records.SequenceEqual(theirs[tag]), $"Tag {tag}: VFP walks {Show(theirs[tag])} but the index holds {Show(records)}.");
    }

    // After VFP has written through its own index, Thunder.Dbf must find exactly what a rebuild gives.
    protected void AssertEveryTagMatchesARebuild(string path)
    {
        var after = TagOrders(path);
        var rebuilt = Directory.CreateDirectory(Path.Combine(_dir.FullName, "rebuilt-" + Guid.NewGuid().ToString("N")[..8]));
        foreach (var file in Directory.GetFiles(_dir.FullName))
            File.Copy(file, Path.Combine(rebuilt.FullName, Path.GetFileName(file)));
        var copy = Path.Combine(rebuilt.FullName, Path.GetFileName(path));
        using (var table = OpenForWriting(copy))
            table.RebuildIndex();
        var expected = TagOrders(copy);
        var unique = UniqueTags(path);
        Assert.Equal(expected.Keys.Order(), after.Keys.Order());
        foreach (var (tag, records) in expected)
        {
            // A UNIQUE tag keeps whichever record got the key first until FoxPro reindexes, so it
            // matches a rebuild only right after one. VFP's walk already checked it.
            if (unique.Contains(tag))
                continue;
            Assert.True(records.SequenceEqual(after[tag]), $"Tag {tag} after VFP's edits: the file holds {Show(after[tag])}, a rebuild gives {Show(records)}.");
        }
    }

    protected static Dictionary<string, List<int>> TagOrders(string path)
    {
        using var table = DbfTable.Open(path);
        using var index = table.OpenStructuralIndex()!;
        return index.Tags.ToDictionary(t => t.Name.ToUpperInvariant(), t => ((IDbfIndexTag)t).Entries().Select(e => e.RecordNumber).ToList());
    }

    private static HashSet<string> UniqueTags(string path)
    {
        using var table = DbfTable.Open(path);
        using var index = table.OpenStructuralIndex()!;
        return index.Tags.Where(t => ((IDbfIndexTag)t).IsUnique).Select(t => t.Name.ToUpperInvariant()).ToHashSet();
    }

    private static string Show(List<int> records) =>
        records.Count <= 12 ? string.Join(",", records) : string.Join(",", records.Take(12)) + $",... ({records.Count})";

    // ---- values ----------------------------------------------------------------------------

    // VFP lists every record (deleted ones too) and every value, and it must be what Thunder.Dbf reads.
    // Returns VFP's list, one line per record: "RECNO <tab> * or space <tab> FIELD=value <tab> ...".
    protected List<string> AssertVfpReadsWhatThunderReads(string path, string step)
    {
        var ours = Values(path);
        var theirs = RunVfpLines(path, "dump-" + step, DumpScript);
        for (var i = 0; i < Math.Min(ours.Count, theirs.Count); i++)
            Assert.True(ours[i] == theirs[i], $"{step}: VFP reads record {i + 1} as\n  {theirs[i]}\nbut Thunder.Dbf reads\n  {ours[i]}");
        Assert.True(ours.Count == theirs.Count, $"{step}: VFP reads {theirs.Count} records, Thunder.Dbf {ours.Count}.");
        return theirs;
    }

    // Thunder.Dbf's side of the list, written the way DumpScript writes VFP's: numbers with the field's
    // decimals, dates as YYYYMMDD, datetimes as YYYYMMDDhhmmss, logicals T or F, memos with \r and \n.
    // A blank number reads as 0 and a blank logical as F in VFP, so they're listed that way here too.
    protected static List<string> Values(string path)
    {
        using var table = DbfTable.Open(path);
        var lines = new List<string>();
        foreach (var record in table.ReadAll())
        {
            var line = new StringBuilder($"{record.RecordNumber}\t{(record.IsDeleted ? "*" : " ")}");
            for (var i = 0; i < table.Fields.Count; i++)
            {
                var field = table.Fields[i];
                if (field.IsSystem)
                    continue;
                line.Append('\t').Append(field.Name.ToUpperInvariant()).Append('=').Append(record.IsNull(i) ? ".NULL." : Listed(field, record[i]));
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    private static string Listed(DbfField field, object? value) => field.Type switch
    {
        'C' => (string?)value ?? "",
        'M' => ((string?)value ?? "").Replace("\r", "\\r").Replace("\n", "\\n"),
        'N' or 'F' => Convert.ToDecimal(value ?? 0m, CultureInfo.InvariantCulture).ToString("F" + field.DecimalCount, CultureInfo.InvariantCulture),
        'I' => ((int?)value ?? 0).ToString(CultureInfo.InvariantCulture),
        'Y' => ((decimal?)value ?? 0m).ToString("F4", CultureInfo.InvariantCulture),
        'B' => ((double?)value ?? 0d).ToString("F" + field.DecimalCount, CultureInfo.InvariantCulture),
        'D' => value is DateOnly d ? d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "",
        'T' => value is DateTime t ? t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : "",
        'L' => value is true ? "T" : "F",
        _ => "?",
    };

    // Lines cut down to the record number, the deleted mark and the named fields, to compare a table
    // before and after its structure changed.
    protected static List<string> Only(IEnumerable<string> lines, IReadOnlySet<string> fields) =>
        lines.Select(line => string.Join("\t", line.Split('\t').Where((part, i) => i < 2 || fields.Contains(part[..part.IndexOf('=')])))).ToList();

    // ---- running VFP -----------------------------------------------------------------------

    protected void Edit(string path, bool nulls = false) => RunVfp(path, "edit", output => EditScript(output, nulls));

    private Dictionary<string, List<int>> RunVfp(string table, string name, Func<string, string> body)
    {
        var orders = new Dictionary<string, List<int>>();
        List<int>? current = null;
        foreach (var line in RunVfpLines(table, name, body))
        {
            if (line.StartsWith("TAG ", StringComparison.Ordinal))
                orders[line[4..].Trim()] = current = [];
            else if (int.TryParse(line, out var record))
                current!.Add(record);
        }
        return orders;
    }

    // Writes a .prg that opens the table SHARED and runs body (which writes its results to the file it's
    // given), runs VFP 9 hidden, and reads back what it wrote.
    private List<string> RunVfpLines(string table, string name, Func<string, string> body)
    {
        var output = Path.Combine(_dir.FullName, $"vfp-{name}.txt");
        var errors = Path.Combine(_dir.FullName, "vfp-errors.txt");
        var program = Path.Combine(_dir.FullName, name + ".prg");
        File.Delete(output);
        File.Delete(errors);

        var script = $"""
            _SCREEN.Visible = .F.
            SET SAFETY OFF
            SET DELETED OFF
            SET EXCLUSIVE OFF
            SET TALK OFF
            SET CPDIALOG OFF
            ON ERROR DO LogError WITH ERROR(), MESSAGE(), LINENO()
            USE "{table}" SHARED
            {body(output)}
            USE
            QUIT

            PROCEDURE LogError(tnError, tcMessage, tnLine)
                STRTOFILE(TRANSFORM(tnError) + " " + tcMessage + " at line " + TRANSFORM(tnLine) + CHR(13) + CHR(10), "{errors}", .T.)
                QUIT
            ENDPROC

            """;
        File.WriteAllText(program, script.ReplaceLineEndings("\r\n"), Encoding.Latin1);
        File.Delete(Path.ChangeExtension(program, ".fxp")); // VFP -T runs a stale compiled .fxp over a newer .prg

        using var process = Process.Start(new ProcessStartInfo(Vfp!, $"-T \"{program}\"") { UseShellExecute = false, WorkingDirectory = _dir.FullName })!;
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill();
            Assert.Fail("VFP didn't finish within 5 minutes.");
        }
        Assert.False(File.Exists(errors), File.Exists(errors) ? "VFP reported: " + File.ReadAllText(errors, Encoding.Latin1) : "");
        Assert.True(File.Exists(output), "VFP finished without writing its results.");
        // The FoxPro tables here are all code page 1252 (language driver 0x03), as VFP writes this file.
        return File.ReadAllLines(output, Encoding.Latin1).Where(line => line.Length > 0).ToList();
    }

    // Opening and appending to the file once per record is slow enough on a busy PC to time out a
    // 10,000-record table, so the lines are gathered and written in 1 MB pieces.
    private static string WalkTagsScript(string output) => $"""
        LOCAL lnT, lcTag, lcOut
        lcOut = ""
        FOR lnT = 1 TO TAGCOUNT()
            lcTag = TAG(lnT)
            SET ORDER TO TAG (lcTag)
            lcOut = lcOut + "TAG " + UPPER(lcTag) + CHR(13) + CHR(10)
            SCAN
                lcOut = lcOut + TRANSFORM(RECNO()) + CHR(13) + CHR(10)
                IF LEN(lcOut) > 1000000
                    STRTOFILE(lcOut, "{output}", .T.)
                    lcOut = ""
                ENDIF
            ENDSCAN
        ENDFOR
        STRTOFILE(lcOut, "{output}", .T.)
        """;

    // In record order, every field AFIELDS() lists (which leaves out _NullFlags), as Values lists them.
    private static string DumpScript(string output) => $"""
        LOCAL lnF, laF[1], lnCount, lcOut, lcLine, luV, lcV
        lnCount = AFIELDS(laF)
        lcOut = ""
        SET ORDER TO 0
        SCAN
            lcLine = TRANSFORM(RECNO()) + CHR(9) + IIF(DELETED(), "*", " ")
            FOR lnF = 1 TO lnCount
                luV = EVALUATE(laF[lnF, 1])
                DO CASE
                CASE ISNULL(luV)
                    lcV = ".NULL."
                CASE laF[lnF, 2] = "C"
                    lcV = RTRIM(luV)
                CASE laF[lnF, 2] = "M"
                    lcV = STRTRAN(STRTRAN(luV, CHR(13), "\r"), CHR(10), "\n")
                CASE laF[lnF, 2] $ "NFB"
                    lcV = ALLTRIM(STR(luV, 25, laF[lnF, 4]))
                CASE laF[lnF, 2] = "I"
                    lcV = TRANSFORM(luV)
                CASE laF[lnF, 2] = "Y"
                    lcV = ALLTRIM(STR(MTON(luV), 25, 4))
                CASE laF[lnF, 2] = "D"
                    lcV = ALLTRIM(DTOS(luV))
                CASE laF[lnF, 2] = "T"
                    lcV = IIF(EMPTY(luV), "", TTOC(luV, 1))
                CASE laF[lnF, 2] = "L"
                    lcV = IIF(luV, "T", "F")
                OTHERWISE
                    lcV = "?"
                ENDCASE
                lcLine = lcLine + CHR(9) + UPPER(laF[lnF, 1]) + "=" + lcV
            ENDFOR
            lcOut = lcOut + lcLine + CHR(13) + CHR(10)
            IF LEN(lcOut) > 1000000
                STRTOFILE(lcOut, "{output}", .T.)
                lcOut = ""
            ENDIF
        ENDSCAN
        STRTOFILE(lcOut, "{output}", .T.)
        """;

    // VFP appends 150 records and edits and deletes others, through its own indexes and locks. Whole
    // numbers of 7 digits or more get unique values, since candidate keys live there. With nulls, every
    // fifth new record and every third edited one get .NULL. in each field that can hold it.
    private static string EditScript(string output, bool nulls) => $"""
        LOCAL lnI, lnF, laF[1], lnCount, llNulls
        llNulls = {(nulls ? ".T." : ".F.")}
        lnCount = AFIELDS(laF)
        FOR lnI = 1 TO 150
            APPEND BLANK
            FOR lnF = 1 TO lnCount
                DO CASE
                CASE llNulls AND laF[lnF, 5] AND lnI % 5 = 0
                    REPLACE (laF[lnF, 1]) WITH .NULL.
                CASE laF[lnF, 2] = "C"
                    REPLACE (laF[lnF, 1]) WITH LEFT("V" + SYS(2015), laF[lnF, 3])
                CASE laF[lnF, 2] = "I" OR (laF[lnF, 2] = "N" AND laF[lnF, 4] = 0 AND laF[lnF, 3] >= 7)
                    REPLACE (laF[lnF, 1]) WITH 800000 + lnI
                CASE laF[lnF, 2] $ "NF"
                    REPLACE (laF[lnF, 1]) WITH INT(RAND() * 10 ^ MAX(MIN(laF[lnF, 3] - laF[lnF, 4] - 2, 3), 0))
                CASE laF[lnF, 2] = "B"
                    REPLACE (laF[lnF, 1]) WITH INT(RAND() * 1000)
                CASE laF[lnF, 2] = "Y"
                    REPLACE (laF[lnF, 1]) WITH NTOM(INT(RAND() * 10000000) / 100)
                CASE laF[lnF, 2] = "D"
                    REPLACE (laF[lnF, 1]) WITH DATE() - INT(RAND() * 3000)
                CASE laF[lnF, 2] = "T"
                    REPLACE (laF[lnF, 1]) WITH DATETIME(2026, 10, 31, 12, 0, 0) - INT(RAND() * 100000000)
                CASE laF[lnF, 2] = "L"
                    REPLACE (laF[lnF, 1]) WITH RAND() < 0.5
                CASE laF[lnF, 2] = "M"
                    REPLACE (laF[lnF, 1]) WITH "VFP memo " + TRANSFORM(lnI) + CHR(13) + CHR(10) + REPLICATE("v", lnI * 7)
                ENDCASE
            ENDFOR
        ENDFOR
        FOR lnI = 1 TO 40
            GO INT(RAND() * RECCOUNT()) + 1
            IF RLOCK()
                FOR lnF = 1 TO lnCount
                    DO CASE
                    CASE llNulls AND laF[lnF, 5] AND lnI % 3 = 0
                        REPLACE (laF[lnF, 1]) WITH .NULL.
                    CASE laF[lnF, 2] = "C"
                        REPLACE (laF[lnF, 1]) WITH LEFT("E" + SYS(2015), laF[lnF, 3])
                    CASE laF[lnF, 2] = "M" AND lnI % 2 = 0
                        REPLACE (laF[lnF, 1]) WITH "Edited by VFP " + TRANSFORM(lnI) + REPLICATE("e", lnI * 11)
                    ENDCASE
                ENDFOR
                IF lnI % 4 = 0
                    DELETE
                ENDIF
                UNLOCK
            ENDIF
        ENDFOR
        STRTOFILE("done", "{output}")
        """;
}
