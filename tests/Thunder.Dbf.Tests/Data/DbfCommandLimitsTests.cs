//      ______________
//     |  SELECT ...  |  64 KB?  10,000 words?  64 levels deep?    ProjectThunder :: Thunder.Dbf tests
//     |  ((((((((... |  -> refused before it costs anything       SQL that arrives from a web form
//     |______________|                                             gets limits, a clock and a brake.

using System.Data;
using Thunder.Dbf.Data;

namespace Thunder.Dbf.Tests.Data;

public sealed class DbfCommandLimitsTests : IDisposable
{
    private readonly DataFolder _data = new();
    private readonly DbfConnection _connection;

    public DbfCommandLimitsTests() => _connection = _data.Open(writable: true);

    public void Dispose()
    {
        _connection.Dispose();
        _data.Dispose();
    }

    [Fact]
    public void Huge_statements_are_refused_before_parsing()
    {
        var sql = "SELECT PRODUCTID FROM products WHERE " + string.Join(" OR ", Enumerable.Repeat("PRODUCTID = 1", 6000));
        var error = Assert.Throws<DbfException>(() => _connection.Command(sql).ExecuteReader());
        Assert.Equal(DbfErrorCode.UnsupportedSql, error.Code);
    }

    [Fact]
    public void Deep_nesting_is_refused_instead_of_overflowing_the_stack()
    {
        var sql = "SELECT PRODUCTID FROM products WHERE " + new string('(', 500) + "PRODUCTID = 1" + new string(')', 500);
        var error = Assert.Throws<DbfException>(() => _connection.Command(sql).ExecuteReader());
        Assert.Equal(DbfErrorCode.UnsupportedSql, error.Code);
        Assert.Contains("nests", error.Message);

        var notNot = "SELECT PRODUCTID FROM products WHERE " + string.Concat(Enumerable.Repeat("NOT ", 200)) + "PRODUCTID = 1";
        Assert.Equal(DbfErrorCode.UnsupportedSql, Assert.Throws<DbfException>(() => _connection.Command(notNot).ExecuteReader()).Code);
    }

    [Theory]
    [InlineData("ORDER BY UNITPRICE DESC", 5, 0)]
    [InlineData("ORDER BY UNITPRICE DESC", 5, 10)]
    [InlineData("ORDER BY CATEGORYID, PRODUCTNAM", 7, 3)]
    public void A_top_N_sort_gives_the_same_rows_as_a_full_sort(string orderBy, int limit, int offset)
    {
        var full = _connection.Column($"SELECT PRODUCTID FROM products {orderBy}");
        var top = _connection.Column($"SELECT PRODUCTID FROM products {orderBy} LIMIT {limit} OFFSET {offset}");
        Assert.Equal(full.Skip(offset).Take(limit), top);
    }

    [Fact]
    public void Cancel_stops_a_running_reader()
    {
        var command = _connection.Command("SELECT PRODUCTID FROM products");
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        command.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Read());
    }

    [Fact]
    public void A_reader_past_its_CommandTimeout_stops_with_an_explained_error()
    {
        var command = _connection.Command("SELECT PRODUCTID FROM products");
        command.CommandTimeout = 1;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Thread.Sleep(1100);
        var error = Assert.Throws<DbfException>(() => reader.Read());
        Assert.Equal(DbfErrorCode.Timeout, error.Code);
        Assert.Equal(504, error.HttpStatus);
        Assert.Throws<ArgumentOutOfRangeException>(() => command.CommandTimeout = -1);
    }

    [Fact]
    public void SchemaOnly_never_runs_a_write()
    {
        using (var reader = _connection.Command("DELETE FROM people WHERE NAME = @n", ("n", "Alice")).ExecuteReader(CommandBehavior.SchemaOnly))
            Assert.Equal(-1, reader.RecordsAffected);

        Assert.Equal(["Alice", "Bob"], _connection.Column("SELECT NAME FROM people"));
    }

    [Fact]
    public void UPDATE_leaves_other_columns_as_they_are_on_disk()
    {
        // Another program changes BIRTHDATE after the connection opened; UPDATE NAME keeps it. (The
        // race inside one UPDATE is covered by ConcurrentEditTests, through UpdateFields.)
        using (var legacy = DbfTable.Open(_data.PathOf("people.dbf"), writable: true))
        {
            var values = legacy.Read(0).Values.ToArray();
            values[1] = new DateOnly(2001, 2, 3);
            legacy.Update(0, values);
        }

        Assert.Equal(1, _connection.Command("UPDATE people SET NAME = @n WHERE _RECNO = 1", ("n", "Alicia")).ExecuteNonQuery());

        using var check = DbfTable.Open(_data.PathOf("people.dbf"));
        Assert.Equal("Alicia", check.Read(0)["NAME"]);
        Assert.Equal(new DateOnly(2001, 2, 3), check.Read(0)[1]);
    }
}
