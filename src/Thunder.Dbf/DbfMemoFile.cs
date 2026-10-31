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
//   Freed blocks form a chain from header byte 0: [next free][block count]. The last link
//   points at the end of the file, so appending means moving that link, not the header.
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
    /// <summary>dBASE III .dbt: fixed 512-byte blocks, text ended by 0x1A.</summary>
    DbaseIII,
    /// <summary>dBASE IV .dbt: blocks with a length header, and a free-block chain.</summary>
    DbaseIV,
    /// <summary>FoxPro .fpt: block headers that record each memo's type and length.</summary>
    FoxPro,
}

/// <summary>A memo's bytes and its FoxPro block type (1 = text, 0 = picture, 2 = object; dBASE memos are always 1).</summary>
internal readonly record struct DbfMemo(byte[] Data, int BlockType);

/// <summary>A memo file (.dbt or .fpt) holding the long values of a table's memo fields.</summary>
internal abstract class DbfMemoFile : IDisposable
{
    public const int TextBlock = 1;
    public const int PictureBlock = 0;
    public const int ObjectBlock = 2;

    private const int HeaderSize = 512;

    /// <summary>Largest memo Thunder.Dbf will load into memory, so a corrupt length can't exhaust it.</summary>
    public static int MaxMemoBytes { get; set; } = 64 * 1024 * 1024;

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

    /// <summary>First block that can hold data: everything before it is the 512-byte header.</summary>
    protected int FirstDataBlock => (HeaderSize + BlockSize - 1) / BlockSize;

