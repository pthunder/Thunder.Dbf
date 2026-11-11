//      LIKE '%50%'   ~  "50% off"        ProjectThunder :: Thunder.Dbf tests
//      TOTAL/QTY     ->  a number        What a read-through of the whole library found, one test
//      rebuild x 3   ->  same .mdx size  per defect. Each fails without its fix.

using System.Globalization;
using Thunder.Dbf.Data;

namespace Thunder.Dbf.Tests;

public sealed class AuditRegressionTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-audit-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string PathOf(string name) => Path.Combine(_dir.FullName, name);

    // ---- SQL ------------------------------------------------------------------------------

    [Theory]
    [InlineData("50% off", "%50%", true)]
    [InlineData("100% sure", "100%", true)]
    [InlineData("100% sure", "%sure", true)]
    [InlineData("a%b", "a%b", true)]
    [InlineData("a%b", "a_b", true)]
    [InlineData("%", "%", true)]
    [InlineData("50% off", "%60%", false)]
    [InlineData("half off", "%50%", false)]
    public void Like_takes_a_percent_in_the_pattern_as_a_wildcard_whatever_the_text_holds(string text, string pattern, bool expected) =>
        Assert.Equal(expected, DbfValues.Like(text, pattern));

    [Fact]
    public void A_reader_gives_nullable_dates_and_keeps_HasRows_after_the_last_row()
    {
        using (var table = DbfTable.Create(PathOf("sale.dbf"), DbfTableFormat.DbaseIV,
                   [DbfFieldDefinition.Character("NOTE", 20), DbfFieldDefinition.Date("SOLD")]))
        {
            table.Append("50% off", new DateOnly(2024, 1, 15));
            table.Append("half off", null);
        }

        using var connection = new DbfConnection($"Data Source={_dir.FullName}");
        connection.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT NOTE FROM sale WHERE NOTE LIKE '%50%'";
        Assert.Equal("50% off", command.ExecuteScalar());

        command.CommandText = "SELECT SOLD FROM sale";
        using var reader = command.ExecuteReader();
        Assert.True(reader.HasRows);
        Assert.True(reader.Read());
        Assert.Equal(new DateOnly(2024, 1, 15), reader.GetFieldValue<DateOnly?>(0));
        Assert.Equal(new DateOnly(2024, 1, 15), reader.GetFieldValue<DateOnly>(0));
        Assert.True(reader.Read());
        Assert.Null(reader.GetFieldValue<DateOnly?>(0));
        Assert.False(reader.Read());
        Assert.True(reader.HasRows); // the result had rows; other providers keep saying so
    }

    [Fact]
    public void HasRows_is_false_for_a_result_with_no_rows()
    {
        using (DbfTable.Create(PathOf("none.dbf"), DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NOTE", 5)]))
        {
        }
        using var connection = new DbfConnection($"Data Source={_dir.FullName}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NOTE FROM none";
        using var reader = command.ExecuteReader();
        Assert.False(reader.HasRows);
        Assert.False(reader.Read());
        Assert.False(reader.HasRows);
    }

    // ---- values ---------------------------------------------------------------------------

    [Theory]
    [InlineData("895")]   // Kamenicky: a number in range that .NET has no code page for
    [InlineData("1234")]
    [InlineData("nonsense")]
    public void A_cpg_file_naming_an_encoding_that_isnt_there_falls_back_to_the_header(string label)
    {
        var path = PathOf("t.dbf");
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 10)]))
            table.Append("x");
        File.WriteAllText(Path.ChangeExtension(path, ".cpg"), label);

        using var opened = DbfTable.Open(path);
        Assert.Equal(1252, opened.Encoding.CodePage);
        Assert.Equal("x", opened.Read(0)["NAME"]);
    }

    [Fact]
    public void A_double_keeps_every_digit_in_a_numeric_field()
    {
        using var table = DbfTable.Create(PathOf("n.dbf"), DbfTableFormat.DbaseIV,
            [DbfFieldDefinition.Numeric("BIG", 20), DbfFieldDefinition.Numeric("SMALL", 20, 17)]);
        table.Append(9007199254740992d, 0.1234567890123456d); // 2^53: sixteen digits, exact in a double
        table.Append(0.1f, 0.1d);
        table.Append(-123456789012345678d, 1e-17d);

        Assert.Equal(9007199254740992m, table.Read(0)["BIG"]);
        Assert.Equal(0.1234567890123456m, table.Read(0)["SMALL"]);
        Assert.Equal(0m, table.Read(1)["BIG"]); // 0.1 rounds to the field's no decimals
        Assert.Equal(0.1m, table.Read(1)["SMALL"]);
        Assert.Equal(decimal.Parse((-123456789012345678d).ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
            table.Read(2)["BIG"]);
        Assert.Equal(0.00000000000000001m, table.Read(2)["SMALL"]);
    }

    public sealed class Person
    {
        public string? Note { get; set; }
        public DateTime Hired { get; set; }
        public DateOnly Left { get; set; }
        public char Code { get; set; }
    }

    [Fact]
    public void Reading_a_record_and_updating_it_leaves_its_blank_fields_blank()
    {
        var path = PathOf("p.dbf");
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV,
                   [DbfFieldDefinition.Character("NOTE", 10), DbfFieldDefinition.Date("HIRED"), DbfFieldDefinition.Date("LEFT"), DbfFieldDefinition.Character("CODE", 1)]))
        {
            table.Append("a", null, null, "");
            var before = table.Read(0);
            var person = table.Read<Person>(0);
            Assert.Equal(default, person.Hired); // a blank reads as the type's default
            person.Note = "b";
            table.UpdateRecord(0, person);

            Assert.Equal("b", table.Read(0)["NOTE"]);
            Assert.Null(table.Read(0)["HIRED"]);
            Assert.Null(table.Read(0)["LEFT"]);

            // And a new record with the properties unset.
            table.AppendRecord(new Person { Note = "c" });
            Assert.Null(table.Read(1)["HIRED"]);
            _ = before;
        }

        // The stored bytes: spaces, not 00010101 and not a NUL.
        var bytes = File.ReadAllBytes(path);
        using var check = DbfTable.Open(path);
        var record = bytes.AsSpan(check.Header.HeaderLength, check.Header.RecordLength);
        Assert.Equal(" b                          "u8.ToArray(), record.ToArray());
    }

    // ---- index expressions ----------------------------------------------------------------

    [Fact]
    public void A_key_that_divides_by_a_field_still_has_a_type()
    {
        using var table = DbfTable.Create(PathOf("d.dbf"), DbfTableFormat.DbaseIV,
            [DbfFieldDefinition.Numeric("TOTAL", 8, 2), DbfFieldDefinition.Numeric("QTY", 5)]);

        // On a blank record QTY is 0. That used to escape as a DivideByZeroException and stop the
        // index, and a writable open of its table, from opening.
        Assert.Equal('N', IndexKeys.InferKeyType("TOTAL/QTY", table, fallback: 'C'));
        Assert.Equal('C', IndexKeys.InferKeyType("STR(TOTAL/QTY,10,2)", table, fallback: 'L'));
        Assert.Equal('N', IndexKeys.InferKeyType("TOTAL/(QTY-TOTAL)", table, fallback: 'C')); // zero on any made-up record
        Assert.Equal('N', IndexKeys.InferKeyType("100/(QTY-QTY)", table, fallback: 'C'));
        Assert.Equal('L', IndexKeys.InferKeyType("TOTAL/QTY>1", table, fallback: 'C'));

        // Asking for the type doesn't change what a real record's key does.
        table.Append(5m, 0);
        Assert.Throws<DivideByZeroException>(() => DbfExpression.Parse("TOTAL/QTY").Evaluate(table.Read(0)));
    }

    [Theory]
    [InlineData("QTY=1.AND.OK", true)]
    [InlineData("QTY=2.OR.OK", true)]
    [InlineData("QTY=2.AND..NOT.OK", false)]
    [InlineData("QTY>0.AND.OK", true)]
    [InlineData("QTY=1.0.AND.OK", true)]
    [InlineData("QTY<1.5", true)]
    [InlineData("QTY>.5", true)]
    [InlineData("QTY=1.", true)]
    public void A_number_right_before_a_dotted_operator_is_just_the_number(string expression, bool expected)
    {
        using var table = DbfTable.Create(PathOf("e.dbf"), DbfTableFormat.DbaseIV,
            [DbfFieldDefinition.Numeric("QTY", 5), DbfFieldDefinition.Logical("OK")]);
        table.Append(1, true);
        Assert.Equal(expected, DbfExpression.Parse(expression).Evaluate(table.Read(0)));
    }

    // ---- FoxPro .cdx, read ----------------------------------------------------------------

    private DbfTable OpenKinds()
    {
        foreach (var file in new[] { "kinds.DBF", "kinds.FPT", "kinds.CDX" })
            File.Copy(Path.Combine(Fixtures.Root, "vfp9", file), PathOf(file));
        return DbfTable.Open(PathOf("kinds.DBF"));
    }

    [Fact]
    public void A_currency_tag_Visual_FoxPro_made_is_read_and_sought_by_its_values()
    {
        using var table = OpenKinds();
        using var cdx = table.OpenStructuralIndex()!;
        var tag = cdx["PRICE"];
        var records = table.ReadAll().ToList();

        // Every key is the record's own price, in order.
        var entries = tag.Entries().ToList();
        Assert.Equal(records.Count, entries.Count);
        Assert.All(entries, e => Assert.Equal(records[e.RecordNumber - 1]["PRICE"], e.Key));
        Assert.Equal(entries.Select(e => (decimal)e.Key!).Order(), entries.Select(e => (decimal)e.Key!));

        // And each record is found by it.
        foreach (var record in records)
            Assert.Contains(tag.SeekExact(record["PRICE"]), e => e.RecordNumber == record.RecordNumber);
        Assert.Empty(tag.SeekExact(123456.7891m));
    }

    [Fact]
    public void A_tag_on_a_nullable_field_is_read_and_sought_by_its_values_and_by_null()
    {
        using var table = OpenKinds();
        using var cdx = table.OpenStructuralIndex()!;
        var tag = cdx["OPT"];
        var records = table.ReadAll().ToList();
        var opt = table.Fields.ToList().FindIndex(f => f.Name == "OPT");

        var entries = tag.Entries().ToList();
        Assert.Equal(records.Count, entries.Count);
        Assert.All(entries, e =>
        {
            var record = records[e.RecordNumber - 1];
            Assert.Equal(record.IsNull(opt) ? null : record["OPT"], e.Key);
        });

        var nulls = records.Where(r => r.IsNull(opt)).Select(r => r.RecordNumber).ToList();
        Assert.NotEmpty(nulls);
        Assert.Equal(nulls, tag.SeekExact(null).Select(e => e.RecordNumber).Order());

        foreach (var record in records.Where(r => !r.IsNull(opt)))
        {
            var hits = tag.SeekExact(record["OPT"]).ToList();
            Assert.Contains(hits, e => e.RecordNumber == record.RecordNumber);
            // A prefix match, as a character seek is: "O4" also finds "O41".
            Assert.All(hits, e => Assert.StartsWith((string)record["OPT"]!, (string)e.Key!));
        }
    }

    // ---- .mdx rebuild ---------------------------------------------------------------------

    [Fact]
    public void Rebuilding_an_mdx_reuses_its_pages_instead_of_growing_the_file()
    {
        var path = PathOf("big.dbf");
        const int count = 1500;
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV,
                   [DbfFieldDefinition.Character("NAME", 20), DbfFieldDefinition.Numeric("QTY", 8)]))
        {
            table.FlushWritesToDisk = false;
            table.AppendRange(Enumerable.Range(0, count).Select(i => new object?[] { "Name" + (i * 7919 % count), i }));
        }

        var mdx = Path.ChangeExtension(path, ".mdx");
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            table.FlushWritesToDisk = false;
            table.CreateIndexTag("NAME", "NAME");
            table.CreateIndexTag("QTY", "QTY");
            var built = new FileInfo(mdx).Length;

            for (var i = 0; i < 3; i++)
            {
                table.RebuildIndex();
                Assert.Equal(built, new FileInfo(mdx).Length);
            }

            // Fewer keys: the pages left over go on the free list, and the file is no bigger.
            for (var i = 0; i < count; i += 2)
                table.Delete(i);
            Assert.Equal(count / 2, table.Pack());
            Assert.Equal(built, new FileInfo(mdx).Length);
            table.RebuildIndex();
            Assert.Equal(built, new FileInfo(mdx).Length);
        }

        // The rebuilt index is whole: every record is found by both tags, in order.
        using var reopened = DbfTable.Open(path, writable: true);
        Assert.Equal(count / 2, reopened.RecordCount);
        using var index = reopened.OpenProductionIndex()!;
        Assert.Equal(reopened.ReadAll().Select(r => (string)r["NAME"]!).Order(StringComparer.Ordinal),
            index["NAME"].Entries().Select(e => (string)e.Key!));
        Assert.Equal(Enumerable.Range(0, count).Where(i => i % 2 == 1).Select(i => (decimal)i),
            index["QTY"].Entries().Select(e => Convert.ToDecimal(e.Key, CultureInfo.InvariantCulture)));
        foreach (var record in reopened.ReadAll())
            Assert.Equal(record.RecordNumber, reopened.Seek(index["NAME"], record["NAME"]!)!.RecordNumber);
        reopened.Append("Name-new", 99999); // and it takes writes
        using var after = reopened.OpenProductionIndex()!;
        Assert.NotNull(reopened.Seek(after["NAME"], "Name-new"));
    }

    // ---- PACK -----------------------------------------------------------------------------

    private string PackTable()
    {
        var path = PathOf("pack.dbf");
        using var table = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 10)]);
        table.AppendRange(Enumerable.Range(0, 40).Select(i => new object?[] { "N" + i }));
        table.Delete(3);
        return path;
    }

    [Fact]
    public void A_pack_whose_safety_copy_fails_leaves_the_table_as_it_was_and_writable()
    {
        var path = PackTable();
        var before = File.ReadAllBytes(path);
        using (var failing = DbfTable.Open(path, writable: true, exclusive: true))
        {
            failing.CrashPoint = point => { if (point == "pack copy") throw new IOException("There is not enough space on the disk."); };
            Assert.Throws<IOException>(() => failing.Pack());
        }

        // No copy under the name that means "a pack didn't finish", and none left half made: that
        // name tells whoever finds it to put the copy in the table's place.
        Assert.False(File.Exists(path + ".packing"));
        Assert.False(File.Exists(path + ".packing.tmp"));
        Assert.Equal(before, File.ReadAllBytes(path));

        using var table = DbfTable.Open(path, writable: true, exclusive: true);
        table.Update(0, "still ok"); // writes aren't refused
        Assert.Equal(1, table.Pack()); // and the pack can be tried again
        Assert.Equal(39, table.RecordCount);
        Assert.False(File.Exists(path + ".packing"));
    }

    [Fact]
    public void A_half_made_copy_a_killed_pack_left_is_not_an_unfinished_pack()
    {
        var path = PackTable();
        File.WriteAllBytes(path + ".packing.tmp", [1, 2, 3]); // the process died while copying

        using var table = DbfTable.Open(path, writable: true, exclusive: true);
        table.Update(0, "fine");
        Assert.Equal(1, table.Pack());
        Assert.False(File.Exists(path + ".packing.tmp"));
    }

    // ---- a commit that fails --------------------------------------------------------------

    [Fact]
    public void An_append_whose_commit_fails_doesnt_leave_its_keys_for_the_next_record()
    {
        var path = PathOf("keys.dbf");
        using (var created = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 10)]))
        {
            created.Append("ONE");
            created.Append("TWO");
        }
        using (var exclusive = DbfTable.Open(path, writable: true, exclusive: true))
            exclusive.CreateIndexTag("NAME", "NAME");

        using (var table = DbfTable.Open(path, writable: true))
        {
            // The disk fills as the header is about to be written: the row was never counted.
            table.CrashPoint = point => { if (point == "committing") throw new IOException("There is not enough space on the disk."); };
            Assert.Throws<IOException>(() => table.Append("STALE"));
            table.CrashPoint = null;
            Assert.Equal(2, table.RecordCount);

            table.Append("FRESH"); // record 3, with its own key and no one else's
            Assert.Equal(3, table.RecordCount);
            using var index = table.OpenProductionIndex()!;
            Assert.Equal(["FRESH", "ONE", "TWO"], index["NAME"].Entries().Select(e => (string)e.Key!));
            Assert.Null(table.Seek(index["NAME"], "STALE"));
            Assert.Equal(3, table.Seek(index["NAME"], "FRESH")!.RecordNumber);
        }

        // In step: a writable open checks every tag's key count against the records.
        using var reopened = DbfTable.Open(path, writable: true);
        reopened.Append("LATER");
        Assert.Equal(4, reopened.RecordCount);
    }

    // ---- memo files -----------------------------------------------------------------------

    private (string Path, int PointerOffset) VfpMemoTable(string name)
    {
        var path = PathOf(name);
        using var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Character("NAME", 5), DbfFieldDefinition.Memo("NOTE")]);
        table.Append("a", "hello");
        return (path, table.Header.HeaderLength + table.Fields[1].Offset);
    }

    [Fact]
    public void A_FoxPro_memo_file_with_one_byte_blocks_is_read_and_written()
    {
        // SET BLOCKSIZE TO 0 in Visual FoxPro: a block is one byte, so a memo's block number is its offset.
        var (path, pointer) = VfpMemoTable("tiny.dbf");
        var fpt = new byte[512 + 8 + 5];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fpt, (uint)fpt.Length); // next free block
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(fpt.AsSpan(6), 1);     // block size
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fpt.AsSpan(512), 1);   // a text memo
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fpt.AsSpan(516), 5);
        "hello"u8.CopyTo(fpt.AsSpan(520));
        File.WriteAllBytes(Path.ChangeExtension(path, ".fpt"), fpt);
        var dbf = File.ReadAllBytes(path);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dbf.AsSpan(pointer), 512);
        File.WriteAllBytes(path, dbf);

        using (var table = DbfTable.Open(path, writable: true))
        {
            Assert.Equal("hello", table.Read(0)["NOTE"]);
            table.Append("b", "a second memo");
            Assert.Equal("a second memo", table.Read(1)["NOTE"]);
        }
        using var reopened = DbfTable.Open(path, writable: true, exclusive: true);
        Assert.Equal(["hello", "a second memo"], reopened.ReadAll().Select(r => (string)r["NOTE"]!));
        Assert.Equal(512 + 8 + 5 + 8 + 13, new FileInfo(Path.ChangeExtension(path, ".fpt")).Length);
    }

    [Fact]
    public void A_memo_header_naming_an_end_past_2_GB_is_not_padded_to()
    {
        var (path, _) = VfpMemoTable("wild.dbf");
        var memoPath = Path.ChangeExtension(path, ".fpt");
        var fpt = File.ReadAllBytes(memoPath);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fpt, 0x7FFF_FFFF); // garbage: 137 GB of 64-byte blocks
        File.WriteAllBytes(memoPath, fpt);

        // An exclusive writable open pads a short .fpt to the end its header names. Not to this one:
        // it used to ask for the whole gap as one array.
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            Assert.Equal("hello", table.Read(0)["NOTE"]);
            table.Append("b", "after");
            Assert.Equal("after", table.Read(1)["NOTE"]);
        }
        Assert.True(new FileInfo(memoPath).Length < 4096);
    }

    // ---- table names ----------------------------------------------------------------------

    [Fact]
    public void A_table_with_a_dot_in_its_name_opens_by_the_name_it_is_listed_under()
    {
        using (var table = DbfTable.Create(PathOf("SALES.2019.dbf"), DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 5)]))
            table.Append("dot");
        using (var table = DbfTable.Create(PathOf("PLAIN.dbf"), DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 5)]))
            table.Append("plain");

        using var database = DbfDatabase.Open(_dir.FullName);
        Assert.Equal(["PLAIN", "SALES.2019"], database.TableNames);
        foreach (var name in new[] { "SALES.2019", "sales.2019.DBF" })
        {
            using var table = database.OpenTable(name);
            Assert.Equal("dot", table.Read(0)["NAME"]);
        }
        Assert.All(database.Check().Tables, t => Assert.True(t.CanRead));

        // A companion file's name still gets the message that says what a table name is.
        var error = Assert.Throws<DbfException>(() => database.OpenTable("PLAIN.DBT"));
        Assert.Equal(DbfErrorCode.TableNotFound, error.Code);
        Assert.Contains("isn't a table name", error.Message);
        Assert.Equal(DbfErrorCode.TableNotFound, Assert.Throws<DbfException>(() => database.OpenTable("SALES.2020")).Code);

        // ADO.NET's schema lists and opens it too.
        using var connection = new DbfConnection($"Data Source={_dir.FullName}");
        connection.Open();
        var columns = connection.GetSchema("Columns");
        Assert.Contains(columns.Rows.Cast<System.Data.DataRow>(), r => (string)r["TABLE_NAME"] == "SALES.2019" && (string)r["COLUMN_NAME"] == "NAME");
    }

    // ---- locks, where a process's locks on a file go when any handle to it closes ----------

    [Fact]
    public void An_exclusive_open_that_fails_doesnt_drop_this_processes_locks_on_the_table()
    {
        if (!OperatingSystem.IsLinux())
            return; // Windows locks belong to handles; macOS has no byte-range locks through .NET

        var path = PathOf("held.dbf");
        using (var created = DbfTable.Create(path, [DbfFieldDefinition.Character("NAME", 10)]))
            created.Append("one");

        using var table = DbfTable.Open(path, writable: true);
        using var held = table.LockRecord(0);
        Assert.Equal("held", ProbeLock(path, DbfLocks.HeaderByte - 1));

        // The file is open here, so this fails. It used to fail inside the open, whose descriptor was
        // closed on the way out, and on Linux that close let go of the record lock above.
        var error = Assert.Throws<DbfException>(() => DbfTable.Open(path, writable: true, exclusive: true));
        Assert.Equal(DbfErrorCode.FileInUse, error.Code);
        Assert.Equal("held", ProbeLock(path, DbfLocks.HeaderByte - 1));

        // A second table on the same file, opened and closed, leaves it held too.
        DbfTable.Open(path).Dispose();
        Assert.Equal("held", ProbeLock(path, DbfLocks.HeaderByte - 1));
    }

    // Another process asks for one byte without waiting.
    private static string ProbeLock(string path, long offset)
    {
        var script = $"import fcntl,sys\nf=open(sys.argv[1],'r+b')\ntry:\n    fcntl.lockf(f,fcntl.LOCK_EX|fcntl.LOCK_NB,1,{offset})\n    print('free')\nexcept OSError:\n    print('held')";
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("python3", ["-I", "-c", script, path]) { RedirectStandardOutput = true })!;
        var answer = process.StandardOutput.ReadLine();
        process.WaitForExit();
        return answer ?? "no answer";
    }

    // ---- what a second review of the fixes above found -------------------------------------

    [Fact]
    public void A_currency_tag_keyed_as_a_number_the_way_Harbour_does_is_still_read_that_way()
    {
        // Harbour keys a currency field as an ordered double, Visual FoxPro as its ten-thousandths.
        // A tag over the numeric twin of the currency field, renamed onto the currency field, is
        // byte for byte what Harbour writes.
        var path = PathOf("hb.dbf");
        decimal[] prices = [1m, -2.5m, 0m, 1234.5678m, 99999.9999m, -0.0001m];
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro,
                   [DbfFieldDefinition.Currency("PRICE"), DbfFieldDefinition.Numeric("AMOUNT", 14, 4)]))
        {
            foreach (var price in prices)
                table.Append(price, price);
        }
        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true))
            table.CreateIndexTag("HB", "AMOUNT");
        var cdxPath = Path.ChangeExtension(path, ".cdx");
        var cdx = File.ReadAllBytes(cdxPath);
        var at = cdx.AsSpan().IndexOf("AMOUNT"u8);
        Assert.True(at > 0 && cdx.AsSpan(at + 1).IndexOf("AMOUNT"u8) < 0);
        "PRICE\0"u8.CopyTo(cdx.AsSpan(at));
        File.WriteAllBytes(cdxPath, cdx);

        using (var opened = DbfTable.Open(path))
        using (var index = opened.OpenStructuralIndex()!)
        {
            var tag = index["HB"];
            Assert.Equal("PRICE", tag.Expression);
            Assert.Equal(prices.Order(), tag.Entries().Select(e => Convert.ToDecimal(e.Key, CultureInfo.InvariantCulture)));
            foreach (var record in opened.ReadAll())
                Assert.Contains(tag.SeekExact(record["PRICE"]), e => e.RecordNumber == record.RecordNumber);
        }

        // And one Visual FoxPro's way, on the same field, is read its way.
        using (var table = DbfTable.Open(path, writable: true, maintainFoxProIndexes: true, exclusive: true))
            table.CreateIndexTag("VFP", "PRICE");
        using var again = DbfTable.Open(path);
        using var both = again.OpenStructuralIndex()!;
        Assert.Equal(prices.Order(), both["VFP"].Entries().Select(e => (decimal)e.Key!));
        Assert.Equal(prices.Order(), both["HB"].Entries().Select(e => Convert.ToDecimal(e.Key, CultureInfo.InvariantCulture)));
    }

    public sealed class Nullable
    {
        public string? Name { get; set; }
        public char Code { get; set; }
        public DateTime Seen { get; set; }
    }

    [Fact]
    public void A_blank_in_a_nullable_field_is_written_back_blank_and_not_as_NULL()
    {
        var path = PathOf("nul.dbf");
        using var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro,
        [
            DbfFieldDefinition.Character("NAME", 5),
            DbfFieldDefinition.Character("CODE", 1) with { Nullable = true },
            DbfFieldDefinition.Date("SEEN") with { Nullable = true },
        ]);
        table.Append("a", "", "");      // blank, not .NULL.: empty text is a blank date too
        table.Append("b", null, null);  // .NULL.
        Assert.False(table.Read(0).IsNull(1));
        Assert.False(table.Read(0).IsNull(2));
        Assert.Null(table.Read(0)["SEEN"]);
        Assert.True(table.Read(1).IsNull(1));

        var row = table.Read<Nullable>(0);
        row.Name = "c";
        table.UpdateRecord(0, row);
        Assert.Equal("c", table.Read(0)["NAME"]);
        Assert.False(table.Read(0).IsNull(1)); // still a blank
        Assert.False(table.Read(0).IsNull(2));
        Assert.Null(table.Read(0)["SEEN"]);
        Assert.Equal("", table.Read(0)["CODE"]);

        table.AppendRecord(new Nullable { Name = "d" });
        Assert.False(table.Read(2).IsNull(1));
        Assert.False(table.Read(2).IsNull(2));
    }

    private static (string Path, string Mdx) TwoTagTable(string path)
    {
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV,
                   [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Character("CITY", 10)]))
        {
            table.FlushWritesToDisk = false;
            table.AppendRange(Enumerable.Range(0, 300).Select(i => new object?[] { "N" + (i * 7 % 300), "C" + (i * 11 % 300) }));
        }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            table.CreateIndexTag("NAME", "NAME");
            table.CreateIndexTag("CITY", "CITY");
        }
        return (path, Path.ChangeExtension(path, ".mdx"));
    }

    private static void AssertBothTagsWhole(string path)
    {
        using var table = DbfTable.Open(path, writable: true);
        using var index = table.OpenProductionIndex()!;
        foreach (var name in new[] { "NAME", "CITY" })
        {
            Assert.Equal(table.ReadAll().Select(r => (string)r[name]!).Order(StringComparer.Ordinal), index[name].Entries().Select(e => (string)e.Key!));
            foreach (var record in table.ReadAll().Take(40))
                Assert.Equal(record[name], table.Read(index[name].SeekExact(record[name]).First().RecordNumber - 1)[name]);
        }
    }

    [Fact]
    public void Rebuilding_an_mdx_whose_tags_share_a_page_doesnt_build_one_tag_in_anothers_new_tree()
    {
        var (path, mdx) = TwoTagTable(PathOf("shared.dbf"));
        // Damage: the second tag's header is pointed at the first tag's root, so both trees reach it.
        var bytes = File.ReadAllBytes(mdx);
        int[] headers;
        using (var table = DbfTable.Open(path))
        using (var index = table.OpenProductionIndex()!)
            headers = [index["NAME"].HeaderPage, index["CITY"].HeaderPage];
        bytes.AsSpan(headers[0] * 512, 4).CopyTo(bytes.AsSpan(headers[1] * 512, 4));
        File.WriteAllBytes(mdx, bytes);

        using (var table = DbfTable.Open(path, writable: true, exclusive: true, allowUnmaintainedIndexes: true))
            table.RebuildIndex();
        AssertBothTagsWhole(path);
    }

    [Fact]
    public void Rebuilding_an_mdx_never_reuses_a_page_that_is_on_its_free_list()
    {
        var (path, mdx) = TwoTagTable(PathOf("freed.dbf"));
        // Damage, as a rebuild cut off between freeing a tree and naming its new root leaves it: a
        // tag's root is also the head of the free list.
        var bytes = File.ReadAllBytes(mdx);
        int header;
        using (var table = DbfTable.Open(path))
        using (var index = table.OpenProductionIndex()!)
            header = index["NAME"].HeaderPage;
        var root = BitConverter.ToInt32(bytes, header * 512);
        BitConverter.GetBytes(root).CopyTo(bytes, 36);
        File.WriteAllBytes(mdx, bytes);
        var length = bytes.Length;

        using (var table = DbfTable.Open(path, writable: true, exclusive: true, allowUnmaintainedIndexes: true))
            table.RebuildIndex();
        AssertBothTagsWhole(path);

        // Built in new pages: no tag's root is the page a program that reuses the list would hand out.
        var after = File.ReadAllBytes(mdx);
        Assert.True(after.Length > length);
        using var check = DbfTable.Open(path);
        using var rebuilt = check.OpenProductionIndex()!;
        Assert.All(new[] { "NAME", "CITY" }, name => Assert.NotEqual(root, BitConverter.ToInt32(after, rebuilt[name].HeaderPage * 512)));
    }

    [Fact]
    public void A_commit_that_fails_after_the_header_went_out_keeps_the_note_that_keys_are_owed()
    {
        var path = PathOf("owed.dbf");
        using (var created = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Character("NAME", 10)]))
            created.Append("ONE");
        using (var exclusive = DbfTable.Open(path, writable: true, exclusive: true))
            exclusive.CreateIndexTag("NAME", "NAME", unique: true);

        using (var table = DbfTable.Open(path, writable: true))
        {
            table.UseJournal = true;
            var failed = false;
            table.CrashPoint = point =>
            {
                if (point == "header committed" && !failed)
                {
                    failed = true;
                    throw new IOException("The flush after the header failed.");
                }
            };
            // AppendRange commits again on its way out of a failure. That second commit succeeds, with
            // the row counted and its key dropped, and used to clear the journal's note as if all were done.
            Assert.Throws<IOException>(() => table.AppendRange([["TWO"]], commitEvery: 1));
            table.CrashPoint = null;

            Assert.Equal(2, table.RecordCount); // counted
            Assert.True(DbfJournal.IsPending(path));
            Assert.Equal(DbfErrorCode.IndexDamaged, Assert.Throws<DbfException>(() => table.Append("THREE")).Code);
        }
    }
}
