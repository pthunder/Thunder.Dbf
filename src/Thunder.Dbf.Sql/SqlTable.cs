//    ________________________________________
//   | _id | _recno | _deleted | NAME | QTY | |    ProjectThunder :: Thunder.Dbf.Sql
//   |-----|--------|----------|------|-----| |    The SQL twin of a DBF table: the same fields,
//   |  1  |   1    |    0     | ...  | ... | |    plus _id (the SQL key), _recno (which DBF
//   |  2  |  NULL  |    0     | ...  | ... | |    record it mirrors; NULL until a new SQL row
//   |_____|________|__________|______|_____|_|    reaches the DBF) and _deleted.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Thunder.Dbf.Sql;

/// <summary>The SQL table that mirrors one DBF table, and the SQL to read and write it.</summary>
internal sealed class SqlTable
{
    public const string IdColumn = "_id";
    public const string RecnoColumn = "_recno";
    public const string DeletedColumn = "_deleted";

    private readonly DbConnection _connection;
    private readonly SqlDialect _dialect;

    public SqlTable(DbConnection connection, SqlDialect dialect, string name, IReadOnlyList<DbfField> fields)
    {
        _connection = connection;
        _dialect = dialect;
        Name = name;
        Fields = fields.Where(f => !f.IsSystem).ToList();

        // A DBF field called _ID, _RECNO or _DELETED would collide with the mirror's own columns.
        if (Fields.FirstOrDefault(f => IsReserved(f.Name)) is { } clash)
            throw new DbfException(DbfErrorCode.UnsupportedFormat,
                $"The table has a field named {clash.Name}, which the SQL mirror uses for its own column.",
                $"The mirror adds {IdColumn}, {RecnoColumn} and {DeletedColumn}. Rename the DBF field, or copy the table to one without it first.");
    }

    public static bool IsReserved(string name) =>
        name.Equals(IdColumn, StringComparison.OrdinalIgnoreCase)
        || name.Equals(RecnoColumn, StringComparison.OrdinalIgnoreCase)
        || name.Equals(DeletedColumn, StringComparison.OrdinalIgnoreCase);

    public string Name { get; }

    /// <summary>The DBF fields mirrored as columns (Visual FoxPro's hidden _NullFlags is left out).</summary>
    public IReadOnlyList<DbfField> Fields { get; }

    private string Q(string identifier) => _dialect.Quote(identifier);

