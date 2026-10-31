//    { "_recno": 7,                     ProjectThunder :: Thunder.Dbf
//      "NAME": "Smith",       <--->     +---+----------------+------------+----------+
//      "BALANCE": 1234.50,              |   | Smith          |    1234.50 | 19970412 |
//      "HIRED": "1997-04-12" }          +---+----------------+------------+----------+
//
// Records out as JSON objects, and JSON back in, checked against the table's own schema
// (type, width, decimals, code page) before a single byte is written.

using System.Globalization;
using System.Text.Json;

namespace Thunder.Dbf.Server;

/// <summary>Converts records to JSON-ready objects and validates JSON bodies against a table's fields.</summary>
public static class RecordJson
{
    public const string RecnoProperty = "_recno";
    public const string DeletedProperty = "_deleted";

    /// <summary>Fields the API exposes: never Visual FoxPro's internal _NullFlags, and only those the key may see.</summary>
    public static IEnumerable<(int Index, DbfField Field)> VisibleFields(DbfTable table, Func<string, bool>? canSee) =>
        table.Fields.Select((f, i) => (i, f)).Where(x => !x.f.IsSystem && x.f.Type != '0' && (canSee is null || canSee(x.f.Name)));

    /// <summary>A record as an ordered object: <c>_recno</c>, <c>_deleted</c>, then each visible field.</summary>
    public static Dictionary<string, object?> ToJson(DbfRecord record, DbfTable table, Func<string, bool>? canSee)
    {
        var json = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [RecnoProperty] = record.RecordNumber,
            [DeletedProperty] = record.IsDeleted,
        };
        foreach (var (index, field) in VisibleFields(table, canSee))
            json[field.Name] = ToJsonValue(record[index]);
        return json;
    }

    // System.Text.Json already writes DateOnly as yyyy-MM-dd, decimals as numbers and byte[] as base64.
    private static object? ToJsonValue(object? value) => value switch
    {
        double d when !double.IsFinite(d) => null,
        DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF", CultureInfo.InvariantCulture),
        _ => value,
    };

    /// <summary>The DBF type letter in words, for the schema endpoint.</summary>
    public static string TypeName(DbfField field) => field.Type switch
    {
        'C' => "character",
        'N' => "numeric",
        'F' => "float",
        'D' => "date",
        'L' => "logical",
        'M' when !field.IsBinary => "memo",
        'M' or 'B' when field.Length != 8 => "binary memo",
        'G' => "general (OLE) memo",
        'P' => "picture memo",
        'I' => "integer",
        'Y' => "currency",
        'B' => "double",
        'T' => "datetime",
        _ => "unsupported",
    };

    /// <summary>The JSON type a client should send, or null when the field can't be written through the API.</summary>
    public static string? JsonType(DbfField field) => field.Type switch
    {
        'C' or 'D' or 'T' => "string",
        'M' when !field.IsBinary => "string",
        'M' or 'G' or 'P' => "string (base64)",
        'B' when field.Length != 8 => "string (base64)",
        'N' or 'F' or 'I' or 'Y' or 'B' => "number",
        'L' => "boolean",
        _ => null,
    };

    public static bool IsWritable(DbfField field) => !field.IsAutoIncrement && JsonType(field) is not null;

    /// <summary>
    /// Validates a JSON object against the table and returns the new value for each field it names,
    /// by field index. Fields the key can't see are treated as unknown, so the error doesn't confirm they exist.
    /// </summary>
    public static Dictionary<int, object?> Parse(JsonElement body, DbfTable table, Func<string, bool> canSee)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw ApiProblem.BadRequest("The body must be a JSON object of field names and values.");

        var visible = VisibleFields(table, canSee).ToDictionary(x => x.Field.Name, StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<int, object?>();
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in body.EnumerateObject())
        {
            // Clients may send back a record they read; its bookkeeping properties are ignored.
            if (property.Name is RecnoProperty or DeletedProperty)
                continue;

            if (!visible.TryGetValue(property.Name, out var entry))
            {
                errors[property.Name] = ["The table has no field by that name."];
                continue;
            }
            if (values.ContainsKey(entry.Index))
            {
                errors[entry.Field.Name] = ["The field is given more than once."];
                continue;
            }

            var error = TryConvert(entry.Field, property.Value, table, out var value);
            if (error is not null)
                errors[entry.Field.Name] = [error];
            else
                values[entry.Index] = value;
        }

        if (errors.Count > 0)
            throw new ApiValidationProblem(errors);
        return values;
    }

    // Returns an error message, or null with the converted value.
    private static string? TryConvert(DbfField field, JsonElement json, DbfTable table, out object? value)
    {
        value = null;
        if (field.IsAutoIncrement)
            return "The field is numbered automatically and can't be set.";
        if (JsonType(field) is not { } expected)
            return $"Fields of type '{field.Type}' can't be written through the API yet.";

        if (json.ValueKind == JsonValueKind.Null)
        {
            // Visual FoxPro's binary number types have no blank: null would quietly become 0.
            return field.Type is 'I' or 'Y' || (field.Type == 'B' && field.Length == 8) ? field.IsNullable ? null : "The field can't be null." : null;
        }

        switch (field.Type)
        {
            case 'C':
            {
                if (json.ValueKind != JsonValueKind.String)
                    return Expected(expected);
                var text = json.GetString()!;
                var bytes = table.Encoding.GetBytes(text);
                if (table.Encoding.GetString(bytes) != text)
                    return $"The text has characters code page {table.Encoding.CodePage} can't store.";
                if (bytes.Length > field.Length)
                    return $"The field holds {field.Length} bytes; this text needs {bytes.Length}.";
                value = text;
                return null;
            }

            case 'N' or 'F':
            {
                if (json.ValueKind != JsonValueKind.Number)
                    return Expected(expected);
                if (!json.TryGetDecimal(out var number))
                    return "The number is too large.";
                if (decimal.Round(number, field.DecimalCount) != number)
                    return field.DecimalCount == 0 ? "The field holds whole numbers only." : $"The field keeps {field.DecimalCount} decimal places; this number has more.";
                var formatted = number.ToString("F" + field.DecimalCount, CultureInfo.InvariantCulture);
                if (formatted.Length > field.Length)
                    return $"The field is {field.Length} characters wide ({field.DecimalCount} decimals); {formatted} doesn't fit.";
                value = number;
                return null;
            }

            case 'I':
            {
                if (json.ValueKind != JsonValueKind.Number || !json.TryGetInt32(out var number))
                    return "The field needs a whole number between -2147483648 and 2147483647.";
                value = number;
                return null;
            }

            case 'Y':
            {
                if (json.ValueKind != JsonValueKind.Number || !json.TryGetDecimal(out var number))
                    return Expected(expected);
                if (decimal.Round(number, 4) != number)
                    return "Currency keeps 4 decimal places; this number has more.";
                if (Math.Abs(number) > 922_337_203_685_477.5807m)
                    return "The number is outside the currency range.";
                value = number;
                return null;
            }

            case 'B' when field.Length == 8:
            {
                if (json.ValueKind != JsonValueKind.Number || !json.TryGetDouble(out var number) || !double.IsFinite(number))
                    return Expected(expected);
                value = number;
                return null;
            }

            case 'D':
            {
                if (json.ValueKind != JsonValueKind.String
                    || !DateOnly.TryParseExact(json.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return "The field needs a date like \"1997-04-12\".";
                value = date;
                return null;
            }

            case 'T':
            {
                string[] formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd"];
                if (json.ValueKind != JsonValueKind.String
                    || !DateTime.TryParseExact(json.GetString(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
                    return "The field needs a date and time like \"1997-04-12T14:30:00\", without a time zone.";
                value = moment;
                return null;
            }

            case 'L':
            {
                if (json.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Expected(expected);
                value = json.GetBoolean();
                return null;
            }

            case 'M' when !field.IsBinary:
            {
                if (json.ValueKind != JsonValueKind.String)
                    return Expected(expected);
                if (table.MemoFormat is null)
                    return "The table's memo file is missing, so memos can't be written.";
                var text = json.GetString()!;
                if (table.Encoding.GetString(table.Encoding.GetBytes(text)) != text)
                    return $"The text has characters code page {table.Encoding.CodePage} can't store.";
                value = text;
                return null;
            }

            default: // binary memos
            {
                if (json.ValueKind != JsonValueKind.String)
                    return Expected(expected);
                if (table.MemoFormat is null)
                    return "The table's memo file is missing, so memos can't be written.";
                try
                {
                    value = Convert.FromBase64String(json.GetString()!);
                    return null;
                }
                catch (FormatException)
                {
                    return "The field needs base64-encoded bytes.";
                }
            }
        }
    }

    private static string Expected(string type) => $"The field needs a JSON {type}.";

    /// <summary>A record's full values for the write journal: every field, hidden or not.</summary>
    public static Dictionary<string, object?> ForJournal(DbfRecord record, DbfTable table) => ToJson(record, table, canSee: null);

    /// <summary>One parsed request value as the journal writes it.</summary>
    public static object? ForJournalValue(object? value) => ToJsonValue(value);
}
