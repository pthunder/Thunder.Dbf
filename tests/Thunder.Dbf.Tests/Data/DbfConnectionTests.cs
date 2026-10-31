//       ___
//      [___]---.       ProjectThunder :: Thunder.Dbf tests
//       | |    |       Plug in, look around, unplug: connections,
//       |_|    '--o    schema discovery and the provider factory.

using System.Data;
using System.Data.Common;
using Thunder.Dbf.Data;

namespace Thunder.Dbf.Tests.Data;

public sealed class DbfConnectionTests : IDisposable
{
    private readonly DataFolder _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Open_and_close_change_state_and_raise_events()
    {
        using var connection = new DbfConnection(_data.ReadOnlyConnectionString);
        var changes = new List<(ConnectionState From, ConnectionState To)>();
        connection.StateChange += (_, e) => changes.Add((e.OriginalState, e.CurrentState));

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(_data.Path, connection.DataSource);

        connection.Open();
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(System.IO.Path.GetFileName(_data.Path), connection.Database);
        Assert.Throws<InvalidOperationException>(connection.Open);

        connection.Close();
        connection.Close(); // closing twice is harmless
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal([(ConnectionState.Closed, ConnectionState.Open), (ConnectionState.Open, ConnectionState.Closed)], changes);
    }

    [Fact]
    public void Connections_are_read_only_by_default()
    {
        using var connection = _data.Open();
        Assert.True(connection.ReadOnly);

        var error = Assert.Throws<DbfException>(() =>
            connection.Command("UPDATE people SET NAME = @name WHERE NAME = 'Alice'", ("name", "Mallory")).ExecuteNonQuery());
        Assert.Equal(DbfErrorCode.ReadOnlyConnection, error.Code);
        Assert.Contains("Read Only=false", error.Hint);
        Assert.Equal(["Alice", "Bob"], connection.Column("SELECT NAME FROM people"));
    }

    [Fact]
    public void Read_only_false_allows_writes()
    {
        using var connection = _data.Open(writable: true);
        Assert.False(connection.ReadOnly);
        Assert.Equal(1, connection.Command("UPDATE people SET NAME = @name WHERE NAME = 'Alice'", ("name", "Alicia")).ExecuteNonQuery());
    }

    [Fact]
    public void A_plain_folder_path_works_as_a_connection_string()
    {
        using var connection = new DbfConnection(_data.Path);
        connection.Open();
        Assert.True(connection.ReadOnly);
        Assert.Equal(2, connection.Column("SELECT NAME FROM people").Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Data Source=/x;Colour=blue")]
    [InlineData("Data Source=/x;Read Only=maybe")]
    public void Bad_connection_strings_are_explained(string connectionString)
    {
        using var connection = new DbfConnection(connectionString);
        var error = Assert.Throws<DbfException>(connection.Open);
        Assert.Equal(DbfErrorCode.InvalidConnectionString, error.Code);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void Commands_need_an_open_connection()
    {
        using var connection = new DbfConnection(_data.ReadOnlyConnectionString);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM people";
        Assert.Throws<InvalidOperationException>(() => command.ExecuteReader());
        Assert.Throws<InvalidOperationException>(() => new DbfCommand("SELECT * FROM people").ExecuteReader());
    }

    [Fact]
    public void Transactions_are_refused_plainly()
    {
        using var connection = _data.Open();
        Assert.Throws<NotSupportedException>(() => connection.BeginTransaction());
    }

    [Fact]
    public void GetSchema_lists_tables()
    {
        using var connection = _data.Open();

        var tables = connection.GetSchema("Tables");
        var names = tables.Rows.Cast<DataRow>().Select(r => (string)r["TABLE_NAME"]).ToList();
        Assert.Equal(["BAUSTEIN", "calls", "CHANGELOG", "client", "FolderRoot", "people", "products", "SYMLIST"], names);
        Assert.All(tables.Rows.Cast<DataRow>(), r => Assert.Equal("TABLE", r["TABLE_TYPE"]));

        var one = connection.GetSchema("Tables", [null, null, "PEOPLE", null]);
        Assert.Equal("people", Assert.Single(one.Rows.Cast<DataRow>())["TABLE_NAME"]);

        var collections = connection.GetSchema();
        Assert.Contains("Columns", collections.Rows.Cast<DataRow>().Select(r => (string)r[DbMetaDataColumnNames.CollectionName]));
    }

    [Fact]
    public void GetSchema_lists_columns_with_types()
    {
        using var connection = _data.Open();

        var columns = connection.GetSchema("Columns", [null, null, "products"]).Rows.Cast<DataRow>().ToList();
        Assert.Equal(10, columns.Count); // _NullFlags is bookkeeping and stays hidden
        Assert.DoesNotContain(columns, c => (string)c["COLUMN_NAME"] == "_NullFlags");

        var price = columns.Single(c => (string)c["COLUMN_NAME"] == "UNITPRICE");
        Assert.Equal("Currency", price["DATA_TYPE"]);
        Assert.Equal("Y", price["DBF_TYPE"]);
        Assert.Equal(typeof(decimal).FullName, price["CLR_TYPE"]);
        Assert.Equal(6, price["ORDINAL_POSITION"]);

        var id = Assert.Single(connection.GetSchema("Columns", [null, null, "CHANGELOG", "id"]).Rows.Cast<DataRow>());
        Assert.Equal("Numeric", id["DATA_TYPE"]);
        Assert.Equal(10, id["NUMERIC_PRECISION"]);
        Assert.Equal(0, id["NUMERIC_SCALE"]);
        Assert.Equal("YES", id["IS_NULLABLE"]);

        Assert.Throws<ArgumentException>(() => connection.GetSchema("Procedures"));
    }

    [Fact]
    public void The_provider_factory_registers_and_creates_everything()
    {
        DbProviderFactories.RegisterFactory(DbfProviderFactory.InvariantName, DbfProviderFactory.Instance);
        var factory = DbProviderFactories.GetFactory("Thunder.Dbf");
        Assert.Same(DbfProviderFactory.Instance, factory);

        using var connection = factory.CreateConnection()!;
        connection.ConnectionString = _data.ReadOnlyConnectionString;
        connection.Open();
        Assert.Same(factory, DbProviderFactories.GetFactory(connection));

        using var command = factory.CreateCommand()!;
        command.Connection = connection;
        command.CommandText = "SELECT NAME FROM people WHERE BIRTHDATE > @after";
        var parameter = factory.CreateParameter()!;
        parameter.ParameterName = "@after";
        parameter.Value = new DateTime(1985, 1, 1);
        command.Parameters.Add(parameter);

        Assert.Equal("Alice", command.ExecuteScalar());
    }
}
