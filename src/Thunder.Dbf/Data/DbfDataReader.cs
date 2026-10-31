//    ___________________________
//   |  ID |  NAME     |  HIRED   |     ProjectThunder :: Thunder.Dbf
//   |-----+-----------+----------|     Rows stream out one record at a time:
//   |   1 | Smith     | 1997-04  |  <- Read()  the table never has to fit in memory.
//   |   2 | O'Hara    | 2001-11  |
//   |  .. | ...       | ...      |     C -> string   N F Y -> decimal   D T -> DateTime
//   '---------------------------'      L -> bool     I -> int           B -> double   M -> string

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Thunder.Dbf.Data;

/// <summary>
/// Forward-only rows from a <see cref="DbfCommand"/>. Records are read from the file as you call
/// <see cref="Read"/>, so memory stays flat however big the table (ORDER BY keeps only its sort keys).
/// Blank numeric, date and logical fields read as <see cref="DBNull"/>; blank character fields read as "".
/// </summary>
/// <remarks>
/// N and F fields are <see cref="decimal"/>. The rare FoxPro value written in scientific notation beyond
/// decimal's range reads as a <see cref="double"/> from <see cref="GetValue"/>, and <see cref="GetDecimal"/> refuses it.
/// </remarks>
public sealed class DbfDataReader : DbDataReader
{
    private readonly IReadOnlyList<DbfReaderColumn> _columns;
    private readonly Func<DbfRecord, object?>[] _getters;
    private readonly DbfConnection? _closeWith;
    private readonly DbfQuery? _query;
    private readonly int _recordsAffected;
    private IEnumerator<DbfRecord>? _rows;
    private DbfRecord? _current;
    private DbfRecord? _peeked;
    private object?[]? _values;
    private bool _closed;

    internal DbfDataReader(DbfQuery query, CommandBehavior behavior, DbfConnection? connection)
    {
        _query = query;
        _columns = query.Columns;
        _getters = _columns.Select(query.Getter).ToArray();
        _closeWith = connection;
        _recordsAffected = -1;

        if (!behavior.HasFlag(CommandBehavior.SchemaOnly))
        {
            try
            {
                var rows = query.Rows();
                _rows = (behavior.HasFlag(CommandBehavior.SingleRow) ? rows.Take(1) : rows).GetEnumerator();
            }
            catch
            {
                query.Dispose(); // a bad LIMIT or OFFSET value: don't leave the table open
                throw;
            }
        }
    }

    // The empty reader an INSERT, UPDATE or DELETE returns from ExecuteReader.
    internal DbfDataReader(int recordsAffected, DbfConnection? connection)
    {
        _columns = [];
        _getters = [];
        _closeWith = connection;
        _recordsAffected = recordsAffected;
    }

    public override int Depth => 0;

    public override int FieldCount => _columns.Count;

    public override bool HasRows
    {
        get
        {
            ThrowIfClosed();
            if (_current is not null || _peeked is not null)
                return true;
            if (_rows is not null && _rows.MoveNext())
            {
                _peeked = _rows.Current;
                return true;
            }
            return false;
        }
    }

    public override bool IsClosed => _closed;

