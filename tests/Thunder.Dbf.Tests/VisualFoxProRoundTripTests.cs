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

public sealed class VisualFoxProRoundTripTests : VisualFoxProHarness
{
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
        if (!HaveVfp())
            return;

        foreach (var file in files)
            File.Copy(Path.Combine(Fixtures.Root, file), Path.Combine(_dir.FullName, Path.GetFileName(file)));
        var path = Path.Combine(_dir.FullName, table);
        FreeFromMissingDatabase(Vfp!, path);

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
        AssertVfpWalksEveryTagAsTheIndexHolds(path);

        // 3. VFP writes through its own index; Thunder.Dbf must find exactly what a rebuild gives.
        Edit(path);
        AssertEveryTagMatchesARebuild(path);
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
