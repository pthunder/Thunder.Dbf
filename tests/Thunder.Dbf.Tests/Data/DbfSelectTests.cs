//         _
//       _| |_        ProjectThunder :: Thunder.Dbf tests
//      |_   _|       SELECT: filters, sorting, paging, parameters,
//        |_|  \      and the index taking the shortcut only when
//          \__/      it gives exactly the rows a scan would.

using Thunder.Dbf.Data;

namespace Thunder.Dbf.Tests.Data;

public sealed class DbfSelectTests : IDisposable
{
    private readonly DataFolder _data = new();
    private readonly DbfConnection _connection;

    public DbfSelectTests() => _connection = _data.Open();

    public void Dispose()
    {
        _connection.Dispose();
        _data.Dispose();
    }

    [Fact]
    public void Select_star_returns_every_live_row_and_field()
    {
        using var reader = _connection.Command("SELECT * FROM client").ExecuteReader();
        Assert.Equal(10, reader.FieldCount);
        Assert.Equal("CLIENT_ID", reader.GetName(0));
        Assert.Equal("CLIEN_HIST", reader.GetName(9));

        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        Assert.Equal(8, ids.Count);
        Assert.Equal("A00001", ids[0]);
    }

    [Fact]
    public void Where_filters_with_parameters_and_literals()
    {
        Assert.Equal(["Chang"], _connection.Column("SELECT PRODUCTNAM FROM products WHERE PRODUCTID = @id", ("id", 2)));
        Assert.Equal(["Chang"], _connection.Column("SELECT PRODUCTNAM FROM products WHERE PRODUCTID = 2"));
        Assert.Equal(["Chang"], _connection.Column("SELECT PRODUCTNAM FROM products WHERE PRODUCTNAM = 'Chang'"));

        var cheapAndDiscontinued = _connection.Column(
            "SELECT PRODUCTID FROM products WHERE UNITPRICE < @max AND DISCONTINU = TRUE", ("max", 20m));
        var expected = Products().Where(p => p.Price < 20m && p.Discontinued).Select(p => (object)p.Id).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, cheapAndDiscontinued);
    }

    [Fact]
    public void Where_supports_every_comparison_and_boolean_operator()
    {
        var products = Products();
        void Check(string where, Func<Product, bool> predicate, params (string, object?)[] parameters)
        {
            var expected = products.Where(predicate).Select(p => (object)p.Id).ToList();
            Assert.Equal(expected, _connection.Column($"SELECT PRODUCTID FROM products WHERE {where}", parameters));
        }

        Check("CATEGORYID = 1", p => p.Category == 1);
        Check("CATEGORYID <> 1", p => p.Category != 1);
        Check("CATEGORYID != 1", p => p.Category != 1);
        Check("UNITPRICE > 50", p => p.Price > 50);
        Check("UNITPRICE >= 50", p => p.Price >= 50);
        Check("UNITPRICE <= 10", p => p.Price <= 10);
        Check("CATEGORYID = 1 OR CATEGORYID = 2", p => p.Category is 1 or 2);
        Check("NOT (CATEGORYID = 1 OR CATEGORYID = 2) AND UNITPRICE > @p", p => p.Category is not (1 or 2) && p.Price > 30, ("p", 30));
        Check("(CATEGORYID = 1 OR CATEGORYID = 2) AND NOT DISCONTINU = TRUE", p => p.Category is 1 or 2 && !p.Discontinued);
        Check("PRODUCTNAM LIKE 'ch%'", p => p.Name.StartsWith("ch", StringComparison.OrdinalIgnoreCase));
        Check("PRODUCTNAM LIKE @pattern", p => p.Name.Contains("Sauce", StringComparison.OrdinalIgnoreCase), ("pattern", "%sauce%"));
        Check("PRODUCTNAM NOT LIKE '%a%'", p => !p.Name.Contains('a', StringComparison.OrdinalIgnoreCase));
        Check("PRODUCTNAM LIKE 'Ch_i'", p => p.Name == "Chai");
        Check("1 = 1", _ => true);
        Check("@a = @b", _ => false, ("a", 1), ("b", 2));
    }

    [Fact]
    public void Null_follows_sql_rules()
    {
        // Blank dates and numbers are NULL; comparisons with NULL are never true.
        using var writable = _data.Open(writable: true);
        writable.Command("INSERT INTO people (NAME) VALUES ('Nobody')").ExecuteNonQuery();

        Assert.Equal(["Nobody"], _connection.Column("SELECT NAME FROM people WHERE BIRTHDATE IS NULL"));
        Assert.Equal(["Alice", "Bob"], _connection.Column("SELECT NAME FROM people WHERE BIRTHDATE IS NOT NULL"));
        Assert.Empty(_connection.Column("SELECT NAME FROM people WHERE BIRTHDATE = NULL"));
        Assert.Empty(_connection.Column("SELECT NAME FROM people WHERE BIRTHDATE = @d", ("d", DBNull.Value)));
        Assert.Equal(["Alice", "Bob"], _connection.Column("SELECT NAME FROM people WHERE NOT BIRTHDATE IS NULL"));
        Assert.Equal(["Nobody"], _connection.Column("SELECT NAME FROM people WHERE BIRTHDATE > @d OR NAME = 'Nobody'", ("d", DateTime.MaxValue)));
    }

    [Fact]
    public void Dates_compare_with_DateTime_DateOnly_or_iso_text()
    {
        Assert.Equal(["Alice"], _connection.Column("SELECT NAME FROM people WHERE BIRTHDATE = @d", ("d", new DateTime(1987, 3, 1))));
        Assert.Equal(["Alice"], _connection.Column("SELECT NAME FROM people WHERE BIRTHDATE = @d", ("d", new DateOnly(1987, 3, 1))));
        Assert.Equal(["Bob"], _connection.Column("SELECT NAME FROM people WHERE BIRTHDATE < '1985-01-01'"));
    }

    [Fact]
    public void Text_equality_ignores_dBASE_padding_but_not_case()
    {
        Assert.Equal(["Chai"], _connection.Column("SELECT PRODUCTNAM FROM products WHERE PRODUCTNAM = @n", ("n", "Chai   ")));
        Assert.Empty(_connection.Column("SELECT PRODUCTNAM FROM products WHERE PRODUCTNAM = 'CHAI'"));
        Assert.Equal(["Chai"], _connection.Column("SELECT PRODUCTNAM FROM products WHERE PRODUCTNAM LIKE 'CHAI'"));
    }

    [Fact]
    public void Order_by_sorts_on_several_columns_both_ways()
    {
        var expected = Products().OrderBy(p => p.Category).ThenByDescending(p => p.Price).ThenBy(p => p.Id).Select(p => (object)p.Id).ToList();
        Assert.Equal(expected, _connection.Column("SELECT PRODUCTID FROM products ORDER BY CATEGORYID, UNITPRICE DESC"));

        var byName = Products().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (object)p.Name).ToList();
        Assert.Equal(byName, _connection.Column("SELECT PRODUCTNAM FROM products ORDER BY PRODUCTNAM ASC"));
    }

    [Fact]
    public void Order_by_puts_nulls_first()
    {
        using var writable = _data.Open(writable: true);
        writable.Command("INSERT INTO people (NAME) VALUES ('Nobody')").ExecuteNonQuery();
        Assert.Equal(["Nobody", "Bob", "Alice"], _connection.Column("SELECT NAME FROM people ORDER BY BIRTHDATE"));
        Assert.Equal(["Alice", "Bob", "Nobody"], _connection.Column("SELECT NAME FROM people ORDER BY BIRTHDATE DESC"));
    }

    [Fact]
    public void Limit_and_offset_page_through_results()
    {
        var all = Products().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (object)p.Name).ToList();

        Assert.Equal(all.Take(5), _connection.Column("SELECT PRODUCTNAM FROM products ORDER BY PRODUCTNAM LIMIT 5"));
        Assert.Equal(all.Skip(5).Take(5), _connection.Column("SELECT PRODUCTNAM FROM products ORDER BY PRODUCTNAM LIMIT @n OFFSET @skip", ("n", 5), ("skip", 5)));
        Assert.Equal(all.Skip(75), _connection.Column("SELECT PRODUCTNAM FROM products ORDER BY PRODUCTNAM LIMIT 10 OFFSET 75"));
        Assert.Empty(_connection.Column("SELECT PRODUCTNAM FROM products LIMIT 0"));
        Assert.Equal([(object)"Chai", "Chang"], _connection.Column("SELECT PRODUCTNAM FROM products LIMIT 2"));
        Assert.Equal(75, _connection.Column("SELECT PRODUCTNAM FROM products OFFSET 2").Count);

        var error = Assert.Throws<DbfException>(() => _connection.Column("SELECT * FROM products LIMIT @n", ("n", -1)));
        Assert.Contains("LIMIT", error.Message);
    }

    [Fact]
    public void Columns_can_be_chosen_and_aliased()
    {
        using var reader = _connection.Command("SELECT PRODUCTNAM AS Name, [UNITPRICE] Price, _RECNO FROM products WHERE PRODUCTID = 3").ExecuteReader();
        Assert.Equal(["Name", "Price", "_RECNO"], Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        Assert.True(reader.Read());
        Assert.Equal("Aniseed Syrup", reader["name"]);
        Assert.Equal(10m, reader["Price"]);
        Assert.Equal(3, reader.GetInt32(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Deleted_records_are_hidden_unless_the_query_asks_for_them()
    {
        using (var writable = _data.Open(writable: true))
            Assert.Equal(1, writable.Command("DELETE FROM products WHERE PRODUCTID = 1").ExecuteNonQuery());

        Assert.Empty(_connection.Column("SELECT PRODUCTID FROM products WHERE PRODUCTID = 1"));
        Assert.Equal(76, _connection.Column("SELECT PRODUCTID FROM products").Count);
        Assert.Equal([1], _connection.Column("SELECT PRODUCTID FROM products WHERE _DELETED = TRUE"));
        Assert.Equal(77, _connection.Column("SELECT PRODUCTID FROM products WHERE _DELETED = TRUE OR _DELETED = FALSE").Count);

        using var reader = _connection.Command("SELECT _RECNO, _DELETED, PRODUCTNAM FROM products WHERE _DELETED = @d", ("d", true)).ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal("Chai", reader.GetString(2));
    }

    [Fact]
    public void Record_numbers_find_one_record_directly()
    {
        using var command = _connection.Command("SELECT PRODUCTNAM FROM products WHERE _RECNO = @n", ("n", 4));
        Assert.Equal("read record 4 of products", command.Explain());
        Assert.Equal("Chef Anton's Cajun Seasoning", command.ExecuteScalar());

        Assert.Empty(_connection.Column("SELECT PRODUCTNAM FROM products WHERE _RECNO = 999"));
        Assert.Empty(_connection.Column("SELECT PRODUCTNAM FROM products WHERE _RECNO = 0"));
        Assert.Equal(3, _connection.Column("SELECT PRODUCTNAM FROM products WHERE _RECNO <= 3").Count);
    }

    // ---- index-assisted lookups -----------------------------------------------------------

    [Fact]
    public void Equality_on_an_indexed_field_seeks_the_mdx_tag()
    {
        using var numeric = _connection.Command("SELECT * FROM CHANGELOG WHERE ACTION = @action", ("action", 13));
        Assert.Equal("seek CHANGELOG index tag ACTION_TAG for ACTION = 13", numeric.Explain());

        using var text = _connection.Command("SELECT * FROM CHANGELOG WHERE @user = USER AND ACTION > 0", ("user", "Win2"));
        Assert.Equal("seek CHANGELOG index tag USER_TAG for USER = 'Win2'", text.Explain());

        using var symbols = _connection.Command("SELECT * FROM SYMLIST WHERE _SKZ = @skz", ("skz", "GET"));
        Assert.StartsWith("seek SYMLIST index tag __SKZ", symbols.Explain());
        Assert.Equal([8], _connection.Column("SELECT _RECNO FROM SYMLIST WHERE _SKZ = @skz", ("skz", "GET")));
    }

    [Fact]
    public void Queries_the_index_cannot_answer_exactly_fall_back_to_a_scan()
    {
        string Plan(string sql, params (string, object?)[] parameters) => _connection.Command(sql, parameters).Explain();

        Assert.Equal("scan CHANGELOG", Plan("SELECT * FROM CHANGELOG WHERE ACTION > 1"));                    // not equality
        Assert.Equal("scan CHANGELOG", Plan("SELECT * FROM CHANGELOG WHERE ACTION = 1 OR ACTION = 2"));      // OR
        Assert.Equal("scan CHANGELOG", Plan("SELECT * FROM CHANGELOG WHERE LINE = 0"));                      // no tag on LINE
        Assert.Equal("scan CHANGELOG", Plan("SELECT * FROM CHANGELOG WHERE ACTION = @a", ("a", "two")));     // not a number
        Assert.Equal("scan CHANGELOG", Plan("SELECT * FROM CHANGELOG WHERE ACTION = NULL"));
        Assert.Equal("scan BAUSTEIN", Plan("SELECT * FROM BAUSTEIN WHERE TYP = '00010'"));                   // tag is TYP + NUMMER
        Assert.Equal("scan client", Plan("SELECT * FROM client WHERE CLIENT_ID = 'A00001'"));               // no .mdx file at all

        // A UNIQUE tag keeps one record per key, so it could miss duplicates the table really has.
        Assert.Equal("scan CHANGELOG (index tag ID_TAG is UNIQUE, so it can leave out records with a repeated key)",
            Plan("SELECT * FROM CHANGELOG WHERE ID = 2"));

        // FOR .NOT. DELETED() tags can't answer questions about deleted records.
        Assert.StartsWith("scan CHANGELOG (index tag ACTION_TAG leaves out deleted records",
            Plan("SELECT * FROM CHANGELOG WHERE ACTION = 2 AND _DELETED = TRUE"));

        // FOR .FALSE. holds nothing at all.
        Assert.StartsWith("scan SYMLIST (index tag __OPHIST only holds records FOR .FALSE.", Plan("SELECT * FROM SYMLIST WHERE _OPHIST = 'x'"));
    }

    [Fact]
    public void Index_lookups_return_exactly_the_rows_a_scan_does()
    {
        var checkedLookups = 0;
        foreach (var (table, column) in new[] { ("CHANGELOG", "USER"), ("CHANGELOG", "ACTION"), ("CHANGELOG", "OBJECTID"),
            ("CHANGELOG", "LEVEL"), ("CHANGELOG", "DATETIME"), ("SYMLIST", "_ID"), ("SYMLIST", "_SKZ"), ("SYMLIST", "_OPIEC") })
        {
            var values = _connection.Column($"SELECT {column} FROM {table}").Distinct().ToList();
            values.Add(values[0] is string ? "no such value" : 123456m);
            if (values[0] is string first)
            {
                values.Add("");          // a prefix of every key: the index must not return them all
                values.Add(first[..1]);  // a prefix of some keys
            }

            foreach (var value in values)
            {
                using var seek = _connection.Command($"SELECT _RECNO FROM {table} WHERE {column} = @v", ("v", value));
                Assert.StartsWith($"seek {table}", seek.Explain());

                // The same condition, written so the planner can't use the index.
                var viaIndex = _connection.Column($"SELECT _RECNO FROM {table} WHERE {column} = @v", ("v", value));
                var viaScan = _connection.Column($"SELECT _RECNO FROM {table} WHERE {column} = @v OR {column} = @v", ("v", value));
                Assert.Equal(viaScan, viaIndex);
                checkedLookups++;
            }
        }
        Assert.True(checkedLookups > 30, $"only {checkedLookups} lookups");
    }

    [Fact]
    public void Index_lookups_match_a_plain_table_scan_too()
    {
        using var table = DbfTable.Open(_data.PathOf("SYMLIST.DBF"));
        foreach (var record in table.ReadAll())
        {
            var skz = (string)record["_SKZ"]!;
            var expected = table.ReadAll().Where(r => !r.IsDeleted && (string)r["_SKZ"]! == skz).Select(r => (object)r.RecordNumber);
            Assert.Equal(expected, _connection.Column("SELECT _RECNO FROM SYMLIST WHERE _SKZ = @skz", ("skz", skz)));
        }
    }

    [Fact]
    public void Indexes_are_not_trusted_when_the_connection_may_leave_them_stale()
    {
        using var connection = new DbfConnection(_data.WritableConnectionString); // includes Allow Unmaintained Indexes=true
        connection.Open();
        Assert.Equal("scan CHANGELOG", connection.Command("SELECT * FROM CHANGELOG WHERE ACTION = 2").Explain());
    }

    // ---- helpers --------------------------------------------------------------------------

    private sealed record Product(int Id, string Name, int Category, decimal Price, bool Discontinued);

    private List<Product> Products()
    {
        using var table = DbfTable.Open(_data.PathOf("products.dbf"));
        return table.ReadAll().Select(r => new Product((int)r["PRODUCTID"]!, (string)r["PRODUCTNAM"]!, (int)r["CATEGORYID"]!,
            (decimal)r["UNITPRICE"]!, (bool)r["DISCONTINU"]!)).ToList();
    }
}
