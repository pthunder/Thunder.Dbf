//          WHERE ID = @id
//              |
//      +-------+--------+---------------+       ProjectThunder :: Thunder.Dbf
//      |                |               |       Bind names to fields, values to parameters,
//   _RECNO = n?    an MDX tag on ID?   neither   then pick the cheapest way to find the rows.
//      |                |               |
//   read one       SeekExact(@id)     scan       Every candidate is re-checked against the
//   record         record numbers     them all   whole WHERE, so a plan can only be faster,
//                                                never different.

using System.Diagnostics;
using System.Globalization;

namespace Thunder.Dbf.Data;

/// <summary>A column a reader returns: a table field or one of the pseudo-columns.</summary>
internal sealed record DbfReaderColumn(string Name, DbfField? Field, int FieldIndex, Type ClrType, string TypeName, bool AllowsNull)
{
    public const int RecordNumber = -1;
    public const int Deleted = -2;

    public string BaseName => Field?.Name ?? (FieldIndex == RecordNumber ? DbfValues.RecordNumberColumn : DbfValues.DeletedColumn);
}

/// <summary>One statement bound to a table and its parameter values, ready to run. Owns (and disposes) the table.</summary>
internal sealed class DbfQuery : IDisposable
{
    private readonly SqlStatement _statement;
    private readonly DbfTable _table;
    private readonly string _tableName;
    private readonly Func<string, object?> _parameter;
    private readonly bool _trustIndexes;

    private Func<DbfRecord, bool?> _filter = _ => true;
    private string _plan = "";
    private Func<IEnumerable<int>> _candidates = () => [];
    private volatile bool _canceled;
    private long _deadline = long.MaxValue;
    private int _timeoutSeconds;

    /// <summary>ORDER BY without a small LIMIT holds every match's sort keys; this caps that memory.</summary>
    internal const long MaxSortBytes = 256L * 1024 * 1024;

    /// <summary>ORDER BY with LIMIT + OFFSET up to this many rows keeps only that many keys (a top-N sort).</summary>
    internal const int MaxTopN = 100_000;

    private DbfQuery(SqlStatement statement, DbfTable table, string tableName, Func<string, object?> parameter, bool trustIndexes)
    {
        _statement = statement;
        _table = table;
        _tableName = tableName;
        _parameter = parameter;
        _trustIndexes = trustIndexes;
    }

    public SqlStatement Statement => _statement;

    public void Dispose() => _table.Dispose();

    /// <summary>Stops a running statement at the next record it looks at (DbCommand.Cancel).</summary>
    public void Cancel() => _canceled = true;

    /// <summary>Starts the CommandTimeout clock; 0 means no limit, as in ADO.NET.</summary>
    public void StartClock(int timeoutSeconds)
    {
        _timeoutSeconds = timeoutSeconds;
        _deadline = timeoutSeconds > 0 ? Stopwatch.GetTimestamp() + timeoutSeconds * Stopwatch.Frequency : long.MaxValue;
    }

    // Checked once per record read, so a scan of a huge table can be stopped.
    private void ThrowIfStopped()
    {
        if (_canceled)
            throw new OperationCanceledException("The command was canceled.");
        if (Stopwatch.GetTimestamp() > _deadline)
            throw new DbfException(DbfErrorCode.Timeout, $"The command ran longer than its {_timeoutSeconds}-second CommandTimeout.",
                "Narrow the WHERE so an index can answer it, add a LIMIT, or raise CommandTimeout (0 means no limit).");
    }

    public bool IsQuery => _statement is SqlSelect;

    /// <summary>The columns a SELECT returns, in order.</summary>
    public IReadOnlyList<DbfReaderColumn> Columns { get; private set; } = [];

    /// <summary>How the rows are found: a record-number lookup, an index seek, or a scan.</summary>
    public string Plan => _plan;

