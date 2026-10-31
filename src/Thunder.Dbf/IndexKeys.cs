//     .ndx   .ntx   .cdx           ProjectThunder :: Thunder.Dbf
//       \      |      /            Three file formats, one idea: a sorted list of
//        \     |     /             (key, record number) pairs, cut into pages.
//      ( "SMITH", 42 )             What follows is the part they share.

using System.Buffers.Binary;
using System.Globalization;

namespace Thunder.Dbf;

/// <summary>
/// One key in an .ndx, .ntx or .cdx index, and the 1-based record number it points at.
/// <see cref="Key"/> is a <see cref="string"/>, a number (<see cref="decimal"/>, or <see cref="double"/>
/// beyond decimal's range), a <see cref="DateOnly"/>? (null for a blank date) or a <see cref="bool"/>.
/// </summary>
public readonly record struct IndexEntry(object? Key, int RecordNumber);

/// <summary>Helpers the .ndx, .ntx and .cdx readers share: key types, probes and decoding.</summary>
internal static class IndexKeys
{
    /// <summary>Julian day number of 0001-01-01, the day <see cref="DateOnly.DayNumber"/> counts from.</summary>
    private const int JulianDayOfDayNumberZero = 1721426;

    /// <summary>
    /// Works out what an index key expression returns ('C', 'N', 'D' or 'L') by evaluating it against a
    /// blank record of <paramref name="table"/>, which is how Clipper, FoxPro and Harbour decide it too.
    /// Returns <paramref name="fallback"/> when the expression can't be evaluated here (an unsupported
    /// function, say) or returns something else.
    /// </summary>
    public static char InferKeyType(string expression, DbfTable table, char fallback)
    {
        object? value;
        try
        {
            value = DbfExpression.Parse(expression).Evaluate(BlankRecord(table));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or KeyNotFoundException
                                       or FormatException or ArgumentException or OverflowException)
        {
            return fallback;
        }

        return value switch
        {
            string => 'C',
            decimal or double => 'N',
            DateOnly or null => 'D', // a blank date evaluates to null
            bool => 'L',
            _ => fallback,
        };
    }

    // Every field empty: spaces, zero, false, blank date. Built by hand rather than decoded from a
    // space-filled buffer so binary fields (I, Y, B) and nullable fields still produce their type.
    private static DbfRecord BlankRecord(DbfTable table)
    {
        var values = new object?[table.Fields.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var field = table.Fields[i];
            values[i] = DbfValueCodec.IsMemoField(field) ? "" : field.Type switch
            {
                'C' or 'V' => "",
                'N' or 'F' or 'B' or 'Y' => 0m,
                'I' or '+' => 0,
                'L' => false,
                _ => null,
            };
        }
        return new DbfRecord(table.Header, 0, isDeleted: false, values, table.Encoding);
    }

    /// <summary>
    /// Ascending byte comparison of a stored character key with a probe, as dBASE's SEEK does it: a
    /// shorter probe matches any key it prefixes; a longer one only matches when its extra bytes are
    /// the spaces the key would have been padded with.
    /// </summary>
    public static int CompareCharacter(ReadOnlySpan<byte> key, ReadOnlySpan<byte> probe)
    {
        var window = Math.Min(probe.Length, key.Length);
        var order = key[..window].SequenceCompareTo(probe[..window]);
        if (order != 0 || probe.Length <= key.Length)
            return order;
        return probe[key.Length..].IndexOfAnyExcept((byte)' ') < 0 ? 0 : -1;
    }

    /// <summary>A stored number as a decimal when it fits (with the digits it was written with), else a double.</summary>
    public static object FromDouble(double value)
    {
        if (double.IsFinite(value) && Math.Abs(value) < 7.9e28
            && decimal.TryParse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var exact))
            return exact;
        return value;
    }

    /// <summary>Turns a seek value into a double for indexes that store numbers as IEEE doubles.</summary>
    public static double ToDouble(object? key, string tagName) => key switch
    {
        null => throw new ArgumentNullException(nameof(key), $"Tag {tagName} has numeric keys; seek with a number."),
        double d => d,
        float f => f,
        _ => Convert.ToDouble(key, CultureInfo.InvariantCulture),
    };

    public static double JulianDay(DateOnly date) => date.DayNumber + (double)JulianDayOfDayNumberZero;

    /// <summary>A Julian day number as a date; 0 (and anything outside DateOnly's range) is a blank date.</summary>
    public static DateOnly? FromJulianDay(double julian)
    {
        var dayNumber = Math.Floor(julian) - JulianDayOfDayNumberZero;
        return julian == 0 || !(dayNumber >= 0 && dayNumber <= DateOnly.MaxValue.DayNumber) ? null : DateOnly.FromDayNumber((int)dayNumber);
    }

    /// <summary>Turns a seek value into a Julian day number: a <see cref="DateOnly"/>, a <see cref="DateTime"/> or null (blank).</summary>
    public static double ToJulianDay(object? key, string tagName) => key switch
    {
        null => 0d,
        DateOnly d => JulianDay(d),
        DateTime dt => JulianDay(DateOnly.FromDateTime(dt)),
        _ => throw new ArgumentException($"Tag {tagName} has date keys; seek with a DateOnly (or null for a blank date), not {key.GetType().Name}."),
    };

    /// <summary>The text of a character probe. Anything that isn't a string is formatted invariantly.</summary>
    public static string ToText(object? key, string tagName) =>
        key as string ?? Convert.ToString(key, CultureInfo.InvariantCulture)
        ?? throw new ArgumentNullException(nameof(key), $"Tag {tagName} has character keys; seek with a string.");

    public static bool ToBool(object? key, string tagName) =>
        key as bool? ?? throw new ArgumentException($"Tag {tagName} has logical keys; seek with true or false.");

    /// <summary>Reads text up to the first NUL (or the end), trimmed of spaces.</summary>
    public static string ReadText(ReadOnlySpan<byte> bytes, System.Text.Encoding encoding)
    {
        var nul = bytes.IndexOf((byte)0);
        return encoding.GetString(nul >= 0 ? bytes[..nul] : bytes).Trim(' ');
    }

    /// <summary>Reads a record number, refusing 0 and values past int.MaxValue, which no table can have.</summary>
    public static int RecordNumber(uint value, string tagName) =>
        value is 0 or > int.MaxValue
            ? throw new InvalidDataException($"Tag {tagName} points at record {value}, which can't exist; the index is corrupt.")
            : (int)value;

    /// <summary>Reads an IEEE double stored big-endian with its bits arranged to sort as unsigned bytes (FoxPro, Harbour).</summary>
    public static double ReadOrderedDouble(ReadOnlySpan<byte> key)
    {
        Span<byte> bytes = stackalloc byte[8];
        key[..8].CopyTo(bytes);
        if ((bytes[0] & 0x80) != 0)
            bytes[0] ^= 0x80;          // zero or positive: only the sign bit was flipped
        else
        {
            for (var i = 0; i < 8; i++)
                bytes[i] ^= 0xFF;      // negative: every bit was flipped
        }
        return BinaryPrimitives.ReadDoubleBigEndian(bytes);
    }

    /// <summary>The inverse of <see cref="ReadOrderedDouble"/>.</summary>
    public static byte[] WriteOrderedDouble(double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, value == 0 ? 0d : value); // -0.0 is stored as 0.0
        if (value >= 0)
            bytes[0] ^= 0x80;
        else
        {
            for (var i = 0; i < 8; i++)
                bytes[i] ^= 0xFF;
        }
        return bytes;
    }
}
