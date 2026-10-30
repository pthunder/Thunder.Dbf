//      _____________
//     /            /|     ProjectThunder :: Thunder.Dbf
//    /  M E M O   / |     Long text lives next door, in blocks.
//   /____________/  |     The .dbf only keeps the block number.
//   |  .dbt .fpt |  /
//   |____________|/
//
// Three ways the same idea was built:
//
// dBASE III .dbt   block size 512, little-endian next-free pointer at byte 0
//   +------------------------------------------+------+------+
//   | memo text ...                            | 0x1A | 0x1A |   ends at the first 0x1A
//   +------------------------------------------+------+------+
//
// dBASE IV .dbt    block size at bytes 20-21 (little-endian), usually 512
//   +-------------+-------------------+----------------------+
//   | FF FF 08 00 | length (LE, +8)   | memo text ...        |   length counts its own 8-byte header
//   +-------------+-------------------+----------------------+
//
// FoxPro .fpt      512-byte header, block size at bytes 6-7, everything BIG-endian
//   +-------------+-------------------+----------------------+
//   | type (BE)   | length (BE)       | data ...             |   type 1 = text, 0 = picture, 2 = object
//   +-------------+-------------------+----------------------+

using System.Buffers.Binary;
using System.Text;

namespace Thunder.Dbf;

/// <summary>The kinds of memo file Thunder.Dbf reads and writes.</summary>
public enum DbfMemoFormat
{
    DbaseIII,
    DbaseIV,
    FoxPro,
}

/// <summary>A memo file (.dbt or .fpt) holding the long values of a table's memo fields.</summary>
internal abstract class DbfMemoFile : IDisposable
{
    private const int DbtHeaderSize = 512;

    private readonly bool _leaveOpen;

    protected DbfMemoFile(Stream stream, int blockSize, bool leaveOpen)
    {
        Stream = stream;
        BlockSize = blockSize;
        _leaveOpen = leaveOpen;
    }

    protected Stream Stream { get; }
    public int BlockSize { get; }
    public abstract DbfMemoFormat Format { get; }

