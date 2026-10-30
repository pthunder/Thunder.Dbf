//    _____________
//   |  RECORDS   |     ProjectThunder :: Thunder.Dbf tests
//   |------------|     Write it, read it, change it, delete it,
//   | [x] append |     and make sure dBASE would still recognize it.
//   | [x] update |
//   | [x] delete |
//   |____________|

namespace Thunder.Dbf.Tests;

public class DbfTableTests
{
    private static readonly DbfFieldDefinition[] Employees =
    [
        DbfFieldDefinition.Character("NAME", 20),
        DbfFieldDefinition.Numeric("BALANCE", 10, 2),
        DbfFieldDefinition.Date("HIRED"),
        DbfFieldDefinition.Logical("ACTIVE"),
        DbfFieldDefinition.Numeric("QTY", 5),
    ];

    [Fact]
    public void Appended_records_read_back()
    {
        using var stream = new MemoryStream();
        using (var table = DbfTable.Create(stream, Employees, leaveOpen: true))
        {
            Assert.Equal(0, table.Append("Smith", 1234.5m, new DateOnly(1997, 4, 12), true, 3));
            Assert.Equal(1, table.Append("Jones", null, null, null, null));
        }

        using var reopened = DbfTable.Open(stream, leaveOpen: true);
        Assert.Equal(2, reopened.RecordCount);

        var smith = reopened.Read(0);
        Assert.Equal("Smith", smith["name"]);
        Assert.Equal(1234.50m, smith["BALANCE"]);
        Assert.Equal(new DateOnly(1997, 4, 12), smith["HIRED"]);
        Assert.Equal(true, smith["ACTIVE"]);
        Assert.Equal(3m, smith["QTY"]);
        Assert.False(smith.IsDeleted);
        Assert.Equal(1, smith.RecordNumber);

        Assert.Equal(["Jones", null, null, null, null], reopened.Read(1).Values);
    }

    [Fact]
    public void File_layout_matches_dBASE()
    {
        using var stream = new MemoryStream();
        using (var table = DbfTable.Create(stream, Employees, leaveOpen: true))
            table.Append("Smith", 1234.5m, new DateOnly(1997, 4, 12), true, 3);

        var bytes = stream.ToArray();
        var header = DbfHeader.Read(new MemoryStream(bytes));
        var record = System.Text.Encoding.ASCII.GetString(bytes, header.HeaderLength, header.RecordLength);

        Assert.Equal(0x03, header.Version);
        Assert.Equal(32 + 32 * 5 + 1, header.HeaderLength);
        Assert.Equal(" Smith                  1234.5019970412T    3", record);
        Assert.Equal(0x1A, bytes[^1]);
        Assert.Equal(header.HeaderLength + header.RecordLength + 1, bytes.Length);
    }

