//     ____________
//    | CONTACTS   |   ProjectThunder :: Thunder.Dbf CRM sample
//    | NAME  TEL  |   Any dBASE or FoxPro table as a contact list: its own fields, its own
//    |____________|   format, its own indexes. The phone edits it; FoxPro opens it again.

using System.Globalization;
using Thunder.Dbf;

namespace ThunderDbfMaui;

/// <summary>One row of the contact list: its record index and what the list shows.</summary>
public sealed record ContactRow(int Index, string Title, string Subtitle, string FollowUp, bool Due);

/// <summary>
/// A contact table: the app's sample, or a table someone opened from Files. Which field is the
/// name, the company and so on is worked out from the field names, so a 1990s FoxPro CUSTOMER.DBF
/// works as well as the sample. Every call opens the table and closes it again, as an app sharing
/// the file with other programs should.
/// </summary>
public sealed class CrmTable
{
    private CrmTable(string path, DbfFieldMap map)
    {
        Path = path;
        Map = map;
    }

    public string Path { get; }

    public DbfFieldMap Map { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    public static CrmTable Open(string path)
    {
        using var table = DbfTable.Open(path);
        return new CrmTable(path, DbfFieldMap.Guess(table.Fields));
    }

    /// <summary>The sample: a dBASE IV table with a memo for notes and an index on the name.</summary>
    public static CrmTable CreateSample(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, "contacts.dbf");
        if (!File.Exists(path))
        {
            using (var table = DbfTable.Create(path,
            [
                DbfFieldDefinition.Numeric("ID", 6), DbfFieldDefinition.Character("NAME", 40),
                DbfFieldDefinition.Character("COMPANY", 40), DbfFieldDefinition.Character("PHONE", 20),
                DbfFieldDefinition.Character("EMAIL", 60), DbfFieldDefinition.Date("FOLLOWUP"),
                DbfFieldDefinition.Memo("NOTES"),
            ]))
            {
                string[] first = ["Ada", "Grace", "Alan", "Linus", "Barbara", "Dennis", "Margaret", "Ken", "Frances", "Edsger"];
                string[] last = ["Lovelace", "Hopper", "Turing", "Torvalds", "Liskov", "Ritchie", "Hamilton", "Thompson", "Allen", "Dijkstra"];
                string[] companies = ["Analytical Engines", "COBOL Works", "Bletchley Systems", "Kernel & Co", "Abstraction Inc", "Bell Street Labs", "Apollo Guidance", "Unix Partners"];
                var today = DateOnly.FromDateTime(DateTime.Today);
                var random = new Random(1979);
                for (var i = 0; i < 60; i++)
                {
                    var name = $"{first[i % first.Length]} {last[(i + i / first.Length) % last.Length]}";
                    table.Append(i + 1, name, companies[i % companies.Length], $"555-{random.Next(1000, 9999)}",
                        $"{name.ToLowerInvariant().Replace(' ', '.')}@example.com",
                        i % 4 == 0 ? today.AddDays(random.Next(-5, 14)) : null,
                        i % 3 == 0 ? $"Met at the {1980 + i % 20} user group. Interested in the upgrade." : null);
                }
            }
            using var exclusive = DbfTable.Open(path, writable: true, exclusive: true);
            exclusive.CreateIndexTag("NAME", "name");
        }
        return Open(path);
    }

    /// <summary>Contacts matching the search (any shown field), optionally only follow-ups due by today.</summary>
    public List<ContactRow> List(string? search, bool dueOnly)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rows = new List<ContactRow>();
        using var table = DbfTable.Open(Path);
        foreach (var record in table.ReadAll())
        {
            if (record.IsDeleted)
                continue;
            var title = Map.Title(record);
            var subtitle = Map.Subtitle(record);
            var followUp = Map.FollowUp is { } f ? record[f] as DateOnly? : null;
            var due = followUp is { } d && d <= today;
            if (dueOnly && !due)
                continue;
            if (!string.IsNullOrWhiteSpace(search)
                && !title.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !subtitle.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(new ContactRow(record.Index, title, subtitle, followUp is { } date ? date.ToString("d MMM", CultureInfo.CurrentCulture) : "", due));
        }
        return [.. rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Every field of one record as text, in field order (hidden system fields left out).</summary>
    public List<(DbfField Field, string Text)> Read(int index)
    {
        using var table = DbfTable.Open(Path);
        var record = table.Read(index);
        return [.. table.Fields.Select((f, i) => (f, i)).Where(x => !x.f.IsSystem).Select(x => (x.f, DbfFieldMap.ToText(record[x.i])))];
    }

    /// <summary>The fields a new record gets an editor for.</summary>
    public List<DbfField> Fields()
    {
        using var table = DbfTable.Open(Path);
        return [.. table.Fields.Where(f => !f.IsSystem)];
    }

    /// <summary>Saves edited text into a record (or appends one when index is null); returns its index.</summary>
    public int Save(int? index, IReadOnlyDictionary<string, string> texts)
    {
        using var table = DbfTable.Open(Path, writable: true, maintainFoxProIndexes: true);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in table.Fields.Where(f => !f.IsSystem))
        {
            if (texts.TryGetValue(field.Name, out var text))
                values[field.Name] = DbfFieldMap.FromText(field, text);
        }
        if (index is { } existing)
        {
            table.UpdateFields(existing, values);
            return existing;
        }
        var row = table.Fields.Where(f => !f.IsSystem).Select(f => values.GetValueOrDefault(f.Name)).ToArray();
        return table.Append(row);
    }

    public void Delete(int index)
    {
        using var table = DbfTable.Open(Path, writable: true, maintainFoxProIndexes: true);
        table.Delete(index);
    }

    /// <summary>What the table is: format, size, and the files that travel with it.</summary>
    public string Describe()
    {
        using var table = DbfTable.Open(Path);
        var format = table.Header.IsVisualFoxPro ? "Visual FoxPro" : table.Header.IsFoxPro ? "FoxPro" : "dBASE";
        var live = table.ReadAll().Count(r => !r.IsDeleted);
        return $"{Name}: {format}, {live:N0} contacts, {table.Fields.Count(f => !f.IsSystem)} fields\n"
            + $"Files: {string.Join(", ", Companions().Select(System.IO.Path.GetFileName))}\n"
            + $"Name: {Map.Name ?? "?"}, company: {Map.Company ?? "-"}, phone: {Map.Phone ?? "-"}, email: {Map.Email ?? "-"}, "
            + $"follow-up: {Map.FollowUp ?? "-"}, notes: {Map.Notes ?? "-"}";
    }

    /// <summary>The table and the memo and index files beside it, which share its name.</summary>
    public List<string> Companions()
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(Path);
        string[] extensions = [".dbf", ".dbt", ".fpt", ".mdx", ".cdx"];
        return [.. Directory.GetFiles(folder).Where(f => string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
            && extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))];
    }
}

