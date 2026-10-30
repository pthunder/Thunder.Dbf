//     _______
//    | o   o |     ProjectThunder :: Thunder.Dbf tests
//    |   ^   |     Every bug a second pair of eyes found, pinned
//    |  '-'  |     down so it can't sneak back in.
//    |_______|
//
// Each test reproduces a finding from the Codex review of 6a8d5aa, using the
// failing input Codex described wherever it gave one.

using System.Buffers.Binary;
using System.Text;

namespace Thunder.Dbf.Tests;

public class CodexReviewTests
{
    private static readonly string FixtureRoot = FindFixtureRoot();

    private static MemoryStream Copy(string fixture)
    {
        var stream = new MemoryStream();
        stream.Write(File.ReadAllBytes(Path.Combine(FixtureRoot, fixture)));
        return stream;
    }

    [Fact]
    public void Wide_character_fields_use_the_decimals_byte_as_high_length_byte()
    {
        using var table = DbfTable.Open(Path.Combine(FixtureRoot, "dbfdatareader/FolderRoot.dbf"));

        Assert.Equal(1040, table.Fields[0].Length);           // NAME: bytes 10 04
        Assert.Equal(1041, table.Fields[1].Offset);           // DISC_ID
        Assert.Equal(3542, table.Fields[^2].Offset);          // FOLD_RT_ID
        Assert.Equal(0, table.Fields[0].DecimalCount);
    }

    [Fact]
    public void Writes_are_refused_while_a_production_index_would_go_stale()
    {
        using var stream = Copy("dbfdatareader/client.dbf");
        using var memo = Copy("dbfdatareader/client.dbt");

        using (var table = DbfTable.Open(stream, leaveOpen: true, memoStream: memo))
        {
            Assert.True(table.Header.HasProductionMdx);
            var error = Assert.Throws<DbfException>(() => table.Delete(0));
            Assert.Equal(DbfErrorCode.IndexWouldGoStale, error.Code);
        }

        using var allowed = DbfTable.Open(stream, leaveOpen: true, memoStream: memo, allowUnmaintainedIndexes: true);
        allowed.Delete(0);
        Assert.True(allowed.Read(0).IsDeleted);
    }

    [Fact]
    public void Updates_keep_memo_pointers_when_the_memo_file_is_missing()
    {
        using var stream = Copy("dbfread/no_memofile.dbf");
        var memoField = 2;

        using var table = DbfTable.Open(stream, leaveOpen: true, ignoreMissingMemo: true);
        var field = table.Fields[memoField];
        var before = stream.ToArray().AsSpan(table.Header.HeaderLength + field.Offset, field.Length).ToArray();

        var values = table.Read(0).Values.ToArray();
        values[0] = "Renamed";
        table.Update(0, values);

        var after = stream.ToArray().AsSpan(table.Header.HeaderLength + field.Offset, field.Length).ToArray();
        Assert.Equal(before, after);
        Assert.NotEqual(0, BinaryPrimitives.ReadInt32LittleEndian(after));
        Assert.Equal("Renamed", table.Read(0)["NAME"]);
    }

    [Fact]
    public void Visual_FoxPro_null_flags_are_read_and_written()
    {
        using var stream = Copy("infused-dbf/dbase_31.dbf");
        using var table = DbfTable.Open(stream, leaveOpen: true, allowUnmaintainedIndexes: true);
        var nullFlags = table.Fields.Single(f => f.Type == '0');

        // Bit 0 belongs to SUPPLIERID, the first nullable field.
        var bytes = stream.GetBuffer();
        bytes[table.Header.HeaderLength + nullFlags.Offset] |= 0x01;
        Assert.Null(table.Read(0)["SUPPLIERID"]);

        // Writing null to CATEGORYID (bit 1) sets its bit; writing a value clears it again.
        var values = table.Read(0).Values.ToArray();
        values[3] = null;
        table.Update(0, values);
        Assert.Null(table.Read(0)["CATEGORYID"]);
        Assert.Equal(0x03, stream.ToArray()[table.Header.HeaderLength + nullFlags.Offset] & 0x03);

        values[3] = 7;
        table.Update(0, values);
        Assert.Equal(7, table.Read(0)["CATEGORYID"]);
        Assert.Equal(0x01, stream.ToArray()[table.Header.HeaderLength + nullFlags.Offset] & 0x03);
    }

    [Fact]
    public void Autoincrement_fields_take_and_advance_the_counter()
    {
        using var stream = Copy("infused-dbf/dbase_31.dbf");
        object?[] template;
        using (var table = DbfTable.Open(stream, leaveOpen: true, allowUnmaintainedIndexes: true))
        {
            Assert.True(table.Fields[0].IsAutoIncrement);
            Assert.Equal(78, table.Header.NextAutoIncrement(0));

            template = table.Read(0).Values.ToArray();
            template[0] = null;
            var index = table.Append(template);
            Assert.Equal(78, table.Read(index)["PRODUCTID"]);

            template[0] = 100;
            table.Append(template);
        }

        // The counter is saved in the descriptor, so a reopened table (or VFP) continues from 101.
        using var reopened = DbfTable.Open(stream, leaveOpen: true);
        Assert.Equal(101, reopened.Header.NextAutoIncrement(0));
    }

    [Fact]
    public void Create_rejects_layouts_that_overflow_dBASE_IV_limits()
    {
        var tooManyFields = Enumerable.Range(0, 256).Select(i => DbfFieldDefinition.Character($"F{i}", 1)).ToArray();
        var tooWide = Enumerable.Range(0, 17).Select(i => DbfFieldDefinition.Character($"F{i}", 254)).ToArray();

        Assert.Throws<ArgumentException>(() => DbfTable.Create(new MemoryStream(), tooManyFields));
        Assert.Throws<ArgumentException>(() => DbfTable.Create(new MemoryStream(), tooWide));
    }

