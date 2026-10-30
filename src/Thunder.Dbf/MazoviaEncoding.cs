//    ___________
//   |  ą ć ę ł  |     ProjectThunder :: Thunder.Dbf
//   |  ń ó ś ź ż|     Mazovia: the Polish DOS code page dBASE marks as driver 0x69.
//   |___________|     .NET doesn't ship it, so here it is.
//
// Bytes 0x00-0x7F are ASCII. 0x80-0xFF come from C-Kermit's table (as carried by dosemu2),
// checked against the phpwcms ConvertTables copy; the two agree on every Polish letter.
// 0x9B is the "zł" (złoty) sign, which has no single Unicode character; like C-Kermit we use ¢
// so text round-trips byte for byte.

using System.Text;

namespace Thunder.Dbf;

/// <summary>The Mazovia (Polish DOS) single-byte code page, known as code page 620.</summary>
public sealed class MazoviaEncoding : Encoding
{
    public const int MazoviaCodePage = 620;

    public static readonly MazoviaEncoding Instance = new();

    private static readonly char[] HighHalf =
    [
        'Ç', 'ü', 'é', 'â', 'ä', 'à', 'ą', 'ç', // 0x80
        'ê', 'ë', 'è', 'ï', 'î', 'ć', 'Ä', 'Ą', // 0x88
        'Ę', 'ę', 'ł', 'ô', 'ö', 'Ć', 'û', 'ù', // 0x90
        'Ś', 'Ö', 'Ü', '¢', 'Ł', '¥', 'ś', 'ƒ', // 0x98
        'Ź', 'Ż', 'ó', 'Ó', 'ń', 'Ń', 'ź', 'ż', // 0xA0
        '¿', '⌐', '¬', '½', '¼', '¡', '«', '»', // 0xA8
        '░', '▒', '▓', '│', '┤', '╡', '╢', '╖', // 0xB0
        '╕', '╣', '║', '╗', '╝', '╜', '╛', '┐', // 0xB8
        '└', '┴', '┬', '├', '─', '┼', '╞', '╟', // 0xC0
        '╚', '╔', '╩', '╦', '╠', '═', '╬', '╧', // 0xC8
        '╨', '╤', '╥', '╙', '╘', '╒', '╓', '╫', // 0xD0
        '╪', '┘', '┌', '█', '▄', '▌', '▐', '▀', // 0xD8
        'α', 'ß', 'Γ', 'π', 'Σ', 'σ', 'µ', 'τ', // 0xE0
        'Φ', 'Θ', 'Ω', 'δ', '∞', 'φ', 'ε', '∩', // 0xE8
        '≡', '±', '≥', '≤', '⌠', '⌡', '÷', '≈', // 0xF0
        '°', '∙', '·', '√', 'ⁿ', '²', '■', ' ', // 0xF8
    ];

    private static readonly Dictionary<char, byte> Reverse =
        HighHalf.Select((c, i) => (c, b: (byte)(0x80 + i))).ToDictionary(x => x.c, x => x.b);

    public override int CodePage => MazoviaCodePage;
    public override string WebName => "mazovia";
    public override string EncodingName => "Polish (Mazovia)";
    public override bool IsSingleByte => true;

    public override int GetByteCount(char[] chars, int index, int count) => count;

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        for (var i = 0; i < charCount; i++)
        {
            var c = chars[charIndex + i];
            bytes[byteIndex + i] = c < 0x80 ? (byte)c : Reverse.TryGetValue(c, out var b) ? b : (byte)'?';
        }
        return charCount;
    }

    public override int GetCharCount(byte[] bytes, int index, int count) => count;

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        for (var i = 0; i < byteCount; i++)
        {
            var b = bytes[byteIndex + i];
            chars[charIndex + i] = b < 0x80 ? (char)b : HighHalf[b - 0x80];
        }
        return byteCount;
    }

    public override int GetMaxByteCount(int charCount) => charCount;
    public override int GetMaxCharCount(int byteCount) => byteCount;
}
