//      _____________________________________
//     | SELECT  *  FROM  CONTACT1           |     ProjectThunder :: Thunder.Dbf
//     | WHERE   COMPANY = @company          |     A deliberately small SQL: enough for
//     | ORDER BY CONTACT  LIMIT 50          |     Dapper, reports and DataTable.Load,
//     |_____________________________________|     and nothing that can surprise you.
//              |
//       lex    v    parse    v    bind (DbfQuery)    v    run
//
// Grammar (keywords are case-insensitive; one statement per command):
//   select  := SELECT ( * | column [AS alias] {, column [AS alias]} ) FROM table
//              [WHERE condition] [ORDER BY column [ASC|DESC] {, ...}] [LIMIT count] [OFFSET count]
//   insert  := INSERT INTO table ( column {, column} ) VALUES ( value {, value} )
//   update  := UPDATE table SET column = value {, column = value} WHERE condition
//   delete  := DELETE FROM table WHERE condition
//   condition := and { OR and }
//   and       := not { AND not }
//   not       := NOT not | ( condition ) | operand ( = | <> | != | < | <= | > | >= ) operand
//              | operand [NOT] LIKE operand | operand IS [NOT] NULL
//   operand   := column | @parameter | 'text' | number | TRUE | FALSE | NULL
//   value     := @parameter | 'text' | number | TRUE | FALSE | NULL
//   count     := integer | @parameter
//
// Names are plain identifiers, or quoted as [name] or "name" when they clash with a keyword.

using System.Globalization;
using System.Text;

namespace Thunder.Dbf.Data;

// ---- syntax tree --------------------------------------------------------------------------

internal abstract record SqlStatement(string Table);

internal sealed record SqlSelect(
    string Table,
    IReadOnlyList<SqlSelectItem>? Columns, // null means SELECT *
    SqlCondition? Where,
    IReadOnlyList<SqlOrderItem> OrderBy,
    SqlOperand? Limit,
    SqlOperand? Offset) : SqlStatement(Table);

internal sealed record SqlInsert(string Table, IReadOnlyList<string> Columns, IReadOnlyList<SqlOperand> Values) : SqlStatement(Table);

internal sealed record SqlUpdate(string Table, IReadOnlyList<SqlAssignment> Assignments, SqlCondition Where) : SqlStatement(Table);

internal sealed record SqlDelete(string Table, SqlCondition Where) : SqlStatement(Table);

internal sealed record SqlSelectItem(string Column, string? Alias);

internal sealed record SqlOrderItem(string Column, bool Descending);

internal sealed record SqlAssignment(string Column, SqlOperand Value);

internal abstract record SqlCondition;

internal sealed record SqlAnd(SqlCondition Left, SqlCondition Right) : SqlCondition;

internal sealed record SqlOr(SqlCondition Left, SqlCondition Right) : SqlCondition;

internal sealed record SqlNot(SqlCondition Inner) : SqlCondition;

internal sealed record SqlCompare(SqlOperand Left, string Operator, SqlOperand Right) : SqlCondition;

internal sealed record SqlLike(SqlOperand Value, SqlOperand Pattern, bool Negated) : SqlCondition;

internal sealed record SqlIsNull(SqlOperand Value, bool Negated) : SqlCondition;

internal abstract record SqlOperand;

internal sealed record SqlColumn(string Name) : SqlOperand;

internal sealed record SqlLiteral(object? Value) : SqlOperand;

internal sealed record SqlParameterRef(string Name) : SqlOperand;

// ---- errors -------------------------------------------------------------------------------

/// <summary>The errors the SQL layer raises, in one place so their codes stay consistent.</summary>
internal static class DbfSqlError
{
    internal const DbfErrorCode SqlCode = DbfErrorCode.UnsupportedSql;
    internal const DbfErrorCode ValueCode = DbfErrorCode.InvalidValue;

    private const string GrammarHint =
        "Thunder.Dbf understands SELECT, INSERT, UPDATE and DELETE on one table, with WHERE (= <> < <= > >= LIKE IS NULL AND OR NOT), " +
        "ORDER BY, LIMIT and OFFSET. Pass values as @parameters.";

    public static DbfException Unsupported(string what, string? hint = null) =>
        new(SqlCode, $"{what} isn't supported by Thunder.Dbf's SQL.", hint ?? GrammarHint);

