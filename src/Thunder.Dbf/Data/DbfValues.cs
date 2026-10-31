//    .-------.    .-------.
//    | N(10) | -> | decimal|     ProjectThunder :: Thunder.Dbf
//    | D(8)  | -> |DateTime|     dBASE field types, as ADO.NET sees them,
//    | L(1)  | -> |  bool  |     and the rules for comparing and storing values.
//    '-------'    '-------'

using System.Globalization;
using System.Text;

namespace Thunder.Dbf.Data;

/// <summary>Type mapping, comparison and coercion shared by commands and readers.</summary>
internal static class DbfValues
{
    /// <summary>The name of the record-number pseudo-column (1-based, like dBASE's RECNO()).</summary>
    public const string RecordNumberColumn = "_RECNO";

    /// <summary>The name of the deletion-mark pseudo-column.</summary>
    public const string DeletedColumn = "_DELETED";

    /// <summary>Fields ADO.NET never sees: Visual FoxPro's _NullFlags bookkeeping.</summary>
    public static bool IsHidden(DbfField field) => field.Type == '0' || field.IsSystem;

    /// <summary>The .NET type every non-null value of the field has after <see cref="Normalize"/>.</summary>
    public static Type ClrType(DbfField field) => field.Type switch
    {
        'C' => typeof(string),
        'N' or 'F' => typeof(decimal),
        'D' => typeof(DateTime),
        'L' => typeof(bool),
        'I' when field.Length == 4 => typeof(int),
        'Y' when field.Length == 8 => typeof(decimal),
        'B' when field.Length == 8 => typeof(double),
        'T' when field.Length == 8 => typeof(DateTime),
        'M' when !field.IsBinary => typeof(string),
        _ => typeof(byte[]),
    };

    /// <summary>A readable name for the field's dBASE type, as GetDataTypeName and GetSchema report it.</summary>
    public static string TypeName(DbfField field) => field.Type switch
    {
        'C' => "Character",
        'N' => "Numeric",
        'F' => "Float",
        'D' => "Date",
        'L' => "Logical",
        'M' => "Memo",
        'G' => "General",
        'P' => "Picture",
        'I' when field.Length == 4 => "Integer",
        'Y' when field.Length == 8 => "Currency",
        'B' when field.Length == 8 => "Double",
        'B' => "Binary",
        'T' when field.Length == 8 => "DateTime",
        'V' => "Varchar",
        'Q' => "Varbinary",
        _ => $"Type {field.Type}",
    };

    /// <summary>
    /// Whether the field can read as null. Character fields read blanks as "", and Visual FoxPro's
    /// binary types have no blank, so those are only null when the field is marked nullable.
    /// </summary>
    public static bool AllowsNull(DbfField field) => field.IsNullable || field.Type switch
    {
        'C' => false,
        'I' when field.Length == 4 => false,
        'Y' or 'B' when field.Length == 8 => false,
        _ => true,
    };

    /// <summary>Turns a decoded record value into the type <see cref="ClrType"/> promises.</summary>
    public static object? Normalize(DbfField field, object? value, Encoding encoding) => value switch
    {
        null => null,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        byte[] bytes when field.Type == 'M' && !field.IsBinary => encoding.GetString(bytes), // a non-text block in a text memo
        _ => value,
    };

