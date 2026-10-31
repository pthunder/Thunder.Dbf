//        .-----.
//       /     /      ProjectThunder :: Thunder.Dbf sample
//      /     /__     Read a legacy data folder: test the connection, list the tables,
//     /__      /     read records, and look one up through its dBASE IV index.
//       /    /
//      /   /           dotnet run --project samples/ReadLegacyData -- [folder]
//     /  /
//    //                With no folder, it reads the Siemens STEP 7 test data in this repo.
//   '

using Thunder.Dbf;

var folder = args.Length > 0 ? args[0] : Path.Combine(RepoRoot(), "tests", "fixtures", "siemens-step7", "siprom");

// 1. Connect. Read-only by default, so pointing this at live data can't change it.
using var db = DbfDatabase.Open(folder);

// 2. The handshake: reads only headers, reports anything that would get in the way.
Console.WriteLine(db.Check());

// 3. Read every record of every table.
foreach (var name in db.TableNames)
{
    var table = db[name];
    Console.WriteLine($"{name}: {table.RecordCount} records, fields {string.Join(", ", table.Fields.Select(f => f.Name))}");
    foreach (var record in table.ReadAll().Where(r => !r.IsDeleted).Take(3))
        Console.WriteLine("  " + string.Join(" | ", record.Values.Take(6).Select(Show)));
}

// Short and printable: some legacy "text" fields hold binary blobs.
static string Show(object? value)
{
    var text = new string((value?.ToString() ?? "(blank)").Trim().Where(c => !char.IsControl(c)).ToArray());
    return text.Length > 24 ? text[..21] + "..." : text;
}

// 4. Find a record through the production index, like dBASE's SEEK.
var changelog = db.TableNames.FirstOrDefault(n => n.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase));
if (changelog is not null)
{
    using var index = db[changelog].OpenProductionIndex()!;
    Console.WriteLine($"\nIndex tags: {string.Join(", ", index.Tags.Select(t => $"{t.Name} = {t.Expression}"))}");

    var found = db[changelog].Seek(index["ID_TAG"], 2m);
    Console.WriteLine($"SEEK 2 on ID_TAG found record {found?.RecordNumber}: {found?["USER"]} at {found?["DATETIME"]}");

    Console.WriteLine("In USER order:");
    foreach (var record in db[changelog].ReadInOrder(index["USER_TAG"]))
        Console.WriteLine($"  {record["USER"]}");
}

// Failures explain themselves: a code for programs, a message and hint for people.
try
{
    _ = db["../../somewhere-else"];
}
catch (DbfException ex)
{
    Console.WriteLine($"\nRefused, as it should be: [{ex.Code}] {ex.Message}");
}

static string RepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Thunder.Dbf.slnx")))
            return dir.FullName;
    throw new DirectoryNotFoundException("Run this sample from inside the Thunder.Dbf repository, or pass a folder.");
}
