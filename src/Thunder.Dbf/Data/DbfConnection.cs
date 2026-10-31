//          ___
//     ____/ | \____         ProjectThunder :: Thunder.Dbf
//    |  =[  o  ]=  |        A folder of tables behind the plain ADO.NET plug,
//    |____\_|_/____|        so Dapper, reports and DataTable.Load just work.
//          |||
//          |||              using var connection = new DbfConnection(@"Data Source=C:\Data");
//      ~~~~~~~~~~~          connection.Open();   // read-only unless Read Only=false

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Thunder.Dbf.Data;

/// <summary>
/// An ADO.NET connection to a folder of DBF tables. The connection string is anything
/// <see cref="DbfDatabase.Open"/> accepts: a folder path, or <c>Data Source=...;Read Only=...;Code Page=...</c>.
/// Read-only unless the connection string says <c>Read Only=false</c>. Not thread-safe, like other ADO.NET connections.
/// </summary>
public sealed class DbfConnection : DbConnection
{
    private string _connectionString = "";
    private DbfDatabase? _database;

    public DbfConnection()
    {
    }

    public DbfConnection(string connectionString) => _connectionString = connectionString ?? "";

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_database is not null)
                throw new InvalidOperationException("Close the connection before changing its connection string.");
            _connectionString = value ?? "";
        }
    }

    /// <summary>The folder's name. A folder of tables is one database, so there is nothing to change to.</summary>
    public override string Database => _database is null ? "" : Path.GetFileName(_database.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>The data folder: the full path once open, the Data Source setting before.</summary>
    public override string DataSource => _database?.Folder ?? DataSourceSetting();

    public override string ServerVersion => typeof(DbfDatabase).Assembly.GetName().Version?.ToString() ?? "0.0";

    public override ConnectionState State => _database is null ? ConnectionState.Closed : ConnectionState.Open;

    /// <summary>True when writes are refused, which is the default.</summary>
    public bool ReadOnly => RequireOpen().ReadOnly;

    /// <summary>The open folder, for anything ADO.NET can't express (index tags, the Check() handshake).</summary>
    public DbfDatabase DbfDatabase => RequireOpen();

    protected override DbProviderFactory DbProviderFactory => DbfProviderFactory.Instance;

    public override void Open()
    {
        if (_database is not null)
            throw new InvalidOperationException("The connection is already open.");
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new DbfException(DbfErrorCode.InvalidConnectionString, "The connection has no connection string.",
                @"Set ConnectionString to the data folder, like Data Source=C:\Data, before opening.");

        _database = DbfDatabase.Open(_connectionString);
        OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
    }

    public override void Close()
    {
        if (_database is null)
            return;
        _database.Dispose();
        _database = null;
        OnStateChange(new StateChangeEventArgs(ConnectionState.Open, ConnectionState.Closed));
    }

    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException("A DbfConnection is one folder. Open another connection for another folder.");

    /// <summary>Not supported: dBASE files have no transactions, so each command's changes are written as it runs.</summary>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("DBF tables have no transactions; each INSERT, UPDATE or DELETE is written as it runs.");

    /// <summary>Creates a command on this connection.</summary>
    public new DbfCommand CreateCommand() => new() { Connection = this };

    protected override DbCommand CreateDbCommand() => CreateCommand();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }

    // ---- schema ---------------------------------------------------------------------------

    /// <summary>The collections <see cref="GetSchema(string, string?[])"/> knows.</summary>
    public override DataTable GetSchema() => GetSchema(DbMetaDataCollectionNames.MetaDataCollections);

    public override DataTable GetSchema(string collectionName) => GetSchema(collectionName, []);

    /// <summary>
    /// <c>MetaDataCollections</c>, <c>Tables</c> (restrictions: catalog, schema, table, type) or
    /// <c>Columns</c> (restrictions: catalog, schema, table, column). Catalog and schema are always null.
    /// </summary>
    public override DataTable GetSchema(string collectionName, string?[] restrictionValues)
    {
        var database = RequireOpen();
        restrictionValues ??= [];
        string? Restriction(int i) => i < restrictionValues.Length ? restrictionValues[i] : null;

        switch (collectionName?.ToUpperInvariant())
        {
            case "METADATACOLLECTIONS":
            {
                var table = new DataTable(DbMetaDataCollectionNames.MetaDataCollections) { Locale = CultureInfo.InvariantCulture };
                table.Columns.Add(DbMetaDataColumnNames.CollectionName, typeof(string));
                table.Columns.Add(DbMetaDataColumnNames.NumberOfRestrictions, typeof(int));
                table.Columns.Add(DbMetaDataColumnNames.NumberOfIdentifierParts, typeof(int));
                table.Rows.Add(DbMetaDataCollectionNames.MetaDataCollections, 0, 0);
                table.Rows.Add("Tables", 4, 1);
                table.Rows.Add("Columns", 4, 2);
                return table;
            }

            case "TABLES":
            {
                var table = new DataTable("Tables") { Locale = CultureInfo.InvariantCulture };
                foreach (var name in new[] { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE" })
                    table.Columns.Add(name, typeof(string));
                if (Restriction(3) is { } type && !type.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
                    return table;
                foreach (var name in TablesMatching(database, Restriction(2)))
                    table.Rows.Add(null, null, name, "TABLE");
                return table;
            }

            case "COLUMNS":
            {
                var table = new DataTable("Columns") { Locale = CultureInfo.InvariantCulture };
                table.Columns.Add("TABLE_CATALOG", typeof(string));
                table.Columns.Add("TABLE_SCHEMA", typeof(string));
                table.Columns.Add("TABLE_NAME", typeof(string));
                table.Columns.Add("COLUMN_NAME", typeof(string));
                table.Columns.Add("ORDINAL_POSITION", typeof(int));
                table.Columns.Add("DATA_TYPE", typeof(string));
                table.Columns.Add("DBF_TYPE", typeof(string));
                table.Columns.Add("COLUMN_SIZE", typeof(int));
                table.Columns.Add("NUMERIC_PRECISION", typeof(int));
                table.Columns.Add("NUMERIC_SCALE", typeof(int));
                table.Columns.Add("IS_NULLABLE", typeof(string));
                table.Columns.Add("CLR_TYPE", typeof(string));

                foreach (var tableName in TablesMatching(database, Restriction(2)))
                {
                    using var dbf = database.OpenTable(tableName);
                    var position = 0;
                    foreach (var field in dbf.Fields.Where(f => !DbfValues.IsHidden(f)))
                    {
                        position++;
                        if (Restriction(3) is { } column && !column.Equals(field.Name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var numeric = field.Type is 'N' or 'F';
                        table.Rows.Add(null, null, tableName, field.Name, position, DbfValues.TypeName(field), field.Type.ToString(),
                            field.Length, numeric ? field.Length : DBNull.Value, numeric ? field.DecimalCount : DBNull.Value,
                            DbfValues.AllowsNull(field) ? "YES" : "NO", DbfValues.ClrType(field).FullName);
                    }
                }
                return table;
            }

            default:
                throw new ArgumentException($"The schema collection '{collectionName}' isn't available. Use MetaDataCollections, Tables or Columns.",
                    nameof(collectionName));
        }
    }

    private static IEnumerable<string> TablesMatching(DbfDatabase database, string? name) =>
        name is null ? database.TableNames : database.TableNames.Where(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ---- helpers --------------------------------------------------------------------------

    internal DbfDatabase RequireOpen() => _database ?? throw NotOpen();

    private static InvalidOperationException NotOpen() => new("The connection isn't open. Call Open() first.");

    private string DataSourceSetting()
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || !_connectionString.Contains('='))
            return _connectionString;
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = _connectionString };
            foreach (var key in new[] { "Data Source", "DataSource", "Folder", "Path" })
            {
                if (builder.TryGetValue(key, out var value))
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
        }
        catch (ArgumentException)
        {
            // Open() reports a malformed connection string properly.
        }
        return "";
    }
}