    /// <summary>Turns a parameter value into something the comparison and storage rules understand.</summary>
    public static object? FromParameter(object? value) => value switch
    {
        null or DBNull => null,
        char c => c.ToString(),
        Enum e => Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture),
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset dto => dto.DateTime,
        _ => value,
    };

    // ---- comparing ------------------------------------------------------------------------

    private static bool IsNumber(object value) => value is decimal or double or float or int or long or short or byte
        or sbyte or uint or ulong or ushort;

    /// <summary>
    /// Compares two non-null values the way WHERE does: numbers by value, text by ordinal with trailing
    /// spaces ignored (dBASE pads text), dates by instant. A string meets a number or date by being parsed.
    /// </summary>
    public static int Compare(object left, object right)
    {
        if (IsNumber(left) && IsNumber(right))
            return CompareNumbers(left, right);
        if (left is string a && right is string b)
            return string.CompareOrdinal(a.TrimEnd(' '), b.TrimEnd(' '));
        if (left is DateTime x && right is DateTime y)
            return x.CompareTo(y);
        if (left is bool p && right is bool q)
            return p.CompareTo(q);

        // Mixed types: a text value meets the other side's type.
        if (left is string s)
            return Compare(ParseLike(s, right), right);
        if (right is string t)
            return Compare(left, ParseLike(t, left));

        throw DbfSqlError.Value($"A {Describe(left)} can't be compared with a {Describe(right)}.",
            "Pass a parameter of the column's type: numbers for N/F/I/Y/B, DateTime for D/T, bool for L, string for C/M.");
    }

    private static int CompareNumbers(object left, object right)
    {
        if (left is double or float || right is double or float)
            return Convert.ToDouble(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));
        return Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
    }

    // Parses text into the type of `like`, for comparisons such as DATECOL > '2024-01-31'.
    private static object ParseLike(string text, object like)
    {
        var trimmed = text.Trim();
        if (IsNumber(like) && decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number;
        if (like is DateTime && DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
            return moment;
        if (like is bool && TryParseBool(trimmed, out var flag))
            return flag;
        throw DbfSqlError.Value($"'{Truncate(text)}' can't be compared with a {Describe(like)}.",
            "Pass a parameter of the column's type instead of text, or text in invariant form (2024-01-31, 12.5, true).");
    }

    private static string Describe(object value) => value switch
    {
        string => "text value",
        DateTime => "date",
        bool => "true/false value",
        byte[] => "binary value",
        _ when IsNumber(value) => "number",
        _ => value.GetType().Name,
    };

    private static string Truncate(string text) => text.Length <= 40 ? text : text[..40] + "...";

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.ToUpperInvariant())
        {
            case "TRUE" or "T" or "Y" or "YES" or "1" or ".T.":
                value = true;
                return true;
            case "FALSE" or "F" or "N" or "NO" or "0" or ".F.":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    /// <summary>
    /// SQL LIKE with % (any run) and _ (one character), case-insensitive, trailing spaces ignored.
    /// Runs in O(text x pattern) at worst, so a hostile pattern can't make it backtrack forever.
    /// </summary>
    public static bool Like(string text, string pattern)
    {
        text = text.TrimEnd(' ');
        int t = 0, p = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '_' || CharEquals(pattern[p], text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '%')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '%')
            p++;
        return p == pattern.Length;
    }

    private static bool CharEquals(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    // ---- storing --------------------------------------------------------------------------

    /// <summary>
    /// Converts a parameter or literal into the value the field stores, or throws a
    /// <see cref="DbfException"/> naming the field and the problem.
    /// </summary>
    public static object? ForField(DbfField field, object? value)
    {
        value = FromParameter(value);
        if (value is null)
            return null;

        try
        {
            return field.Type switch
            {
                'C' => value switch
                {
                    string s => s,
                    Guid g => g.ToString(),
                    _ when IsNumber(value) => Convert.ToString(value, CultureInfo.InvariantCulture),
                    _ => throw Mismatch(field, value),
                },
                'N' or 'F' or 'Y' or 'B' when field.Type != 'B' || field.Length == 8 => value switch
                {
                    double or float => value,
                    string s => decimal.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                    _ when IsNumber(value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                    _ => throw Mismatch(field, value),
                },
                'I' when field.Length == 4 => value switch
                {
                    string s => int.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture),
                    decimal d when d != decimal.Truncate(d) => throw Mismatch(field, value),
                    double d when d != Math.Truncate(d) => throw Mismatch(field, value),
                    _ when IsNumber(value) => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                    _ => throw Mismatch(field, value),
                },
                'D' or 'T' => value switch
                {
                    DateTime dt => dt,
                    string s => DateTime.Parse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None),
                    _ => throw Mismatch(field, value),
                },
                'L' => value switch
                {
                    bool b => b,
                    string s when TryParseBool(s.Trim(), out var b) => b,
                    _ => throw Mismatch(field, value),
                },
                'M' => value is string or byte[] ? value : throw Mismatch(field, value),
                _ => value is byte[] ? value : throw Mismatch(field, value),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            throw DbfSqlError.Value($"Column {field.Name} ({TypeName(field)}) can't store {Show(value)}: {ex.Message}",
                $"Pass a {ClrType(field).Name} for {field.Name}.", ex);
        }
    }

    private static DbfException Mismatch(DbfField field, object value) =>
        DbfSqlError.Value($"Column {field.Name} ({TypeName(field)}) can't store a {Describe(value)}.",
            $"Pass a {ClrType(field).Name} (or null) for {field.Name}.");

    private static string Show(object value) => value is string s ? $"'{Truncate(s)}'" : $"a {Describe(value)}";
}