/// <summary>Which of a table's fields play which part in a contact, guessed from their names.</summary>
public sealed record DbfFieldMap(string? Name, string? First, string? Last, string? Company, string? Phone, string? Email, string? FollowUp, string? Notes)
{
    public static DbfFieldMap Guess(IReadOnlyList<DbfField> fields)
    {
        string? Find(Func<DbfField, bool> type, params string[] names) =>
            names.Select(n => fields.FirstOrDefault(f => !f.IsSystem && type(f) && f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .Concat(names.Select(n => fields.FirstOrDefault(f => !f.IsSystem && type(f) && f.Name.Contains(n, StringComparison.OrdinalIgnoreCase))))
                .FirstOrDefault(f => f is not null)?.Name;
        static bool Text(DbfField f) => f.Type is 'C' or 'V';
        static bool Date(DbfField f) => f.Type is 'D' or 'T';
        static bool Memo(DbfField f) => f.Type is 'M';

        var first = Find(Text, "FIRST", "FNAME", "FIRSTNAME");
        var last = Find(Text, "LAST", "LNAME", "LASTNAME", "SURNAME");
        var name = Find(Text, "NAME", "CONTACT", "FULLNAME", "CUSTNAME", "CUSTOMER");
        var company = Find(Text, "COMPANY", "COMPNAME", "FIRM", "ORG", "BUSINESS");
        if (name == company)
            company = null;
        name ??= first is null && last is null ? company ?? fields.FirstOrDefault(f => !f.IsSystem && Text(f))?.Name : null;
        return new DbfFieldMap(name, first, last, company,
            Find(Text, "PHONE", "TEL", "PHONE1", "MOBILE", "CELL"),
            Find(Text, "EMAIL", "E_MAIL", "MAIL"),
            Find(Date, "FOLLOWUP", "NEXTCALL", "CALLBACK", "NEXT", "REMIND", "DUE"),
            Find(Memo, "NOTES", "NOTE", "COMMENTS", "COMMENT", "REMARKS", "MEMO"));
    }

    public string Title(DbfRecord record)
    {
        var name = Name is { } n ? ToText(record[n]) : "";
        if (name.Length == 0 && (First is not null || Last is not null))
            name = $"{(First is { } f ? ToText(record[f]) : "")} {(Last is { } l ? ToText(record[l]) : "")}".Trim();
        return name.Length > 0 ? name : $"Record {record.RecordNumber}";
    }

    public string Subtitle(DbfRecord record) =>
        string.Join("  ·  ", new[] { Company, Phone, Email }.Where(f => f is not null).Select(f => ToText(record[f!])).Where(t => t.Length > 0));

    /// <summary>A field's value as editable text.</summary>
    public static string ToText(object? value) => value switch
    {
        null => "",
        string s => s.TrimEnd(),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        bool b => b ? "Y" : "N",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        byte[] bytes => $"({bytes.Length} bytes)",
        _ => value.ToString() ?? "",
    };

    /// <summary>Edited text back to the field's type; blank is null. Throws FormatException on bad input.</summary>
    public static object? FromText(DbfField field, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        var invariant = CultureInfo.InvariantCulture;
        return field.Type switch
        {
            'N' or 'F' or 'Y' => decimal.Parse(text, NumberStyles.Number, invariant),
            'I' => int.Parse(text, NumberStyles.Integer, invariant),
            'B' => double.Parse(text, NumberStyles.Float, invariant),
            'D' => DateOnly.Parse(text, invariant),
            'T' => DateTime.Parse(text, invariant),
            'L' => text.ToUpperInvariant() is "Y" or "T" or "YES" or "TRUE" or "1",
            _ => text,
        };
    }
}
