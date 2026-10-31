//        ___________
//       |  O'Hara'; |       ProjectThunder :: Thunder.Dbf tests
//       |  DROP --  |  -->  [ just a string ]
//       |___________|       Values are data, never SQL. And anything the SQL
//                           subset can't do is refused by name, not guessed at.

using Thunder.Dbf.Data;

namespace Thunder.Dbf.Tests.Data;

public sealed class DbfSqlSafetyTests : IDisposable
{
    private readonly DataFolder _data = new();
    private readonly DbfConnection _connection;

    public DbfSqlSafetyTests() => _connection = _data.Open(writable: true);

    public void Dispose()
    {
        _connection.Dispose();
        _data.Dispose();
    }

    // ---- injection ------------------------------------------------------------------------

    [Theory]
    [InlineData("x' OR '1'='1")]
    [InlineData("' OR 1=1 --")]
    [InlineData("Chai'; DELETE FROM products; --")]
    [InlineData("@other")]
    [InlineData("%")]
    public void Parameter_values_are_compared_as_data(string hostile)
    {
        Assert.Empty(_connection.Column("SELECT PRODUCTID FROM products WHERE PRODUCTNAM = @name", ("name", hostile)));
        Assert.Equal(77, _connection.Column("SELECT PRODUCTID FROM products").Count);
    }

    [Fact]
    public void Quotes_in_values_round_trip_verbatim()
    {
        Assert.Equal([4], _connection.Column("SELECT PRODUCTID FROM products WHERE PRODUCTNAM = @name", ("name", "Chef Anton's Cajun Seasoning")));

        const string hostile = "'); DELETE --"; // fits the 16-character NAME field
        Assert.Equal(1, _connection.Command("INSERT INTO people (NAME) VALUES (@n)", ("n", hostile)).ExecuteNonQuery());
        Assert.Equal(["Alice", "Bob", hostile], _connection.Column("SELECT NAME FROM people"));

        Assert.Equal(1, _connection.Command("UPDATE people SET NAME = @n WHERE NAME = @old", ("n", "O'Hara"), ("old", hostile)).ExecuteNonQuery());
        Assert.Equal(["O'Hara"], _connection.Column("SELECT NAME FROM people WHERE NAME LIKE @p", ("p", "%'%")));
    }

    [Fact]
    public void Hostile_values_cannot_widen_a_delete()
    {
        Assert.Equal(0, _connection.Command("DELETE FROM people WHERE NAME = @n", ("n", "Alice' OR '1'='1")).ExecuteNonQuery());
        Assert.Equal(2, _connection.Column("SELECT NAME FROM people").Count);
    }

    [Fact]
    public void Quoted_literals_in_the_sql_escape_by_doubling()
    {
        Assert.Equal([4], _connection.Column("SELECT PRODUCTID FROM products WHERE PRODUCTNAM = 'Chef Anton''s Cajun Seasoning'"));
    }

    [Theory]
    [InlineData("SELECT * FROM [../people]")]
    [InlineData("SELECT * FROM [/etc/passwd]")]
    [InlineData("SELECT * FROM [people.dbt]")]
    [InlineData("SELECT * FROM nosuchtable")]
    public void Table_names_cannot_reach_outside_the_folder(string sql)
    {
        var error = Assert.Throws<DbfException>(() => _connection.Command(sql).ExecuteReader());
        Assert.Contains(error.Code, new[] { DbfErrorCode.PathEscapesFolder, DbfErrorCode.TableNotFound });
    }