    [Fact]
    public void A_corrupt_next_free_pointer_does_not_send_writes_into_the_far_distance()
    {
        using var stream = new MemoryStream();
        using var memo = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Memo("NOTES")], memoStream: memo);

        memo.Position = 0;
        memo.Write([0xFF, 0xFF, 0xFF, 0x7F]);
        table.Append("Smith", "Still lands at the end of the file.");

        Assert.True(memo.Length <= 1024, $"Memo file grew to {memo.Length:N0} bytes.");
        Assert.Equal("Still lands at the end of the file.", table.Read(0)["NOTES"]);
    }

    [Fact]
    public void dBASE_IV_free_chains_survive_appends()
    {
        using var stream = new MemoryStream();
        using var memo = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Memo("NOTES")], memoStream: memo);
        table.Append("A", "one");
        table.Append("B", "two");
        table.Append("C", "three"); // blocks 1, 2, 3; file ends at block 4

        // Pretend block 2 was freed: header -> 2, block 2 -> [next = 4 (end of file), count = 1].
        var bytes = memo.GetBuffer();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1024), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1028), 1);

        table.Append("D", "four");

        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(memo.ToArray().AsSpan(0)));    // chain head kept
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(memo.ToArray().AsSpan(1024))); // last link moved past the new memo
        Assert.Equal("four", table.Read(3)["NOTES"]);
    }

    [Fact]
    public void Datetimes_keep_their_day_and_come_back_unspecified()
    {
        using var stream = Copy("infused-dbf/dbase_30.dbf");
        using var memo = Copy("infused-dbf/dbase_30.fpt");
        using var table = DbfTable.Open(stream, leaveOpen: true, memoStream: memo, allowUnmaintainedIndexes: true);

        var values = table.Read(0).Values.ToArray();
        var updated = table.Fields.Select((f, i) => (f, i)).Single(x => x.f.Name == "UPDATED").i;
        values[updated] = new DateTime(2026, 10, 30, 23, 59, 59, 999).AddTicks(9_999);
        table.Update(0, values);

        var read = (DateTime)table.Read(0)["UPDATED"]!;
        Assert.Equal(new DateTime(2026, 10, 30, 23, 59, 59, 999), read);
        Assert.Equal(DateTimeKind.Unspecified, read.Kind);
    }

    [Fact]
    public void Scientific_floats_beyond_decimal_range_read_as_double()
    {
        using var stream = Copy("infused-dbf/dbase_8b.dbf");
        using var memo = Copy("infused-dbf/dbase_8b.dbt");
        using var table = DbfTable.Open(stream, leaveOpen: true, memoStream: memo);
        var floatField = table.Fields.Single(f => f.Name == "FLOAT");

        var target = stream.GetBuffer().AsSpan(table.Header.HeaderLength + floatField.Offset, floatField.Length);
        target.Fill((byte)' ');
        "1E+100"u8.CopyTo(target[^6..]);

        Assert.Equal(1e100, table.Read(0)["FLOAT"]);
    }

    [Fact]
    public void Updates_leave_unchanged_fields_alone_even_when_they_would_not_re_encode()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Numeric("BIG", 6), DbfFieldDefinition.Character("NAME", 5)]);
        table.Append(1, "a");
        var big = table.Fields[0];
        " 1E+10"u8.CopyTo(stream.GetBuffer().AsSpan(table.Header.HeaderLength + big.Offset, big.Length));

        table.Update(0, 10_000_000_000m, "b");

        Assert.Equal(" 1E+10", Encoding.ASCII.GetString(stream.ToArray(), table.Header.HeaderLength + big.Offset, big.Length));
        Assert.Equal("b", table.Read(0)["NAME"]);
    }

    [Fact]
    public void Failed_appends_do_not_touch_the_memo_file()
    {
        using var stream = new MemoryStream();
        using var memo = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Character("C", 1), DbfFieldDefinition.Memo("NOTES")], memoStream: memo);
        var before = memo.ToArray();

        Assert.Throws<ArgumentException>(() => table.Append("too long", "new memo"));

        Assert.Equal(before, memo.ToArray());
        Assert.Equal(0, table.RecordCount);
    }

    [Fact]
    public void Memo_pointers_outside_the_readable_range_are_rejected()
    {
        using var stream = new MemoryStream();
        using var memo = new MemoryStream();
        using var table = DbfTable.Create(stream, [DbfFieldDefinition.Memo("NOTES")], memoStream: memo);

        Assert.Throws<ArgumentOutOfRangeException>(() => table.Append(3_000_000_000L));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Append(-1));
    }

    [Fact]
    public void Failed_create_leaves_no_half_made_table_behind()
    {
        var dir = Directory.CreateTempSubdirectory("thunder-");
        try
        {
            var path = Path.Combine(dir.FullName, "people.dbf");
            File.WriteAllText(Path.Combine(dir.FullName, "people.dbt"), "someone else's memos");

            var error = Assert.Throws<DbfException>(() => DbfTable.Create(path, [DbfFieldDefinition.Memo("NOTES")]));
            Assert.Equal(DbfErrorCode.TableAlreadyExists, error.Code);
            Assert.False(File.Exists(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0x0C, 850)]
    [InlineData(0x96, 10007)]
    [InlineData(0x97, 10029)]
    [InlineData(0x98, 10006)]
    public void Language_drivers_Codex_found_missing_now_map(byte driver, int codePage)
    {
        Assert.Equal(codePage, DbfEncoding.CodePageFor(driver));
    }

    private static string FindFixtureRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find tests/fixtures above the test output folder.");
    }
}
