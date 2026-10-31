//      _______
//     |  tmp  |___      ProjectThunder :: Thunder.Dbf tests
//     |  .dbf .mdx |    Every ADO.NET test gets its own copy of the fixtures,
//     |____________|    so writes can't leak between tests or into the repo.

using Thunder.Dbf.Data;

namespace Thunder.Dbf.Tests.Data;

/// <summary>A temp folder holding copies of the fixture tables the ADO.NET tests use.</summary>
public sealed class DataFolder : IDisposable
{
    private static readonly string FixtureRoot = FindFixtureRoot();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("thunder-ado-");

    public DataFolder()
    {
        Copy("dbfdatareader/client.dbf", "client.dbf");                       // dBASE IV with memos, 8 rows
        Copy("dbfdatareader/client.dbt", "client.dbt");
        Copy("siemens-step7/siprom/CHANGELOG.DBF", "CHANGELOG.DBF");          // MDX: ID_TAG, USER_TAG, ...
        Copy("siemens-step7/siprom/CHANGELOG.DBT", "CHANGELOG.DBT");
        Copy("siemens-step7/siprom/CHANGELOG.MDX", "CHANGELOG.MDX");
        Copy("siemens-step7/ombstx/offline/00000001/BAUSTEIN.DBF", "BAUSTEIN.DBF"); // MDX: ID_TAG, TYP + NUMMER
        Copy("siemens-step7/ombstx/offline/00000001/BAUSTEIN.DBT", "BAUSTEIN.DBT");
        Copy("siemens-step7/ombstx/offline/00000001/BAUSTEIN.MDX", "BAUSTEIN.MDX");
        Copy("siemens-step7/YDBs/15940/SYMLIST.DBF", "SYMLIST.DBF");          // MDX: _SKZ, UPPER(LTRIM(TRIM(_SKZ))), ...
        Copy("siemens-step7/YDBs/15940/SYMLIST.MDX", "SYMLIST.MDX");
        Copy("infused-dbf/dbase_31.dbf", "products.dbf");                     // Visual FoxPro: I, Y, L, 77 rows
        Copy("infused-dbf/foxprodb/calls.dbf", "calls.dbf");                  // Visual FoxPro: T and an .fpt memo
        Copy("infused-dbf/foxprodb/calls.FPT", "calls.FPT");
        Copy("dbfdatareader/FolderRoot.dbf", "FolderRoot.dbf");               // Visual FoxPro: B (double)
        Copy("dbfread/people.dbf", "people.dbf");                             // dBASE III: C and D
    }

    public string Path => _folder.FullName;

    public string ReadOnlyConnectionString => $"Data Source={Path}";

    // products.dbf and client.dbf have their header's index flag set but no index file beside them,
    // which the library treats as an index it can't maintain. Allowing that keeps them writable here.
    public string WritableConnectionString => $"Data Source={Path};Read Only=false;Allow Unmaintained Indexes=true";

    public DbfConnection Open(bool writable = false)
    {
        var connection = new DbfConnection(writable ? WritableConnectionString : ReadOnlyConnectionString);
        connection.Open();
        return connection;
    }

    public string PathOf(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => _folder.Delete(recursive: true);

    private void Copy(string fixture, string name) =>
        File.Copy(System.IO.Path.Combine(FixtureRoot, fixture), System.IO.Path.Combine(Path, name));

    private static string FindFixtureRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find tests/fixtures above the test output folder.");
    }
}

/// <summary>Small helpers for running SQL in tests.</summary>
internal static class Sql
{
    public static DbfCommand Command(this DbfConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    /// <summary>Every value of the first column, in order.</summary>
    public static List<object> Column(this DbfConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var values = new List<object>();
        while (reader.Read())
            values.Add(reader.GetValue(0));
        return values;
    }
}
