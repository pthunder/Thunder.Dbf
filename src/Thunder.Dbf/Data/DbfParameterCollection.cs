//     ___     ___     ___
//    |@id|   |@nm|   |@ct|  ...      ProjectThunder :: Thunder.Dbf
//    |___|   |___|   |___|           The parameters of one DbfCommand, found by
//      |_______|_______|____\        name with or without the '@'.
//                           /

using System.Collections;
using System.Data.Common;

namespace Thunder.Dbf.Data;

/// <summary>The <see cref="DbfParameter"/>s of a <see cref="DbfCommand"/>.</summary>
public sealed class DbfParameterCollection : DbParameterCollection
{
    private readonly List<DbfParameter> _items = [];

    /// <inheritdoc />
    public override int Count => _items.Count;

    /// <inheritdoc />
    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    /// <summary>Adds a parameter with a name and value, and returns it.</summary>
    public DbfParameter AddWithValue(string parameterName, object? value)
    {
        var parameter = new DbfParameter(parameterName, value);
        _items.Add(parameter);
        return parameter;
    }

    /// <inheritdoc />
    public override int Add(object value)
    {
        _items.Add(Cast(value));
        return _items.Count - 1;
    }

    /// <inheritdoc />
    public override void AddRange(Array values)
    {
        foreach (var value in values)
            Add(value!);
    }

    /// <inheritdoc />
    public override void Clear() => _items.Clear();

    /// <inheritdoc />
    public override bool Contains(object value) => value is DbfParameter p && _items.Contains(p);

    /// <inheritdoc />
    public override bool Contains(string value) => IndexOf(value) >= 0;

    /// <inheritdoc />
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    public override int IndexOf(object value) => value is DbfParameter p ? _items.IndexOf(p) : -1;

    /// <inheritdoc />
    public override int IndexOf(string parameterName)
    {
        var bare = DbfParameter.Bare(parameterName ?? "");
        return _items.FindIndex(p => p.BareName.Equals(bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public override void Insert(int index, object value) => _items.Insert(index, Cast(value));

    /// <inheritdoc />
    public override void Remove(object value) => _items.Remove(Cast(value));

    /// <inheritdoc />
    public override void RemoveAt(int index) => _items.RemoveAt(index);

    /// <inheritdoc />
    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOrThrow(parameterName));

    /// <summary>The parameter at <paramref name="index"/>.</summary>
    public new DbfParameter this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>The parameter called <paramref name="parameterName"/>.</summary>
    public new DbfParameter this[string parameterName]
    {
        get => _items[IndexOrThrow(parameterName)];
        set => _items[IndexOrThrow(parameterName)] = value;
    }

    /// <inheritdoc />
    protected override DbParameter GetParameter(int index) => _items[index];

    /// <inheritdoc />
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOrThrow(parameterName)];

    /// <inheritdoc />
    protected override void SetParameter(int index, DbParameter value) => _items[index] = Cast(value);

    /// <inheritdoc />
    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOrThrow(parameterName)] = Cast(value);

    /// <summary>The value for <c>@name</c> in the SQL, or an explained error when nobody added it.</summary>
    internal object? ValueFor(string name)
    {
        var index = IndexOf(name);
        if (index < 0)
            throw DbfSqlError.Value($"The command uses @{name}, but no parameter with that name was added.",
                $"Add it, like command.Parameters.AddWithValue(\"{name}\", value). Use DBNull.Value (or null) for NULL.");
        return _items[index].Value;
    }

    private int IndexOrThrow(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index >= 0 ? index : throw new IndexOutOfRangeException($"There's no parameter named '{parameterName}'.");
    }

    private static DbfParameter Cast(object? value) => value as DbfParameter
        ?? throw new InvalidCastException($"Only DbfParameter objects can be added, not {value?.GetType().Name ?? "null"}. Use command.CreateParameter().");
}
