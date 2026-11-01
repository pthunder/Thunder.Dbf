//    fixture --corrupt--> copy --open/read/seek/write--> explained error?   no: a finding
//
//   ProjectThunder :: Thunder.Dbf fuzzer
//   Corrupted copies of the test fixtures, thrown at every reader and writer. Each case is
//   reproducible from its seed. A finding is an exception that isn't a DbfException (or, from
//   the index classes, InvalidDataException or NotSupportedException), a hang over 10 s, or more
//   than 512 MB allocated. A hang or crash ends the process; fuzz.sh names the seed and goes on.
//
//   usage: Thunder.Dbf.Fuzz <fixturesDir> <workDir> <findingsDir> <firstSeed> <count>
//   (or tools/Thunder.Dbf.Fuzz/fuzz.sh <firstSeed> <lastSeed>; see docs/testing.md)

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Thunder.Dbf;

var (fixtures, work, findingsDir) = (args[0], args[1], args[2]);
var (first, count) = (int.Parse(args[3]), int.Parse(args[4]));
Directory.CreateDirectory(work);
Directory.CreateDirectory(findingsDir);
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Table sets: each .dbf and every file next to it with the same stem (memo, indexes).
var sets = Directory.EnumerateFiles(fixtures, "*", SearchOption.AllDirectories)
    .Where(f => Path.GetExtension(f).Equals(".dbf", StringComparison.OrdinalIgnoreCase))
    .Select(dbf => Directory.EnumerateFiles(Path.GetDirectoryName(dbf)!)
        .Where(f => Path.GetFileNameWithoutExtension(f).Equals(Path.GetFileNameWithoutExtension(dbf), StringComparison.OrdinalIgnoreCase))
        .Where(f => new FileInfo(f).Length < 4_000_000)
        .ToList())
    .Where(s => s.Count > 0 && s.Sum(f => new FileInfo(f).Length) < 6_000_000)
    .OrderBy(s => s[0], StringComparer.Ordinal)
    .ToList();
Console.Error.WriteLine($"{sets.Count} table sets");

var findingsLog = Path.Combine(findingsDir, "findings.jsonl");
var progress = Path.Combine(findingsDir, "progress.txt");
var explained = new HashSet<Type> { typeof(DbfException), typeof(InvalidDataException), typeof(NotSupportedException) };
var stats = new Dictionary<string, int>();

for (var seed = first; seed < first + count; seed++)
{
    var random = new Random(seed);
    var set = sets[random.Next(sets.Count)];
    var dir = Path.Combine(work, $"c{seed % 64}");
    if (Directory.Exists(dir))
        foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f);
    Directory.CreateDirectory(dir);
    foreach (var f in set) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
    var victim = Path.Combine(dir, Path.GetFileName(set[random.Next(set.Count)]));
    var mutation = Mutate(victim, random);
    File.WriteAllText(progress, $"{seed}\t{set[0]}\t{Path.GetFileName(victim)}\t{mutation}");

    var allocatedBefore = GC.GetTotalAllocatedBytes(true);
    Exception? thrown = null;
    var thread = new Thread(() =>
    {
        try { Exercise(Path.Combine(dir, Path.GetFileName(set.First(f => Path.GetExtension(f).Equals(".dbf", StringComparison.OrdinalIgnoreCase)))), dir); }
        catch (Exception ex) { thrown = ex; }
    }, 64 * 1024 * 1024) { IsBackground = true };
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(10)))
    {
        Record("hang", null, $"no answer after 10 s");
        Environment.Exit(3); // the stuck thread can't be stopped
    }
    var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
    if (allocated > 512L * 1024 * 1024)
        Record("allocation", null, $"{allocated / (1024 * 1024)} MB allocated");
    if (thrown is not null)
    {
        var root = thrown is AggregateException { InnerException: { } inner } ? inner : thrown;
        var kind = explained.Contains(root.GetType()) ? "explained" : "unexplained";
        Bump(kind + ":" + root.GetType().Name);
        if (kind == "unexplained")
            Record("exception", root, root.Message);
    }
    else
    {
        Bump("clean");
    }

    void Record(string what, Exception? ex, string detail)
    {
        var frame = ex?.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Thunder.Dbf")) ?? "";
        var signature = $"{what}|{ex?.GetType().Name}|{frame}";
        var entry = new { seed, set = set[0].Replace(fixtures, ""), file = Path.GetFileName(victim), mutation, what, type = ex?.GetType().FullName, detail, frame, signature, stack = ex?.ToString() };
        File.AppendAllText(findingsLog, JsonSerializer.Serialize(entry) + "\n");
        var keep = Path.Combine(findingsDir, $"seed-{seed}");
        Directory.CreateDirectory(keep);
        foreach (var f in Directory.EnumerateFiles(dir)) File.Copy(f, Path.Combine(keep, Path.GetFileName(f)), overwrite: true);
        Bump("FINDING:" + what);
    }
}
File.WriteAllText(Path.Combine(findingsDir, $"stats-{first}.json"), JsonSerializer.Serialize(stats));
return 0;

