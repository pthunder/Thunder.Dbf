//     contacts.AsQueryable<Contact>()
//         .Where(c => c.Zip == "94105")    --> an .mdx tag on ZIP?  yes: SeekExact("94105")
//         .Where(c => c.Credit > 1000)                              no:  scan every record
//         .OrderBy(c => c.Company)
//         .Take(10)                         ...then LINQ to Objects re-checks every candidate
//                                              against the whole query, so a seek can only
//     ProjectThunder :: Thunder.Dbf           make it faster, never change the answer.

using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Thunder.Dbf;

/// <summary>Helpers for LINQ queries made with <see cref="DbfTypedRecords.AsQueryable{T}"/>.</summary>
public static class DbfQueryable
{
    /// <summary>
    /// How the query finds its records, without running it: for example
    /// <c>seek CONTACT1 index tag ZIP for ZIP = '94105'</c> or <c>scan CONTACT1</c>.
    /// </summary>
    public static string Explain(this IQueryable query) =>
        query.Provider is IDbfQueryProvider provider
            ? provider.Plan(query.Expression)
            : throw new ArgumentException("Explain works on queries made with DbfTable.AsQueryable.", nameof(query));
}

internal interface IDbfQueryProvider
{
    string Plan(Expression expression);
}

internal sealed class DbfQueryable<TElement>(IQueryProvider provider, Expression? expression = null) : IOrderedQueryable<TElement>
{
    public Type ElementType => typeof(TElement);

    public Expression Expression { get; } = expression ?? Expression.Constant(null, typeof(DbfQueryable<TElement>));

    public IQueryProvider Provider { get; } = provider;

