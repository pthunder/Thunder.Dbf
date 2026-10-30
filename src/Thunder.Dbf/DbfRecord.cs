namespace Thunder.Dbf;

/// <summary>One row read from a DBF table.</summary>
public sealed class DbfRecord
{
    private readonly DbfHeader _header;
    private readonly object?[] _values;

    internal DbfRecord(DbfHeader header, int index, bool isDeleted, object?[] values)
    {
        _header = header;
        Index = index;
        IsDeleted = isDeleted;
        _values = values;
    }

    internal DbfHeader Header => _header;

    /// <summary>Zero-based position in the table.</summary>
    public int Index { get; }

    /// <summary>One-based record number, as dBASE's RECNO() reports it.</summary>
    public int RecordNumber => Index + 1;

    /// <summary>True when the record is marked deleted ('*') but not yet packed away.</summary>
    public bool IsDeleted { get; }

    public IReadOnlyList<object?> Values => _values;

    public object? this[int fieldIndex] => _values[fieldIndex];

    public object? this[string fieldName]
    {
        get
        {
            for (var i = 0; i < _header.Fields.Count; i++)
            {
                if (string.Equals(_header.Fields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
                    return _values[i];
            }
            throw new KeyNotFoundException($"The table has no field named '{fieldName}'.");
        }
    }
}
