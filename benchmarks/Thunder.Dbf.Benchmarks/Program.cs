//          _,--._
//        ,'      `.        ProjectThunder :: Thunder.Dbf benchmarks
//   |\  /  ______   \      How fast can a 1988 file format go
//   | \/  /      \   |     on a 2026 machine?
//   |  \ | (o)(o) |  |
//    \  \ \  __  /  /      dotnet run -c Release --project benchmarks/Thunder.Dbf.Benchmarks
//     `--`-`----'--'
//
// Compares full-table scans against DbfDataReader, a widely used open-source DBF reader.
// WriteBenchmarks times appends that keep a dBASE IV .mdx and a FoxPro .cdx in step, and
// seeks through an .mdx. `-- gc` runs a mixed workload and reports GC pause time, which
// BenchmarkDotNet doesn't. Run either under net10.0 or net11.0 (-f) to compare runtimes.
// BenchmarkDotNet 0.15 can't yet build a separate process for .NET 11, so there the
// benchmarks run in this process.
// The README's Visual FoxPro and OLE DB figures were measured separately on Windows and
// aren't part of these benchmarks.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DbfDataReaderOptions = DbfDataReader.DbfDataReaderOptions;
using Thunder.Dbf;
using System.Diagnostics;
using System.Runtime;
using DbfTable = Thunder.Dbf.DbfTable;

if (args is ["gc", ..])
{
    GcReport.Run(args.Length > 1 ? int.Parse(args[1]) : 5);
    return;
}
if (Environment.Version.Major >= 11 && !args.Contains("--inProcess", StringComparer.OrdinalIgnoreCase))
    args = [.. args, "--inProcess"];
