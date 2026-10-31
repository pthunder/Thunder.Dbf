//      .---------.
//      | reader  |>>>  string  decimal  DateTime     ProjectThunder :: Thunder.Dbf tests
//      '---------'>>>  bool    int      double       The reader's types, its schema table,
//          |||                                      and the tools that lean on them:
//         DataTable.Load, Dapper-style mapping.

using System.Data;
using System.Data.Common;
using System.Reflection;
using Thunder.Dbf.Data;

namespace Thunder.Dbf.Tests.Data;

public sealed class DbfDataReaderTests : IDisposable
{
    private readonly DataFolder _data = new();
    private readonly DbfConnection _connection;

    public DbfDataReaderTests() => _connection = _data.Open();

    public void Dispose()
    {
        _connection.Dispose();
        _data.Dispose();
    }

    [Fact]
    public void Field_types_map_to_dotnet_types()
    {
        using var reader = _connection.Command("SELECT * FROM products WHERE PRODUCTID = 5").ExecuteReader();
        Assert.Equal(typeof(int), reader.GetFieldType(reader.GetOrdinal("PRODUCTID")));
        Assert.Equal(typeof(string), reader.GetFieldType(reader.GetOrdinal("PRODUCTNAM")));
        Assert.Equal(typeof(decimal), reader.GetFieldType(reader.GetOrdinal("UNITPRICE")));
        Assert.Equal(typeof(bool), reader.GetFieldType(reader.GetOrdinal("DISCONTINU")));
        Assert.Equal("Currency", reader.GetDataTypeName(reader.GetOrdinal("UNITPRICE")));

        Assert.True(reader.Read());
        Assert.Equal(5, reader.GetInt32(0));
        Assert.Equal(5L, reader.GetInt64(0));
        Assert.Equal("Chef Anton's Gumbo Mix", reader.GetString(1));
        Assert.Equal(21.35m, reader.GetDecimal(reader.GetOrdinal("UNITPRICE")));
        Assert.Equal(21.35, reader.GetDouble(reader.GetOrdinal("UNITPRICE")));
        Assert.True(reader.GetBoolean(reader.GetOrdinal("DISCONTINU")));
        Assert.Throws<InvalidCastException>(() => reader.GetDateTime(0));
        Assert.Throws<InvalidCastException>(() => reader.GetString(0));
    }

    [Fact]
    public void Numeric_date_double_datetime_and_memo_fields()
    {
        using (var reader = _connection.Command("SELECT ID, ACTION, DATETIME FROM CHANGELOG WHERE ID = 3").ExecuteReader())
        {
            Assert.Equal(typeof(decimal), reader.GetFieldType(0));
            Assert.True(reader.Read());
            Assert.Equal(3m, reader.GetDecimal(0));
            Assert.Equal(13, reader.GetInt32(1));
            Assert.Equal(13, reader.GetInt16(1));
        }

        using (var reader = _connection.Command("SELECT BIRTHDATE FROM people WHERE NAME = 'Bob'").ExecuteReader())
        {
            Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
            Assert.True(reader.Read());
            Assert.Equal(new DateTime(1980, 11, 12), reader.GetDateTime(0));
            Assert.Equal(new DateTime(1980, 11, 12), reader.GetValue(0));
            Assert.Equal(new DateOnly(1980, 11, 12), reader.GetFieldValue<DateOnly>(0));
        }

        using (var reader = _connection.Command("SELECT FOLD_RT_ID, TS FROM FolderRoot WHERE FOLD_RT_ID = 2").ExecuteReader())
        {
            Assert.Equal(typeof(double), reader.GetFieldType(0));
            Assert.Equal(typeof(byte[]), reader.GetFieldType(1));
            Assert.True(reader.Read());
            Assert.Equal(2.0, reader.GetDouble(0));
            Assert.Equal(8, reader.GetBytes(1, 0, null, 0, 0));
        }

        using (var reader = _connection.Command("SELECT CALL_DATE, NOTES, CALL_ID FROM calls LIMIT 1").ExecuteReader())
        {
            Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
            Assert.Equal(typeof(string), reader.GetFieldType(1));
            Assert.Equal("Memo", reader.GetDataTypeName(1));
            Assert.True(reader.Read());
            Assert.IsType<DateTime>(reader.GetValue(0));
            Assert.True(reader.IsDBNull(1) || reader.GetValue(1) is string);
        }

        using (var reader = _connection.Command("SELECT CLIEN_HIST FROM client WHERE CLIENT_ID = 'A00001'").ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.StartsWith("85-200", reader.GetString(0));
            var buffer = new char[6];
            Assert.Equal(6, reader.GetChars(0, 0, buffer, 0, 6));
            Assert.Equal("85-200", new string(buffer));
        }
    }

