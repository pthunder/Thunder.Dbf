//    handle --fstat--> (device, inode) ==? (device, inode) <--stat-- path
//
//   ProjectThunder :: Thunder.Dbf
//   Whether an open file is still the one at its path. On Linux and macOS a file renamed away keeps
//   its inode and every handle on it, so a handle opened just before a change of structure moved the
//   .dbf into its backup would go on writing there. .NET doesn't expose inode numbers, so they're
//   read from the C library's fstat and stat, in a layout proven against a file of known size first.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Thunder.Dbf;

internal static class FileIdentity
{
    /// <summary>For the tests: replaces the comparison (true, false, or null for "can't tell").</summary>
    [ThreadStatic]
    internal static Func<FileStream, string, bool?>? Override;

    /// <summary>How a process reads struct stat: which symbols, and where the fields it needs are.</summary>
    internal sealed record StatAbi(bool IntelMacSymbols, int DeviceBytes, int InodeAt, int SizeAt);

    // struct stat on 64-bit Linux (glibc and musl, x64 and arm64): 64-bit st_dev at 0, st_ino at 8,
    // st_size at 48. On macOS with 64-bit inodes: 32-bit st_dev, st_ino at 8, st_size at 96; an x64
    // process (Rosetta's included, which report an Arm64 machine) reaches that layout only through
    // the $INODE64 symbols. Anything else isn't read at all.
    internal static StatAbi? AbiFor(bool linux, bool macOS, Architecture process) => (linux, macOS, process) switch
    {
        (true, false, Architecture.X64 or Architecture.Arm64) => new StatAbi(IntelMacSymbols: false, DeviceBytes: 8, InodeAt: 8, SizeAt: 48),
        (false, true, Architecture.Arm64) => new StatAbi(IntelMacSymbols: false, DeviceBytes: 4, InodeAt: 8, SizeAt: 96),
        (false, true, Architecture.X64) => new StatAbi(IntelMacSymbols: true, DeviceBytes: 4, InodeAt: 8, SizeAt: 96),
        _ => null,
    };

    /// <summary>For the tests: the ABI to use instead of this process's own.</summary>
    [ThreadStatic]
    internal static StatAbi? AbiOverride;

    private static readonly Lazy<StatAbi?> Proven = new(() => Prove(AbiFor(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture)));

    /// <summary>
    /// True when the handle and the path name the same file, false when they don't, null when this
    /// process can't tell (then nothing is checked). On Windows it's always true: a file opened
    /// without delete sharing (as .NET opens every file) can't be renamed while the handle is open.
    /// </summary>
    internal static bool? Same(FileStream stream, string path)
    {
        if (Override is { } replaced)
            return replaced(stream, path);
        if (OperatingSystem.IsWindows())
            return true;
        var abi = AbiOverride is { } forced ? Prove(forced) : Proven.Value;
        if (abi is null)
            return null;
        var open = FStatOf(stream, abi);
        if (open is null)
            return null;
        var named = StatOf(path, abi);
        return named is not null && Key(open, abi) == Key(named, abi);
    }

    /// <summary>True when this process reads inodes, so <see cref="Same"/> never answers null.</summary>
    internal static bool Available => !OperatingSystem.IsWindows() && Proven.Value is not null;

    // The layout is trusted only once a scratch file's fstat and stat, read through it, give the
    // file's real size and one inode: a wrong layout (a 32-bit-inode stat read as 64-bit, say) fails.
    private static StatAbi? Prove(StatAbi? abi)
    {
        if (abi is null || IntPtr.Size != 8)
            return null;
        var scratch = Path.Combine(Path.GetTempPath(), $"thunder-dbf-stat-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            const int Length = 12_345;
            using var stream = new FileStream(scratch, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.DeleteOnClose);
            stream.Write(new byte[Length]);
            stream.Flush();
            var open = FStatOf(stream, abi);
            var named = StatOf(scratch, abi);
            return open is not null && named is not null
                && BinaryPrimitives.ReadInt64LittleEndian(open.AsSpan(abi.SizeAt)) == Length
                && BinaryPrimitives.ReadInt64LittleEndian(named.AsSpan(abi.SizeAt)) == Length
                && Key(open, abi) == Key(named, abi) && Key(open, abi).Inode != 0
                ? abi : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null; // glibc before 2.33 exports stat only as __xstat; a temp folder that can't be written
        }
    }

    private static (ulong Device, ulong Inode) Key(byte[] stat, StatAbi abi) =>
        (abi.DeviceBytes == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(stat) : BinaryPrimitives.ReadUInt32LittleEndian(stat),
         BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(abi.InodeAt)));

    private const int StatSize = 256; // more than any of them needs

    private static byte[]? FStatOf(FileStream stream, StatAbi abi)
    {
        var handle = stream.SafeFileHandle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var stat = new byte[StatSize];
            var fd = (int)handle.DangerousGetHandle();
            return (abi.IntelMacSymbols ? FStatIntelMac(fd, stat) : FStat(fd, stat)) == 0 ? stat : null;
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
    }

    private static byte[]? StatOf(string path, StatAbi abi)
    {
        var stat = new byte[StatSize];
        return (abi.IntelMacSymbols ? StatIntelMac(path, stat) : Stat(path, stat)) == 0 ? stat : null;
    }

    [DllImport("libc", EntryPoint = "fstat")]
    private static extern int FStat(int fd, [Out] byte[] stat);

    [DllImport("libc", EntryPoint = "stat")]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] stat);

    [DllImport("libc", EntryPoint = "fstat$INODE64")]
    private static extern int FStatIntelMac(int fd, [Out] byte[] stat);

    [DllImport("libc", EntryPoint = "stat$INODE64")]
    private static extern int StatIntelMac([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] stat);
}