    public static DbfException Syntax(string message, string? hint = null) =>
        new(SqlCode, message, hint ?? GrammarHint);

    public static DbfException Value(string message, string hint, Exception? inner = null) =>
        new(ValueCode, message, hint, inner: inner);
}

// ---- lexer --------------------------------------------------------------------------------

internal enum SqlTokenKind { Word, QuotedName, Number, Text, Parameter, Symbol, End }

internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Position)
{
    public bool Is(string keyword) => Kind == SqlTokenKind.Word && Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(string symbol) => Kind == SqlTokenKind.Symbol && Text == symbol;

    public string Describe() => Kind switch
    {
        SqlTokenKind.End => "the end of the command",
        SqlTokenKind.Text => "a text literal",
        SqlTokenKind.Parameter => $"'@{Text}'",
        _ => $"'{Text}'",
    };
}

internal static class SqlLexer
{
    public static List<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if ((c == '-' && At(sql, i + 1) == '-') || (c == '/' && At(sql, i + 1) == '*'))
                throw DbfSqlError.Unsupported("A comment", "Remove the comment from the command text.");

            if (char.IsLetter(c) || c == '_')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                tokens.Add(new(SqlTokenKind.Word, sql[start..i], start));
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(At(sql, i + 1))) || IsNegativeNumber(sql, i, tokens))
            {
                if (c == '-')
                    i++;
                while (i < sql.Length && (char.IsAsciiDigit(sql[i]) || sql[i] == '.'))
                    i++;
                if (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_'))
                    throw DbfSqlError.Syntax($"'{sql[start..(i + 1)]}' near position {start + 1} isn't a number or a name.");
                tokens.Add(new(SqlTokenKind.Number, sql[start..i], start));
            }
            else if (c == '\'')
            {
                tokens.Add(new(SqlTokenKind.Text, ReadQuoted(sql, ref i, '\'', "text literal"), start));
            }
            else if (c is '"' or '[')
            {
                var name = ReadQuoted(sql, ref i, c == '[' ? ']' : '"', "quoted name");
                if (name.Length == 0)
                    throw DbfSqlError.Syntax($"The quoted name at position {start + 1} is empty.");
                tokens.Add(new(SqlTokenKind.QuotedName, name, start));
            }
            else if (c is '@' or ':')
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                if (i == start + 1)
                    throw DbfSqlError.Syntax($"'{c}' at position {start + 1} needs a parameter name after it, like @id.");
                tokens.Add(new(SqlTokenKind.Parameter, sql[(start + 1)..i], start));
            }
            else if (c == '.')
            {
                throw DbfSqlError.Unsupported($"A qualified name like alias.column (the '.' at position {start + 1})",
                    "Use the plain column or table name; one table is queried at a time. Quote a table file name as [CLIENT.DBF].");
            }
            else if (c == '?')
            {
                throw DbfSqlError.Unsupported("A positional '?' parameter", "Name every parameter, like WHERE ID = @id, and add it with that name.");
            }
            else
            {
                var two = i + 1 < sql.Length ? sql.Substring(i, 2) : "";
                if (two is "<>" or "!=" or "<=" or ">=")
                {
                    tokens.Add(new(SqlTokenKind.Symbol, two, start));
                    i += 2;
                }
                else if (c is '=' or '<' or '>' or '(' or ')' or ',' or '*' or ';')
                {
                    tokens.Add(new(SqlTokenKind.Symbol, c.ToString(), start));
                    i++;
                }
                else if (c is '+' or '-' or '/' or '%' or '|' or '&')
                {
                    throw DbfSqlError.Unsupported($"Arithmetic or concatenation ('{c}' at position {start + 1})",
                        "Compare columns with values directly; compute anything else in .NET after reading.");
                }
                else
                {
                    throw DbfSqlError.Syntax($"Unexpected character '{c}' at position {start + 1}.");
                }
            }
        }

        // A single trailing semicolon is harmless; anything after it would be a second statement.
        var semicolon = tokens.FindIndex(t => t.IsSymbol(";"));
        if (semicolon >= 0)
        {
            if (semicolon != tokens.Count - 1)
                throw DbfSqlError.Unsupported("More than one statement in a command",
                    "Run each statement as its own command. Values belong in parameters, never in the command text.");
            tokens.RemoveAt(semicolon);
        }

        tokens.Add(new(SqlTokenKind.End, "", sql.Length));
        return tokens;
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    // A minus sign starts a negative number only where a value is expected: after an operator, '(' or ','.
    private static bool IsNegativeNumber(string sql, int i, List<SqlToken> tokens) =>
        sql[i] == '-' && (char.IsAsciiDigit(At(sql, i + 1)) || (At(sql, i + 1) == '.' && char.IsAsciiDigit(At(sql, i + 2))))
        && tokens.Count > 0 && tokens[^1].Kind == SqlTokenKind.Symbol && tokens[^1].Text != ")";

    // Reads 'it''s' style text: the closing quote doubled means a literal quote.
    private static string ReadQuoted(string sql, ref int i, char close, string what)
    {
        var start = i;
        var text = new StringBuilder();
        i++;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (At(sql, i + 1) == close)
                {
                    text.Append(close);
                    i += 2;
                    continue;
                }
                i++;
                return text.ToString();
            }
            text.Append(sql[i++]);
        }
        throw DbfSqlError.Syntax($"The {what} starting at position {start + 1} never closes.");
    }
}

