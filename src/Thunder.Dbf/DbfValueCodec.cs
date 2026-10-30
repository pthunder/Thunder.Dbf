//    _
//   / |__     ProjectThunder :: Thunder.Dbf
//  /_   /     Bytes in, values out (and back again).
//    | /
//    |/
//
// +------+-----------------------+---------------------------+
// | type | on disk               | in .NET                   |
// +------+-----------------------+---------------------------+
// |  C   | text, space padded    | string (trailing trimmed) |
// | N  F | ASCII, right aligned  | decimal?                  |
// |  D   | YYYYMMDD              | DateOnly?                 |
// |  L   | T F Y N ?             | bool?                     |
// | MGPB | memo block, 10 ASCII  | int? (block number)       |
// +------+-----------------------+---------------------------+
// |  Visual FoxPro binary types, all little-endian           |
// +------+-----------------------+---------------------------+
// | MGP  | 4-byte block number   | int?                      |
// |  I   | int32                 | int                       |
// |  Y   | int64 / 10,000        | decimal                   |
// |  B   | IEEE double           | double                    |
// |  T   | Julian day + millis   | DateTime?                 |
// +------+-----------------------+---------------------------+
// |  ??  | whatever it is        | byte[] (untouched)        |
// +------+-----------------------+---------------------------+

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

/// <summary>Converts field bytes to .NET values and back.</summary>
internal static class DbfValueCodec
{
    private const byte Space = (byte)' ';
    private const int JulianDayOfDayNumberZero = 1721426; // Julian day of 0001-01-01
    private const int MillisecondsPerDay = 86_400_000;
    private const decimal CurrencyScale = 10_000m;

    /// <summary>Fields whose bytes in the .dbf are only a pointer into the memo file.</summary>
    public static bool IsMemoField(DbfField field) => field.Type is 'M' or 'G' or 'P' || (field.Type == 'B' && field.Length == 10);

    // Visual FoxPro stores memo pointers as 4-byte integers; every other dialect uses 10 ASCII digits.
    private static bool IsBinaryMemo(DbfField field) => field.Type is 'M' or 'G' or 'P' && field.Length == 4;

    public static object? Decode(DbfField field, ReadOnlySpan<byte> raw, Encoding encoding)
    {
        if (IsBinaryMemo(field))
        {
            var block = BinaryPrimitives.ReadInt32LittleEndian(raw);
            return block <= 0 ? null : block;
        }

        switch (field.Type)
        {
            case 'I' when field.Length == 4:
                return BinaryPrimitives.ReadInt32LittleEndian(raw);

            case 'Y' when field.Length == 8:
                return BinaryPrimitives.ReadInt64LittleEndian(raw) / CurrencyScale;

            case 'B' when field.Length == 8:
                return BinaryPrimitives.ReadDoubleLittleEndian(raw);

            case 'T' when field.Length == 8:
            {
                var julianDay = BinaryPrimitives.ReadInt32LittleEndian(raw);
                if (julianDay == 0)
                    return null;
                var dayNumber = julianDay - JulianDayOfDayNumberZero;
                var millis = BinaryPrimitives.ReadInt32LittleEndian(raw[4..]);
                if (dayNumber < 0 || dayNumber > DateOnly.MaxValue.DayNumber || millis < 0 || millis >= MillisecondsPerDay)
                    throw new FormatException($"Field {field.Name} holds an impossible datetime (day {julianDay}, {millis} ms).");
                // DBF datetimes carry no time zone, so they come back as Unspecified wall-clock times.
                return DateOnly.FromDayNumber(dayNumber).ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromMilliseconds(millis)));
            }

            case 'C':
                return encoding.GetString(raw).TrimEnd(' ', '\0');

