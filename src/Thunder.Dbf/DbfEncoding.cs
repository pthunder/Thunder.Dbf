//      _______
//     |  437  |      ProjectThunder :: Thunder.Dbf
//     |  850  |      Byte 29 says which code page the text is in.
//     | 1252  |      Believe it when it's set; guess when it's 0.
//     |_______|

using System.Text;

namespace Thunder.Dbf;

/// <summary>Maps the DBF language driver byte (header offset 29) to a .NET <see cref="Encoding"/>.</summary>
public static class DbfEncoding
{
    // Most legacy files predate Unicode and were written by Windows apps, so 1252 is the safest guess.
    public const int DefaultCodePage = 1252;

    private static readonly Dictionary<byte, int> CodePages = new()
    {
        [0x01] = 437, [0x02] = 850, [0x03] = 1252, [0x04] = 10000,
        [0x08] = 865, [0x09] = 437, [0x0A] = 850, [0x0B] = 437,
        [0x0C] = 850, [0x0D] = 437, [0x0E] = 850, [0x0F] = 437, [0x10] = 850,
        [0x11] = 437, [0x12] = 850, [0x13] = 932, [0x14] = 850,
        [0x15] = 437, [0x16] = 850, [0x17] = 865, [0x18] = 437,
        [0x19] = 437, [0x1A] = 850, [0x1B] = 437, [0x1C] = 863,
        [0x1D] = 850, [0x1F] = 852, [0x22] = 852, [0x23] = 852,
        [0x24] = 860, [0x25] = 850, [0x26] = 866, [0x37] = 850,
        [0x40] = 852, [0x4D] = 936, [0x4E] = 949, [0x4F] = 950,
        [0x50] = 874, [0x57] = 1252, [0x58] = 1252, [0x59] = 1252,
        [0x64] = 852, [0x69] = MazoviaEncoding.MazoviaCodePage, [0x65] = 866, [0x66] = 865, [0x67] = 861,
        [0x6A] = 737, [0x6B] = 857, [0x78] = 950, [0x79] = 949,
        [0x7A] = 936, [0x7B] = 932, [0x7C] = 874, [0x7D] = 1255,
        [0x7E] = 1256, [0x87] = 852, [0x88] = 857, [0x96] = 10007,
        [0x97] = 10029, [0x98] = 10006, [0xC8] = 1250,
        [0xC9] = 1251, [0xCA] = 1254, [0xCB] = 1253, [0xCC] = 1257,
    };

    static DbfEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The code page for a language driver byte, or null when the byte is 0 or unknown.</summary>
    public static int? CodePageFor(byte languageDriver) =>
        CodePages.TryGetValue(languageDriver, out var codePage) ? codePage : null;

    /// <summary>The encoding for a language driver byte, falling back to <see cref="DefaultCodePage"/>.</summary>
    public static Encoding For(byte languageDriver) => GetEncoding(CodePageFor(languageDriver) ?? DefaultCodePage);

    public static Encoding GetEncoding(int codePage) =>
        codePage == MazoviaEncoding.MazoviaCodePage ? MazoviaEncoding.Instance : Encoding.GetEncoding(codePage);

    /// <summary>A copy of <paramref name="encoding"/> that throws on characters it can't encode, instead of writing '?'.</summary>
    public static Encoding Strict(Encoding encoding)
    {
        var strict = (Encoding)encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        return strict;
    }

    public static Encoding GetEncodingByName(string name) =>
        name.Equals("mazovia", StringComparison.OrdinalIgnoreCase) ? MazoviaEncoding.Instance : Encoding.GetEncoding(name);

    /// <summary>
    /// Reads a shapefile-style .cpg sidecar ("UTF-8", "1252", "ANSI 1251", ...). Returns null when the file
    /// is missing or names an encoding .NET doesn't know.
    /// </summary>
    public static Encoding? FromCpgFile(string path)
    {
        if (!File.Exists(path))
            return null;

        string name;
        try
        {
            // An encoding label is a few bytes; read no more than a small cap of a hostile sidecar.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[64];
            var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == buffer.Length && file.Length > buffer.Length)
                return null;
            name = Encoding.ASCII.GetString(buffer, 0, read).Trim();
        }
        catch (Exception ex) when (DbfException.Translate(ex, path, FileAccess.Read) is { } translated)
        {
            throw translated;
        }
        if (name.StartsWith("ANSI ", StringComparison.OrdinalIgnoreCase))
            name = name[5..];

        if (name.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) || name.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            return int.TryParse(name, out var codePage) ? GetEncoding(codePage) : Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