void Bump(string key) => stats[key] = stats.GetValueOrDefault(key) + 1;

// Everything a program pointed at an old folder would do: open, check, read every record and memo,
// walk every index tag and seek in it, then write through a writable open.
static void Exercise(string dbf, string dir)
{
    try
    {
        using var db = DbfDatabase.Open(dir);
        db.Check();
    }
    catch (Exception ex) when (ex is DbfException) { }

    using (var table = DbfTable.Open(dbf, ignoreMissingMemo: true))
    {
        var n = 0;
        foreach (var record in table.ReadAll())
        {
            foreach (var value in record.Values)
                _ = value?.ToString();
            if (++n > 200_000) break;
        }
        try
        {
            using var cdx = table.OpenStructuralIndex();
            if (cdx is not null)
                foreach (IDbfIndexTag tag in ((IDbfIndex)cdx).Tags)
                {
                    var keys = tag.Entries().Take(50_000).Select(e => e.Key).ToList();
                    foreach (var key in keys.Take(5))
                        _ = table.Seek(tag, key);
                }
        }
        catch (Exception ex) when (ex is DbfException or InvalidDataException or NotSupportedException) { }

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try
            {
                IDbfIndex? index = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".cdx" or ".idx" => CdxIndex.Open(file, table: table),
                    ".mdx" => MdxIndex.Open(file),
                    ".ntx" => NtxIndex.Open(file, table: table),
                    ".ndx" => NdxIndex.Open(file, table: table),
                    _ => null,
                };
                using (index)
                    if (index is not null)
                        foreach (var tag in index.Tags)
                            _ = tag.Entries().Take(50_000).Count();
            }
            catch (Exception ex) when (ex is DbfException or InvalidDataException or NotSupportedException) { }
        }
    }

    // Writing: an append and an update through a writable open, which must refuse or succeed.
    try
    {
        using var writable = DbfTable.Open(dbf, writable: true, maintainFoxProIndexes: true, ignoreMissingMemo: true);
        if (writable.RecordCount > 0)
        {
            var values = writable.Read(0).Values.ToArray();
            writable.Append(values);
            writable.Update(0, values);
        }
    }
    // A value that doesn't fit its field is the caller's mistake, by design an ArgumentException or OverflowException.
    catch (Exception ex) when (ex is DbfException or ArgumentException or OverflowException) { }
}

// One to six corruptions of one file, biased to its header, where the lengths and pointers live.
static string Mutate(string path, Random random)
{
    var bytes = File.ReadAllBytes(path).ToList();
    var notes = new List<string>();
    var steps = random.Next(1, 7);
    int[] interesting = [0, 1, -1, 0x7F, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, int.MaxValue, int.MinValue, 0x7FFFFFFE, 0x200, 0x400, bytes.Count, bytes.Count + 1, bytes.Count - 1];
    for (var i = 0; i < steps && bytes.Count > 0; i++)
    {
        var at = random.Next(3) == 0 ? random.Next(bytes.Count) : random.Next(Math.Min(bytes.Count, 1100));
        switch (random.Next(7))
        {
            case 0:
                bytes[at] ^= (byte)(1 << random.Next(8));
                notes.Add($"flip@{at}");
                break;
            case 1:
                bytes[at] = (byte)interesting[random.Next(6)];
                notes.Add($"byte@{at}");
                break;
            case 2 when at + 4 <= bytes.Count:
            {
                var v = interesting[random.Next(interesting.Length)];
                var b = random.Next(2) == 0 ? BitConverter.GetBytes(v) : BitConverter.GetBytes(v).Reverse().ToArray();
                for (var j = 0; j < 4; j++) bytes[at + j] = b[j];
                notes.Add($"int32@{at}={v}");
                break;
            }
            case 3 when at + 2 <= bytes.Count:
            {
                var v = (ushort)interesting[random.Next(interesting.Length)];
                var b = BitConverter.GetBytes(v);
                bytes[at] = b[0]; bytes[at + 1] = b[1];
                notes.Add($"int16@{at}={v}");
                break;
            }
            case 4:
                bytes.RemoveRange(at, bytes.Count - at);
                notes.Add($"truncate@{at}");
                break;
            case 5:
            {
                var len = Math.Min(random.Next(1, 600), bytes.Count - at);
                for (var j = 0; j < len; j++) bytes[at + j] = 0;
                notes.Add($"zero@{at}+{len}");
                break;
            }
            default:
            {
                var len = Math.Min(random.Next(1, 600), bytes.Count - at);
                var src = random.Next(bytes.Count - len + 1);
                var chunk = bytes.GetRange(src, len);
                for (var j = 0; j < len; j++) bytes[at + j] = chunk[j];
                notes.Add($"copy{src}->{at}+{len}");
                break;
            }
        }
    }
    File.WriteAllBytes(path, bytes.ToArray());
    return string.Join(" ", notes);
}