    /// <summary>Memo files sit next to the table with the same base name; the extension's case varies by app.</summary>
    public static string? FindFor(string tablePath, DbfHeader header)
    {
        var extension = FormatFor(header) == DbfMemoFormat.FoxPro ? ".fpt" : ".dbt";
        var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
        var wanted = Path.GetFileNameWithoutExtension(tablePath) + extension;

        return Directory.EnumerateFiles(directory)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), wanted, StringComparison.OrdinalIgnoreCase));
    }

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
        // Real files can be shorter than the 512-byte header when they hold no memos yet (SIMATIC
        // Manager writes 24-byte .DBTs). The fields we need end by byte 24; writes always start
        // past the header region regardless.
        const int MinimumHeader = 24;
        var fileHeader = new byte[HeaderSize];
        stream.Position = 0;
        if (stream.ReadAtLeast(fileHeader, MinimumHeader, throwOnEndOfStream: false) < MinimumHeader)
            throw new InvalidDataException($"The memo file is shorter than the {MinimumHeader} bytes its header needs.");

        var blockSize = format switch
        {
            DbfMemoFormat.DbaseIII => 512,
            DbfMemoFormat.DbaseIV => OrDefault(BinaryPrimitives.ReadUInt16LittleEndian(fileHeader.AsSpan(20, 2)), 512),
            _ => OrDefault(BinaryPrimitives.ReadUInt16BigEndian(fileHeader.AsSpan(6, 2)), 64),
        };
        if (blockSize < 16)
            throw new InvalidDataException($"The memo file's block size of {blockSize} bytes is too small to hold a memo header.");

        return format switch
        {
            DbfMemoFormat.DbaseIII => new DbaseIIIMemo(stream, leaveOpen),
            DbfMemoFormat.DbaseIV => new DbaseIVMemo(stream, blockSize, leaveOpen),
            _ => new FoxProMemo(stream, blockSize, leaveOpen),
        };

        static int OrDefault(int value, int fallback) => value == 0 ? fallback : value;
    }

    /// <summary>Writes an empty dBASE IV memo file, the kind <see cref="DbfHeader.Create"/> tables expect.</summary>
    public static DbfMemoFile CreateDbaseIV(Stream stream, string tableName, bool leaveOpen = false)
    {
        var header = new byte[HeaderSize];
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
    public DbfMemo Read(int block)
    {
        DbfTable.DiscardReadBuffer(Stream);
        long offset = (long)block * BlockSize;
        if (block < FirstDataBlock || offset >= Stream.Length)
            throw new InvalidDataException($"Memo block {block} is outside the memo file's data area.");

        Stream.Position = offset;
        return ReadAt(Stream.Length - offset);
    }

    /// <summary>Throws if <paramref name="data"/> can't be stored, without touching the file.</summary>
    public void Validate(ReadOnlySpan<byte> data, int blockType)
    {
        if (data.Length > MaxMemoBytes)
            throw new ArgumentException($"Memos are limited to {MaxMemoBytes:N0} bytes (DbfMemoFile.MaxMemoBytes).");
        _ = Frame(data, blockType);
    }

    /// <summary>Appends a memo in new blocks at the end of the file and returns its first block number.</summary>
    public int Write(ReadOnlySpan<byte> data, int blockType)
    {
        Validate(data, blockType);
        var framed = Frame(data, blockType);
        // Allocate from the physical end of the file; never trust a stored pointer to say where that is.
        var block = checked((int)Math.Max(FirstDataBlock, (Stream.Length + BlockSize - 1) / BlockSize));
        var blocksUsed = (framed.Length + BlockSize - 1) / BlockSize;
        var newEnd = checked(block + blocksUsed);

        Stream.Position = (long)block * BlockSize;
        Stream.Write(framed);
        Stream.Write(new byte[blocksUsed * BlockSize - framed.Length]);

        MoveEndOfFile(block, newEnd);
        Stream.Flush();
        return block;
    }

    public void FlushToDisk() => DbfTable.FlushToDisk(Stream);

    public void Dispose()
    {
        if (!_leaveOpen)
            Stream.Dispose();
    }

    protected abstract DbfMemo ReadAt(long remaining);
    protected abstract byte[] Frame(ReadOnlySpan<byte> data, int blockType);

    /// <summary>Records that the file now ends at <paramref name="newEnd"/> instead of <paramref name="oldEnd"/>.</summary>
    protected virtual void MoveEndOfFile(int oldEnd, int newEnd) => WriteUInt32(0, newEnd, BigEndian);

    protected virtual bool BigEndian => false;

    protected byte[] ReadBounded(long length, long remaining)
    {
        if (length < 0 || length > remaining)
            throw new InvalidDataException($"A memo claims {length} bytes but only {remaining} remain in the file.");
        if (length > MaxMemoBytes)
            throw new InvalidDataException($"A memo claims {length:N0} bytes, over the {MaxMemoBytes:N0}-byte limit (DbfMemoFile.MaxMemoBytes).");
        var data = new byte[length];
        Stream.ReadExactly(data);
        return data;
    }

    protected int ReadUInt32(long position, bool bigEndian)
    {
        Span<byte> bytes = stackalloc byte[4];
        Stream.Position = position;
        Stream.ReadExactly(bytes);
        var value = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    protected void WriteUInt32(long position, int value, bool bigEndian)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        Stream.Position = position;
        Stream.Write(bytes);
    }

    private sealed class DbaseIIIMemo(Stream stream, bool leaveOpen) : DbfMemoFile(stream, 512, leaveOpen)
    {
        private const byte Terminator = 0x1A;

        public override DbfMemoFormat Format => DbfMemoFormat.DbaseIII;

        protected override DbfMemo ReadAt(long remaining) => new(ReadToTerminator(Stream, remaining), TextBlock);

        protected override byte[] Frame(ReadOnlySpan<byte> data, int blockType)
        {
            if (data.Contains(Terminator))
                throw new ArgumentException("dBASE III memos can't contain the 0x1A byte; it marks where the memo ends.");
            return [.. data, Terminator, Terminator];
        }

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
                    return memo.ToArray();
                if (memo.Length > MaxMemoBytes)
                    throw new InvalidDataException($"A memo runs past the {MaxMemoBytes:N0}-byte limit without its 0x1A terminator.");
                remaining -= read;
            }

            // The file ended before a terminator. Keep the text (it's often the last memo of a
            // truncated file) but drop the zero padding that fills the rest of its block.
            var data = memo.ToArray();
            var length = data.Length;
            while (length > 0 && data[length - 1] == 0)
                length--;
            return data[..length];
        }
    }

    private sealed class DbaseIVMemo(Stream stream, int blockSize, bool leaveOpen) : DbfMemoFile(stream, blockSize, leaveOpen)
    {
        private const int BlockHeaderSize = 8;
        private static ReadOnlySpan<byte> Signature => [0xFF, 0xFF, 0x08, 0x00];

        public override DbfMemoFormat Format => DbfMemoFormat.DbaseIV;

        protected override DbfMemo ReadAt(long remaining)
        {
            var start = Stream.Position;
            Span<byte> blockHeader = stackalloc byte[BlockHeaderSize];
            if (remaining >= BlockHeaderSize)
            {
                Stream.ReadExactly(blockHeader);
                if (blockHeader[..4].SequenceEqual(Signature))
                {
                    var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[4..]) - BlockHeaderSize;
                    return new(ReadBounded(length, remaining - BlockHeaderSize), TextBlock);
                }
            }

            // Some tools write 0x8B tables with dBASE III-style memos; fall back to the terminator.
            Stream.Position = start;
            return new(DbaseIIIMemo.ReadToTerminator(Stream, remaining), TextBlock);
        }

        protected override byte[] Frame(ReadOnlySpan<byte> data, int blockType)
        {
            var framed = new byte[BlockHeaderSize + data.Length];
            Signature.CopyTo(framed);
            BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(4), (uint)framed.Length);
            data.CopyTo(framed.AsSpan(BlockHeaderSize));
            return framed;
        }

        // Walk the free chain to the link that points at the old end of file and move it.
        protected override void MoveEndOfFile(int oldEnd, int newEnd)
        {
            var current = ReadUInt32(0, bigEndian: false);
            if (current < FirstDataBlock || current >= oldEnd)
            {
                WriteUInt32(0, newEnd, bigEndian: false);
                return;
            }

            for (var steps = 0; steps <= oldEnd; steps++)
            {
                var next = ReadUInt32((long)current * BlockSize, bigEndian: false);
                if (next >= oldEnd)
                {
                    WriteUInt32((long)current * BlockSize, newEnd, bigEndian: false);
                    return;
                }
                if (next <= current)
                    break;
                current = next;
            }

            throw new InvalidDataException("The memo file's free-block chain is damaged; refusing to write to it.");
        }
    }

    private sealed class FoxProMemo(Stream stream, int blockSize, bool leaveOpen) : DbfMemoFile(stream, blockSize, leaveOpen)
    {
        private const int BlockHeaderSize = 8;

        public override DbfMemoFormat Format => DbfMemoFormat.FoxPro;
        protected override bool BigEndian => true;

        protected override DbfMemo ReadAt(long remaining)
        {
            Span<byte> blockHeader = stackalloc byte[BlockHeaderSize];
            if (remaining < BlockHeaderSize)
                throw new InvalidDataException("The memo file ends inside a FoxPro block header.");
            Stream.ReadExactly(blockHeader);
            var type = (int)BinaryPrimitives.ReadUInt32BigEndian(blockHeader);
            var length = BinaryPrimitives.ReadUInt32BigEndian(blockHeader[4..]);
            return new(ReadBounded(length, remaining - BlockHeaderSize), type);
        }

        protected override byte[] Frame(ReadOnlySpan<byte> data, int blockType)
        {
            var framed = new byte[BlockHeaderSize + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)blockType);
            BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(4), (uint)data.Length);
            data.CopyTo(framed.AsSpan(BlockHeaderSize));
            return framed;
        }
    }
}
