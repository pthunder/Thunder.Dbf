//    _______   _______   _______   _______
//   | dBIII |  | dBIV  |  | FP 2  |  |  VFP  |     ProjectThunder :: Thunder.Dbf tests
//   |_______|  |_______|  |_______|  |_______|     Pick a dialect, any dialect:
//       \          \          /          /          the bytes had better match it.
//        '---------- CREATE TABLE ------'

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class CreateFormatTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-create-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string PathOf(string name) => Path.Combine(_dir.FullName, name);

    [Theory]
    [InlineData(DbfTableFormat.DbaseIII, 0x03, 0x83, ".dbt", 0x57)]
    [InlineData(DbfTableFormat.DbaseIV, 0x03, 0x8B, ".dbt", 0x57)]
    [InlineData(DbfTableFormat.FoxPro2, 0x03, 0xF5, ".fpt", 0x03)]
    [InlineData(DbfTableFormat.VisualFoxPro, 0x30, 0x30, ".fpt", 0x03)]
    public void Each_format_writes_its_own_version_byte_and_memo_file(DbfTableFormat format, byte plain, byte withMemo, string memoExtension, byte languageDriver)
    {
        var plainPath = PathOf("PLAIN.DBF");
        DbfTable.Create(plainPath, [DbfFieldDefinition.Character("NAME", 10)], format).Dispose();
        var bytes = SharedFile.ReadAllBytes(plainPath);
        Assert.Equal(plain, bytes[0]);
        Assert.Equal(languageDriver, bytes[29]);

        var memoPath = PathOf("NOTES.DBF");
        using (var table = DbfTable.Create(memoPath, [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Memo("NOTES")], format))
            table.Append("Ada", "A memo long enough to need a block of its own.");

        Assert.Equal(withMemo, SharedFile.ReadAllBytes(memoPath)[0]);
        Assert.True(File.Exists(Path.ChangeExtension(memoPath, memoExtension)));
        Assert.Single(_dir.GetFiles("NOTES.*"), f => !f.Name.EndsWith(".DBF", StringComparison.Ordinal));

        using var reopened = DbfTable.Open(memoPath);
        Assert.Equal("A memo long enough to need a block of its own.", reopened.Read(0)["NOTES"]);
    }

    [Fact]
    public void Visual_FoxPro_tables_hold_its_own_types_nulls_and_autoincrement()
    {
        var path = PathOf("PEOPLE.DBF");
        var hired = new DateTime(2026, 10, 31, 14, 30, 15, 250);
        using (var table = DbfTable.Create(path,
        [
            DbfFieldDefinition.Integer("ID") with { AutoIncrement = true, AutoIncrementNext = 100, AutoIncrementStep = 5 },
            DbfFieldDefinition.Character("NAME", 20),
            DbfFieldDefinition.Character("CODE", 5) with { Nullable = true },
            DbfFieldDefinition.Currency("RATE"),
            DbfFieldDefinition.Double("SCORE", 2),
            DbfFieldDefinition.DateTime("HIRED") with { Nullable = true },
            DbfFieldDefinition.Memo("NOTES"),
        ], DbfTableFormat.VisualFoxPro))
        {
            // Values cover the fields as created, plus the hidden _NullFlags field at the end.
            Assert.Equal("_NullFlags", table.Fields[^1].Name);
            Assert.True(table.Fields[^1].IsSystem);
            table.Append(null, "Ada", "AB1", 12.3456m, 98.25, hired, "First", null);
            table.Append(null, "Grace", null, 0m, 0.0, null, null, null);
        }

        var bytes = SharedFile.ReadAllBytes(path);
        Assert.Equal(0x31, bytes[0]);                                   // Visual FoxPro with autoincrement
        Assert.Equal(0x02, bytes[28]);                                  // has a memo file
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        Assert.Equal(32 + 32 * 8 + 1 + 263, headerLength);              // 7 fields + _NullFlags, terminator, backlink
        Assert.Equal(0x0D, bytes[32 + 32 * 8]);
        Assert.Equal(4, bytes[32 + 32 * 6 + 16]);                       // memo pointers are 4 binary bytes
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32 + 32 + 12)));   // NAME starts after the delete flag and the 4-byte ID
        Assert.Equal(0x0C, bytes[32 + 18]);                             // ID: autoincrement + binary, as VFP 9 writes it
        Assert.Equal(0x06, bytes[32 + 32 * 5 + 18]);                    // HIRED: nullable + binary
        Assert.Equal(0x05, bytes[32 + 32 * 7 + 18]);                    // _NullFlags: system + binary
        Assert.Equal(5, bytes[32 + 23]);                                // ID's step

        using var reopened = DbfTable.Open(path);
        Assert.True(reopened.Header.IsVisualFoxPro);
        Assert.Equal(110, reopened.Header.NextAutoIncrement(0));

        var ada = reopened.Read(0);
        Assert.Equal(100, ada["ID"]);
        Assert.Equal("Ada", ada["NAME"]);
        Assert.Equal("AB1", ada["CODE"]);
        Assert.Equal(12.3456m, ada["RATE"]);
        Assert.Equal(98.25, ada["SCORE"]);
        Assert.Equal(hired, ada["HIRED"]);
        Assert.Equal("First", ada["NOTES"]);

        var grace = reopened.Read(1);
        Assert.Equal(105, grace["ID"]);
        Assert.True(grace.IsNull(2));
        Assert.True(grace.IsNull(5));
        Assert.False(grace.IsNull(1));
    }

    [Fact]
    public void Tables_without_nullable_fields_have_no_null_flags_field()
    {
        var path = PathOf("PLAIN.DBF");
        using var table = DbfTable.Create(path, [DbfFieldDefinition.Integer("QTY"), DbfFieldDefinition.Character("NAME", 8)], DbfTableFormat.VisualFoxPro);
        Assert.Equal(2, table.Fields.Count);
        Assert.Equal(0x30, SharedFile.ReadAllBytes(path)[0]);
    }

    [Fact]
    public void Visual_FoxPro_types_and_flags_need_a_Visual_FoxPro_table()
    {
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([DbfFieldDefinition.Integer("QTY")], DbfTableFormat.DbaseIV));
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([DbfFieldDefinition.DateTime("AT")], DbfTableFormat.FoxPro2));
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([DbfFieldDefinition.Character("NAME", 5) with { Nullable = true }], DbfTableFormat.DbaseIV));
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([DbfFieldDefinition.Float("RATE", 8, 2)], DbfTableFormat.DbaseIII));
    }

    [Fact]
    public void Bad_Visual_FoxPro_definitions_are_refused()
    {
        var vfp = DbfTableFormat.VisualFoxPro;
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([DbfFieldDefinition.Character("NAME", 5) with { AutoIncrement = true }], vfp));
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([DbfFieldDefinition.Integer("ID") with { AutoIncrement = true, Nullable = true }], vfp));
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([DbfFieldDefinition.Integer("ID") with { AutoIncrement = true, AutoIncrementStep = 0 }], vfp));
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([DbfFieldDefinition.Integer("ID") with { Binary = true }], vfp));
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([new DbfFieldDefinition("RATE", 'Y', 8, 2)], vfp));
        Assert.Throws<ArgumentException>(() => DbfHeader.Create([new DbfFieldDefinition("NOTES", 'M', 6)], vfp));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbfHeader.Create([DbfFieldDefinition.Integer("ID")], (DbfTableFormat)42));
    }

    [Fact]
    public void The_old_overloads_still_make_dBASE_IV()
    {
        using var stream = new MemoryStream();
        using (DbfTable.Create(stream, [DbfFieldDefinition.Character("NAME", 5)], languageDriver: 0x01, leaveOpen: true)) { }
        Assert.Equal(0x03, stream.ToArray()[0]);
        Assert.Equal(0x01, stream.ToArray()[29]);
    }

    [Fact]
    public void A_FoxPro_memo_file_refuses_a_stray_fpt()
    {
        File.WriteAllBytes(PathOf("PEOPLE.FPT"), [1, 2, 3]);
        var error = Assert.Throws<DbfException>(() =>
            DbfTable.Create(PathOf("PEOPLE.DBF"), [DbfFieldDefinition.Memo("NOTES")], DbfTableFormat.VisualFoxPro));
        Assert.Equal(DbfErrorCode.TableAlreadyExists, error.Code);
        Assert.False(File.Exists(PathOf("PEOPLE.DBF")));
    }
}
