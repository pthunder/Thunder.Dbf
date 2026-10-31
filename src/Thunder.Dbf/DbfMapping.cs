//      CONTACT1.DBF                    class Contact
//     +------------+------------+      {
//     | COMPANY C  | ZIP  C     |  ==>     public string Company { get; set; }   (by name, any case)
//     | LASTDATE D | CREDIT N   |  ==>     [DbfField("LASTDATE")] public DateTime? LastCall { get; set; }
//     +------------+------------+          [DbfRecordNumber] public int Recno { get; set; }
//                                      }
//     ProjectThunder :: Thunder.Dbf
//     Records as plain C# objects: properties match fields by name, or by [DbfField], and values
//     are converted to the property's type (decimal to int, DateOnly to DateTime, and so on).

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace Thunder.Dbf;

/// <summary>Maps a property to the field with this name, when the names differ.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DbfFieldAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Leaves a property out of reading and writing.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DbfIgnoreAttribute : Attribute;

/// <summary>Receives the record's 1-based number (dBASE's RECNO()) when reading. Never written.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DbfRecordNumberAttribute : Attribute;

/// <summary>Receives the record's deletion mark when reading. Never written; use Delete or Recall.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DbfDeletedAttribute : Attribute;

/// <summary>How one class maps onto one table's fields, worked out once per class and table layout.</summary>
internal sealed class DbfMapper<T> where T : class, new()
{
    private static readonly ConcurrentDictionary<string, DbfMapper<T>> Cache = new();

    private readonly (PropertyInfo Property, int Field)[] _fields;
    private readonly PropertyInfo? _recordNumber;
    private readonly PropertyInfo? _deleted;

    private DbfMapper(IReadOnlyList<DbfField> fields)
    {
        var mapped = new List<(PropertyInfo, int)>();
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.IsDefined(typeof(DbfIgnoreAttribute)) || property.GetIndexParameters().Length > 0)
                continue;
            if (property.IsDefined(typeof(DbfRecordNumberAttribute)))
            {
                _recordNumber = property;
                continue;
            }
            if (property.IsDefined(typeof(DbfDeletedAttribute)))
            {
                _deleted = property;
                continue;
            }