    public bool Exists(DbTransaction? transaction = null)
    {
        try
        {
            using var command = Command($"select count(*) from {Q(Name)} where 1 = 0", transaction);
            command.ExecuteScalar();
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    public void Create(DbTransaction? transaction = null)
    {
        var columns = new List<string>
        {
            _dialect.IdentityColumn(IdColumn),
            _dialect.UniqueAllowsManyNulls ? $"{Q(RecnoColumn)} bigint null unique" : $"{Q(RecnoColumn)} bigint null",
            $"{Q(DeletedColumn)} {_dialect.BooleanType} not null default {(_dialect.BooleanType == "boolean" ? "false" : "0")}",
        };
        columns.AddRange(Fields.Select(f => $"{Q(f.Name)} {_dialect.TypeFor(f)} null"));
        using (var command = Command($"create table {Q(Name)} ({string.Join(", ", columns)})", transaction))
            command.ExecuteNonQuery();

        // SQL Server's UNIQUE allows only one NULL, and every row added on the SQL side starts with a
        // NULL _recno, so it gets a unique index that leaves the NULLs out instead.
        if (!_dialect.UniqueAllowsManyNulls)
        {
            using var index = Command($"create unique index {Q("ux_" + Name + RecnoColumn)} on {Q(Name)} ({Q(RecnoColumn)}) " +
                $"where {Q(RecnoColumn)} is not null", transaction);
            index.ExecuteNonQuery();
        }
    }

    public void Drop(DbTransaction? transaction = null)
    {
        using var command = Command($"drop table {Q(Name)}", transaction);
        command.ExecuteNonQuery();
    }

    public void Insert(int? recno, bool deleted, IReadOnlyList<object?> values, DbTransaction? transaction)
    {
        var names = new[] { RecnoColumn, DeletedColumn }.Concat(Fields.Select(f => f.Name)).ToList();
        using var command = Command($"insert into {Q(Name)} ({string.Join(", ", names.Select(Q))}) " +
            $"values ({string.Join(", ", names.Select((_, i) => $"@p{i}"))})", transaction);
        AddParameter(command, "@p0", recno.HasValue ? (long)recno.Value : null);
        AddParameter(command, "@p1", deleted);
        for (var i = 0; i < values.Count; i++)
            AddParameter(command, $"@p{i + 2}", values[i]);
        command.ExecuteNonQuery();
    }

    public void UpdateByRecno(int recno, bool deleted, IReadOnlyList<object?> values, DbTransaction? transaction)
    {
        var sets = new List<string> { $"{Q(DeletedColumn)} = @deleted" };
        sets.AddRange(Fields.Select((f, i) => $"{Q(f.Name)} = @p{i}"));
        using var command = Command($"update {Q(Name)} set {string.Join(", ", sets)} where {Q(RecnoColumn)} = @recno", transaction);
        AddParameter(command, "@deleted", deleted);
        for (var i = 0; i < values.Count; i++)
            AddParameter(command, $"@p{i}", values[i]);
        AddParameter(command, "@recno", (long)recno);
        command.ExecuteNonQuery();
    }

    public void SetRecno(long id, int recno, DbTransaction? transaction)
    {
        using var command = Command($"update {Q(Name)} set {Q(RecnoColumn)} = @recno where {Q(IdColumn)} = @id", transaction);
        AddParameter(command, "@recno", (long)recno);
        AddParameter(command, "@id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Every row: its _id, _recno, _deleted and field values (converted to DBF types).</summary>
    public List<SqlRow> ReadAll(DbTransaction? transaction = null)
    {
        var columns = new[] { IdColumn, RecnoColumn, DeletedColumn }.Concat(Fields.Select(f => f.Name));
        using var command = Command($"select {string.Join(", ", columns.Select(Q))} from {Q(Name)}", transaction);
        using var reader = command.ExecuteReader();
        var rows = new List<SqlRow>();
        while (reader.Read())
        {
            var values = new object?[Fields.Count];
            for (var i = 0; i < Fields.Count; i++)
                values[i] = _dialect.FromDatabase(reader.GetValue(i + 3), Fields[i]);
            rows.Add(new SqlRow(
                System.Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : System.Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.GetValue(2) is bool b ? b : System.Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) != 0,
                values));
        }
        return rows;
    }

    /// <summary>The DBF record's field values in column order, ready for SQL.</summary>
    public object?[] ValuesOf(DbfRecord record, DbfTable table)
    {
        var values = new object?[Fields.Count];
        for (var i = 0; i < Fields.Count; i++)
            values[i] = record[IndexIn(table, Fields[i])];
        return values;
    }

    public static int IndexIn(DbfTable table, DbfField field)
    {
        for (var i = 0; i < table.Fields.Count; i++)
        {
            if (ReferenceEquals(table.Fields[i], field) || table.Fields[i].Name == field.Name)
                return i;
        }
        throw new KeyNotFoundException(field.Name);
    }

    /// <summary>
    /// A fingerprint of a row's content (deleted flag included), the same whichever side it was read
    /// from: values are first put in a canonical form, so 1.50 from SQL and 1.5 from the DBF agree.
    /// Each value is written as its length and text, and null has its own marker, so no two different
    /// rows can produce the same input to the hash.
    /// </summary>
    public string Fingerprint(bool deleted, IReadOnlyList<object?> values)
    {
        var text = new StringBuilder(deleted ? "D" : "L");
        for (var i = 0; i < Fields.Count; i++)
        {
            if (values[i] is null)
            {
                text.Append('|').Append('N');
                continue;
            }
            var canonical = Canonical(Fields[i], values[i]);
            text.Append('|').Append(canonical.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(canonical);
        }
        return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Canonical(DbfField field, object? value) => value switch
    {
        string s => field.Type is 'C' ? s.TrimEnd(' ') : s,
        decimal d => CanonicalDecimal(d),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        int n => n.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "T" : "F",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        byte[] bytes => System.Convert.ToBase64String(bytes),
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    // 1.50 and 1.5 are the same number; decimal remembers the trailing zero, so drop it.
    private static string CanonicalDecimal(decimal d)
    {
        var text = d.ToString(CultureInfo.InvariantCulture);
        return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
    }

    private DbCommand Command(string sql, DbTransaction? transaction)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = _dialect.ToDatabase(value);
        command.Parameters.Add(parameter);
    }
}

/// <summary>One row of a mirrored SQL table.</summary>
internal sealed record SqlRow(long Id, int? Recno, bool Deleted, object?[] Values);
