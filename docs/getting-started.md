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
- flushes the memo, record and index in a crash-safe order

## 6. When something goes wrong

Every failure in the files, the folder or the data is a `DbfException` with a `Code`, a safe `Message`, a `Hint` saying what to do, and an `HttpStatus` for web layers; a bad argument from your own code is an ordinary `ArgumentException`:

```csharp
try { db["CONTACT9"].ReadAll().ToList(); }
catch (DbfException ex) { Console.WriteLine($"{ex.Code}: {ex.Message}"); }
// TableNotFound: There's no table called 'CONTACT9'. Check the spelling; TableNames lists the tables in the folder.
```

## Mobile and AOT

The core package is safe to trim and to compile ahead of time, as an iOS app must be and an Android app can be: it's marked `IsAotCompatible`, so the trim and AOT analyzers check its build, which fails on the warnings that mark an unsafe change (the list is in `Thunder.Dbf.csproj`; the rest stay warnings). CI also publishes [samples/NativeAot](../samples/NativeAot) with `PublishAot=true` and every warning as an error, on Linux and macOS, and runs it (create, append, index, seek, read, typed records and ADO.NET, in code page 866). It hasn't yet been run on an iOS or Android device.

- **Everything works but one API.** Records, memos, indexes, locks, ADO.NET and every code page work as they do anywhere else. Typed records (`Read<T>`, `ReadAll<T>`, `AppendRecord`, `UpdateRecord`) keep your class's public properties for you. One corner: a blank field read into a property whose type is your own struct with a parameterless constructor runs that constructor, but trimming keeps it only if something asks for it, such as `[DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(MyStruct))]`; otherwise the value is zeroed storage. `AsQueryable<T>()` doesn't: LINQ's queryable operators need reflection and run-time code, so it's marked `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` and your build warns. Use `ReadAll<T>()` with ordinary LINQ, or `Seek` through an index tag, instead.
- **Small:** one managed DLL of about 500 KB, with no native libraries to ship per platform (SQLite brings its own `e_sqlite3`), and trimming drops what your app doesn't call. The whole sample, .NET runtime included, is a 5.7 MB native binary on macOS (Apple silicon).
- **Keep tables in the app's own folder** (`FileSystem.AppDataDirectory` in .NET MAUI). Like an SQLite database there, they're deleted when the app is uninstalled, so give users an export or a backup if their data must outlive the app.
- **Tables shipped inside the app package** aren't ordinary files on Android. Copy them to the app's folder on first run, or read them with `DbfTable.Open(stream)`.

## Next

- [Use cases](use-cases.md) for where this goes from here.
- The [samples](../samples), which you can run straight from the repository.
- The [command line tool](cli.md), for checking a folder or importing a CSV without writing code.
