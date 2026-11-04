//    handle --fstat--> (device, inode) ==? (device, inode) <--stat-- path
//
//   ProjectThunder :: Thunder.Dbf
//   Whether an open file is still the one at its path. On Linux and macOS a file renamed away keeps
//   its inode and every handle on it, so a handle opened just before a change of structure moved the
//   .dbf into its backup would go on writing there. .NET doesn't expose inode numbers, so they're
//   read from the C library's fstat and stat.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Thunder.Dbf;

internal static partial class FileIdentity
{
    /// <summary>For the tests: replaces the comparison (true, false, or null for "can't tell").</summary>
    [ThreadStatic]
    internal static Func<FileStream, string, bool?>? Override;

    private static bool _unavailable;

    /// <summary>
    /// True when the handle and the path name the same file, false when they don't, null when this
    /// system can't tell. On Windows it's always true: a file opened without delete sharing (as .NET
    /// opens every file) can't be renamed while the handle is open, so it's still at its path.
    /// </summary>
    internal static bool? Same(FileStream stream, string path)
    {
        if (Override is { } replaced)
            return replaced(stream, path);
        if (OperatingSystem.IsWindows())
            return true;
        if (_unavailable || IntPtr.Size != 8 || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return null;

        var handle = stream.SafeFileHandle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();
            var open = new byte[StatSize];
            var named = new byte[StatSize];
            int openResult, namedResult;
            if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.X64)
            {
                // Intel macOS keeps the 32-bit-inode stat under the plain names.
                openResult = FStatIntelMac(fd, open);
                namedResult = StatIntelMac(path, named);
            }
            else
            {
                openResult = FStat(fd, open);
                namedResult = Stat(path, named);
            }
            if (openResult != 0)
                return null;
            return namedResult == 0 && Key(open) == Key(named);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _unavailable = true; // glibc before 2.33 exports stat only as __xstat
            return null;
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
    }

    // struct stat on 64-bit Linux (glibc and musl, x64 and arm64) starts with a 64-bit st_dev and a
    // 64-bit st_ino; macOS with a 32-bit st_dev, its 16-bit mode and link count, then the 64-bit st_ino.
    private const int StatSize = 256; // more than any of them needs
    private static (ulong Device, ulong Inode) Key(byte[] stat) => OperatingSystem.IsMacOS()
        ? (BinaryPrimitives.ReadUInt32LittleEndian(stat), BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(8)))
        : (BinaryPrimitives.ReadUInt64LittleEndian(stat), BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(8)));

    [DllImport("libc", EntryPoint = "fstat")]
    private static extern int FStat(int fd, [Out] byte[] stat);

    [DllImport("libc", EntryPoint = "stat")]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] stat);

    [DllImport("libc", EntryPoint = "fstat$INODE64")]
    private static extern int FStatIntelMac(int fd, [Out] byte[] stat);

    [DllImport("libc", EntryPoint = "stat$INODE64")]
    private static extern int StatIntelMac([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] stat);
}