    public IEnumerator<TElement> GetEnumerator() => Provider.Execute<IEnumerable<TElement>>(Expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Runs LINQ over one table. The root of every query is a placeholder; at run time it's replaced by
/// the candidate records (from a seek or a scan) as LINQ to Objects, and the query runs unchanged on top.
/// </summary>
internal sealed class DbfQueryProvider<T>(DbfTable table) : IQueryProvider, IDbfQueryProvider where T : new()
{
    private readonly DbfMapper<T> _mapper = DbfMapper<T>.For(table);

    public IQueryable CreateQuery(Expression expression)
    {
        var element = ElementTypeOf(expression.Type);
        return (IQueryable)Activator.CreateInstance(typeof(DbfQueryable<>).MakeGenericType(element), this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new DbfQueryable<TElement>(this, expression);

    public object? Execute(Expression expression) => Run(expression);

    public TResult Execute<TResult>(Expression expression) => (TResult)Run(expression)!;

    public string Plan(Expression expression) => Choose(expression).Plan;

    private object? Run(Expression expression)
    {
        var (_, candidates) = Choose(expression);
        var source = candidates.AsQueryable();
        var rewritten = new ReplaceRoot(source).Visit(expression);

        // A sequence comes back as a lazy LINQ to Objects query; a single value (Count, First, ...) runs now.
        return typeof(IEnumerable).IsAssignableFrom(rewritten.Type) && rewritten.Type != typeof(string)
            ? source.Provider.CreateQuery(rewritten)
            : source.Provider.Execute(rewritten);
    }

    // ---- choosing how to find the records -------------------------------------------------

    private (string Plan, IEnumerable<T> Candidates) Choose(Expression expression)
    {
        var name = table.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : "table";
        foreach (var (field, value) in Equalities(expression))
        {
            if (value is not null && TrySeek(field, value, out var records, out var tag))
                return ($"seek {name} index tag {tag} for {table.Fields[field].Name} = {Show(value)}", records!);
        }
        return ($"scan {name}", table.ReadAll<T>());
    }

    // FIELD = constant conditions from the Where clauses applied straight to the table, before
    // anything (Select, Skip, OrderBy...) could change what the condition means.
    private IEnumerable<(int Field, object? Value)> Equalities(Expression expression)
    {
        var wheres = new List<LambdaExpression>();
        var node = expression;
        while (node is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            if (call.Method.Name == nameof(Queryable.Where) && call.Arguments.Count == 2
                && StripQuotes(call.Arguments[1]) is LambdaExpression { Parameters.Count: 1 } lambda)
                wheres.Add(lambda);
            else
                wheres.Clear(); // a Where above a Select or Skip doesn't filter the table's own records
            node = call.Arguments[0];
        }

        foreach (var lambda in wheres)
        {
            foreach (var condition in Conjuncts(lambda.Body))
            {
                if (condition is not BinaryExpression { NodeType: ExpressionType.Equal } equal)
                    continue;
                var parameter = lambda.Parameters[0];
                if (Side(equal.Left, equal.Right, parameter) is { } hit)
                    yield return hit;
                else if (Side(equal.Right, equal.Left, parameter) is { } flipped)
                    yield return flipped;
            }
        }
    }

    private (int, object?)? Side(Expression member, Expression value, ParameterExpression parameter)
    {
        if (StripConvert(member) is not MemberExpression { Expression: { } owner } access || owner != parameter)
            return null;
        var field = _mapper.FieldOf(access.Member);
        if (field < 0 || References(value, parameter))
            return null;
        try
        {
            return (field, Expression.Lambda(Expression.Convert(value, typeof(object))).Compile().DynamicInvoke());
        }
        catch (TargetInvocationException)
        {
            return null; // the value can't be worked out up front; the scan will evaluate it per record
        }
    }

    private bool TrySeek(int field, object value, out IEnumerable<T>? records, out string? tagName)
    {
        records = null;
        tagName = null;
        MdxIndex? index;
        try
        {
            index = table.OpenProductionIndex();
        }
        catch (Exception ex) when (ex is DbfException or InvalidDataException or IOException)
        {
            return false;
        }
        if (index is null)
            return false;

        using (index)
        {
            var dbfField = table.Fields[field];
            foreach (var tag in index.Tags)
            {
                if (!tag.Expression.Trim().Equals(dbfField.Name, StringComparison.OrdinalIgnoreCase)
                    || Data.DbfQuery.WhyNotSeek(tag, dbfField, includeDeleted: false) is not null
                    || SeekKey(tag.KeyType, value) is not { } key)
                    continue;

                // Read the record numbers now, while the index is open; the records themselves stream.
                var numbers = tag.SeekExact(key).Select(e => e.RecordNumber).Distinct().Order().ToList();
                records = Load(numbers);
                tagName = tag.Name;
                return true;
            }
        }
        return false;
    }

    private IEnumerable<T> Load(List<int> recordNumbers)
    {
        foreach (var number in recordNumbers)
        {
            if (number < 1 || number > table.RecordCount)
                continue; // an index entry past the end can't be trusted with a read
            var record = table.Read(number - 1);
            if (!record.IsDeleted)
                yield return _mapper.ToObject(record);
        }
    }

    private static object? SeekKey(char keyType, object value)
    {
        try
        {
            return (keyType, value) switch
            {
                ('C', string s) => s.TrimEnd(' '),
                ('C', char c) => c.ToString(),
                ('N', bool) => null,
                ('N', double or float) => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
                ('N', IConvertible) when value is not string and not DateTime => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                ('D', DateOnly d) => d,
                ('D', DateTime t) when t.TimeOfDay == TimeSpan.Zero => DateOnly.FromDateTime(t),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
        {
            return null;
        }
    }

    // ---- expression helpers ---------------------------------------------------------------

    private static IEnumerable<Expression> Conjuncts(Expression expression) =>
        expression is BinaryExpression { NodeType: ExpressionType.AndAlso } and
            ? Conjuncts(and.Left).Concat(Conjuncts(and.Right))
            : [expression];

    private static Expression StripQuotes(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;

    private static Expression StripConvert(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert ? StripConvert(convert.Operand) : expression;

    private static bool References(Expression expression, ParameterExpression parameter)
    {
        var finder = new ParameterFinder(parameter);
        finder.Visit(expression);
        return finder.Found;
    }

    private static Type ElementTypeOf(Type sequence) =>
        sequence.GetInterfaces().Prepend(sequence)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0]
        ?? throw new ArgumentException($"{sequence.Name} isn't a sequence.");

    private static string Show(object value) => value switch
    {
        string s => $"'{(s.Length <= 30 ? s : s[..30] + "...")}'",
        DateTime t => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private sealed class ReplaceRoot(IQueryable<T> source) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node) =>
            node.Type == typeof(DbfQueryable<T>) ? Expression.Constant(source, typeof(IQueryable<T>)) : node;
    }

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found |= node == parameter;
            return node;
        }
    }
}
