//      " -123.45"  -> -123.45m      ProjectThunder :: Thunder.Dbf tests
//      "20261114"  -> 2026-11-14    Numeric, date and memo fields are parsed straight from their bytes.
//      "    1E+05" -> 100000        The old reads went through a string first; the reference below is
//                                   that code, and every case must read the same, error included.

using System.Globalization;
using System.Text;

namespace Thunder.Dbf.Tests;

public sealed class FieldParsingTests
{
    private static readonly DbfField Number = new("AMT", 'N', 20, 4, 1);
    private static readonly DbfField Floating = new("RATE", 'F', 20, 4, 1);
    private static readonly DbfField DateField = new("HIRED", 'D', 8, 0, 1);
    private static readonly DbfField MemoField = new("NOTES", 'M', 10, 0, 1);

    // Numbers as legacy apps, other libraries and damaged files leave them in a field.
    public static TheoryData<string> Numbers =>
    [
        "", "   ", "\0\0\0", "0", "-0", "  123.45", "-123.45", "123.45  ", "\0\0 42", ".5", "5.", "-.5", "-5.",
        "0.10", "00012", "-0000.0100", "1234567890123456789012345678", "12345678901234567890123456789",
        "0.000000000000000000000000001", "0.0000000000000000000000000001", "1.5E-28", "1E-100", "1E+05",
        "1e5", "-2.5E3", "+7", "-", ".", "-.", "1.2.3", "12-3", "1 2", "1,5", "**********", "*****.**",
        "*", "abc", "١٢٣", "9999999999999999999999999999.9", "7.9E+28", "1E+300", "NaN", "Infinity",
        "  -1.0000  ", "0.0000", "-0.0000", "1234567890.12345678901234567890", "-0.12345678901234567890123456789",
    ];

    [Theory]
    [MemberData(nameof(Numbers))]
    public void Numbers_read_as_they_did_through_text(string text)
    {
        foreach (var field in new[] { Number, Floating })
        {
            var raw = Bytes(text);
            Assert.Equal(Outcome(() => OldNumber(field, raw)), Outcome(() => DbfValueCodec.Decode(field, raw, Encoding.ASCII)));
        }
    }

    public static TheoryData<string> Dates =>
    [
        "", "        ", "\0\0\0\0\0\0\0\0", "00000000", "20261114", "19000101", "99991231", "00010101",
        "20260230", "2026111", "202611145", " 2026111", "2026-11-", "abcdefgh", "2026١١14", "20261114\0",
    ];

    [Theory]
    [MemberData(nameof(Dates))]
    public void Dates_read_as_they_did_through_text(string text)
    {
        var raw = Bytes(text);
        Assert.Equal(Outcome(() => OldDate(DateField, raw)), Outcome(() => DbfValueCodec.Decode(DateField, raw, Encoding.ASCII)));
    }

    public static TheoryData<string> Blocks =>
    [
        "", "          ", "0", "         0", "         7", "0000000007", "2147483647", "2147483648", "-5", "+5",
        "5 ", " 5 ", "1 2", "abc", "٥", "\0\0\0\0\0\0\0\0\0\0", "  12\0\0",
    ];

    [Theory]
    [MemberData(nameof(Blocks))]
    public void Memo_block_numbers_read_as_they_did_through_text(string text)
    {
        var raw = Bytes(text);
        Assert.Equal(Outcome(() => OldBlock(MemoField, raw)), Outcome(() => DbfValueCodec.Decode(MemoField, raw, Encoding.ASCII)));
    }

    [Fact]
    public void Random_numbers_read_as_they_did_through_text()
    {
        var random = new Random(1988);
        const string alphabet = "0123456789-.+eE* \0";
        for (var i = 0; i < 20_000; i++)
        {
            var length = random.Next(1, 20);
            var text = random.Next(3) == 0
                ? new string([.. Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)])])
                : (random.NextDouble() * Math.Pow(10, random.Next(-30, 30)) * (random.Next(2) == 0 ? -1 : 1)).ToString(random.Next(2) == 0 ? "R" : "F" + random.Next(0, 10), CultureInfo.InvariantCulture);
            var raw = Bytes(text);
            Assert.True(Outcome(() => OldNumber(Number, raw)) == Outcome(() => DbfValueCodec.Decode(Number, raw, Encoding.ASCII)), $"'{text}'");
        }
    }

    // Non-ASCII characters go in as UTF-8, as a damaged or foreign file might hold them.
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // The value with its type, or the exception's type and message: both must match.
    private static string Outcome(Func<object?> read)
    {
        try
        {
            var value = read();
            return value switch
            {
                null => "null",
                decimal d => $"decimal {d.ToString(CultureInfo.InvariantCulture)}",
                double d => $"double {d.ToString("R", CultureInfo.InvariantCulture)}",
                IFormattable f => $"{value.GetType().Name} {f.ToString(null, CultureInfo.InvariantCulture)}",
                _ => $"{value.GetType().Name} {value}",
            };
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    // ---- the text-based reads these replaced, kept as the reference ---------------------------

    private static string Ascii(ReadOnlySpan<byte> raw) => Encoding.ASCII.GetString(raw).Trim(' ', '\0');

    private static object? OldNumber(DbfField field, byte[] raw)
    {
        var text = Ascii(raw);
        if (text.Length == 0 || text.All(c => c == '*'))
            return null;
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && DbfValueCodec.SameDigits(text, number.ToString(CultureInfo.InvariantCulture)))
            return number;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble))
            return asDouble;
        throw new FormatException($"Field {field.Name} holds '{text}', which isn't a number.");
    }

    private static object? OldDate(DbfField field, byte[] raw)
    {
        var text = Ascii(raw);
        if (text.Length == 0 || text == "00000000")
            return null;
        if (DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new FormatException($"Field {field.Name} holds '{text}', which isn't a YYYYMMDD date.");
    }

    private static object? OldBlock(DbfField field, byte[] raw)
    {
        var text = Ascii(raw);
        if (text.Length == 0)
            return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var block))
            return block == 0 ? null : block;
        throw new FormatException($"Field {field.Name} holds '{text}', which isn't a memo block number.");
    }
}
