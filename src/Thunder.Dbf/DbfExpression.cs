//      ___________________________
//     |  UPPER(LASTNAME) + DTOS(  |     ProjectThunder :: Thunder.Dbf
//     |  HIRED) .AND. .NOT.       |     The dBASE expressions that index keys
//     |  DELETED()                |     and FOR filters are written in.
//     |___________________________|
//                 |
//          parse  v  evaluate against a record
//
// Grammar (lowest precedence first):
//   or      := and   { .OR. and }
//   and     := not   { .AND. not }
//   not     := (.NOT. | !) not | compare
//   compare := sum   [ (= == <> # != < <= > >= $) sum ]
//   sum     := product { (+ | -) product }
//   product := unary   { (* | /) unary }
//   unary   := - unary | primary
//   primary := number | 'string' | "string" | [string] | .T. | .F.
//            | name ( args ) | name | alias->name | ( or )

using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

/// <summary>
/// A parsed dBASE expression, such as an index key or FOR filter. Character fields evaluate
/// padded to their full width, as in dBASE, so <c>FIRST + LAST</c> keeps FIRST's trailing spaces.
/// </summary>
public sealed class DbfExpression
{
    private readonly Node _root;

    private DbfExpression(string text, Node root)
    {
        Text = text;
        _root = root;
    }

    public string Text { get; }

    public static DbfExpression Parse(string text)
    {
        var parser = new Parser(text);
        var root = parser.ParseOr();
        parser.ExpectEnd();
        return new DbfExpression(text, root);
    }

    /// <summary>Evaluates to a string, decimal, bool or DateOnly? for <paramref name="record"/>.</summary>
    public object? Evaluate(DbfRecord record) => _root.Evaluate(record);

    // ---- syntax tree ----------------------------------------------------------------------

    private abstract class Node
    {
        public abstract object? Evaluate(DbfRecord record);
    }

    private sealed class Literal(object? value) : Node
    {
        public override object? Evaluate(DbfRecord record) => value;
    }

    private sealed class FieldRef(string name) : Node
    {
        public override object? Evaluate(DbfRecord record)
        {
            var fields = record.Header.Fields;
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (!string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = record[i];
                return field.Type switch
                {
                    'C' => ((string?)value ?? "").PadRight(field.Length),
                    'N' or 'F' or 'I' or 'Y' or 'B' when value is null => 0m,
                    'N' or 'F' or 'I' or 'Y' => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                    'B' => value is double d ? (decimal)d : value,
                    'L' => value is true,
                    'D' => value as DateOnly?,
                    'M' => value as string ?? "",
                    _ => value,
                };
            }
            throw new KeyNotFoundException($"The expression uses '{name}', which isn't a field of this table.");
        }
    }

    private sealed class Unary(string op, Node operand) : Node
    {
        public override object? Evaluate(DbfRecord record)
        {
            var value = operand.Evaluate(record);
            return op switch
            {
                "NOT" => !AsBool(value),
                "-" => -AsNumber(value),
                _ => throw new InvalidOperationException(op),
            };
        }
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override object? Evaluate(DbfRecord record)
        {
            if (op == "AND")
                return AsBool(left.Evaluate(record)) && AsBool(right.Evaluate(record));
            if (op == "OR")
                return AsBool(left.Evaluate(record)) || AsBool(right.Evaluate(record));

            var a = left.Evaluate(record);
            var b = right.Evaluate(record);
            return op switch
            {
                "+" when a is string s1 => s1 + AsString(b),
                "+" when a is DateOnly d => d.AddDays((int)AsNumber(b)),
                "+" => AsNumber(a) + AsNumber(b),
                // dBASE's "-" on strings moves the left side's trailing spaces to the end.
                "-" when a is string s1 => s1.TrimEnd() + AsString(b) + new string(' ', s1.Length - s1.TrimEnd().Length),
                "-" when a is DateOnly d1 && b is DateOnly d2 => (decimal)(d1.DayNumber - d2.DayNumber),
                "-" when a is DateOnly d => d.AddDays(-(int)AsNumber(b)),
                "-" => AsNumber(a) - AsNumber(b),
                "*" => AsNumber(a) * AsNumber(b),
                "/" => AsNumber(a) / AsNumber(b),
                "$" => AsString(b).Contains(AsString(a), StringComparison.Ordinal),
                _ => Compare(op, a, b),
            };
        }