    /// <summary>
    /// Binds a parsed statement: resolves column names, reads parameter values once, and picks a plan.
    /// <paramref name="trustIndexes"/> is false when the connection may have left an index out of date.
    /// </summary>
    public static DbfQuery Bind(SqlStatement statement, DbfTable table, string tableName, Func<string, object?> parameter, bool trustIndexes)
    {
        var query = new DbfQuery(statement, table, tableName, parameter, trustIndexes);
        try
        {
            query.BindStatement();
            return query;
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    private void BindStatement()
    {
        switch (_statement)
        {
            case SqlSelect select:
                Columns = BindColumns(select.Columns);
                foreach (var order in select.OrderBy)
                {
                    var column = Resolve(order.Column);
                    if (column.ClrType == typeof(byte[]))
                        throw DbfSqlError.Unsupported($"ORDER BY on the binary column {column.Name}");
                }
                BindWhere(select.Where);
                break;
            case SqlUpdate update:
                BindWhere(update.Where);
                break;
            case SqlDelete delete:
                BindWhere(delete.Where);
                break;
            case SqlInsert:
                _plan = $"append to {_tableName}";
                break;
        }
    }

    // ---- SELECT ---------------------------------------------------------------------------

    /// <summary>The matching records, streamed (ORDER BY keeps only sort keys and record numbers in memory).</summary>
    public IEnumerable<DbfRecord> Rows()
    {
        var select = (SqlSelect)_statement;
        var offset = Count(select.Offset, "OFFSET") ?? 0;
        var limit = Count(select.Limit, "LIMIT");
        if (limit == 0)
            return [];

        var matches = Matches();
        if (select.OrderBy.Count > 0)
        {
            // With a small LIMIT, keep only the best LIMIT + OFFSET keys instead of every match's.
            long? keep = limit is { } l ? (long)l + offset : null;
            matches = Sorted(matches, select.OrderBy, keep is <= MaxTopN ? (int)keep : null);
        }
        if (offset > 0)
            matches = matches.Skip(offset);
        if (limit is { } n)
            matches = matches.Take(n);
        return matches;
    }

    private IEnumerable<DbfRecord> Matches()
    {
        var count = _table.RecordCount;
        foreach (var index in _candidates())
        {
            ThrowIfStopped();
            if (index < 0 || index >= count)
                continue;
            var record = _table.Read(index);
            if (_filter(record) == true)
                yield return record;
        }
    }

    // Sorts by keys alone, then re-reads each record in order, so memory holds keys, not rows.
    // With a top-N bound only the best N keys are ever held; without one the keys' size is capped.
    private IEnumerable<DbfRecord> Sorted(IEnumerable<DbfRecord> matches, IReadOnlyList<SqlOrderItem> orderBy, int? topN)
    {
        var columns = orderBy.Select(o => Getter(Resolve(o.Column))).ToArray();
        Comparison<(object?[] Keys, int Index)> compare = (a, b) =>
        {
            for (var i = 0; i < orderBy.Count; i++)
            {
                var order = CompareNullsFirst(a.Keys[i], b.Keys[i]);
                if (order != 0)
                    return orderBy[i].Descending ? -order : order;
            }
            return a.Index.CompareTo(b.Index); // stable: ties keep table order
        };

        List<(object?[] Keys, int Index)> keyed;
        if (topN is { } n)
        {
            // A max-heap of the best n so far: a new row replaces the worst when it sorts before it.
            var heap = new PriorityQueue<(object?[] Keys, int Index), (object?[] Keys, int Index)>(
                Comparer<(object?[] Keys, int Index)>.Create((a, b) => compare(b, a)));
            foreach (var record in matches)
            {
                var item = (columns.Select(get => get(record)).ToArray(), record.Index);
                if (heap.Count < n)
                    heap.Enqueue(item, item);
                else if (compare(item, heap.Peek()) < 0)
                    heap.EnqueueDequeue(item, item);
            }
            keyed = new List<(object?[] Keys, int Index)>(heap.Count);
            while (heap.TryDequeue(out var item, out _))
                keyed.Add(item);
        }
        else
        {
            keyed = [];
            long bytes = 0;
            foreach (var record in matches)
            {
                var keys = columns.Select(get => get(record)).ToArray();
                bytes += 48 + keys.Sum(k => k is string s ? 24 + 2L * s.Length : 24);
                if (bytes > MaxSortBytes)
                    throw new DbfException(DbfErrorCode.QueryTooLarge,
                        $"ORDER BY would hold more than {MaxSortBytes / (1024 * 1024)} MB of sort keys in memory.",
                        "Add a LIMIT (top-N sorts stay small), narrow the WHERE, or read in index order with DbfTable.ReadInOrder.");
                keyed.Add((keys, record.Index));
            }
        }

        keyed.Sort(compare);
        foreach (var (_, index) in keyed)
        {
            ThrowIfStopped();
            yield return _table.Read(index);
        }
    }

    private static int CompareNullsFirst(object? a, object? b) =>
        a is null ? (b is null ? 0 : -1) : b is null ? 1 : DbfValues.Compare(a, b);

    private int? Count(SqlOperand? operand, string clause)
    {
        if (operand is null)
            return null;
        var value = Constant(operand);
        try
        {
            var count = value is null ? -1 : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (count >= 0 && count <= int.MaxValue && count == decimal.Truncate(count))
                return (int)count;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            // fall through to the explained error
        }
        throw DbfSqlError.Value($"{clause} needs a whole number from 0 up, not {(value is null ? "null" : $"'{value}'")}.",
            $"Pass an int for {clause}.");
    }

    // ---- INSERT, UPDATE, DELETE -----------------------------------------------------------

    /// <summary>Runs a write and returns the number of records it touched.</summary>
    public int Execute() => _statement switch
    {
        SqlInsert insert => Insert(insert),
        SqlUpdate update => Update(update),
        SqlDelete => Delete(),
        _ => throw new InvalidOperationException("Only INSERT, UPDATE and DELETE run without a reader."),
    };

    private int Insert(SqlInsert insert)
    {
        var values = new object?[_table.Fields.Count];
        var seen = new HashSet<int>();
        for (var i = 0; i < insert.Columns.Count; i++)
        {
            var column = Resolve(insert.Columns[i]);
            if (column.Field is null)
                throw DbfSqlError.Value($"{column.Name} is a pseudo-column and can't be inserted.",
                    "Leave _RECNO and _DELETED out of INSERT; new records get the next number and start undeleted.");
            if (!seen.Add(column.FieldIndex))
                throw DbfSqlError.Syntax($"INSERT names column {column.Name} twice.", "Name each column once.");
            values[column.FieldIndex] = DbfValues.ForField(column.Field, Constant(insert.Values[i]));
        }

        Write(() => _table.Append(values));
        return 1;
    }

    private int Update(SqlUpdate update)
    {
        var changes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        bool? deleted = null;
        foreach (var assignment in update.Assignments)
        {
            var column = Resolve(assignment.Column);
            var value = Constant(assignment.Value);
            if (column.FieldIndex == DbfReaderColumn.Deleted)
            {
                deleted = DbfValues.FromParameter(value) as bool?
                    ?? throw DbfSqlError.Value("_DELETED can only be set to TRUE or FALSE.",
                        "SET _DELETED = TRUE marks records deleted; SET _DELETED = FALSE recalls them.");
                continue;
            }
            if (column.Field is null)
                throw DbfSqlError.Value($"{column.Name} is read-only.", "A record's number is its position in the file and can't change.");
            if (!changes.TryAdd(column.Field.Name, DbfValues.ForField(column.Field, value)))
                throw DbfSqlError.Syntax($"UPDATE sets column {column.Name} twice.", "Set each column once.");
        }

        // Find every match before changing any, so a change can't move a record in or out of the WHERE mid-scan.
        var targets = Matches().Select(r => r.Index).ToList();
        foreach (var index in targets)
        {
            ThrowIfStopped();
            // Only the SET columns are written; the rest stay as they are on disk at that moment,
            // even if the legacy app changed them after this command found the record.
            if (changes.Count > 0)
                Write(() => _table.UpdateFields(index, changes));
            if (deleted is { } mark)
                Write(() => { if (mark) _table.Delete(index); else _table.Recall(index); });
        }
        return targets.Count;
    }

    private int Delete()
    {
        var targets = Matches().Select(r => r.Index).ToList();
        foreach (var index in targets)
        {
            ThrowIfStopped();
            Write(() => _table.Delete(index));
        }
        return targets.Count;
    }

    // Library argument errors become explained DbfExceptions; DbfExceptions (locks, read-only, stale index) pass through.
    private static void Write(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            throw DbfSqlError.Value($"The value can't be stored: {ex.Message}", "Check the value fits the column's type and width.", ex);
        }
    }

    // ---- names and values -----------------------------------------------------------------

    private IReadOnlyList<DbfReaderColumn> BindColumns(IReadOnlyList<SqlSelectItem>? items)
    {
        if (items is null)
        {
            return _table.Fields
                .Select((field, index) => (field, index))
                .Where(f => !DbfValues.IsHidden(f.field))
                .Select(f => FieldColumn(f.field, f.index, f.field.Name))
                .ToList();
        }
        return items.Select(item =>
        {
            var column = Resolve(item.Column);
            return item.Alias is null ? column : column with { Name = item.Alias };
        }).ToList();
    }

    /// <summary>A table field by name (case-insensitive), or _RECNO / _DELETED when no field has that name.</summary>
    public DbfReaderColumn Resolve(string name)
    {
        for (var i = 0; i < _table.Fields.Count; i++)
        {
            var field = _table.Fields[i];
            if (!DbfValues.IsHidden(field) && field.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return FieldColumn(field, i, field.Name);
        }
        if (name.Equals(DbfValues.RecordNumberColumn, StringComparison.OrdinalIgnoreCase))
            return new(DbfValues.RecordNumberColumn, null, DbfReaderColumn.RecordNumber, typeof(int), "RecordNumber", false);
        if (name.Equals(DbfValues.DeletedColumn, StringComparison.OrdinalIgnoreCase))
            return new(DbfValues.DeletedColumn, null, DbfReaderColumn.Deleted, typeof(bool), "Logical", false);

        var known = _table.Fields.Where(f => !DbfValues.IsHidden(f)).Select(f => f.Name).ToList();
        throw DbfSqlError.Syntax($"Table {_tableName} has no column '{name}'.",
            $"Its columns are {string.Join(", ", known.Take(20))}{(known.Count > 20 ? ", ..." : "")}, plus the pseudo-columns _RECNO and _DELETED.");
    }

    private static DbfReaderColumn FieldColumn(DbfField field, int index, string name) =>
        new(name, field, index, DbfValues.ClrType(field), DbfValues.TypeName(field), DbfValues.AllowsNull(field));

    /// <summary>Reads a column's value from a record, normalized to the column's type.</summary>
    public Func<DbfRecord, object?> Getter(DbfReaderColumn column)
    {
        var encoding = _table.Encoding;
        return column.FieldIndex switch
        {
            DbfReaderColumn.RecordNumber => r => r.RecordNumber,
            DbfReaderColumn.Deleted => r => r.IsDeleted,
            var i => r => DbfValues.Normalize(column.Field!, r.Values[i], encoding),
        };
    }

    private object? Constant(SqlOperand operand) => operand switch
    {
        SqlLiteral literal => literal.Value,
        SqlParameterRef parameter => DbfValues.FromParameter(_parameter(parameter.Name)),
        _ => throw DbfSqlError.Syntax("Expected a value, not a column name.", "Values are @parameters or literals."),
    };

    // ---- WHERE ----------------------------------------------------------------------------

    private void BindWhere(SqlCondition? where)
    {
        var condition = where is null ? null : Compile(where);
        var includeDeleted = where is not null && Mentions(where, DbfValues.DeletedColumn);
        _filter = (condition, includeDeleted) switch
        {
            (null, _) => r => !r.IsDeleted,
            (_, true) => condition,
            _ => r => !r.IsDeleted ? condition(r) : false,
        };
        ChoosePlan(where, includeDeleted);
    }

    private Func<DbfRecord, bool?> Compile(SqlCondition condition)
    {
        switch (condition)
        {
            case SqlAnd and:
            {
                var (left, right) = (Compile(and.Left), Compile(and.Right));
                return r =>
                {
                    var a = left(r);
                    if (a == false)
                        return false;
                    var b = right(r);
                    return b == false ? false : a == true && b == true ? true : null;
                };
            }
            case SqlOr or:
            {
                var (left, right) = (Compile(or.Left), Compile(or.Right));
                return r =>
                {
                    var a = left(r);
                    if (a == true)
                        return true;
                    var b = right(r);
                    return b == true ? true : a == false && b == false ? false : null;
                };
            }
            case SqlNot not:
            {
                var inner = Compile(not.Inner);
                return r => !inner(r);
            }
            case SqlIsNull isNull:
            {
                var value = Operand(isNull.Value);
                return isNull.Negated ? r => value(r) is not null : r => value(r) is null;
            }
            case SqlLike like:
            {
                var (value, pattern) = (Operand(like.Value), Operand(like.Pattern));
                return r =>
                {
                    if (value(r) is not { } v || pattern(r) is not { } p)
                        return null;
                    if (v is not string text || p is not string mask)
                        throw DbfSqlError.Value("LIKE works on text only.", "Use LIKE with character or memo columns and a text pattern, like NAME LIKE @pattern.");
                    return DbfValues.Like(text, mask) != like.Negated;
                };
            }
            case SqlCompare compare:
            {
                var (left, right) = (Operand(compare.Left), Operand(compare.Right));
                Func<int, bool> test = compare.Operator switch
                {
                    "=" => c => c == 0,
                    "<>" => c => c != 0,
                    "<" => c => c < 0,
                    "<=" => c => c <= 0,
                    ">" => c => c > 0,
                    _ => c => c >= 0,
                };
                return r => left(r) is { } a && right(r) is { } b ? test(DbfValues.Compare(a, b)) : null;
            }
            default:
                throw new InvalidOperationException($"Unknown condition {condition.GetType().Name}.");
        }
    }

    private Func<DbfRecord, object?> Operand(SqlOperand operand)
    {
        if (operand is SqlColumn column)
        {
            var bound = Resolve(column.Name);
            if (bound.ClrType == typeof(byte[]))
                throw DbfSqlError.Unsupported($"Filtering on the binary column {bound.Name}");
            return Getter(bound);
        }
        var value = Constant(operand); // parameters are read once, not per row
        return _ => value;
    }

    private static bool Mentions(SqlCondition condition, string column) => condition switch
    {
        SqlAnd and => Mentions(and.Left, column) || Mentions(and.Right, column),
        SqlOr or => Mentions(or.Left, column) || Mentions(or.Right, column),
        SqlNot not => Mentions(not.Inner, column),
        SqlIsNull n => IsColumn(n.Value, column),
        SqlLike l => IsColumn(l.Value, column) || IsColumn(l.Pattern, column),
        SqlCompare c => IsColumn(c.Left, column) || IsColumn(c.Right, column),
        _ => false,
    };

    private static bool IsColumn(SqlOperand operand, string name) =>
        operand is SqlColumn c && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    // ---- plans ----------------------------------------------------------------------------

    private void ChoosePlan(SqlCondition? where, bool includeDeleted)
    {
        var equalities = Conjuncts(where)
            .OfType<SqlCompare>()
            .Where(c => c.Operator == "=")
            .Select(c => c.Left is SqlColumn col && c.Right is not SqlColumn ? (col, c.Right)
                : c.Right is SqlColumn col2 && c.Left is not SqlColumn ? (col2, c.Left)
                : default)
            .Where(e => e.Item1 is not null)
            .Select(e => (Column: Resolve(e.Item1.Name), Value: Constant(e.Item2)))
            .ToList();

        // 1. WHERE _RECNO = n reads one record.
        foreach (var (column, value) in equalities)
        {
            if (column.FieldIndex == DbfReaderColumn.RecordNumber && AsRecordNumber(value) is { } number)
            {
                _plan = $"read record {number} of {_tableName}";
                _candidates = () => [number - 1];
                return;
            }
        }

        // 2. WHERE FIELD = value, where an MDX tag's key is exactly that field.
        if (_trustIndexes && _table.Header.HasProductionMdx)
        {
            var lookups = equalities.Where(e => e.Column.Field is not null && e.Value is not null).ToList();
            if (lookups.Count > 0 && TrySeek(lookups, includeDeleted))
                return;
        }

        // 3. Everything else reads every record.
        if (_plan.Length == 0)
            _plan = $"scan {_tableName}";
        _candidates = () => Enumerable.Range(0, _table.RecordCount);
    }

    private bool TrySeek(List<(DbfReaderColumn Column, object? Value)> lookups, bool includeDeleted)
    {
        MdxIndex? index;
        try
        {
            index = _table.OpenProductionIndex();
        }
        catch (Exception ex) when (ex is DbfException or InvalidDataException or IOException)
        {
            _plan = $"scan {_tableName} (its .mdx index can't be read: {ex.Message})";
            return false;
        }
        if (index is null)
            return false;

        string? skipped = null;
        using (index)
        {
            foreach (var (column, value) in lookups)
            {
                foreach (var tag in index.Tags)
                {
                    if (!tag.Expression.Trim().Equals(column.Field!.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (WhyNotSeek(tag, column.Field, includeDeleted) is { } reason)
                    {
                        skipped ??= reason;
                        continue;
                    }
                    if (SeekKey(tag, value!) is not { } key)
                        continue;

                    List<int> records;
                    try
                    {
                        records = tag.SeekExact(key).Select(e => e.RecordNumber - 1).Distinct().Order().ToList();
                    }
                    catch (InvalidDataException ex)
                    {
                        _plan = $"scan {_tableName} (index tag {tag.Name} is damaged: {ex.Message})";
                        return false;
                    }

                    _plan = $"seek {_tableName} index tag {tag.Name} for {column.Field.Name} = {Show(value!)}";
                    _candidates = () => records;
                    return true;
                }
            }
        }

        if (skipped is not null)
            _plan = $"scan {_tableName} ({skipped})";
        return false;
    }

    /// <summary>
    /// Why a tag whose key is exactly <paramref name="field"/> can't answer FIELD = value, or null
    /// when it can: it must keep every key (not UNIQUE), store the field's type, and have no FOR
    /// filter that could drop a row the query wants.
    /// </summary>
    internal static string? WhyNotSeek(MdxTag tag, DbfField field, bool includeDeleted)
    {
        if (tag.IsUnique)
            return $"index tag {tag.Name} is UNIQUE, so it can leave out records with a repeated key";

        var keyMatches = (tag.KeyType, field.Type) switch
        {
            ('C', 'C') or ('N', 'N' or 'F') or ('D', 'D') => true,
            _ => false,
        };
        if (!keyMatches)
            return $"index tag {tag.Name} stores {tag.KeyType} keys for a {DbfValues.TypeName(field)} field";

        if (tag.ForExpression is null)
            return null;
        var filter = tag.ForExpression.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (filter is not (".NOT.DELETED()" or "!DELETED()" or ".NOT.(DELETED())"))
            return $"index tag {tag.Name} only holds records FOR {tag.ForExpression}";
        return includeDeleted ? $"index tag {tag.Name} leaves out deleted records, and the query asks about _DELETED" : null;
    }

    // The key to seek with, or null when the value can't be one (the scan then reports any mismatch).
    private static object? SeekKey(MdxTag tag, object value)
    {
        try
        {
            return (tag.KeyType, value) switch
            {
                ('C', string s) => s.TrimEnd(' '),
                ('N', double or float) => value,
                ('N', string s) => decimal.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                ('N', bool) => null,
                ('N', _) => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                ('D', DateTime dt) => DateOnly.FromDateTime(dt),
                ('D', string s) => DateOnly.FromDateTime(DateTime.Parse(s.Trim(), CultureInfo.InvariantCulture)),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static int? AsRecordNumber(object? value)
    {
        try
        {
            var number = value switch
            {
                null or bool or DateTime => -1m,
                string s => decimal.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture),
                _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            };
            return number >= 1 && number <= int.MaxValue && number == decimal.Truncate(number) ? (int)number : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static IEnumerable<SqlCondition> Conjuncts(SqlCondition? condition) => condition switch
    {
        null => [],
        SqlAnd and => Conjuncts(and.Left).Concat(Conjuncts(and.Right)),
        _ => [condition],
    };

    private static string Show(object value) => value switch
    {
        string s => $"'{(s.Length <= 30 ? s : s[..30] + "...")}'",
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
