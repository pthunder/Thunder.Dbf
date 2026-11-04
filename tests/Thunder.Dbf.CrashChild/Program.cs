//      parent test                     this process
//   +--------------+   starts     +---------------------+
//   | waits for    | -----------> | changes a table's   |     ProjectThunder :: Thunder.Dbf tests
//   | the marker   |              | structure, and at   |     A process the crash tests kill for real,
//   |              | <- marker -- | the step named,     |     partway through a change of structure or
//   | then kills   |              | writes the marker   |     through finishing one: no catch or finally
//   | it           | -- kill ---> | and waits forever   |     runs, and the operating system lets go of
//   +--------------+              +---------------------+     its files, as after a real crash.
//
// Usage: Thunder.Dbf.CrashChild <widen|narrow|add-memo|recover> <table path> <step> <marker path>

using Thunder.Dbf;

var (mode, path, step, marker) = (args[0], args[1], args[2], args[3]);
DbfTable.RestructurePoint = point =>
{
    if (point != step)
        return;
    File.WriteAllText(marker, point);
    Thread.Sleep(Timeout.Infinite);
};

using var table = DbfTable.Open(path, writable: true, exclusive: true, maintainFoxProIndexes: true);
if (mode != "recover")
{
    var fields = table.FieldDefinitions.ToList();
    switch (mode)
    {
        case "widen":
            fields[0] = DbfFieldDefinition.Character(fields[0].Name, 20);
            fields.Add(DbfFieldDefinition.Character("CITY", 20));
            break;
        case "narrow":
            fields[0] = DbfFieldDefinition.Character(fields[0].Name, 4);
            break;
        case "add-memo":
            fields.Add(DbfFieldDefinition.Memo("NOTE"));
            break;
    }
    using var changed = table.ModifyStructure(fields, allowLoss: true);
}
File.WriteAllText(marker, "finished");
