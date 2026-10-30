//          _,--._
//        ,'      `.        ProjectThunder :: Thunder.Dbf benchmarks
//   |\  /  ______   \      How fast can a 1988 file format go
//   | \/  /      \   |     on a 2026 machine?
//   |  \ | (o)(o) |  |
//    \  \ \  __  /  /      dotnet run -c Release --project benchmarks/Thunder.Dbf.Benchmarks
//     `--`-`----'--'
//
// Compares full-table scans against DbfDataReader, the most-used managed DBF reader.
// The ADO.NET / OLE DB comparison (Jet, ACE, VFP provider) needs Windows; see README.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DbfDataReaderOptions = DbfDataReader.DbfDataReaderOptions;
using Thunder.Dbf;
using DbfTable = Thunder.Dbf.DbfTable;

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
