//      table open?  ---> File.ReadAllBytes: "in use by another process"  (Windows only)
//                   ---> SharedFile.ReadAllBytes: fine                    ProjectThunder :: Thunder.Dbf tests
//
//      Windows refuses a second handle whose sharing mode clashes with an open one: File.ReadAllBytes
//      won't share with writers and File.OpenWrite wants the file to itself. Tests often look at or
//      patch a file while a table still has it open, so they go through these, which share the way
//      the library itself opens files (and the way a legacy app finds them).

namespace Thunder.Dbf.Tests;

internal static class SharedFile
{
    private const FileShare Everyone = FileShare.ReadWrite | FileShare.Delete;

    public static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, Everyone);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, Everyone);
        stream.SetLength(0);
        stream.Write(bytes);
    }

    public static FileStream OpenWrite(string path) => new(path, FileMode.OpenOrCreate, FileAccess.Write, Everyone);
}