    /// <summary>Records changed by an INSERT, UPDATE or DELETE; -1 for a SELECT.</summary>
    public override int RecordsAffected => _recordsAffected;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        ThrowIfClosed();
        _values = null;
        if (_peeked is not null)
        {
            (_current, _peeked) = (_peeked, null);
            return true;
        }
        if (_rows is not null && _rows.MoveNext())
        {
            _current = _rows.Current;
            return true;
        }
        _current = null;
        return false;
    }

    public override bool NextResult()
    {
        ThrowIfClosed();
        _rows?.Dispose();
        _rows = null;
        _current = _peeked = null;
        return false;
    }

    public override void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _rows?.Dispose();
        _rows = null;
        _current = _peeked = null;
        _query?.Dispose();
        _closeWith?.Close();
    }

    // ---- columns --------------------------------------------------------------------------

    public override string GetName(int ordinal) => Column(ordinal).Name;

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            if (_columns[i].Name.Equals(name, StringComparison.Ordinal))
                return i;
        }
        for (var i = 0; i < _columns.Count; i++)
        {
            if (_columns[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new IndexOutOfRangeException($"The result has no column named '{name}'.");
    }

    /// <summary>The .NET type of the column's values, decided by its dBASE type (see the class remarks).</summary>
    public override Type GetFieldType(int ordinal) => Column(ordinal).ClrType;

    /// <summary>The dBASE type, like Character, Numeric, Date or Memo.</summary>
    public override string GetDataTypeName(int ordinal) => Column(ordinal).TypeName;

    public override DataTable GetSchemaTable()
    {
        var table = new DataTable("SchemaTable") { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        table.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        table.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        table.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        table.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        table.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        table.Columns.Add(SchemaTableColumn.ProviderType, typeof(int));
        table.Columns.Add(SchemaTableColumn.NonVersionedProviderType, typeof(int));
        table.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        table.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        table.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        table.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        table.Columns.Add(SchemaTableColumn.IsAliased, typeof(bool));
        table.Columns.Add(SchemaTableColumn.IsExpression, typeof(bool));
        table.Columns.Add(SchemaTableColumn.BaseSchemaName, typeof(string));
        table.Columns.Add(SchemaTableColumn.BaseTableName, typeof(string));
        table.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));
        table.Columns.Add(SchemaTableOptionalColumn.BaseCatalogName, typeof(string));
        table.Columns.Add(SchemaTableOptionalColumn.BaseServerName, typeof(string));
        table.Columns.Add(SchemaTableOptionalColumn.IsReadOnly, typeof(bool));
        table.Columns.Add(SchemaTableOptionalColumn.IsRowVersion, typeof(bool));
        table.Columns.Add(SchemaTableOptionalColumn.IsHidden, typeof(bool));
        table.Columns.Add(SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool));
        table.Columns.Add(SchemaTableOptionalColumn.ProviderSpecificDataType, typeof(Type));
        table.Columns.Add("DataTypeName", typeof(string));

        for (var i = 0; i < _columns.Count; i++)
        {
            var column = _columns[i];
            var field = column.Field;
            var isLong = field is not null && DbfValueCodec.IsMemoField(field);
            var numeric = field is { Type: 'N' or 'F' };

            var row = table.NewRow();
            row[SchemaTableColumn.ColumnName] = column.Name;
            row[SchemaTableColumn.ColumnOrdinal] = i;
            row[SchemaTableColumn.ColumnSize] = isLong ? int.MaxValue : field?.Length ?? (column.ClrType == typeof(bool) ? 1 : 4);
            row[SchemaTableColumn.NumericPrecision] = numeric ? (short)field!.Length : DBNull.Value;
            row[SchemaTableColumn.NumericScale] = numeric ? (short)field!.DecimalCount : DBNull.Value;
            row[SchemaTableColumn.DataType] = column.ClrType;
            row[SchemaTableColumn.ProviderType] = (int)(field?.Type ?? (column.ClrType == typeof(bool) ? 'L' : 'I'));
            row[SchemaTableColumn.NonVersionedProviderType] = row[SchemaTableColumn.ProviderType];
            row[SchemaTableColumn.IsLong] = isLong;
            row[SchemaTableColumn.AllowDBNull] = column.AllowsNull;
            row[SchemaTableColumn.IsUnique] = false;
            row[SchemaTableColumn.IsKey] = false;
            row[SchemaTableColumn.IsAliased] = !column.Name.Equals(column.BaseName, StringComparison.Ordinal);
            row[SchemaTableColumn.IsExpression] = false;
            row[SchemaTableColumn.BaseColumnName] = column.BaseName;
            row[SchemaTableOptionalColumn.IsReadOnly] = column.FieldIndex == DbfReaderColumn.RecordNumber;
            row[SchemaTableOptionalColumn.IsRowVersion] = false;
            row[SchemaTableOptionalColumn.IsHidden] = false;
            row[SchemaTableOptionalColumn.IsAutoIncrement] = field?.IsAutoIncrement ?? false;
            row[SchemaTableOptionalColumn.ProviderSpecificDataType] = column.ClrType;
            row["DataTypeName"] = column.TypeName;
            table.Rows.Add(row);
        }
        return table;
    }

    // ---- values ---------------------------------------------------------------------------

    public override object GetValue(int ordinal)
    {
        var values = CurrentValues();
        _ = Column(ordinal);
        return values[ordinal] ?? DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        var current = CurrentValues();
        var count = Math.Min(values.Length, current.Length);
        for (var i = 0; i < count; i++)
            values[i] = current[i] ?? DBNull.Value;
        return count;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override string GetString(int ordinal) => Get<string>(ordinal);

    public override bool GetBoolean(int ordinal) => Get<bool>(ordinal);

    public override DateTime GetDateTime(int ordinal) => Get<DateTime>(ordinal);

    public override decimal GetDecimal(int ordinal) => Number(ordinal, Convert.ToDecimal);

    public override double GetDouble(int ordinal) => Number(ordinal, Convert.ToDouble);

    public override float GetFloat(int ordinal) => Number(ordinal, Convert.ToSingle);

    public override int GetInt32(int ordinal) => Number(ordinal, Convert.ToInt32);

    public override long GetInt64(int ordinal) => Number(ordinal, Convert.ToInt64);

    public override short GetInt16(int ordinal) => Number(ordinal, Convert.ToInt16);

    public override byte GetByte(int ordinal) => Number(ordinal, Convert.ToByte);

    public override char GetChar(int ordinal)
    {
        var text = GetString(ordinal);
        return text.Length > 0 ? text[0] : throw new InvalidCastException($"Column {GetName(ordinal)} is empty, so it has no first character.");
    }

    public override Guid GetGuid(int ordinal) => GetValue(ordinal) switch
    {
        Guid guid => guid,
        string text when Guid.TryParse(text, out var guid) => guid,
        var other => throw Cast(ordinal, other, typeof(Guid)),
    };

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = Get<byte[]>(ordinal);
        if (buffer is null)
            return bytes.Length;
        var count = (int)Math.Max(0, Math.Min(length, bytes.Length - dataOffset));
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = GetString(ordinal);
        if (buffer is null)
            return text.Length;
        var count = (int)Math.Max(0, Math.Min(length, text.Length - dataOffset));
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <summary>Typed access; also gives <see cref="DateOnly"/> for date columns.</summary>
    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (typeof(T) == typeof(DateOnly) && value is DateTime moment)
            return (T)(object)DateOnly.FromDateTime(moment);
        if (value is T typed)
            return typed;
        if (value is DBNull && default(T) is null)
            return default!;
        throw Cast(ordinal, value, typeof(T));
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    // ---- helpers --------------------------------------------------------------------------

    private DbfReaderColumn Column(int ordinal) =>
        ordinal >= 0 && ordinal < _columns.Count
            ? _columns[ordinal]
            : throw new IndexOutOfRangeException($"Column {ordinal} is outside 0..{_columns.Count - 1}.");

    private object?[] CurrentValues()
    {
        ThrowIfClosed();
        if (_current is null)
            throw new InvalidOperationException("There's no current row. Call Read() and check it returned true.");
        if (_values is null)
        {
            _values = new object?[_getters.Length];
            for (var i = 0; i < _getters.Length; i++)
                _values[i] = _getters[i](_current);
        }
        return _values;
    }

    private T Get<T>(int ordinal) => GetValue(ordinal) is T value ? value : throw Cast(ordinal, GetValue(ordinal), typeof(T));

    private T Number<T>(int ordinal, Func<object, IFormatProvider, T> convert)
    {
        var value = GetValue(ordinal);
        if (value is DBNull or string or bool or DateTime or byte[])
            throw Cast(ordinal, value, typeof(T));
        try
        {
            return convert(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException ex)
        {
            throw new InvalidCastException($"Column {GetName(ordinal)} holds {value}, which doesn't fit in {typeof(T).Name}.", ex);
        }
    }

    private InvalidCastException Cast(int ordinal, object value, Type wanted) => new(value is DBNull
        ? $"Column {GetName(ordinal)} is null in this row. Check IsDBNull first, or read it as a nullable type."
        : $"Column {GetName(ordinal)} holds a {value.GetType().Name}, not a {wanted.Name}. Its .NET type is {GetFieldType(ordinal).Name}.");

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException("The reader is closed.");
    }
}
