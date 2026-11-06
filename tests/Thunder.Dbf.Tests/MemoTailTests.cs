//      [ header: next free 643 ][ ... block 640 ][ end at 641 ]      ProjectThunder :: Thunder.Dbf tests
//                                                 ^ 2 blocks short   A power cut kept the memo header and
//                                                                    lost the tail: report it, pad it back.

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class MemoTailTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-memo-tail-");

    public void Dispose() => _dir.Delete(recursive: true);

    // The memo file beside the table, whatever case its extension was written in (Linux tells them apart).
    private static string MemoFile(string table, string extension) =>
        Directory.GetFiles(Path.GetDirectoryName(table)!).Single(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData(DbfTableFormat.VisualFoxPro, ".FPT", 64, true)]
    public void A_memo_file_shorter_than_its_header_says_is_reported_and_padded_back(DbfTableFormat format, string extension, int blockSize, bool bigEndian)
    {
        var path = Path.Combine(_dir.FullName, "CALLS.DBF");
        using (var table = DbfTable.Create(path, format, [DbfFieldDefinition.Numeric("ID", 6), DbfFieldDefinition.Memo("NOTES")]))
        {
            for (var i = 1; i <= 20; i++)
                table.Append(i, $"call {i}: " + new string('x', 40 * i));
        }

        // The header's next free block moves two blocks on, and the blocks never reach the disk.
        var memoPath = MemoFile(path, extension);
        var bytes = File.ReadAllBytes(memoPath);
        var next = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        Assert.Equal(bytes.Length, (long)next * blockSize);
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(bytes, next + 2);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, next + 2);
        File.WriteAllBytes(memoPath, bytes);

        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged && p.Message.Contains($"{2 * blockSize:N0} bytes shorter", StringComparison.Ordinal));

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            Assert.Equal((next + 2L) * blockSize, new FileInfo(memoPath).Length); // padded back on open
            Assert.Equal("call 20: " + new string('x', 800), table.Read(19)["NOTES"]); // every memo where it was
            table.Append(21, "after the repair");
        }

        using (var reopened = DbfTable.Open(path))
        {
            Assert.Equal("after the repair", reopened.Read(20)["NOTES"]);
            Assert.Equal("call 1: " + new string('x', 40), reopened.Read(0)["NOTES"]);
        }
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Empty(db.Check().Problems);
    }

    [Theory]
    [InlineData(DbfTableFormat.DbaseIII)]
    [InlineData(DbfTableFormat.DbaseIV)]
    public void A_dbt_whose_last_block_is_short_like_Jet_writes_is_fine(DbfTableFormat format)
    {
        // Jet doesn't pad a .dbt's last block, so the file ends before the header's next free block.
        // dBASE and Jet read it as it is: no problem to report, nothing to pad.
        var path = Path.Combine(_dir.FullName, "JET.DBF");
        using (var table = DbfTable.Create(path, format, [DbfFieldDefinition.Memo("NOTES")]))
        {
            table.Append("first");
            table.Append(new string('j', 700));
        }
        var memoPath = MemoFile(path, ".DBT");
        var bytes = File.ReadAllBytes(memoPath);
        var end = bytes.Length - 1;
        while (bytes[end] == 0)
            end--;
        File.WriteAllBytes(memoPath, bytes[..(end + 1)]); // just past the last memo's end marker
        var shortLength = end + 1L;
        Assert.True(shortLength < bytes.Length);

        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.DoesNotContain(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged);
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            Assert.Equal(shortLength, new FileInfo(memoPath).Length); // not padded
            table.Append("after Jet");
        }
        using (var reopened = DbfTable.Open(path))
        {
            Assert.Equal(new string('j', 700), reopened.Read(1)["NOTES"]);
            Assert.Equal("after Jet", reopened.Read(2)["NOTES"]);
        }
    }

    [Fact]
    public void A_memo_that_lost_its_end_is_reported_and_not_padded_over()
    {
        // The cut took part of a memo a record names, not only free blocks: padding would make it read
        // back with zeros where its text was, so the file stays as it is and Check() says restore it.
        var path = Path.Combine(_dir.FullName, "CALLS.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Memo("NOTES")]))
        {
            table.Append("short");
            table.Append(new string('y', 300));
        }
        var memoPath = MemoFile(path, ".FPT");
        var bytes = File.ReadAllBytes(memoPath);
        File.WriteAllBytes(memoPath, bytes[..^100]); // the header still names the old end

        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged && p.Hint!.Contains("backup", StringComparison.Ordinal));
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            Assert.Equal(bytes.Length - 100, new FileInfo(memoPath).Length); // not padded
            Assert.Equal("short", table.Read(0)["NOTES"]);
            Assert.ThrowsAny<Exception>(() => table.Read(1)["NOTES"]); // still reported, never read as zeros
            // A new memo would land where the cut one's end was and make it read as whole: no writes.
            Assert.Equal(DbfErrorCode.MemoFileDamaged, Assert.Throws<DbfException>(() => table.Append(new string('z', 300))).Code);
        }
        using (var shared = DbfTable.Open(path, writable: true))
            Assert.Equal(DbfErrorCode.MemoFileDamaged, Assert.Throws<DbfException>(() => shared.Append("shared")).Code);
        Assert.Equal(bytes.Length - 100, new FileInfo(memoPath).Length); // nothing written over it
    }

    [Fact]
    public void A_table_opened_from_streams_refuses_writes_over_a_cut_memo()
    {
        var path = Path.Combine(_dir.FullName, "CALLS.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Memo("NOTES")]))
        {
            table.Append("short");
            table.Append(new string('y', 300));
        }
        var memoPath = MemoFile(path, ".FPT");
        var cut = File.ReadAllBytes(memoPath)[..^100];
        File.WriteAllBytes(memoPath, cut);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        using var memo = new FileStream(memoPath, FileMode.Open, FileAccess.ReadWrite);
        using (var table = DbfTable.Open(stream, leaveOpen: true, memoStream: memo))
            Assert.Equal(DbfErrorCode.MemoFileDamaged, Assert.Throws<DbfException>(() => table.Append("new")).Code);
        Assert.Equal(cut.Length, memo.Length); // nothing written into the lost memo's space
    }

    [Fact]
    public void A_memo_file_cut_below_its_header_while_records_name_memos_takes_no_writes()
    {
        // Records name memos in blocks the file no longer reaches, though it's shorter than its own
        // header (which a file with no memo yet may be).
        var path = Path.Combine(_dir.FullName, "CALLS.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Memo("NOTES")]))
            table.Append("a memo in block 8");
        var memoPath = MemoFile(path, ".FPT");
        File.WriteAllBytes(memoPath, File.ReadAllBytes(memoPath)[..24]);

        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged);
        using (var table = DbfTable.Open(path, writable: true))
            Assert.Equal(DbfErrorCode.MemoFileDamaged, Assert.Throws<DbfException>(() => table.Append("would reuse block 8")).Code);
        Assert.Equal(24, new FileInfo(memoPath).Length);
    }

    [Fact]
    public void A_dBASE_III_memo_cut_mid_text_counts_as_cut()
    {
        // A dBASE III memo has no length, only an end marker: cut through, it reads as shorter text.
        var path = Path.Combine(_dir.FullName, "OLD.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIII, [DbfFieldDefinition.Memo("NOTES")]))
            table.Append(new string('m', 800));
        var memoPath = MemoFile(path, ".DBT");
        var bytes = File.ReadAllBytes(memoPath);
        Assert.Equal(1_536, bytes.Length);
        File.WriteAllBytes(memoPath, bytes[..1_024]);

        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged && p.Hint!.Contains("backup", StringComparison.Ordinal));
        using (var shared = DbfTable.Open(path, writable: true))
            Assert.Equal(DbfErrorCode.MemoFileDamaged, Assert.Throws<DbfException>(() => shared.Append("NEW")).Code);
        using (DbfTable.Open(path, writable: true, exclusive: true)) { }
        Assert.Equal(1_024, new FileInfo(memoPath).Length); // not padded over
    }

    [Fact]
    public void A_cut_dBASE_III_memo_isnt_hidden_by_a_whole_one_in_the_same_record()
    {
        // A comes after B in the file (it was rewritten later); the cut takes A's end, not B's.
        var path = Path.Combine(_dir.FullName, "TWO.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIII, [DbfFieldDefinition.Memo("A"), DbfFieldDefinition.Memo("B")]))
        {
            table.Append("a", "b");
            table.UpdateFields(0, new Dictionary<string, object?> { ["A"] = new string('m', 800) });
        }
        var memoPath = MemoFile(path, ".DBT");
        var bytes = File.ReadAllBytes(memoPath);
        File.WriteAllBytes(memoPath, bytes[..^512]); // through A's text

        using (var shared = DbfTable.Open(path, writable: true))
            Assert.Equal(DbfErrorCode.MemoFileDamaged, Assert.Throws<DbfException>(() => shared.Append("x", "y")).Code);
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged && p.Hint!.Contains("backup", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dBASE_IV_free_chain_cut_with_its_last_memo_is_caught()
    {
        // The header names free block 2, whose link would name the end; the cut takes block 2 and the
        // memo in block 3 a record names, so the header seems to point exactly at the file's end.
        var path = Path.Combine(_dir.FullName, "IV.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Memo("NOTES")]))
        {
            table.Append("one");
            table.Append("two");
            table.Append("three");
        }
        var memoPath = MemoFile(path, ".DBT");
        var bytes = File.ReadAllBytes(memoPath);
        Assert.Equal(4 * 512, bytes.Length); // the header, then blocks 1, 2 and 3
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2); // block 2 freed: the chain starts there
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2 * 512), 4); // and links on to the end
        using (var table = DbfTable.Open(path, writable: true, allowUnmaintainedIndexes: true))
            table.UpdateFields(1, new Dictionary<string, object?> { ["NOTES"] = null });
        File.WriteAllBytes(memoPath, bytes[..1_024]); // the cut: blocks 2 and 3 are gone

        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged);
        using (var table = DbfTable.Open(path, writable: true))
            Assert.Equal(DbfErrorCode.MemoFileDamaged, Assert.Throws<DbfException>(() => table.Append("would land in block 2 and 3")).Code);
        Assert.Equal(1_024, new FileInfo(memoPath).Length);
    }

    [Theory]
    [InlineData(DbfTableFormat.VisualFoxPro, ".FPT")]
    [InlineData(DbfTableFormat.FoxPro2, ".FPT")]
    [InlineData(DbfTableFormat.DbaseIII, ".DBT")]
    [InlineData(DbfTableFormat.DbaseIV, ".DBT")]
    public void A_cut_memo_is_caught_when_the_header_lost_its_new_end_too(DbfTableFormat format, string extension)
    {
        // The cut kept the record's pointer and the memo's first blocks, and lost both the memo's tail
        // and the header's new next-free block: the old header names an end inside the file.
        var path = Path.Combine(_dir.FullName, "LOST.DBF");
        using (var table = DbfTable.Create(path, format, [DbfFieldDefinition.Memo("NOTES")]))
            table.Append("short");
        var memoPath = MemoFile(path, extension);
        var before = File.ReadAllBytes(memoPath)[..4]; // the header's next free block before the long memo
        var oldEnd = File.ReadAllBytes(memoPath).Length;
        using (var table = DbfTable.Open(path, writable: true))
            table.Append(new string('z', 2_000));
        var bytes = File.ReadAllBytes(memoPath);
        before.CopyTo(bytes, 0);
        File.WriteAllBytes(memoPath, bytes[..(oldEnd + 512)]); // the long memo's first 512 bytes stay

        using (var db = DbfDatabase.Open(_dir.FullName))
        {
            var problems = db.Check().Problems;
            Assert.Contains(problems, p => p.Code == DbfErrorCode.MemoFileDamaged && p.Hint!.Contains("backup", StringComparison.Ordinal));
            // dBASE IV: the rolled-back header points at the cut memo's own block, not at a free one.
            Assert.DoesNotContain(problems, p => p.Message.Contains("bytes shorter", StringComparison.Ordinal));
        }
        using (var shared = DbfTable.Open(path, writable: true))
            Assert.Equal(DbfErrorCode.MemoFileDamaged, Assert.Throws<DbfException>(() => shared.Append("NEW")).Code);
        Assert.Equal(oldEnd + 512, new FileInfo(memoPath).Length); // nothing written into the lost tail
    }

    [Fact]
    public void A_null_memo_with_a_stale_pointer_doesnt_block_writes()
    {
        // .NULL. wins over whatever the pointer bytes say: a stale block past the end isn't a cut memo.
        var path = Path.Combine(_dir.FullName, "NULLS.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Memo("NOTES") with { Nullable = true }]))
        {
            table.Append("kept");
            table.Append([null]);
        }
        var bytes = File.ReadAllBytes(path);
        int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        int recordLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(headerLength + recordLength + 1), 99_999);
        File.WriteAllBytes(path, bytes);

        using (var table = DbfTable.Open(path, writable: true))
        {
            table.Append("more");
            Assert.Null(table.Read(1)["NOTES"]);
            Assert.Equal("kept", table.Read(0)["NOTES"]);
        }
    }

    [Fact]
    public void A_dBASE_IV_header_rolled_back_onto_a_whole_memo_isnt_written_over()
    {
        // The cut lost only the header's new end: it names the block where the last memo, whole, starts.
        // The next append's chain walk must not take that memo's FF FF 08 00 for a link and overwrite it.
        var path = Path.Combine(_dir.FullName, "IVBACK.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.DbaseIV, [DbfFieldDefinition.Memo("NOTES")]))
            table.Append("one");
        var memoPath = MemoFile(path, ".DBT");
        var before = File.ReadAllBytes(memoPath)[..4];
        using (var table = DbfTable.Open(path, writable: true))
            table.Append("two, kept whole");
        var bytes = File.ReadAllBytes(memoPath);
        before.CopyTo(bytes, 0);
        File.WriteAllBytes(memoPath, bytes);

        using (var table = DbfTable.Open(path, writable: true))
            table.Append("three");
        using (var reopened = DbfTable.Open(path))
        {
            Assert.Equal("one", reopened.Read(0)["NOTES"]);
            Assert.Equal("two, kept whole", reopened.Read(1)["NOTES"]);
            Assert.Equal("three", reopened.Read(2)["NOTES"]);
        }
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.DoesNotContain(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged);
    }

    [Fact]
    public void A_whole_memo_past_the_read_limit_doesnt_block_writes()
    {
        // Visual FoxPro stores memos past Thunder.Dbf's 64 MiB read limit; a whole one isn't a cut one.
        var path = Path.Combine(_dir.FullName, "BIGMEMO.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Memo("NOTES")]))
            table.Append("attachment");
        var memoPath = MemoFile(path, ".FPT");
        const int huge = 65 * 1024 * 1024;
        using (var memo = new FileStream(memoPath, FileMode.Open, FileAccess.ReadWrite))
        {
            var start = memo.Length - 64; // the one memo, in the last 64-byte block
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, huge);
            memo.Position = start + 4;
            memo.Write(length);
            var end = (start + 8 + huge + 63) / 64 * 64;
            memo.SetLength(end);
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)(end / 64));
            memo.Position = 0;
            memo.Write(length);
        }

        using (var table = DbfTable.Open(path, writable: true))
            table.Append("small");
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.DoesNotContain(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged);
    }

    [Fact]
    public void A_shared_open_leaves_a_short_memo_file_to_the_exclusive_one()
    {
        var path = Path.Combine(_dir.FullName, "CALLS.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Memo("NOTES")]))
            table.Append("one");
        var memoPath = MemoFile(path, ".FPT");
        var bytes = File.ReadAllBytes(memoPath);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, BinaryPrimitives.ReadUInt32BigEndian(bytes) + 1);
        File.WriteAllBytes(memoPath, bytes);

        using (DbfTable.Open(path, writable: true)) { }
        Assert.Equal(bytes.Length, new FileInfo(memoPath).Length); // others may have it open: not touched
    }
}