            case 'N' or 'F':
            {
                var text = Ascii(raw);
                if (text.Length == 0 || text.All(c => c == '*'))
                    return null;
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return number;
                // Scientific values beyond decimal's range (FoxPro allows them) come back as double.
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var large))
                    return large;
                throw new FormatException($"Field {field.Name} holds '{text}', which isn't a number.");
            }

            case 'D':
            {
                var text = Ascii(raw);
                if (text.Length == 0 || text == "00000000")
                    return null;
                if (DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return date;
                throw new FormatException($"Field {field.Name} holds '{text}', which isn't a YYYYMMDD date.");
            }

            case 'L':
                return (char)raw[0] switch
                {
                    'T' or 't' or 'Y' or 'y' => true,
                    'F' or 'f' or 'N' or 'n' => false,
                    _ => null,
                };

            case 'M' or 'G' or 'P' or 'B':
            {
                var text = Ascii(raw);
                if (text.Length == 0)
                    return null;
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var block))
                    return block == 0 ? null : block;
                throw new FormatException($"Field {field.Name} holds '{text}', which isn't a memo block number.");
            }

            default:
                return raw.ToArray();
        }
    }

    public static void Encode(DbfField field, object? value, Span<byte> target, Encoding encoding)
    {
        if (IsBinaryMemo(field))
        {
            BinaryPrimitives.WriteInt32LittleEndian(target, value is null ? 0 : CheckedMemoBlock(field, value));
            return;
        }

        if ((field.Type == 'I' && field.Length == 4) || (field.Type is 'Y' or 'B' or 'T' && field.Length == 8))
        {
            EncodeBinary(field, value, target);
            return;
        }

        if (field.Type == '0')
        {
            // Visual FoxPro's _NullFlags: raw bits, zero when nothing is null. DbfTable sets the bits.
            target.Clear();
            if (value is byte[] flags && flags.Length == target.Length)
                flags.CopyTo(target);
            return;
        }

        target.Fill(Space);
        if (value is null)
        {
            if (field.Type == 'L')
                target[0] = (byte)'?';
            return;
        }

        switch (field.Type)
        {
            case 'C':
            {
                var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                var byteCount = encoding.GetByteCount(text);
                if (byteCount > target.Length)
                    throw new ArgumentException($"Field {field.Name} holds {target.Length} bytes; '{text}' needs {byteCount}.");
                encoding.GetBytes(text, target);
                break;
            }

            case 'N' or 'F':
            {
                WriteRightAligned(field, FormatNumber(field, value), target);
                break;
            }

            case 'D':
            {
                var date = value switch
                {
                    DateOnly d => d,
                    DateTime dt => DateOnly.FromDateTime(dt),
                    DateTimeOffset dto => DateOnly.FromDateTime(dto.Date),
                    _ => throw new ArgumentException($"Field {field.Name} needs a DateOnly or DateTime, not {value.GetType().Name}."),
                };
                Encoding.ASCII.GetBytes(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), target);
                break;
            }

            case 'L':
                target[0] = value is bool b
                    ? (byte)(b ? 'T' : 'F')
                    : throw new ArgumentException($"Field {field.Name} needs a bool, not {value.GetType().Name}.");
                break;

            case 'M' or 'G' or 'P' or 'B':
            {
                WriteRightAligned(field, CheckedMemoBlock(field, value).ToString(CultureInfo.InvariantCulture), target);
                break;
            }

            default:
                if (value is not byte[] bytes || bytes.Length != target.Length)
                    throw new ArgumentException($"Field {field.Name} has type '{field.Type}', so it needs exactly {target.Length} raw bytes.");
                bytes.CopyTo(target);
                break;
        }
    }

    // Visual FoxPro has no blank for these types; null writes zeros, which VFP shows as 0 or an empty datetime.
    private static void EncodeBinary(DbfField field, object? value, Span<byte> target)
    {
        target.Clear();
        if (value is null)
            return;

        switch (field.Type)
        {
            case 'I':
                BinaryPrimitives.WriteInt32LittleEndian(target, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case 'Y':
                var scaled = decimal.Round(Convert.ToDecimal(value, CultureInfo.InvariantCulture) * CurrencyScale);
                BinaryPrimitives.WriteInt64LittleEndian(target, decimal.ToInt64(scaled));
                break;
            case 'B':
                BinaryPrimitives.WriteDoubleLittleEndian(target, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case 'T':
                var moment = value switch
                {
                    DateTime dt => dt,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    DateTimeOffset dto => dto.DateTime,
                    _ => throw new ArgumentException($"Field {field.Name} needs a DateTime, not {value.GetType().Name}."),
                };
                // Whole days and milliseconds from integer ticks; sub-millisecond ticks are dropped, never rounded into tomorrow.
                BinaryPrimitives.WriteInt32LittleEndian(target, DateOnly.FromDateTime(moment).DayNumber + JulianDayOfDayNumberZero);
                BinaryPrimitives.WriteInt32LittleEndian(target[4..], (int)(moment.TimeOfDay.Ticks / TimeSpan.TicksPerMillisecond));
                break;
        }
    }

    /// <summary>Memo block numbers must be 1..int.MaxValue, the range every memo reader accepts.</summary>
    public static int CheckedMemoBlock(DbfField field, object value)
    {
        var block = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (block is < 1 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), $"Field {field.Name} can't point at memo block {block}.");
        return (int)block;
    }

    private static string FormatNumber(DbfField field, object value)
    {
        if (value is double or float)
        {
            var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(d))
                throw new ArgumentException($"Field {field.Name} can't store {d}.");
            if (Math.Abs(d) >= 7.9e28)
            {
                // Too big for decimal: scientific notation, as FoxPro writes it, at the most precision that fits.
                for (var digits = field.Length; digits >= 0; digits--)
                {
                    var scientific = d.ToString("0." + new string('#', digits) + "E+0", CultureInfo.InvariantCulture);
                    if (scientific.Length <= field.Length)
                        return scientific;
                }
                throw new OverflowException($"Field {field.Name} holds {field.Length} characters; {d} doesn't fit.");
            }
        }

        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        return number.ToString("F" + field.DecimalCount, CultureInfo.InvariantCulture);
    }

    private static string Ascii(ReadOnlySpan<byte> raw) => Encoding.ASCII.GetString(raw).Trim(' ', '\0');

    private static void WriteRightAligned(DbfField field, string text, Span<byte> target)
    {
        if (text.Length > target.Length)
            throw new OverflowException($"Field {field.Name} holds {target.Length} characters; '{text}' doesn't fit.");
        Encoding.ASCII.GetBytes(text, target[(target.Length - text.Length)..]);
    }
}
