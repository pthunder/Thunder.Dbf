//      ___________
//     |  C# + IL  |  -- dotnet publish -->  [ native binary, no JIT ]     ProjectThunder :: Thunder.Dbf sample
//     |___________|     PublishAot=true       what iOS runs, and what     The core API in a trimmed, ahead-of-time
//                                             Android can run             compiled app: create, append, index, seek, read.
//
//   dotnet publish samples/NativeAot -c Release -o artifacts/native-aot
//   artifacts/native-aot/NativeAot
//
// Exits non-zero if anything reads back differently, so CI can run it.

using Thunder.Dbf;
using Thunder.Dbf.Data;

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
    if (!ok)
        failures++;
}

// On a phone this would be the app's own data folder (FileSystem.AppDataDirectory in .NET MAUI).
var work = Directory.CreateTempSubdirectory("thunder-aot-sample-");
var path = Path.Combine(work.FullName, "PEOPLE.DBF");

// Language driver 0x65 is DOS Cyrillic (code page 866): the text goes through CodePagesEncodingProvider,
// whose tables a trimmed app must still have.
using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV,
    [
        DbfFieldDefinition.Numeric("ID", 6),
        DbfFieldDefinition.Character("NAME", 20),
        DbfFieldDefinition.Date("BORN"),
        DbfFieldDefinition.Logical("ACTIVE"),
        DbfFieldDefinition.Memo("NOTES"),
    ], languageDriver: 0x65))
{
    table.Append(1, "Ада Лавлейс", new DateOnly(1815, 12, 10), true, "Первая программа.");
    table.Append(2, "Grace Hopper", new DateOnly(1906, 12, 9), false, new string('x', 600));
    table.AppendRecord(new Person { Id = 3, Name = "Алан Тьюринг", Active = true }); // no birth date: a blank field
    table.CreateIndexTag("NAME", "NAME");
}

Console.WriteLine("Reading it back:");
using (var table = DbfTable.Open(path, writable: true, exclusive: true))
{
    Check(table.Encoding.CodePage == 866, $"code page {table.Encoding.CodePage}");
    Check(table.RecordCount == 3, $"{table.RecordCount} records");

    var first = table.Read(0);
    Check((string?)first["NAME"] == "Ада Лавлейс", $"Cyrillic text: {first["NAME"]}");
    Check(first["BORN"] is DateOnly { Year: 1815 }, $"date: {first["BORN"]}");
    Check(((string?)table.Read(1)["NOTES"])?.Length == 600, "a memo longer than one block");

    using (var index = table.OpenProductionIndex()!)
    {
        var found = table.Seek(index["NAME"], "Алан Тьюринг");
        Check(found?["ID"] is 3m, $"seek through the .mdx: record {found?.RecordNumber}");
        Check(string.Join(", ", table.ReadInOrder(index["NAME"]).Select(r => r["ID"])) == "2, 1, 3", "index order (code page 866's byte order)");
    }

    // Typed records are trim-safe: Person's public properties are kept for it.
    var people = table.ReadAll<Person>().ToList();
    Check(people.Select(p => p.Name).SequenceEqual(["Ада Лавлейс", "Grace Hopper", "Алан Тьюринг"]), "ReadAll<Person>()");
    Check(people[2].Born is null && people[2].Recno == 3, "a blank date reads as null");
    Check(table.Read<Counted>(2).Born == default, "and as the default into a non-nullable property");

    table.UpdateRecord(1, people[1] with { Active = true });
    Check(table.Read<Person>(1).Active, "UpdateRecord");
}

// ADO.NET, whose readers report each column's .NET type.
using (var connection = new DbfConnection($"Data Source={work.FullName}"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT ID, NAME FROM PEOPLE WHERE ACTIVE = @active ORDER BY ID";
    command.Parameters.Add(new DbfParameter("@active", true));
    using var reader = command.ExecuteReader();
    Check(reader.GetFieldType(1) == typeof(string), "DbDataReader.GetFieldType");
    var names = new List<string>();
    while (reader.Read())
        names.Add(reader.GetString(1));
    Check(names.Count == 3, $"SQL: {string.Join(", ", names)}");
}

work.Delete(recursive: true);
Console.WriteLine(failures == 0 ? "All good." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

internal sealed record Person
{
    [DbfField("ID")] public int Id { get; init; }
    public string Name { get; init; } = "";
    public DateOnly? Born { get; init; }
    public bool Active { get; init; }
    [DbfRecordNumber] public int Recno { get; init; }
}

internal sealed class Counted
{
    public DateTime Born { get; set; }
}