        private static bool Compare(string op, object? a, object? b)
        {
            int order;
            if (a is string s1)
            {
                var s2 = AsString(b);
                // SET EXACT OFF: "Smith" = "Sm" is true; compare only the right side's length.
                if (op is "=" && s2.Length < s1.Length)
                    s1 = s1[..s2.Length];
                order = string.CompareOrdinal(s1, s2);
            }
            else if (a is bool b1)
                order = b1.CompareTo(AsBool(b));
            else if (a is DateOnly or null && b is DateOnly or null)
                order = Nullable.Compare((DateOnly?)a, (DateOnly?)b);
            else
                order = AsNumber(a).CompareTo(AsNumber(b));

            return op switch
            {
                "=" or "==" => order == 0,
                "<>" or "#" or "!=" => order != 0,
                "<" => order < 0,
                "<=" => order <= 0,
                ">" => order > 0,
                ">=" => order >= 0,
                _ => throw new InvalidOperationException(op),
            };
        }
    }

    private sealed class Call(string name, Node[] args) : Node
    {
        public override object? Evaluate(DbfRecord record)
        {
            object? Arg(int i) => args[i].Evaluate(record);
            string S(int i) => AsString(Arg(i));
            decimal N(int i) => AsNumber(Arg(i));
            int Count() => args.Length;

            switch (name)
            {
                case "UPPER": return S(0).ToUpperInvariant();
                case "LOWER": return S(0).ToLowerInvariant();
                case "TRIM" or "RTRIM": return S(0).TrimEnd();
                case "LTRIM": return S(0).TrimStart();
                case "ALLTRIM": return S(0).Trim();
                case "SPACE": return new string(' ', (int)N(0));
                case "LEN": return (decimal)S(0).Length;
                case "LEFT": return Substring(S(0), 1, (int)N(1));
                case "RIGHT":
                {
                    var s = S(0);
                    var n = Math.Clamp((int)N(1), 0, s.Length);
                    return s[^n..];
                }
                case "SUBSTR": return Substring(S(0), (int)N(1), Count() > 2 ? (int)N(2) : int.MaxValue);
                case "STR": return Str(N(0), Count() > 1 ? (int)N(1) : 10, Count() > 2 ? (int)N(2) : 0);
                case "VAL": return Val(S(0));
                case "ABS": return Math.Abs(N(0));
                case "INT": return decimal.Truncate(N(0));
                case "IIF": return AsBool(Arg(0)) ? Arg(1) : Arg(2);
                case "DTOS": return Arg(0) is DateOnly d ? d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "        ";
                case "YEAR": return Arg(0) is DateOnly y ? (decimal)y.Year : 0m;
                case "MONTH": return Arg(0) is DateOnly m ? (decimal)m.Month : 0m;
                case "DAY": return Arg(0) is DateOnly dd ? (decimal)dd.Day : 0m;
                case "DELETED": return record.IsDeleted;
                case "RECNO": return (decimal)record.RecordNumber;
                default: throw new NotSupportedException($"The dBASE function {name}() isn't supported yet.");
            }
        }

        private static string Substring(string s, int start, int length)
        {
            var from = Math.Clamp(start - 1, 0, s.Length);
            return s.Substring(from, Math.Clamp(length, 0, s.Length - from));
        }

        // STR(): right-justified, rounded to the given decimals; asterisks when it doesn't fit.
        private static string Str(decimal value, int length, int decimals)
        {
            var text = Math.Round(value, decimals, MidpointRounding.AwayFromZero)
                .ToString("F" + decimals, CultureInfo.InvariantCulture);
            return text.Length > length ? new string('*', length) : text.PadLeft(length);
        }

        // VAL(): the leading number in a string, 0 when there isn't one.
        private static decimal Val(string s)
        {
            s = s.TrimStart();
            var end = 0;
            while (end < s.Length && (char.IsAsciiDigit(s[end]) || (end == 0 && s[end] is '-' or '+') || s[end] == '.'))
                end++;
            return decimal.TryParse(s[..end], NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0m;
        }
    }