BenchmarkSwitcher.FromAssembly(typeof(ScanBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class ScanBenchmarks
{
    private string _generatedPath = "";
    private string _foxProPath = "";

    [Params(200_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _generatedPath = Path.Combine(Path.GetTempPath(), $"thunder-bench-{Rows}.dbf");
        if (!File.Exists(_generatedPath))
            GenerateTable(_generatedPath, Rows);

        _foxProPath = Path.Combine(FindRepoRoot(), "tests", "fixtures", "infused-dbf", "dbase_f5.dbf");
    }

    [Benchmark(Baseline = true, Description = "DbfDataReader: generated table")]
    public int DbfDataReader_Generated() => ScanWithDbfDataReader(_generatedPath);

    [Benchmark(Description = "Thunder.Dbf: generated table")]
    public int Thunder_Generated() => ScanWithThunder(_generatedPath);

    [Benchmark(Description = "DbfDataReader: FoxPro 2 table with memos")]
    public int DbfDataReader_FoxPro() => ScanWithDbfDataReader(_foxProPath);

    [Benchmark(Description = "Thunder.Dbf: FoxPro 2 table with memos")]
    public int Thunder_FoxPro() => ScanWithThunder(_foxProPath);

    [Benchmark(Description = "Thunder.Dbf: 10,000 random record reads")]
    public int Thunder_RandomAccess()
    {
        using var table = DbfTable.Open(_generatedPath);
        var random = new Random(42);
        var touched = 0;
        for (var i = 0; i < 10_000; i++)
            touched += table.Read(random.Next(table.RecordCount)).Values.Count;
        return touched;
    }

    private static int ScanWithThunder(string path)
    {
        using var table = DbfTable.Open(path);
        var nonNull = 0;
        foreach (var record in table.ReadAll())
        {
            foreach (var value in record.Values)
            {
                if (value is not null)
                    nonNull++;
            }
        }
        return nonNull;
    }

    private static int ScanWithDbfDataReader(string path)
    {
        using var reader = new DbfDataReader.DbfDataReader(path, new DbfDataReaderOptions { SkipDeletedRecords = false });
        var nonNull = 0;
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!reader.IsDBNull(i) && reader.GetValue(i) is not null)
                    nonNull++;
            }
        }
        return nonNull;
    }

    private static void GenerateTable(string path, int rows)
    {
        using var table = DbfTable.Create(path,
        [
            DbfFieldDefinition.Character("NAME", 30),
            DbfFieldDefinition.Character("CITY", 20),
            DbfFieldDefinition.Numeric("BALANCE", 12, 2),
            DbfFieldDefinition.Numeric("QTY", 6),
            DbfFieldDefinition.Date("HIRED"),
            DbfFieldDefinition.Logical("ACTIVE"),
        ]);

        var random = new Random(1988);
        string[] cities = ["Atlanta", "Boston", "Chicago", "Denver", "El Paso", "Fresno"];
        for (var i = 0; i < rows; i++)
        {
            table.Append(
                $"Customer {i:D7}",
                cities[i % cities.Length],
                Math.Round((decimal)random.NextDouble() * 100_000m, 2),
                random.Next(1000),
                new DateOnly(1985, 1, 1).AddDays(random.Next(15_000)),
                i % 3 != 0);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Thunder.Dbf.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Couldn't find the repo root (Thunder.Dbf.slnx).");
    }
}

[MemoryDiagnoser]
public class WriteBenchmarks
{
    private const int Appends = 5_000;
    private string _dir = "";
    private string _mdxPath = "";
    private string _cdxPath = "";
    private string _seekPath = "";
    private DbfTable? _mdxTable;
    private DbfTable? _cdxTable;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Directory.CreateTempSubdirectory("thunder-bench-write-").FullName;
        _seekPath = Path.Combine(_dir, "SEEK.DBF");
        using (var table = DbfTable.Create(_seekPath, DbfTableFormat.DbaseIV, Fields()))
        {
            table.FlushWritesToDisk = false;
            for (var i = 0; i < 50_000; i++)
                table.Append(Row(i));
        }
        using (var table = DbfTable.Open(_seekPath, writable: true, exclusive: true))
            table.CreateIndexTag("ID", "ID");
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    // A fresh, empty table with its index tags before every iteration, each in a folder of its own:
    // appends then measure the same work each time, and no other table's index sits beside it
    // (Thunder.Dbf refuses to write past an index it might leave stale).
    [IterationSetup(Target = nameof(Append_Mdx))]
    public void SetupMdx()
    {
        _mdxPath = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, $"m{Guid.NewGuid():N}")).FullName, "BENCH.DBF");
        using (DbfTable.Create(_mdxPath, DbfTableFormat.DbaseIV, Fields()))
        {
        }
        using (var table = DbfTable.Open(_mdxPath, writable: true, exclusive: true))
        {
            table.CreateIndexTag("ID", "ID");
            table.CreateIndexTag("NAME", "NAME");
        }
        _mdxTable = DbfTable.Open(_mdxPath, writable: true, exclusive: true);
        _mdxTable.FlushWritesToDisk = false;
    }

    [IterationSetup(Target = nameof(Append_Cdx))]
    public void SetupCdx()
    {
        _cdxPath = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, $"c{Guid.NewGuid():N}")).FullName, "BENCH.DBF");
        using (DbfTable.Create(_cdxPath, DbfTableFormat.VisualFoxPro, Fields()))
        {
        }
        using (var table = DbfTable.Open(_cdxPath, writable: true, maintainFoxProIndexes: true, exclusive: true))
        {
            table.CreateIndexTag("ID", "ID");
            table.CreateIndexTag("NAME", "NAME");
        }
        _cdxTable = DbfTable.Open(_cdxPath, writable: true, maintainFoxProIndexes: true, exclusive: true);
        _cdxTable.FlushWritesToDisk = false;
    }

    [IterationCleanup]
    public void CleanupIteration()
    {
        _mdxTable?.Dispose();
        _cdxTable?.Dispose();
        _mdxTable = _cdxTable = null;
    }

    [Benchmark(Description = "Thunder.Dbf: 5,000 appends, .mdx with 2 tags")]
    public int Append_Mdx()
    {
        for (var i = 0; i < Appends; i++)
            _mdxTable!.Append(Row(i));
        return _mdxTable!.RecordCount;
    }

    [Benchmark(Description = "Thunder.Dbf: 5,000 appends, .cdx with 2 tags")]
    public int Append_Cdx()
    {
        for (var i = 0; i < Appends; i++)
            _cdxTable!.Append(Row(i));
        return _cdxTable!.RecordCount;
    }

    [Benchmark(Description = "Thunder.Dbf: 10,000 seeks through an .mdx")]
    public int Seek_Mdx()
    {
        using var table = DbfTable.Open(_seekPath);
        using var index = table.OpenProductionIndex()!;
        var tag = index["ID"];
        var random = new Random(7);
        var found = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (table.Seek(tag, (decimal)random.Next(50_000)) is not null)
                found++;
        }
        return found;
    }

    internal static DbfFieldDefinition[] Fields() =>
    [
        DbfFieldDefinition.Numeric("ID", 8),
        DbfFieldDefinition.Character("NAME", 30),
        DbfFieldDefinition.Numeric("BALANCE", 12, 2),
        DbfFieldDefinition.Date("HIRED"),
    ];

    internal static object?[] Row(int i) =>
        [(decimal)i, $"Customer {(i * 7919) % 1_000_003:D7}", (i % 10_000) / 100m, new DateOnly(1990, 1, 1).AddDays(i % 9_000)];
}

