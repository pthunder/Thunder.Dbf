//      _______________
//     | SELECT * FROM |      ProjectThunder :: Thunder.Dbf sample
//     |   CHANGELOG   |      Plain ADO.NET over dBASE files: the same DbConnection,
//     |_______________|      DbCommand and DbDataReader every .NET tool already speaks.
//
//   dotnet run --project samples/AdoNet
//
// No Jet, no VFP OLE DB, no 32-bit process: a drop-in for reports, Dapper and DataTable.Load.

using System.Data;
using System.Data.Common;
using Thunder.Dbf.Data;

// Work on a copy of a real dBASE IV table with a production .mdx index.
var work = Directory.CreateTempSubdirectory("thunder-ado-sample-");
foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT", "CHANGELOG.MDX" })
    File.Copy(Path.Combine(RepoRoot(), "tests", "fixtures", "siemens-step7", "siprom", file), Path.Combine(work.FullName, file));

// Registering the factory lets tools that work by provider name find it.
DbProviderFactories.RegisterFactory(DbfProviderFactory.InvariantName, DbfProviderFactory.Instance);

using (var connection = new DbfConnection($"Data Source={work.FullName}"))   // read-only by default
{
    connection.Open();

    // Parameters are always data, never SQL: values can't change the statement.
    using var query = connection.CreateCommand();
    query.CommandText = "SELECT ID, USER, ACTION FROM CHANGELOG WHERE ACTION = @action ORDER BY ID";
    query.Parameters.Add(new DbfParameter("@action", 2));
    Console.WriteLine($"Plan: {query.Explain()}");                            // uses the ACTION_TAG index

    using (var reader = query.ExecuteReader())
        while (reader.Read())
            Console.WriteLine($"  #{reader.GetDecimal(0)} {reader.GetString(1)} (action {reader.GetDecimal(2)})");

    // DataTable.Load and anything else that consumes a DbDataReader just works.
    var table = new DataTable();
    using (var all = connection.CreateCommand())
    {
        all.CommandText = "SELECT * FROM CHANGELOG";
        table.Load(all.ExecuteReader());
    }
    Console.WriteLine($"DataTable: {table.Rows.Count} rows, {table.Columns.Count} columns");
}

// Writes need a writable connection, and keep the .mdx index and dBASE locks in step.
using (var writable = new DbfConnection($"Data Source={work.FullName};Read Only=false"))
{
    writable.Open();
    using var update = writable.CreateCommand();
    update.CommandText = "UPDATE CHANGELOG SET USER = @user WHERE ID = @id";
    update.Parameters.Add(new DbfParameter("@user", "renamed"));
    update.Parameters.Add(new DbfParameter("@id", 3));
    Console.WriteLine($"Updated {update.ExecuteNonQuery()} record(s)");

    using var check = writable.CreateCommand();
    check.CommandText = "SELECT _RECNO FROM CHANGELOG WHERE USER = 'renamed'";
    Console.WriteLine($"Found it again through the index at record {check.ExecuteScalar()}");
}

work.Delete(recursive: true);

static string RepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Thunder.Dbf.slnx")))
            return dir.FullName;
    throw new DirectoryNotFoundException("Run this sample from inside the Thunder.Dbf repository.");
}