            var explicitName = property.GetCustomAttribute<DbfFieldAttribute>()?.Name;
            var name = explicitName ?? property.Name;
            var index = IndexOf(fields, name);
            if (index < 0)
            {
                // A typo in [DbfField] is a bug worth hearing about; an extra plain property is just unmapped.
                if (explicitName is not null)
                    throw new ArgumentException($"{typeof(T).Name}.{property.Name} is mapped to field {explicitName}, but the table has no such field.");
                continue;
            }
            mapped.Add((property, index));
        }

        if (mapped.Count == 0)
            throw new ArgumentException($"No property of {typeof(T).Name} matches a field of the table. Name properties after fields, or use [DbfField(\"NAME\")].");
        _fields = [.. mapped];
    }

    public static DbfMapper<T> For(DbfTable table)
    {
        var layout = string.Join(",", table.Fields.Select(f => $"{f.Name}:{f.Type}:{f.Length}:{f.DecimalCount}:{f.IsSystem}"));
        return Cache.GetOrAdd(layout, _ => new DbfMapper<T>(table.Fields));
    }

    /// <summary>
    /// The field a property maps to, or -1, for the LINQ provider. Only properties that are set from
    /// the field when reading count: a get-only property never holds the field's value, so a seek on
    /// it could disagree with a scan.
    /// </summary>
    public int FieldOf(MemberInfo member) =>
        _fields.FirstOrDefault(f => f.Property.Name == member.Name && f.Property.DeclaringType == member.DeclaringType) is { Property: { CanWrite: true, CanRead: true } } hit
            ? hit.Field
            : -1;

    /// <summary>The property type a field maps to, for <see cref="FieldOf"/>'s result.</summary>
    public Type PropertyTypeOf(int field) => _fields.First(f => f.Field == field).Property.PropertyType;

    public T ToObject(DbfRecord record)
    {
        var item = new T();
        foreach (var (property, field) in _fields)
        {
            if (!property.CanWrite)
                continue;
            property.SetValue(item, Convert(record[field], property.PropertyType, property.Name));
        }
        _recordNumber?.SetValue(item, Convert(record.RecordNumber, _recordNumber.PropertyType, _recordNumber.Name));
        _deleted?.SetValue(item, record.IsDeleted);
        return item;
    }

    /// <summary>Values in field order for Append; unmapped fields are left empty.</summary>
    public object?[] ToValues(T item, int fieldCount)
    {
        var values = new object?[fieldCount];
        foreach (var (property, field) in _fields)
            values[field] = ToField(property.GetValue(item));
        return values;
    }

    /// <summary>Every mapped field by name, for UpdateFields.</summary>
    public Dictionary<string, object?> ToChanges(T item, IReadOnlyList<DbfField> fields)
    {
        var changes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (property, field) in _fields)
        {
            if (property.CanRead)
                changes[fields[field].Name] = ToField(property.GetValue(item));
        }
        return changes;
    }

    // Enums are stored as their number, as legacy code stored codes.
    private static object? ToField(object? value) =>
        value is Enum e ? System.Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture) : value;

    private static int IndexOf(IReadOnlyList<DbfField> fields, string name)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (!fields[i].IsSystem && fields[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Converts a field value to a property type: numbers between numeric types, dates both ways, text trimmed.</summary>
    internal static object? Convert(object? value, Type type, string property)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (value is null)
        {
            if (!type.IsValueType || Nullable.GetUnderlyingType(type) is not null)
                return null;
            return Activator.CreateInstance(type); // a blank field reads as the type's default
        }
        if (target.IsInstanceOfType(value))
            return value;

        try
        {
            return (value, target) switch
            {
                (DateOnly d, _) when target == typeof(DateTime) => d.ToDateTime(TimeOnly.MinValue),
                (DateOnly d, _) when target == typeof(DateTimeOffset) => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                (DateTime t, _) when target == typeof(DateOnly) => DateOnly.FromDateTime(t),
                (_, _) when target.IsEnum => Enum.ToObject(target, System.Convert.ChangeType(value, Enum.GetUnderlyingType(target), CultureInfo.InvariantCulture)),
                (_, _) when target == typeof(string) => System.Convert.ToString(value, CultureInfo.InvariantCulture),
                (string s, _) when target == typeof(char) => s.Length > 0 ? s[0] : '\0',
                _ => System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new InvalidCastException($"Can't put the {value.GetType().Name} value '{value}' into {typeof(T).Name}.{property} ({type.Name}).", ex);
        }
    }
}

/// <summary>Reading and writing records as plain C# objects.</summary>
public static class DbfTypedRecords
{
    /// <summary>Reads one record as a <typeparamref name="T"/>.</summary>
    public static T Read<T>(this DbfTable table, int index) where T : class, new() =>
        DbfMapper<T>.For(table).ToObject(table.Read(index));

    /// <summary>Every live record as a <typeparamref name="T"/>, streamed in record order. Deleted records are skipped.</summary>
    public static IEnumerable<T> ReadAll<T>(this DbfTable table) where T : class, new()
    {
        var mapper = DbfMapper<T>.For(table);
        foreach (var record in table.ReadAll())
        {
            if (!record.IsDeleted)
                yield return mapper.ToObject(record);
        }
    }

    // AppendRecord and UpdateRecord, not Append and Update: DbfTable's own Append(params object?[])
    // would otherwise win overload resolution and treat the object as one field value.

    /// <summary>Appends <paramref name="item"/> and returns the new record's 0-based index. Unmapped fields are left blank.</summary>
    public static int AppendRecord<T>(this DbfTable table, T item) where T : class, new() =>
        table.Append(DbfMapper<T>.For(table).ToValues(item, table.Fields.Count));

    /// <summary>
    /// Writes every mapped property of <paramref name="item"/> to record <paramref name="index"/>, through
    /// <see cref="DbfTable.UpdateFields"/>: unmapped fields keep whatever is on disk at that moment.
    /// </summary>
    public static void UpdateRecord<T>(this DbfTable table, int index, T item) where T : class, new() =>
        table.UpdateFields(index, DbfMapper<T>.For(table).ToChanges(item, table.Fields));

    /// <summary>
    /// A LINQ query over the table. Conditions like <c>c =&gt; c.Zip == zip</c> on a field with its own
    /// index tag become seeks; everything else runs in memory over a streamed scan. Deleted records are
    /// left out. Use <see cref="DbfQueryable.Explain"/> to see which happened.
    /// </summary>
    public static IQueryable<T> AsQueryable<T>(this DbfTable table) where T : class, new() =>
        new DbfQueryable<T>(new DbfQueryProvider<T>(table));
}