// GC pause time, collections and allocations for a mixed workload, the numbers a .NET runtime
// upgrade is most likely to move. Median of several runs, after one untimed warm-up.
public static class GcReport
{
    public static void Run(int runs)
    {
        var dir = Directory.CreateTempSubdirectory("thunder-gc-").FullName;
        try
        {
            Workload(dir, 0); // warm-up
            var results = new List<(double Ms, double PauseMs, int G0, int G1, int G2, long Bytes)>();
            for (var run = 1; run <= runs; run++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var pause = GC.GetTotalPauseDuration();
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                var bytes = GC.GetTotalAllocatedBytes(precise: true);
                var clock = Stopwatch.StartNew();
                Workload(dir, run);
                clock.Stop();
                results.Add((clock.Elapsed.TotalMilliseconds, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                    GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
                    GC.GetTotalAllocatedBytes(precise: true) - bytes));
            }
            static double Median(IEnumerable<double> v) { var a = v.Order().ToArray(); return a[a.Length / 2]; }
            Console.WriteLine($"runtime {Environment.Version}, server GC {GCSettings.IsServerGC}, {runs} runs (median)");
            Console.WriteLine($"  wall {Median(results.Select(r => r.Ms)):F0} ms, GC pause {Median(results.Select(r => r.PauseMs)):F1} ms");
            Console.WriteLine($"  gen0 {Median(results.Select(r => (double)r.G0)):F0}, gen1 {Median(results.Select(r => (double)r.G1)):F0}, gen2 {Median(results.Select(r => (double)r.G2)):F0}");
            Console.WriteLine($"  allocated {Median(results.Select(r => (double)r.Bytes)) / 1_048_576:F1} MB");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // 20,000 appends to a table keeping two .mdx tags, a full scan, and 20,000 seeks.
    private static void Workload(string dir, int run)
    {
        var path = Path.Combine(dir, $"GC{run}.DBF");
        using (DbfTable.Create(path, DbfTableFormat.DbaseIV, WriteBenchmarks.Fields()))
        {
        }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            table.CreateIndexTag("ID", "ID");
            table.CreateIndexTag("NAME", "NAME");
            table.FlushWritesToDisk = false;
            for (var i = 0; i < 20_000; i++)
                table.Append(WriteBenchmarks.Row(i));
        }
        using (var table = DbfTable.Open(path))
        {
            var seen = 0;
            foreach (var record in table.ReadAll())
                seen += record.Values.Count;
            using var index = table.OpenProductionIndex()!;
            var tag = index["ID"];
            var random = new Random(run);
            for (var i = 0; i < 20_000; i++)
                seen += table.Seek(tag, (decimal)random.Next(20_000)) is null ? 0 : 1;
            GC.KeepAlive(seen);
        }
    }
}