    [Fact]
    public void Patterns_that_would_backtrack_forever_finish_quickly()
    {
        var pattern = string.Concat(Enumerable.Repeat("%a", 40)) + "%b";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(_connection.Column("SELECT NAME FROM people WHERE NAME LIKE @p", ("p", pattern)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    // ---- unsupported SQL ------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT * FROM products; DELETE FROM products", "More than one statement")]
    [InlineData("SELECT * FROM products -- comment", "comment")]
    [InlineData("SELECT * FROM products /* comment */", "comment")]
    [InlineData("SELECT * FROM products JOIN people ON PRODUCTID = NAME", "JOIN")]
    [InlineData("SELECT * FROM products p WHERE PRODUCTID = 1", "table alias")]
    [InlineData("SELECT * FROM products, people", "more than one table")]
    [InlineData("SELECT COUNT(*) FROM products", "COUNT()")]
    [InlineData("SELECT * FROM products WHERE UPPER(PRODUCTNAM) = 'CHAI'", "UPPER()")]
    [InlineData("SELECT CATEGORYID FROM products GROUP BY CATEGORYID", "GROUP BY")]
    [InlineData("SELECT DISTINCT CATEGORYID FROM products", "DISTINCT")]
    [InlineData("SELECT TOP 5 * FROM products", "TOP")]
    [InlineData("SELECT * FROM products WHERE CATEGORYID IN (1, 2)", "IN (...)")]
    [InlineData("SELECT * FROM products WHERE UNITPRICE BETWEEN 1 AND 2", "BETWEEN")]
    [InlineData("SELECT * FROM products WHERE PRODUCTID = (SELECT 1)", "subquery")]
    [InlineData("SELECT * FROM products WHERE UNITPRICE * 2 > 10", "Arithmetic")]
    [InlineData("SELECT * FROM products WHERE PRODUCTID = ?", "positional")]
    [InlineData("SELECT * FROM products UNION SELECT * FROM people", "UNION")]
    [InlineData("SELECT products.PRODUCTID FROM products", "qualified name")]
    [InlineData("CREATE TABLE x (a INT)", "CREATE")]
    [InlineData("DROP TABLE products", "DROP")]
    [InlineData("TRUNCATE TABLE products", "TRUNCATE")]
    [InlineData("INSERT INTO people (NAME) SELECT NAME FROM people", "INSERT ... SELECT")]
    [InlineData("INSERT INTO people (NAME) VALUES ('a'), ('b')", "several rows")]
    [InlineData("SELECT * FROM products WHERE PRODUCTNAM LIKE 'a!%' ESCAPE '!'", "ESCAPE")]
    public void Unsupported_sql_is_named_in_the_error(string sql, string named)
    {
        var error = Assert.Throws<DbfException>(() => _connection.Command(sql).ExecuteReader());
        Assert.Contains(named, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
        Assert.Equal(77, _connection.Column("SELECT PRODUCTID FROM products").Count);
    }

    [Theory]
    [InlineData("", "no text")]
    [InlineData("SELECT FROM products", "column name")]
    [InlineData("SELECT * FROM products WHERE", "Expected")]
    [InlineData("SELECT * FROM products WHERE NAME = 'open", "never closes")]
    [InlineData("SELECT * FROM products ORDER BY", "column name")]
    [InlineData("SELECT * FROM products WHERE PRODUCTID = 1 extra", "should have ended")]
    [InlineData("SELECT * FROM products LIMIT 1.5", "whole number")]
    [InlineData("INSERT INTO people (NAME, BIRTHDATE) VALUES ('a')", "2 column(s) but gives 1")]
    [InlineData("INSERT INTO people VALUES ('a')", "column list")]
    [InlineData("UPDATE people SET NAME = 'x'", "needs a WHERE")]
    [InlineData("DELETE FROM people", "needs a WHERE")]
    [InlineData("SELECT NOPE FROM people", "no column 'NOPE'")]
    [InlineData("SELECT * FROM people WHERE NAME = @missing", "@missing")]
    [InlineData("SELECT * FROM people WHERE BIRTHDATE > 'soon'", "'soon'")]
    [InlineData("SELECT * FROM people WHERE NAME > 3", "'Alice' can't be compared")]
    [InlineData("SELECT * FROM people WHERE BIRTHDATE LIKE '19%'", "LIKE works on text only")]
    [InlineData("SELECT * FROM [select] WHERE 1 = 1", "no table")]
    [InlineData("SELECT * FROM select", "keyword SELECT")]
    public void Malformed_sql_says_what_is_wrong(string sql, string expected)
    {
        var error = Assert.Throws<DbfException>(() => _connection.Column(sql));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Delete_without_where_suggests_the_explicit_form()
    {
        var error = Assert.Throws<DbfException>(() => _connection.Command("DELETE FROM people").ExecuteNonQuery());
        Assert.Contains("WHERE _DELETED = FALSE", error.Hint);
        Assert.Equal(2, _connection.Command("DELETE FROM people WHERE _DELETED = FALSE").ExecuteNonQuery());
    }

    [Fact]
    public void Prepare_checks_the_sql_without_running_it()
    {
        _connection.Command("SELECT * FROM products WHERE PRODUCTID = @id").Prepare();
        Assert.Throws<DbfException>(() => _connection.Command("SELECT * FROM products WHERE").Prepare());
    }

    [Fact]
    public void Parameters_match_with_or_without_the_at_sign()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT PRODUCTID FROM products WHERE PRODUCTID = @id OR PRODUCTID = :other";
        command.Parameters.AddWithValue("@ID", 1);
        command.Parameters.Add(new DbfParameter("other", 2));

        using var reader = command.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        Assert.Equal([1, 2], ids);
        Assert.True(command.Parameters.Contains("id"));
        Assert.Equal(2, command.Parameters["@other"].Value);
    }

    [Fact]
    public void Only_text_commands_and_input_parameters_exist()
    {
        using var command = _connection.CreateCommand();
        Assert.Throws<NotSupportedException>(() => command.CommandType = System.Data.CommandType.StoredProcedure);
        Assert.Throws<NotSupportedException>(() => command.CreateParameter().Direction = System.Data.ParameterDirection.Output);
        Assert.Throws<InvalidCastException>(() => command.Parameters.Add("not a parameter"));
    }
}
