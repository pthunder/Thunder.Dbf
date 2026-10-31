//     _____________          _____________
//    | a,b,c       |        | A | B  | C   |     ProjectThunder :: Thunder.Dbf sample
//    | 1,x,1997-04 |  --->  |---|----|-----|     A table's shape from a CSV, no Excel round
//    |_____________|        |___|____|_____|     trip: plan it, create it, load it, export it.
//
//   dotnet run --project samples/CsvToDbf -- [file.csv]

using Thunder.Dbf;

var work = Directory.CreateTempSubdirectory("thunder-csv-sample-");
var csv = args.Length > 0 ? args[0] : Path.Combine(work.FullName, "customers.csv");
if (args.Length == 0)
{
    // A small CSV with the usual quirks: a comma inside quotes, a ZIP code with a leading zero,
    // dates, yes/no values, and a note long enough to need a memo field.
    File.WriteAllText(csv, $"""
        Customer Name,City,Balance,Zip,Customer Since,Active,Notes
        "Smith, Jane",Boston,1234.50,02134,1997-04-12,Y,Prefers email.
        Jones Hardware,Atlanta,-20,30301,2001-01-02,N,{new string('x', 300)}
        Ana Pérez,Miami,0,33101,2015-06-30,Y,
        """);
}

// 1. Look before you leap: the plan shows each column's type, width and why.
Console.WriteLine(DbfImport.Plan(csv));

// 2. Create the table and load the rows. ShapeOnly = true would create just the empty table.
var dbf = Path.Combine(work.FullName, "CUSTOMER.DBF");
var plan = DbfImport.FromCsv(csv, dbf);
Console.WriteLine($"Created {Path.GetFileName(dbf)} with {plan.RowCount} rows.");

using (var table = DbfTable.Open(dbf))
{
    foreach (var record in table.ReadAll())
        Console.WriteLine($"  {record["CUSTOMER_N"],-16} {record["ZIP"]}  {record["BALANCE"],9}  since {record["CUSTOMER_S"]}");

    // 3. And back out to CSV, safe to open in Excel (formula-looking text is neutralized).
    var exported = Path.Combine(work.FullName, "exported.csv");
    DbfExport.ToCsv(table, exported);
    Console.WriteLine($"\nExported back to CSV:\n{File.ReadAllText(exported)[..200]}...");
}

work.Delete(recursive: true);