    [Fact]
    public void Update_replaces_values_and_keeps_deletion_flag()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, Employees);
        table.Append("Smith", 1m, null, true, 1);
        table.Delete(0);

        table.Update(0, "Smythe", 2m, new DateOnly(2001, 1, 2), false, 9);

        var record = table.Read(0);
        Assert.True(record.IsDeleted);
        Assert.Equal(["Smythe", 2m, new DateOnly(2001, 1, 2), false, 9m], record.Values);
    }

    [Fact]
    public void Delete_and_recall_toggle_the_flag()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, Employees);
        table.Append("Smith", null, null, null, null);

        table.Delete(0);
        Assert.True(table.Read(0).IsDeleted);

        table.Recall(0);
        Assert.False(table.Read(0).IsDeleted);
    }

    [Fact]
    public void Values_that_do_not_fit_are_rejected()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, Employees);

        Assert.Throws<ArgumentException>(() => table.Append(new string('x', 21), null, null, null, null));
        Assert.Throws<OverflowException>(() => table.Append("Big", 12345678.9m, null, null, null));
        Assert.Throws<ArgumentException>(() => table.Append("Short"));
        Assert.Equal(0, table.RecordCount);
    }

    [Fact]
    public void Reading_outside_the_table_throws()
    {
        using var stream = new MemoryStream();
        using var table = DbfTable.Create(stream, Employees);
        table.Append("Smith", null, null, null, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => table.Read(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Read(-1));
    }

    [Fact]
    public void Text_uses_the_language_driver_code_page()
    {
        using var stream = new MemoryStream();
        using (var table = DbfTable.Create(stream, [DbfFieldDefinition.Character("CITY", 10)], languageDriver: 0x01, leaveOpen: true))
            table.Append("Montréal");

        var bytes = stream.ToArray();
        var header = DbfHeader.Read(new MemoryStream(bytes));
        Assert.Equal(0x82, bytes[header.HeaderLength + 1 + 5]); // 'é' in code page 437

        using var reopened = DbfTable.Open(stream, leaveOpen: true);
        Assert.Equal("Montréal", reopened.Read(0)["CITY"]);
    }

    [Fact]
    public void Lenient_legacy_values_decode()
    {
        using var stream = new MemoryStream();
        using (var table = DbfTable.Create(stream, Employees, leaveOpen: true))
            table.Append("Smith", 1m, null, true, 1);

        // Hand-patch the record the way old apps sometimes left it.
        var bytes = stream.ToArray();
        var header = DbfHeader.Read(new MemoryStream(bytes));
        var at = header.HeaderLength;
        "**********"u8.CopyTo(bytes.AsSpan(at + 21));  // overflowed numeric
        "00000000"u8.CopyTo(bytes.AsSpan(at + 31));    // zero date
        bytes[at + 39] = (byte)'y';                    // lowercase Y logical

        using var table2 = DbfTable.Open(new MemoryStream(bytes));
        Assert.Equal(["Smith", null, null, true, 1m], table2.Read(0).Values);
    }

    [Fact]
    public void Read_only_tables_refuse_writes()
    {
        var bytes = CreateBytes();
        using var table = DbfTable.Open(new MemoryStream(bytes, writable: false));

        Assert.False(table.CanWrite);
        Assert.Equal(DbfErrorCode.ReadOnlyConnection, Assert.Throws<DbfException>(() => table.Append("Smith", null, null, null, null)).Code);
    }

    [Fact]
    public void Files_round_trip_on_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"thunder-{Guid.NewGuid():N}.dbf");
        try
        {
            using (var table = DbfTable.Create(path, Employees))
                table.Append("Smith", 1234.5m, new DateOnly(1997, 4, 12), true, 3);

            using (var table = DbfTable.Open(path, writable: true))
                table.Append("Jones", 10m, null, false, 0);

            using var readOnly = DbfTable.Open(path);
            Assert.Equal(["Smith", "Jones"], readOnly.ReadAll().Select(r => r["NAME"]));
            Assert.Equal(DateOnly.FromDateTime(DateTime.Today), readOnly.Header.LastUpdate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly DbfFieldDefinition[] WithNotes =
    [
        DbfFieldDefinition.Character("NAME", 20),
        DbfFieldDefinition.Memo("NOTES"),
    ];

    [Fact]
    public void Memo_tables_round_trip_text()
    {
        using var stream = new MemoryStream();
        using var memoStream = new MemoryStream();
        var longNote = string.Join("\r\n", Enumerable.Range(1, 100).Select(i => $"Call #{i}: customer wants a faster disk."));

        using (var table = DbfTable.Create(stream, WithNotes, leaveOpen: true, memoStream: memoStream, tableName: "people.dbf"))
        {
            Assert.Equal(0x8B, table.Header.Version);
            Assert.Equal(DbfMemoFormat.DbaseIV, table.MemoFormat);
            table.Append("Smith", "Likes dBASE.");
            table.Append("Jones", longNote);
            table.Append("Brown", null);
        }

        Assert.Equal("PEOPLE", System.Text.Encoding.ASCII.GetString(memoStream.ToArray(), 8, 6));

        using var reopened = DbfTable.Open(stream, leaveOpen: true, memoStream: memoStream);
        Assert.Equal(["Likes dBASE.", longNote, null], reopened.ReadAll().Select(r => r["NOTES"]));
    }

    [Fact]
    public void Updating_a_memo_writes_a_new_block_only_when_the_text_changes()
    {
        using var stream = new MemoryStream();
        using var memoStream = new MemoryStream();
        using var table = DbfTable.Create(stream, WithNotes, memoStream: memoStream);
        table.Append("Smith", "Original note.");
        var sizeAfterAppend = memoStream.Length;

        table.Update(0, "Smith", "Original note.");
        Assert.Equal(sizeAfterAppend, memoStream.Length);

        table.Update(0, "Smith", "Changed note.");
        Assert.True(memoStream.Length > sizeAfterAppend);
        Assert.Equal("Changed note.", table.Read(0)["NOTES"]);
    }

    [Fact]
    public void Memo_tables_need_a_memo_stream()
    {
        Assert.Throws<ArgumentException>(() => DbfTable.Create(new MemoryStream(), WithNotes));
    }

    [Fact]
    public void Memo_files_are_created_next_to_the_table_on_disk()
    {
        var dir = Directory.CreateTempSubdirectory("thunder-");
        try
        {
            var path = Path.Combine(dir.FullName, "people.dbf");
            using (var table = DbfTable.Create(path, WithNotes))
                table.Append("Smith", "On disk.");

            Assert.True(File.Exists(Path.Combine(dir.FullName, "people.dbt")));
            using var reopened = DbfTable.Open(path);
            Assert.Equal("On disk.", reopened.Read(0)["NOTES"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static byte[] CreateBytes()
    {
        using var stream = new MemoryStream();
        using (var table = DbfTable.Create(stream, Employees, leaveOpen: true))
            table.Append("Smith", null, null, null, null);
        return stream.ToArray();
    }
}
