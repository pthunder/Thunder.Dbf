//        __
//       /  \___        ProjectThunder :: Thunder.Dbf tests
//      | ++  _ |       INSERT, UPDATE and DELETE, carefully: only on a writable
//       \__/ \_|       connection, only the rows the WHERE names, and every
//         ~~~~         value converted to the column's type or refused.

using Thunder.Dbf.Data;

namespace Thunder.Dbf.Tests.Data;

public sealed class DbfWriteTests : IDisposable
{
    private readonly DataFolder _data = new();
    private readonly DbfConnection _connection;

    public DbfWriteTests() => _connection = _data.Open(writable: true);

    public void Dispose()
    {
        _connection.Dispose();
        _data.Dispose();
    }

    [Fact]
    public void Insert_appends_a_record_with_converted_values()
    {
        var affected = _connection.Command(
            "INSERT INTO products (PRODUCTID, PRODUCTNAM, CATEGORYID, UNITPRICE, DISCONTINU) VALUES (@id, @name, 3, @price, @gone)",
            ("id", 78L), ("name", "Thunder Tea"), ("price", 12.5), ("gone", false)).ExecuteNonQuery();
        Assert.Equal(1, affected);

        using var table = DbfTable.Open(_data.PathOf("products.dbf"));
        Assert.Equal(78, table.RecordCount);
        var row = table.Read(77);
        Assert.Equal(78, row["PRODUCTID"]);
        Assert.Equal("Thunder Tea", row["PRODUCTNAM"]);
        Assert.Equal(3, row["CATEGORYID"]);
        Assert.Equal(12.5m, row["UNITPRICE"]);
        Assert.Equal(false, row["DISCONTINU"]);
    }

    [Fact]
    public void Insert_stores_dates_memos_and_blanks()
    {
        _connection.Command("INSERT INTO people (NAME, BIRTHDATE) VALUES (@n, @d)", ("n", "Carol"), ("d", new DateOnly(1999, 12, 31))).ExecuteNonQuery();
        _connection.Command("INSERT INTO people (NAME, BIRTHDATE) VALUES ('Dave', '2001-02-03')").ExecuteNonQuery();
        _connection.Command("INSERT INTO client (CLIENT_ID, CLIENT, CLIEN_HIST) VALUES (@id, @c, @memo)",
            ("id", "Z00001"), ("c", "THUNDER"), ("memo", "First order.\r\nSecond order.")).ExecuteNonQuery();

        using var reader = _connection.Command("SELECT NAME, BIRTHDATE FROM people WHERE _RECNO > 2").ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(new DateTime(1999, 12, 31), reader.GetDateTime(1));
        Assert.True(reader.Read());
        Assert.Equal(new DateOnly(2001, 2, 3), reader.GetFieldValue<DateOnly>(1));

        Assert.Equal("First order.\r\nSecond order.",
            _connection.Command("SELECT CLIEN_HIST FROM client WHERE CLIENT_ID = 'Z00001'").ExecuteScalar());
        Assert.Equal("", _connection.Command("SELECT CITY FROM client WHERE CLIENT_ID = 'Z00001'").ExecuteScalar());
    }

    [Fact]
    public void Update_changes_only_the_matching_records_and_columns()
    {
        var affected = _connection.Command("UPDATE products SET UNITPRICE = @price, DISCONTINU = TRUE WHERE CATEGORYID = @cat",
            ("price", 1.25m), ("cat", 1)).ExecuteNonQuery();

        using var table = DbfTable.Open(_data.PathOf("products.dbf"));
        var beverages = table.ReadAll().Where(r => (int)r["CATEGORYID"]! == 1).ToList();
        Assert.Equal(beverages.Count, affected);
        Assert.All(beverages, r => Assert.Equal(1.25m, r["UNITPRICE"]));
        Assert.All(beverages, r => Assert.Equal(true, r["DISCONTINU"]));
        Assert.Equal("Chai", beverages[0]["PRODUCTNAM"]);

        var others = table.ReadAll().Where(r => (int)r["CATEGORYID"]! != 1).ToList();
        Assert.DoesNotContain(others, r => (decimal)r["UNITPRICE"]! == 1.25m);
    }

    [Fact]
    public void Update_by_record_number_and_with_null()
    {
        Assert.Equal(1, _connection.Command("UPDATE people SET BIRTHDATE = NULL WHERE _RECNO = @n", ("n", 2)).ExecuteNonQuery());
        Assert.Equal(["Bob"], _connection.Column("SELECT NAME FROM people WHERE BIRTHDATE IS NULL"));
        Assert.Equal(0, _connection.Command("UPDATE people SET NAME = 'x' WHERE _RECNO = 99").ExecuteNonQuery());
    }

    [Fact]
    public void Update_keeps_memos_it_was_not_asked_to_change()
    {
        var before = (string)_connection.Command("SELECT CLIEN_HIST FROM client WHERE CLIENT_ID = 'A00001'").ExecuteScalar()!;
        _connection.Command("UPDATE client SET CITY = @city WHERE CLIENT_ID = 'A00001'", ("city", "Boston")).ExecuteNonQuery();

        using var reader = _connection.Command("SELECT CITY, CLIEN_HIST FROM client WHERE CLIENT_ID = 'A00001'").ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Boston", reader.GetString(0));
        Assert.Equal(before, reader.GetString(1));
    }