    private static bool AsBool(object? value) => value is true;
    private static string AsString(object? value) => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static decimal AsNumber(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        bool b => b ? 1m : 0m,
        string s => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0m,
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    // ---- parser ---------------------------------------------------------------------------

    private sealed class Parser(string text)
    {
        private int _pos;

        public Node ParseOr()
        {
            var left = ParseAnd();
            while (TryKeyword(".OR."))
                left = new Binary("OR", left, ParseAnd());
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (TryKeyword(".AND."))
                left = new Binary("AND", left, ParseNot());
            return left;
        }

        private Node ParseNot()
        {
            if (TryKeyword(".NOT.") || TryKeyword("!", requireNotFollowedBy: '='))
                return new Unary("NOT", ParseNot());
            return ParseCompare();
        }

        private Node ParseCompare()
        {
            var left = ParseSum();
            foreach (var op in new[] { "==", "<>", "!=", "<=", ">=", "=", "#", "<", ">", "$" })
            {
                if (TrySymbol(op))
                    return new Binary(op, left, ParseSum());
            }
            return left;
        }

        private Node ParseSum()
        {
            var left = ParseProduct();
            while (true)
            {
                if (TrySymbol("+"))
                    left = new Binary("+", left, ParseProduct());
                else if (Peek() == '-' && PeekAt(1) != '>' && TrySymbol("-"))
                    left = new Binary("-", left, ParseProduct());
                else
                    return left;
            }
        }

        private Node ParseProduct()
        {
            var left = ParseUnary();
            while (true)
            {
                if (TrySymbol("*"))
                    left = new Binary("*", left, ParseUnary());
                else if (TrySymbol("/"))
                    left = new Binary("/", left, ParseUnary());
                else
                    return left;
            }
        }

        private Node ParseUnary() => TrySymbol("-") ? new Unary("-", ParseUnary()) : ParsePrimary();

        private Node ParsePrimary()
        {
            SkipSpace();
            if (_pos >= text.Length)
                throw Error("Expected a value");

            var c = text[_pos];
            if (TrySymbol("("))
            {
                var inner = ParseOr();
                Expect(")");
                return inner;
            }
            if (c is '\'' or '"' or '[')
                return new Literal(ReadString(c == '[' ? ']' : c));
            if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(PeekAt(1))))
                return new Literal(ReadNumber());
            if (TryKeyword(".T.") || TryKeyword(".TRUE.") || TryKeyword(".Y."))
                return new Literal(true);
            if (TryKeyword(".F.") || TryKeyword(".FALSE.") || TryKeyword(".N."))
                return new Literal(false);
            if (char.IsLetter(c) || c == '_')
            {
                var name = ReadName();
                if (TrySymbol("->"))
                    name = ReadName(); // alias->field: we only ever have one table
                if (TrySymbol("("))
                {
                    var args = new List<Node>();
                    if (!TrySymbol(")"))
                    {
                        do args.Add(ParseOr());
                        while (TrySymbol(","));
                        Expect(")");
                    }
                    return new Call(name.ToUpperInvariant(), [.. args]);
                }
                return new FieldRef(name);
            }
            throw Error($"Unexpected '{c}'");
        }

        public void ExpectEnd()
        {
            SkipSpace();
            if (_pos < text.Length)
                throw Error($"Unexpected '{text[_pos]}'");
        }

        private string ReadName()
        {
            SkipSpace();
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_'))
                _pos++;
            if (start == _pos)
                throw Error("Expected a name");
            return text[start.._pos];
        }

        private decimal ReadNumber()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] == '.'))
                _pos++;
            return decimal.Parse(text[start.._pos], CultureInfo.InvariantCulture);
        }

        private string ReadString(char close)
        {
            var start = ++_pos;
            var end = text.IndexOf(close, start);
            if (end < 0)
                throw Error("Unterminated string");
            _pos = end + 1;
            return text[start..end];
        }

        private bool TryKeyword(string keyword, char requireNotFollowedBy = '\0')
        {
            SkipSpace();
            if (string.Compare(text, _pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            if (requireNotFollowedBy != '\0' && PeekAt(keyword.Length) == requireNotFollowedBy)
                return false;
            _pos += keyword.Length;
            return true;
        }

        private bool TrySymbol(string symbol) => TryKeyword(symbol);

        private void Expect(string symbol)
        {
            if (!TrySymbol(symbol))
                throw Error($"Expected '{symbol}'");
        }

        private char Peek()
        {
            SkipSpace();
            return _pos < text.Length ? text[_pos] : '\0';
        }

        private char PeekAt(int offset) => _pos + offset < text.Length ? text[_pos + offset] : '\0';

        private void SkipSpace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }

        private FormatException Error(string message) => new($"{message} at position {_pos + 1} of the dBASE expression '{text}'.");
    }
}