// ---- parser -------------------------------------------------------------------------------

internal sealed class SqlParser
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "ORDER", "BY", "ASC", "DESC", "LIMIT", "OFFSET", "INSERT", "INTO", "VALUES",
        "UPDATE", "SET", "DELETE", "AND", "OR", "NOT", "LIKE", "IS", "NULL", "TRUE", "FALSE", "AS",
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "ON", "GROUP", "HAVING", "UNION", "DISTINCT",
        "TOP", "IN", "BETWEEN", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END", "ESCAPE", "INTERSECT", "EXCEPT",
    };

    // Limits that keep hostile SQL from a web form cheap to reject: no statement a person writes
    // by hand comes near them, and the recursive parser can't run out of stack.
    internal const int MaxSqlLength = 64 * 1024;
    internal const int MaxTokens = 10_000;
    internal const int MaxDepth = 64;

    private readonly List<SqlToken> _tokens;
    private int _pos;
    private int _depth;

    private SqlParser(List<SqlToken> tokens) => _tokens = tokens;

    /// <summary>Parses one statement, or throws a <see cref="DbfException"/> naming the part that isn't supported.</summary>
    public static SqlStatement Parse(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw DbfSqlError.Syntax("The command has no text.", "Set CommandText to a SELECT, INSERT, UPDATE or DELETE statement.");

        if (sql.Length > MaxSqlLength)
            throw DbfSqlError.Syntax($"The command is longer than {MaxSqlLength / 1024} KB.", "Send values as @parameters instead of pasting them into the SQL.");
        var tokens = SqlLexer.Tokenize(sql);
        if (tokens.Count > MaxTokens)
            throw DbfSqlError.Syntax($"The command has more than {MaxTokens:N0} words and symbols.", "Split it into smaller statements.");

        var parser = new SqlParser(tokens);
        var statement = parser.ParseStatement();
        parser.ExpectEnd();
        return statement;
    }

    private SqlToken Current => _tokens[_pos];

    private SqlToken Next() => _tokens[_pos++];

    private SqlStatement ParseStatement()
    {
        var first = Current;
        if (first.Is("SELECT"))
            return ParseSelect();
        if (first.Is("INSERT"))
            return ParseInsert();
        if (first.Is("UPDATE"))
            return ParseUpdate();
        if (first.Is("DELETE"))
            return ParseDelete();

        if (first.Kind == SqlTokenKind.Word)
            throw DbfSqlError.Unsupported($"The {first.Text.ToUpperInvariant()} statement");
        throw DbfSqlError.Syntax($"A command must start with SELECT, INSERT, UPDATE or DELETE, not {first.Describe()}.");
    }

    // ---- statements -----------------------------------------------------------------------

    private SqlSelect ParseSelect()
    {
        Expect("SELECT");
        RefuseKeyword("DISTINCT", "SELECT DISTINCT");
        RefuseKeyword("TOP", "SELECT TOP", "Use LIMIT at the end instead, like SELECT * FROM T LIMIT 10.");

        List<SqlSelectItem>? columns = null;
        if (TrySymbol("*"))
        {
            // SELECT * keeps the list null.
        }
        else
        {
            columns = [];
            do
            {
                var column = ParseColumnName("a column name or *");
                string? alias = null;
                if (TryKeyword("AS"))
                    alias = ParseName("an alias after AS");
                else if (Current.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedName && !IsReserved(Current))
                    alias = ParseName("an alias");
                columns.Add(new(column, alias));
            }
            while (TrySymbol(","));
        }

        Expect("FROM");
        var table = ParseTableName();
        RefuseAfterTable();

        var where = TryKeyword("WHERE") ? ParseCondition() : null;
        RefuseKeyword("GROUP", "GROUP BY");
        RefuseKeyword("HAVING", "HAVING");

        var orderBy = new List<SqlOrderItem>();
        if (TryKeyword("ORDER"))
        {
            Expect("BY");
            do
            {
                var column = ParseColumnName("a column name after ORDER BY");
                var descending = TryKeyword("DESC");
                if (!descending)
                    TryKeyword("ASC");
                orderBy.Add(new(column, descending));
            }
            while (TrySymbol(","));
        }

        var limit = TryKeyword("LIMIT") ? ParseCount("LIMIT") : null;
        var offset = TryKeyword("OFFSET") ? ParseCount("OFFSET") : null;
        if (limit is null && Current.Is("LIMIT"))
        {
            Next();
            limit = ParseCount("LIMIT");
        }
        RefuseKeyword("UNION", "UNION");
        RefuseKeyword("INTERSECT", "INTERSECT");
        RefuseKeyword("EXCEPT", "EXCEPT");
        return new SqlSelect(table, columns, where, orderBy, limit, offset);
    }

    private SqlInsert ParseInsert()
    {
        Expect("INSERT");
        Expect("INTO");
        var table = ParseTableName();
        if (!Current.IsSymbol("("))
            throw DbfSqlError.Syntax($"INSERT needs a column list after the table name, but found {Current.Describe()}.",
                "Name the columns, like INSERT INTO T (NAME, CITY) VALUES (@name, @city).");
        Next();

        var columns = new List<string>();
        do
            columns.Add(ParseColumnName("a column name"));
        while (TrySymbol(","));
        ExpectSymbol(")");

        if (Current.Is("SELECT"))
            throw DbfSqlError.Unsupported("INSERT ... SELECT", "Read the rows, then insert them one command at a time.");
        Expect("VALUES");
        ExpectSymbol("(");
        var values = new List<SqlOperand>();
        do
            values.Add(ParseValue());
        while (TrySymbol(","));
        ExpectSymbol(")");

        if (Current.IsSymbol(","))
            throw DbfSqlError.Unsupported("Inserting several rows in one INSERT", "Run the INSERT once per row; reuse the command and change the parameter values.");
        if (columns.Count != values.Count)
            throw DbfSqlError.Syntax($"INSERT names {columns.Count} column(s) but gives {values.Count} value(s).",
                "Give exactly one value per named column.");
        return new SqlInsert(table, columns, values);
    }

    private SqlUpdate ParseUpdate()
    {
        Expect("UPDATE");
        var table = ParseTableName();
        Expect("SET");
        var assignments = new List<SqlAssignment>();
        do
        {
            var column = ParseColumnName("a column name after SET");
            ExpectSymbol("=");
            assignments.Add(new(column, ParseValue()));
        }
        while (TrySymbol(","));

        RefuseKeyword("FROM", "UPDATE ... FROM");
        return new SqlUpdate(table, assignments, ParseRequiredWhere("UPDATE"));
    }

    private SqlDelete ParseDelete()
    {
        Expect("DELETE");
        Expect("FROM");
        var table = ParseTableName();
        RefuseAfterTable();
        return new SqlDelete(table, ParseRequiredWhere("DELETE"));
    }

    // UPDATE and DELETE without WHERE are almost always mistakes on live data, so say so explicitly.
    private SqlCondition ParseRequiredWhere(string statement)
    {
        if (!TryKeyword("WHERE"))
            throw DbfSqlError.Syntax($"{statement} needs a WHERE clause.",
                $"Say which rows to change. To {(statement == "DELETE" ? "delete" : "update")} every live row on purpose, write WHERE _DELETED = FALSE.");
        return ParseCondition();
    }

    // ---- conditions -----------------------------------------------------------------------

    private SqlCondition ParseCondition()
    {
        var left = ParseAnd();
        while (TryKeyword("OR"))
            left = new SqlOr(left, ParseAnd());
        return left;
    }

    private SqlCondition ParseAnd()
    {
        var left = ParseNot();
        while (TryKeyword("AND"))
            left = new SqlAnd(left, ParseNot());
        return left;
    }

    private SqlCondition ParseNot()
    {
        if (++_depth > MaxDepth)
            throw DbfSqlError.Syntax($"The WHERE clause nests more than {MaxDepth} levels of NOT or parentheses.", "Flatten the condition.");
        try
        {
            return ParseNotCore();
        }
        finally
        {
            _depth--;
        }
    }

    private SqlCondition ParseNotCore()
    {
        if (TryKeyword("NOT"))
            return new SqlNot(ParseNot());
        RefuseKeyword("EXISTS", "EXISTS");

        if (Current.IsSymbol("("))
        {
            Next();
            if (Current.Is("SELECT"))
                throw DbfSqlError.Unsupported("A subquery");
            var inner = ParseCondition();
            ExpectSymbol(")");
            return inner;
        }
        return ParsePredicate();
    }

    private SqlCondition ParsePredicate()
    {
        var left = ParseOperand();

        if (TryKeyword("IS"))
        {
            var negated = TryKeyword("NOT");
            Expect("NULL");
            return new SqlIsNull(left, negated);
        }

        var not = TryKeyword("NOT");
        if (TryKeyword("LIKE"))
        {
            var pattern = ParseOperand();
            RefuseKeyword("ESCAPE", "LIKE ... ESCAPE");
            return new SqlLike(left, pattern, not);
        }
        RefuseKeyword("IN", "IN (...)", "Use OR instead, like (CITY = @a OR CITY = @b).");
        RefuseKeyword("BETWEEN", "BETWEEN", "Use two comparisons instead, like X >= @low AND X <= @high.");
        if (not)
            throw DbfSqlError.Syntax($"NOT here must be followed by LIKE, but found {Current.Describe()}.");

        if (Current.Kind == SqlTokenKind.Symbol && Current.Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
        {
            var op = Next().Text;
            return new SqlCompare(left, op == "!=" ? "<>" : op, ParseOperand());
        }

        if (Current.IsSymbol("*"))
            throw ArithmeticRefused();
        throw DbfSqlError.Syntax($"Expected a comparison (=, <>, <, <=, >, >=, LIKE or IS NULL) but found {Current.Describe()}.");
    }

    private static DbfException ArithmeticRefused() =>
        DbfSqlError.Unsupported("Arithmetic ('*')", "Compare columns with values directly; compute anything else in .NET after reading.");

    private SqlOperand ParseOperand()
    {
        var token = Current;
        if (token.Kind == SqlTokenKind.Word && !IsReserved(token) && _tokens[_pos + 1].IsSymbol("("))
            throw DbfSqlError.Unsupported($"The function {token.Text.ToUpperInvariant()}()",
                "Compare columns with values directly. LIKE is case-insensitive, and other functions can run in .NET after reading.");
        if (token.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedName && !(token.Kind == SqlTokenKind.Word && IsValueKeyword(token)))
            return new SqlColumn(ParseColumnName("a column name"));
        if (token.IsSymbol("(") && _tokens[_pos + 1].Is("SELECT"))
            throw DbfSqlError.Unsupported("A subquery");
        if (token.IsSymbol("*"))
            throw ArithmeticRefused();
        return ParseValue();
    }

    private SqlOperand ParseValue()
    {
        var token = Next();
        switch (token.Kind)
        {
            case SqlTokenKind.Parameter:
                return new SqlParameterRef(token.Text);
            case SqlTokenKind.Text:
                return new SqlLiteral(token.Text);
            case SqlTokenKind.Number:
                if (!decimal.TryParse(token.Text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    throw DbfSqlError.Syntax($"'{token.Text}' at position {token.Position + 1} isn't a valid number.");
                return new SqlLiteral(number);
            case SqlTokenKind.Word when token.Is("NULL"):
                return new SqlLiteral(null);
            case SqlTokenKind.Word when token.Is("TRUE"):
                return new SqlLiteral(true);
            case SqlTokenKind.Word when token.Is("FALSE"):
                return new SqlLiteral(false);
            case SqlTokenKind.Word when _tokens[_pos].IsSymbol("("):
                throw DbfSqlError.Unsupported($"The function {token.Text.ToUpperInvariant()}()");
            default:
                throw DbfSqlError.Syntax($"Expected a value (@parameter, 'text', number, TRUE, FALSE or NULL) at position {token.Position + 1}, but found {token.Describe()}.");
        }
    }

    private SqlOperand ParseCount(string clause)
    {
        var token = Current;
        var value = token.Kind is SqlTokenKind.Number or SqlTokenKind.Parameter
            ? ParseValue()
            : throw DbfSqlError.Syntax($"{clause} needs a whole number or a @parameter, but found {token.Describe()}.");
        if (value is SqlLiteral { Value: decimal d } && (d != decimal.Truncate(d) || d > int.MaxValue))
            throw DbfSqlError.Syntax($"{clause} needs a whole number up to {int.MaxValue}, not {token.Text}.");
        return value;
    }

    // ---- names ----------------------------------------------------------------------------

    private string ParseTableName()
    {
        return ParseName("a table name");
    }

    private string ParseColumnName(string what)
    {
        if (Current.IsSymbol("*"))
            throw DbfSqlError.Syntax($"Expected {what} but found '*'.", "Use SELECT * on its own, or list the columns.");
        var token = Current;
        var name = ParseName(what);
        if (Current.IsSymbol("(") && token.Kind == SqlTokenKind.Word)
            throw DbfSqlError.Unsupported($"The function {name.ToUpperInvariant()}()",
                token.Text.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
                    ? "To count rows, read them and count in .NET, or use DbfDatabase directly: db[\"T\"].RecordCount."
                    : null);
        return name;
    }

    private string ParseName(string what)
    {
        var token = Current;
        if (token.Kind == SqlTokenKind.QuotedName || (token.Kind == SqlTokenKind.Word && !IsReserved(token)))
        {
            Next();
            return token.Text;
        }
        if (token.Kind == SqlTokenKind.Word)
            throw DbfSqlError.Syntax($"Expected {what} but found the keyword {token.Text.ToUpperInvariant()}.",
                $"If it really is a name, quote it: [{token.Text}].");
        throw DbfSqlError.Syntax($"Expected {what} but found {token.Describe()}.");
    }

    private static bool IsReserved(SqlToken token) => token.Kind == SqlTokenKind.Word && Reserved.Contains(token.Text);

    private static bool IsValueKeyword(SqlToken token) => token.Is("NULL") || token.Is("TRUE") || token.Is("FALSE");

    // ---- helpers --------------------------------------------------------------------------

    private void RefuseAfterTable()
    {
        foreach (var join in new[] { "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS" })
            RefuseKeyword(join, "JOIN", "Query one table at a time and combine the results in .NET.");
        if (Current.IsSymbol(","))
            throw DbfSqlError.Unsupported("Selecting from more than one table", "Query one table at a time and combine the results in .NET.");
        if (Current.Kind == SqlTokenKind.Word && !IsReserved(Current))
            throw DbfSqlError.Unsupported($"A table alias ('{Current.Text}')", "Refer to columns by their plain names; one table is queried at a time.");
    }

    private void RefuseKeyword(string keyword, string feature, string? hint = null)
    {
        if (Current.Is(keyword))
            throw DbfSqlError.Unsupported(feature, hint);
    }

    private bool TryKeyword(string keyword)
    {
        if (!Current.Is(keyword))
            return false;
        _pos++;
        return true;
    }

    private bool TrySymbol(string symbol)
    {
        if (!Current.IsSymbol(symbol))
            return false;
        _pos++;
        return true;
    }

    private void Expect(string keyword)
    {
        if (!TryKeyword(keyword))
            throw DbfSqlError.Syntax($"Expected {keyword} but found {Current.Describe()}.");
    }

    private void ExpectSymbol(string symbol)
    {
        if (!TrySymbol(symbol))
            throw DbfSqlError.Syntax($"Expected '{symbol}' but found {Current.Describe()}.");
    }

    private void ExpectEnd()
    {
        if (Current.Kind == SqlTokenKind.End)
            return;
        if (Current.Kind == SqlTokenKind.Word && Reserved.Contains(Current.Text))
            throw DbfSqlError.Unsupported($"{Current.Text.ToUpperInvariant()} here");
        throw DbfSqlError.Syntax($"Unexpected {Current.Describe()} at position {Current.Position + 1}; the statement should have ended.");
    }
}