    /// <summary>Memo files sit next to the table with the same base name; the extension's case varies by app.</summary>
    public static string? FindFor(string tablePath, DbfHeader header)
    {
        var extension = FormatFor(header) == DbfMemoFormat.FoxPro ? ".fpt" : ".dbt";
        var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
        var wanted = Path.GetFileNameWithoutExtension(tablePath) + extension;

        return Directory.EnumerateFiles(directory)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static string PathFor(string tablePath, DbfHeader header) =>
        Path.ChangeExtension(tablePath, FormatFor(header) == DbfMemoFormat.FoxPro ? ".fpt" : ".dbt");

    public static DbfMemoFormat FormatFor(DbfHeader header) => header.Version switch
    {
        0x83 => DbfMemoFormat.DbaseIII,
        0xF5 or 0xFB => DbfMemoFormat.FoxPro,
        _ when header.IsVisualFoxPro => DbfMemoFormat.FoxPro,
        _ => DbfMemoFormat.DbaseIV,
    };

    public static DbfMemoFile Open(Stream stream, DbfHeader header, bool leaveOpen = false)
    {
        var format = FormatFor(header);
        var fixedHeader = new byte[24];
        stream.Position = 0;
        if (stream.ReadAtLeast(fixedHeader, fixedHeader.Length, throwOnEndOfStream: false) < fixedHeader.Length)
            throw new InvalidDataException("The memo file is shorter than its header.");

        return format switch
        {
            DbfMemoFormat.DbaseIII => new DbaseIIIMemo(stream, leaveOpen),
            DbfMemoFormat.DbaseIV => new DbaseIVMemo(stream, BlockSizeOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(20, 2)), 512), leaveOpen),
            _ => new FoxProMemo(stream, BlockSizeOrDefault(BinaryPrimitives.ReadUInt16BigEndian(fixedHeader.AsSpan(6, 2)), 64), leaveOpen),
        };
    }

    /// <summary>Writes an empty dBASE IV memo file, the kind <see cref="DbfHeader.Create"/> tables expect.</summary>
    public static DbfMemoFile CreateDbaseIV(Stream stream, string tableName, bool leaveOpen = false)
    {
        var header = new byte[DbtHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        var name = Path.GetFileNameWithoutExtension(tableName).ToUpperInvariant();
        Encoding.ASCII.GetBytes(name.Length > 8 ? name[..8] : name, header.AsSpan(8, 8));
        header[18] = 0x02;
        header[19] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 512);

        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(header);
        stream.Flush();
        return new DbaseIVMemo(stream, 512, leaveOpen);
    }

    /// <summary>Reads the memo stored at <paramref name="block"/>.</summary>
    public byte[] Read(int block)
    {
        long offset = (long)block * BlockSize;
        if (block <= 0 || offset >= Stream.Length)
            throw new InvalidDataException($"Memo block {block} is outside the memo file.");

        Stream.Position = offset;
        return ReadAt(Stream.Length - offset);
    }

    /// <summary>Appends a memo in new blocks and returns the first block number.</summary>
    public int Write(ReadOnlySpan<byte> data, bool isText)
    {
        var framed = Frame(data, isText);
        var endBlock = (int)((Stream.Length + BlockSize - 1) / BlockSize);
        var block = Math.Max(ReadNextFree(), endBlock);
        var blocksUsed = (framed.Length + BlockSize - 1) / BlockSize;

        Stream.Position = (long)block * BlockSize;
        Stream.Write(framed);
        Stream.Write(new byte[blocksUsed * BlockSize - framed.Length]);

        WriteNextFree(block + blocksUsed);
        Stream.Flush();
        return block;
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            Stream.Dispose();
    }

    protected abstract byte[] ReadAt(long remaining);
    protected abstract byte[] Frame(ReadOnlySpan<byte> data, bool isText);
    protected abstract int ReadNextFree();
    protected abstract void WriteNextFree(int block);

    protected byte[] ReadExactlyBounded(long length, long remaining)
    {
        if (length < 0 || length > remaining)
            throw new InvalidDataException($"A memo claims {length} bytes but only {remaining} remain in the file.");
        var data = new byte[length];
        Stream.ReadExactly(data);
        return data;
    }

    protected int ReadUInt32At0(bool bigEndian)
    {
        Span<byte> bytes = stackalloc byte[4];
        Stream.Position = 0;
        Stream.ReadExactly(bytes);
        return (int)(bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes));
    }

    protected void WriteUInt32At0(int value, bool bigEndian)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        Stream.Position = 0;
        Stream.Write(bytes);
    }

    private static int BlockSizeOrDefault(int blockSize, int fallback) => blockSize == 0 ? fallback : blockSize;

    private sealed class DbaseIIIMemo(Stream stream, bool leaveOpen) : DbfMemoFile(stream, 512, leaveOpen)
    {
        private const byte Terminator = 0x1A;

        public override DbfMemoFormat Format => DbfMemoFormat.DbaseIII;

        protected override byte[] ReadAt(long remaining) => ReadToTerminator(Stream, remaining);

        protected override byte[] Frame(ReadOnlySpan<byte> data, bool isText)
        {
            if (data.Contains(Terminator))
                throw new ArgumentException("dBASE III memos can't contain the 0x1A byte; it marks where the memo ends.");
            return [.. data, Terminator, Terminator];
        }

        protected override int ReadNextFree() => ReadUInt32At0(bigEndian: false);
        protected override void WriteNextFree(int block) => WriteUInt32At0(block, bigEndian: false);

        internal static byte[] ReadToTerminator(Stream stream, long remaining)
        {
            using var memo = new MemoryStream();
            var chunk = new byte[512];
            while (remaining > 0)
            {
                var read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                if (read == 0)
                    break;
                var end = Array.IndexOf(chunk, Terminator, 0, read);
                memo.Write(chunk, 0, end >= 0 ? end : read);
                if (end >= 0)
                    break;
                remaining -= read;
            }
            return memo.ToArray();
        }
    }

    private sealed class DbaseIVMemo(Stream stream, int blockSize, bool leaveOpen) : DbfMemoFile(stream, blockSize, leaveOpen)
    {
        private const int BlockHeaderSize = 8;
        private static ReadOnlySpan<byte> Signature => [0xFF, 0xFF, 0x08, 0x00];

        public override DbfMemoFormat Format => DbfMemoFormat.DbaseIV;

        protected override byte[] ReadAt(long remaining)
        {
            var start = Stream.Position;
            Span<byte> blockHeader = stackalloc byte[BlockHeaderSize];
            if (remaining >= BlockHeaderSize)
            {
                Stream.ReadExactly(blockHeader);
                if (blockHeader[..4].SequenceEqual(Signature))
                {
                    var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[4..]) - BlockHeaderSize;
                    return ReadExactlyBounded(length, remaining - BlockHeaderSize);
                }
            }

            // Some tools write 0x8B tables with dBASE III-style memos; fall back to the terminator.
            Stream.Position = start;
            return DbaseIIIMemo.ReadToTerminator(Stream, remaining);
        }

        protected override byte[] Frame(ReadOnlySpan<byte> data, bool isText)
        {
            var framed = new byte[BlockHeaderSize + data.Length];
            Signature.CopyTo(framed);
            BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(4), (uint)framed.Length);
            data.CopyTo(framed.AsSpan(BlockHeaderSize));
            return framed;
        }

        protected override int ReadNextFree() => ReadUInt32At0(bigEndian: false);
        protected override void WriteNextFree(int block) => WriteUInt32At0(block, bigEndian: false);
    }

    private sealed class FoxProMemo(Stream stream, int blockSize, bool leaveOpen) : DbfMemoFile(stream, blockSize, leaveOpen)
    {
        private const int BlockHeaderSize = 8;
        private const uint TextType = 1;
        private const uint PictureType = 0;

        public override DbfMemoFormat Format => DbfMemoFormat.FoxPro;

        protected override byte[] ReadAt(long remaining)
        {
            Span<byte> blockHeader = stackalloc byte[BlockHeaderSize];
            if (remaining < BlockHeaderSize)
                throw new InvalidDataException("The memo file ends inside a FoxPro block header.");
            Stream.ReadExactly(blockHeader);
            var length = BinaryPrimitives.ReadUInt32BigEndian(blockHeader[4..]);
            return ReadExactlyBounded(length, remaining - BlockHeaderSize);
        }

        protected override byte[] Frame(ReadOnlySpan<byte> data, bool isText)
        {
            var framed = new byte[BlockHeaderSize + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(framed, isText ? TextType : PictureType);
            BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(4), (uint)data.Length);
            data.CopyTo(framed.AsSpan(BlockHeaderSize));
            return framed;
        }

        protected override int ReadNextFree() => ReadUInt32At0(bigEndian: true);
        protected override void WriteNextFree(int block) => WriteUInt32At0(block, bigEndian: true);
    }
}