    [Fact]
    public void Every_value_matches_its_declared_field_type()
    {
        foreach (var table in new[] { "products", "calls", "client", "CHANGELOG", "people", "FolderRoot", "SYMLIST", "BAUSTEIN" })
        {
            using var reader = _connection.Command($"SELECT * FROM {table}").ExecuteReader();
            var rows = 0;
            while (reader.Read())
            {
                rows++;
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    if (value is not DBNull)
                        Assert.IsType(reader.GetFieldType(i), value);
                }
            }
            Assert.True(rows > 0, table);
        }
    }

    [Fact]
    public void Blank_values_read_as_DBNull()
    {
        using var writable = _data.Open(writable: true);
        writable.Command("INSERT INTO people (NAME) VALUES ('Nobody')").ExecuteNonQuery();

        using var reader = _connection.Command("SELECT NAME, BIRTHDATE FROM people WHERE NAME = 'Nobody'").ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(1));
        Assert.Same(DBNull.Value, reader[1]);
        Assert.Null(reader.GetFieldValue<DateTime?>(1));
        var error = Assert.Throws<InvalidCastException>(() => reader.GetDateTime(1));
        Assert.Contains("IsDBNull", error.Message);
    }

    [Fact]
    public void The_schema_table_describes_every_column()
    {
        using var reader = _connection.Command("SELECT PRODUCTID, PRODUCTNAM AS NAME, UNITPRICE, _RECNO FROM products").ExecuteReader();
        var schema = reader.GetSchemaTable();
        Assert.Equal(4, schema.Rows.Count);

        var id = schema.Rows[0];
        Assert.Equal("PRODUCTID", id[SchemaTableColumn.ColumnName]);
        Assert.Equal(0, id[SchemaTableColumn.ColumnOrdinal]);
        Assert.Equal(typeof(int), id[SchemaTableColumn.DataType]);
        Assert.Equal(4, id[SchemaTableColumn.ColumnSize]);
        Assert.Equal(false, id[SchemaTableColumn.AllowDBNull]);

        var name = schema.Rows[1];
        Assert.Equal("NAME", name[SchemaTableColumn.ColumnName]);
        Assert.Equal("PRODUCTNAM", name[SchemaTableColumn.BaseColumnName]);
        Assert.Equal(true, name[SchemaTableColumn.IsAliased]);
        Assert.Equal(40, name[SchemaTableColumn.ColumnSize]);

        Assert.Equal(typeof(decimal), schema.Rows[2][SchemaTableColumn.DataType]);
        Assert.Equal("Currency", schema.Rows[2]["DataTypeName"]);
        Assert.Equal(true, schema.Rows[3][SchemaTableOptionalColumn.IsReadOnly]);

        // The column schema ADO.NET builds from it agrees.
        var columns = reader.GetColumnSchema();
        Assert.Equal(["PRODUCTID", "NAME", "UNITPRICE", "_RECNO"], columns.Select(c => c.ColumnName));
        Assert.Equal(typeof(string), columns[1].DataType);
    }

    [Fact]
    public void Numeric_schema_has_precision_and_scale_and_memos_are_long()
    {
        using var reader = _connection.Command("SELECT ID, CAUSE FROM CHANGELOG").ExecuteReader(CommandBehavior.SchemaOnly);
        var schema = reader.GetSchemaTable();
        Assert.Equal((short)10, schema.Rows[0][SchemaTableColumn.NumericPrecision]);
        Assert.Equal((short)0, schema.Rows[0][SchemaTableColumn.NumericScale]);
        Assert.Equal(true, schema.Rows[1][SchemaTableColumn.IsLong]);
        Assert.False(reader.Read()); // SchemaOnly reads no rows
    }

    [Fact]
    public void DataTable_Load_fills_typed_columns()
    {
        using var reader = _connection.Command("SELECT * FROM products ORDER BY UNITPRICE DESC LIMIT 10").ExecuteReader();
        var table = new DataTable();
        table.Load(reader);

        Assert.Equal(10, table.Rows.Count);
        Assert.Equal(typeof(decimal), table.Columns["UNITPRICE"]!.DataType);
        Assert.Equal(typeof(bool), table.Columns["DISCONTINU"]!.DataType);
        Assert.Equal("Côte de Blaye", table.Rows[0]["PRODUCTNAM"]);
        Assert.True(reader.IsClosed);
    }

    [Fact]
    public void Readers_stream_and_peek_without_losing_rows()
    {
        using var reader = _connection.Command("SELECT PRODUCTID FROM products").ExecuteReader();
        Assert.True(reader.HasRows);
        Assert.True(reader.HasRows); // peeking twice doesn't skip anything
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.False(reader.NextResult());
        Assert.False(reader.Read());

        using var empty = _connection.Command("SELECT PRODUCTID FROM products WHERE PRODUCTID = -1").ExecuteReader();
        Assert.False(empty.HasRows);
        Assert.Throws<InvalidOperationException>(() => empty.GetValue(0));
    }

    [Fact]
    public void Enumerating_a_reader_gives_data_records()
    {
        using var reader = _connection.Command("SELECT NAME FROM people").ExecuteReader();
        var names = reader.Cast<IDataRecord>().Select(r => r.GetString(0)).ToList();
        Assert.Equal(["Alice", "Bob"], names);
    }

    [Fact]
    public void CloseConnection_closes_the_connection_with_the_reader()
    {
        var connection = _data.Open();
        var reader = connection.Command("SELECT NAME FROM people").ExecuteReader(CommandBehavior.CloseConnection);
        reader.Read();
        reader.Close();
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void ExecuteScalar_returns_the_first_value()
    {
        Assert.Equal(77, _connection.Command("SELECT PRODUCTID FROM products ORDER BY PRODUCTID DESC").ExecuteScalar());
        Assert.Null(_connection.Command("SELECT PRODUCTID FROM products WHERE PRODUCTID = 0").ExecuteScalar());
    }

    // ---- Dapper-style usage ---------------------------------------------------------------

    private sealed record ProductRow(int ProductId, string ProductNam, decimal UnitPrice, bool Discontinu);

    private sealed class PersonRow
    {
        public string Name { get; set; } = "";
        public DateTime? BirthDate { get; set; }
    }

    [Fact]
    public void Dapper_style_queries_map_rows_to_records_and_classes()
    {
        DbConnection connection = _connection; // only the abstract ADO.NET surface, like Dapper uses

        var products = Query<ProductRow>(connection,
            "SELECT PRODUCTID, PRODUCTNAM, UNITPRICE, DISCONTINU FROM products WHERE CATEGORYID = @Category AND UNITPRICE > @MinPrice ORDER BY UNITPRICE DESC",
            new { Category = 1, MinPrice = 15m });
        Assert.Equal("Côte de Blaye", products[0].ProductNam);
        Assert.All(products, p => Assert.True(p.UnitPrice > 15m));

        var people = Query<PersonRow>(connection, "SELECT NAME, BIRTHDATE FROM people WHERE NAME LIKE @pattern", new { pattern = "%o%" });
        Assert.Equal("Bob", Assert.Single(people).Name);
        Assert.Equal(new DateTime(1980, 11, 12), people[0].BirthDate);
    }

    [Fact]
    public void Dapper_style_execute_runs_writes_with_an_object_of_parameters()
    {
        using var writable = _data.Open(writable: true);
        var affected = Execute(writable, "INSERT INTO people (NAME, BIRTHDATE) VALUES (@Name, @BirthDate)",
            new { Name = "Zoe", BirthDate = (DateTime?)new DateTime(2010, 5, 6) });
        Assert.Equal(1, affected);

        var zoe = Assert.Single(Query<PersonRow>(writable, "SELECT NAME, BIRTHDATE FROM people WHERE NAME = @Name", new { Name = "Zoe" }));
        Assert.Equal(new DateTime(2010, 5, 6), zoe.BirthDate);
    }

    // A minimal stand-in for Dapper: parameters from an object's properties, columns to
    // constructor arguments or settable properties by case-insensitive name.
    private static List<T> Query<T>(DbConnection connection, string sql, object parameters)
    {
        using var command = Prepare(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var results = new List<T>();
        while (reader.Read())
        {
            object? Value(string name, Type type)
            {
                var ordinal = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                var value = reader.GetValue(ordinal);
                return value is DBNull ? null : Convert.ChangeType(value, Nullable.GetUnderlyingType(type) ?? type);
            }

            var constructor = typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            var arguments = constructor.GetParameters().Select(p => Value(p.Name!, p.ParameterType)).ToArray();
            var item = (T)constructor.Invoke(arguments);
            if (arguments.Length == 0)
            {
                foreach (var property in typeof(T).GetProperties().Where(p => p.CanWrite))
                {
                    if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                        property.SetValue(item, Value(property.Name, property.PropertyType));
                }
            }
            results.Add(item);
        }
        return results;
    }

    private static int Execute(DbConnection connection, string sql, object parameters)
    {
        using var command = Prepare(connection, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static DbCommand Prepare(DbConnection connection, string sql, object parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var property in parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = property.Name; // Dapper passes names without the '@'
            parameter.Value = property.GetValue(parameters) ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
