//   fixture --flip a byte--> damaged copy --open, read, seek, write--> an explained DbfException
//
//   ProjectThunder :: Thunder.Dbf tests
//   Damage found by fuzzing the readers and writers with corrupted copies of the fixtures (100,000
//   cases): each one used to escape as a raw .NET exception. Every damaged file must give an
//   explained error instead, and none may hang, crash or allocate without bound.

using System.Buffers.Binary;
using static Thunder.Dbf.Tests.IndexTestSupport;

namespace Thunder.Dbf.Tests;

public sealed class FuzzRegressionTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-fuzz-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Copy(string folder, string name)
    {
        foreach (var file in Directory.EnumerateFiles(Fixture(folder)).Where(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase)))
            File.Copy(file, Path.Combine(_dir.FullName, Path.GetFileName(file)));
        return Directory.EnumerateFiles(_dir.FullName).First(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(f).Equals(".dbf", StringComparison.OrdinalIgnoreCase));
    }

    private static void Patch(string path, Action<byte[]> change)
    {
        var bytes = File.ReadAllBytes(path);
        change(bytes);
        File.WriteAllBytes(path, bytes);
    }

    // A copy cut short: the header counts more records than the file holds.
    [Fact]
    public void A_table_cut_short_is_reported_when_reading_reaches_the_end()
    {
        var path = Copy("infused-dbf", "dbase_03");
        using (var stream = File.OpenWrite(path))
            stream.SetLength(stream.Length / 2);
        using var table = DbfTable.Open(path);
        var error = Assert.Throws<DbfException>(() => table.ReadAll().ToList());
        Assert.Equal(DbfErrorCode.NotADbfFile, error.Code);
        Assert.Contains("ends before record", error.Message);
    }

    // A record count with the top bit set: more records than any table can hold.
    [Fact]
    public void A_record_count_past_what_a_table_can_hold_is_reported_not_overflowed()
    {
        var path = Copy("infused-dbf", "dbase_03");
        Patch(path, bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0xFFFF_FFF0));
        using var table = DbfTable.Open(path);
        Assert.Equal(DbfErrorCode.NotADbfFile, Assert.Throws<DbfException>(() => table.RecordCount).Code);
        Assert.Equal(DbfErrorCode.NotADbfFile, Assert.Throws<DbfException>(() => table.ReadAll().First()).Code);
        using var db = DbfDatabase.Open(_dir.FullName);
        var report = db.Check().Tables.Single(t => t.Name.Equals("dbase_03", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Problems, p => p.Code == DbfErrorCode.NotADbfFile); // reported, not thrown
    }

    // An index whose entries point past the table's end (the table was cut back, the index wasn't).
    [Fact]
    public void A_seek_that_lands_past_the_table_is_reported_as_a_damaged_index()
    {
        var path = Copy("infused-dbf/foxprodb", "calls");
        Patch(path, bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2));
        using var table = DbfTable.Open(path);
        using var index = table.OpenStructuralIndex()!;
        IDbfIndexTag tag = index["CALL_ID"];
        var last = tag.Entries().Last();
        var error = Assert.Throws<DbfException>(() => table.Seek(tag, last.Key));
        Assert.Equal(DbfErrorCode.IndexDamaged, error.Code);
        Assert.Equal(DbfErrorCode.IndexDamaged, Assert.Throws<DbfException>(() => table.ReadInOrder(tag).ToList()).Code);
    }

    // An .ndx whose key expression lost its closing parenthesis: the index is damaged, not the caller's code.
    [Fact]
    public void A_key_expression_that_doesnt_parse_is_reported_as_a_damaged_index_on_write()
    {
        var path = Copy("retroinv", "NUM2000");
        Patch(Path.Combine(_dir.FullName, "NUM2000.NDX"), bytes =>
        {
            Assert.Equal("STR(ID,8)"u8.ToArray(), bytes.AsSpan(24, 9).ToArray());
            bytes[32] = (byte)' ';
        });
        using var table = DbfTable.Open(path, writable: true);
        var error = Assert.Throws<DbfException>(() => table.Append(table.Read(0).Values.ToArray()));
        Assert.Equal(DbfErrorCode.IndexDamaged, error.Code);
        Assert.Contains("key expression", error.Message);
    }

    // Visual FoxPro numbers only Integer fields; the autoincrement flag on a date is damage, and ignored.
    [Fact]
    public void An_autoincrement_flag_on_a_date_field_is_ignored()
    {
        var path = Copy("vfp9", "kinds");
        File.Delete(Path.Combine(_dir.FullName, "kinds.CDX")); // the table on its own: this is about its fields
        Patch(path, bytes =>
        {
            bytes[28] &= unchecked((byte)~0x01);  // no structural .cdx
            bytes[32 + 32 * 5 + 18] |= 0x08;      // the autoincrement flag on field 6, WHEN D
        });
        using var table = DbfTable.Open(path, writable: true);
        Assert.False(table.Fields.Single(f => f.Name == "WHEN").IsAutoIncrement);
        var values = table.Read(0).Values.ToArray();
        table.Append(values);
        Assert.Equal(values[5], table.Read(table.RecordCount - 1)["WHEN"]);
    }

    // A numeric .mdx key whose digits aren't BCD.
    [Fact]
    public void A_numeric_mdx_key_with_digits_that_arent_bcd_is_damage()
    {
        byte[] key = [0x35, 0x10, 0xAB, 0xCD, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<InvalidDataException>(() => MdxKeys.DecodeNumeric(key));
    }
}
