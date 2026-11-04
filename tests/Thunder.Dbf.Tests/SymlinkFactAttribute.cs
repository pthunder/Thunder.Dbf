//      sneaky.dbf -> /elsewhere      ProjectThunder :: Thunder.Dbf tests
//         ^ can't make one?          A test that needs a symbolic link, skipped (not failed) on a
//           then: skipped            machine that can't make one: Windows without the privilege.

namespace Thunder.Dbf.Tests;

/// <summary>A <see cref="FactAttribute"/> that skips when this machine can't create symbolic links.</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (!Symlinks.CanCreate)
            Skip = Symlinks.Why;
    }
}

internal static class Symlinks
{
    public const string Why = "This machine can't create symbolic links (on Windows that takes Developer Mode or an administrator).";

    private static readonly Lazy<bool> Probe = new(() =>
    {
        var dir = Directory.CreateTempSubdirectory("thunder-link-probe-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "target"), "");
            File.CreateSymbolicLink(Path.Combine(dir.FullName, "link"), Path.Combine(dir.FullName, "target"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // "A required privilege is not held by the client"
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    });

    public static bool CanCreate => Probe.Value;
}