    [Fact]
    public void Delete_marks_records_and_recall_brings_them_back()
    {
        Assert.Equal(2, _connection.Command("DELETE FROM people WHERE NAME = 'Alice' OR NAME = 'Bob'").ExecuteNonQuery());
        Assert.Empty(_connection.Column("SELECT NAME FROM people"));

        using (var table = DbfTable.Open(_data.PathOf("people.dbf")))
        {
            Assert.Equal(3, table.RecordCount); // marked, not removed (the third was deleted before)
            Assert.All(table.ReadAll(), r => Assert.True(r.IsDeleted));
        }

        Assert.Equal(1, _connection.Command("UPDATE people SET _DELETED = FALSE WHERE NAME = 'Bob' AND _DELETED = TRUE").ExecuteNonQuery());
        Assert.Equal(["Bob"], _connection.Column("SELECT NAME FROM people"));
    }

    [Fact]
    public void Deleting_again_only_touches_live_rows()
    {
        Assert.Equal(1, _connection.Command("DELETE FROM people WHERE NAME = 'Alice'").ExecuteNonQuery());
        Assert.Equal(0, _connection.Command("DELETE FROM people WHERE NAME = 'Alice'").ExecuteNonQuery());
    }

    [Fact]
    public void Writes_see_records_other_programs_added()
    {
        Assert.Equal(2, _connection.Column("SELECT NAME FROM people").Count);
        using (var legacyApp = DbfTable.Open(_data.PathOf("people.dbf"), writable: true))
            legacyApp.Append("Eve", new DateOnly(1970, 1, 1));

        Assert.Equal(["Alice", "Bob", "Eve"], _connection.Column("SELECT NAME FROM people"));
        Assert.Equal(1, _connection.Command("UPDATE people SET NAME = 'Eva' WHERE NAME = 'Eve'").ExecuteNonQuery());
    }

    [Fact]
    public void Bad_values_are_refused_before_anything_is_written()
    {
        var tooLong = Assert.Throws<DbfException>(() =>
            _connection.Command("INSERT INTO people (NAME) VALUES (@n)", ("n", new string('x', 17))).ExecuteNonQuery());
        Assert.Contains("16", tooLong.Message);

        var notADate = Assert.Throws<DbfException>(() =>
            _connection.Command("UPDATE people SET BIRTHDATE = @d WHERE NAME = 'Alice'", ("d", "someday")).ExecuteNonQuery());
        Assert.Contains("BIRTHDATE", notADate.Message);

        var notANumber = Assert.Throws<DbfException>(() =>
            _connection.Command("UPDATE products SET UNITPRICE = @p WHERE PRODUCTID = 1", ("p", true)).ExecuteNonQuery());
        Assert.Contains("UNITPRICE", notANumber.Message);

        Assert.Throws<DbfException>(() =>
            _connection.Command("UPDATE products SET CATEGORYID = 1.5 WHERE PRODUCTID = 1").ExecuteNonQuery());

        using var table = DbfTable.Open(_data.PathOf("people.dbf"));
        Assert.Equal(3, table.RecordCount);
        Assert.Equal(new DateOnly(1987, 3, 1), table.Read(0)["BIRTHDATE"]);
    }

    [Fact]
    public void Pseudo_columns_cannot_be_written()
    {
        Assert.Throws<DbfException>(() => _connection.Command("UPDATE people SET _RECNO = 5 WHERE NAME = 'Alice'").ExecuteNonQuery());
        Assert.Throws<DbfException>(() => _connection.Command("INSERT INTO people (NAME, _DELETED) VALUES ('x', TRUE)").ExecuteNonQuery());
        Assert.Throws<DbfException>(() => _connection.Command("UPDATE people SET _DELETED = 'maybe' WHERE NAME = 'Alice'").ExecuteNonQuery());
    }

    [Fact]
    public void Writes_keep_the_production_mdx_up_to_date()
    {
        using var strict = new DbfConnection($"Data Source={_data.Path};Read Only=false");
        strict.Open();
        Assert.Equal(1, strict.Command("UPDATE CHANGELOG SET LEVEL = 77 WHERE ID = 1").ExecuteNonQuery());

        // The LEVEL_TAG seek finds the record under its new key, so the index moved with it.
        var lookup = strict.Command("SELECT ID FROM CHANGELOG WHERE LEVEL = 77");
        Assert.StartsWith("seek", lookup.Explain());
        Assert.Equal(1m, lookup.ExecuteScalar());
    }

    [Fact]
    public void Tables_with_an_index_the_library_cannot_maintain_refuse_writes()
    {
        // products.dbf declares a FoxPro structural .cdx, which can't be kept up to date yet.
        using var strict = new DbfConnection($"Data Source={_data.Path};Read Only=false");
        strict.Open();
        var error = Assert.Throws<DbfException>(() =>
            strict.Command("UPDATE products SET UNITPRICE = 1 WHERE PRODUCTID = 1").ExecuteNonQuery());
        Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
    }

    [Fact]
    public void ExecuteReader_on_a_write_reports_records_affected()
    {
        using var reader = _connection.Command("UPDATE people SET NAME = 'Same' WHERE _DELETED = FALSE").ExecuteReader();
        Assert.Equal(2, reader.RecordsAffected);
        Assert.Equal(0, reader.FieldCount);
        Assert.False(reader.Read());
        Assert.Null(_connection.Command("DELETE FROM people WHERE _RECNO = 1").ExecuteScalar());
    }
}
