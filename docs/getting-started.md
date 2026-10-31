# Getting started

```
   ______
  | .dbf |  --->  your code      ProjectThunder :: Thunder.Dbf
  |______|                       Five minutes from a legacy folder to your first record.
```

## 1. Get it

Thunder.Dbf targets .NET 10 and runs on Windows, Linux and macOS with no native drivers. It's one command from nuget.org:

```bash
dotnet add package Thunder.Dbf
```

Or reference the projects from a clone:

```bash
git clone https://github.com/pthunder/Thunder.Dbf.git
```

```bash
dotnet add reference Thunder.Dbf/src/Thunder.Dbf/Thunder.Dbf.csproj
```

## 2. Connect

Point it at the folder the legacy app keeps its data in. It opens **read-only** unless you ask otherwise, so trying it on live data is safe.

```csharp
using Thunder.Dbf;

using var db = DbfDatabase.Open(@"\\server\goldmine\data");
Console.Write(db.Check());   // what's readable, writable, locked or damaged, in milliseconds
```

## 3. Read

```csharp
foreach (var record in db["CONTACT1"].ReadAll())
{
    if (record.IsDeleted)
        continue;
    Console.WriteLine($"{record["COMPANY"]}, {record["CITY"]}");
}
```

Field names are matched in any case. Values come back as .NET types: `string`, `decimal`, `DateOnly`, `bool`, and so on (see [Reading and writing](reading-and-writing.md)).

## 4. Find a record through its index

```csharp
var contacts = db["CONTACT1"];
using var index = contacts.OpenProductionIndex()!;
var acme = contacts.Seek(index["CONTCOMP"], "ACME");   // like dBASE's SEEK
```

## 5. Write, safely

```csharp
using var db = DbfDatabase.Open(folder, new DbfDatabaseOptions { ReadOnly = false });
var table = db["CONTACT1"];

var values = table.Read(0).Values.ToArray();
values[table.Fields.ToList().FindIndex(f => f.Name == "PHONE1")] = "(555) 010-2000";
table.Update(0, values);
```

That one call:

- takes the record lock the legacy app would take
- writes only the field that changed (other fields keep their exact bytes)
- updates every tag of the production `.mdx` index
- flushes the memo, record, index and header in a safe order

## 6. When something goes wrong

Every failure is a `DbfException` with a `Code`, a safe `Message`, a `Hint` saying what to do, and an `HttpStatus` for web layers:

```csharp
try { db["CONTACT9"].ReadAll().ToList(); }
catch (DbfException ex) { Console.WriteLine($"{ex.Code}: {ex.Message}"); }
// TableNotFound: There's no table called 'CONTACT9'. Check the spelling; TableNames lists the tables in the folder.
```

## Next

- [Use cases](use-cases.md) for where this goes from here.
- The [samples](../samples), which you can run straight from the repository.
- The [command line tool](cli.md), for checking a folder or importing a CSV without writing code.
