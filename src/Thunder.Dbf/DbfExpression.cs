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

    /// <summary>
    /// Evaluates to a string, number (decimal, or double beyond decimal's range), bool or DateOnly?
    /// (null for a blank date) for <paramref name="record"/>. Returns null when a Visual FoxPro .NULL.
    /// value flows through the expression; a FOR filter treats that as false.
    /// </summary>
    public object? Evaluate(DbfRecord record)
    {
        var value = _root.Evaluate(record);
        return value is NullValue ? null : value;
    }

    // ---- values ---------------------------------------------------------------------------

    /// <summary>Visual FoxPro's .NULL.: distinct from a blank date or an empty string.</summary>
    private sealed class NullValue
    {
        public static readonly NullValue Instance = new();
    }

    private static InvalidOperationException Mismatch(string what) => new($"Data type mismatch: {what}.");

    private static bool IsNumber(object? v) => v is decimal or double;

    private static decimal Dec(object? v, string where) => v switch
    {
        decimal d => d,
        double d when Math.Abs(d) < 7.9e28 => (decimal)d,
        _ => throw Mismatch($"{where} needs a number"),
    };

    private static double Dbl(object? v) => v is double d ? d : (double)(decimal)v!;

    private static string Str(object? v, string where) => v as string ?? throw Mismatch($"{where} needs a string");

    private static bool Bool(object? v, string where) => v is bool b ? b : throw Mismatch($"{where} needs a logical value");

    private static object Arithmetic(string op, object a, object b)
    {
        if (a is decimal x && b is decimal y)
        {
            try
            {
                return op switch { "+" => x + y, "-" => x - y, "*" => x * y, _ => x / y };
            }
            catch (OverflowException)
            {
                // fall through to double, like dBASE's own floating arithmetic would
            }
        }
        var (p, q) = (Dbl(a), Dbl(b));
        return op switch { "+" => p + q, "-" => p - q, "*" => p * q, _ => p / q };
    }

    private static string TrimSpacesEnd(string s) => s.TrimEnd(' ');

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
                if (value is null && field.IsNullable)
                    return NullValue.Instance;

                switch (field.Type)
                {
                    case 'C':
                    {
                        // dBASE character fields are their full width in bytes, trailing spaces included.
                        var text = (string?)value ?? "";
                        var padding = field.Length - record.Encoding.GetByteCount(text);
                        return padding > 0 ? text + new string(' ', padding) : text;
                    }
                    case 'N' or 'F' or 'I' or 'Y' or 'B' when value is null:
                        return 0m;
                    case 'N' or 'F' or 'B' when value is double d:
                        return d;
                    case 'N' or 'F' or 'I' or 'Y' or 'B':
                        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    case 'L':
                        return value is true;
                    case 'D':
                        return value as DateOnly?;
                    case 'M':
                        return value as string ?? "";
                    default:
                        return value;
                }
            }
            throw new KeyNotFoundException($"The expression uses '{name}', which isn't a field of this table.");
        }
    }

    private sealed class Unary(string op, Node operand) : Node
    {
        public override object? Evaluate(DbfRecord record)
        {
            var value = operand.Evaluate(record);
            if (value is NullValue)
                return value;
            return op switch
            {
                "NOT" => !Bool(value, ".NOT."),
                "-" => value is double d ? -d : -Dec(value, "unary -"),
                _ => throw new InvalidOperationException(op),
            };
        }
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override object? Evaluate(DbfRecord record)
        {
            if (op is "AND" or "OR")
            {
                var l = left.Evaluate(record);
                var r = right.Evaluate(record);
                bool? lb = l is NullValue ? null : Bool(l, $".{op}.");
                bool? rb = r is NullValue ? null : Bool(r, $".{op}.");
                // Three-valued logic, as Visual FoxPro does with .NULL.
                object? result = op == "AND"
                    ? (lb == false || rb == false ? false : lb is null || rb is null ? null : true)
                    : (lb == true || rb == true ? true : lb is null || rb is null ? null : false);
                return result ?? NullValue.Instance;
            }

            var a = left.Evaluate(record);
            var b = right.Evaluate(record);
            if (a is NullValue || b is NullValue)
                return NullValue.Instance;

            switch (op)
            {
                case "+" when a is string s1:
                    return s1 + Str(b, "string +");
                case "+" when a is DateOnly d && IsNumber(b):
                    return d.AddDays((int)Dec(b, "date +"));
                case "+" when IsNumber(a) && IsNumber(b):
                    return Arithmetic("+", a!, b!);
                case "+":
                    throw Mismatch($"can't add {Describe(a)} and {Describe(b)}");

                case "-" when a is string s1:
                {
                    // dBASE's "-" on strings moves the left side's trailing spaces to the end.
                    var s2 = Str(b, "string -");
                    var trimmed = TrimSpacesEnd(s1);
                    return trimmed + s2 + new string(' ', s1.Length - trimmed.Length);
                }
                case "-" when a is DateOnly d1 && b is DateOnly d2:
                    return (decimal)(d1.DayNumber - d2.DayNumber);
                case "-" when a is DateOnly d && IsNumber(b):
                    return d.AddDays(-(int)Dec(b, "date -"));
                case "-" when IsNumber(a) && IsNumber(b):
                    return Arithmetic("-", a!, b!);
                case "-":
                    throw Mismatch($"can't subtract {Describe(b)} from {Describe(a)}");

                case "*" or "/" when IsNumber(a) && IsNumber(b):
                    return Arithmetic(op, a!, b!);
                case "*" or "/":
                    throw Mismatch($"{op} needs numbers");

                case "$":
                    return Str(b, "$").Contains(Str(a, "$"), StringComparison.Ordinal);

                default:
                    return Compare(op, a, b);
            }
        }

        private static bool Compare(string op, object? a, object? b)
        {
            int order;
            if (a is string s1)
            {
                var s2 = Str(b, $"comparison {op}");
                // SET EXACT OFF: compare only as far as the right side goes ("Smith" = "Sm").
                if (op != "==" && s2.Length < s1.Length)
                    s1 = s1[..s2.Length];
                order = string.CompareOrdinal(s1, s2);
            }
            else if (a is bool b1)
                order = b1.CompareTo(Bool(b, $"comparison {op}"));
            else if (a is DateOnly or null && b is DateOnly or null)
                order = Nullable.Compare((DateOnly?)a, (DateOnly?)b);
            else if (IsNumber(a) && IsNumber(b))
                order = a is double || b is double ? Dbl(a).CompareTo(Dbl(b)) : ((decimal)a!).CompareTo((decimal)b!);
            else
                throw Mismatch($"can't compare {Describe(a)} with {Describe(b)}");

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

        private static string Describe(object? v) => v switch
        {
            string => "a string",
            decimal or double => "a number",
            bool => "a logical",
            DateOnly or null => "a date",
            _ => v.GetType().Name,
        };
    }

    private sealed class Call(string name, Node[] args) : Node
    {
        public override object? Evaluate(DbfRecord record)
        {
            var values = new object?[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                values[i] = args[i].Evaluate(record);
                if (values[i] is NullValue && name != "IIF")
                    return NullValue.Instance;
            }

            string S(int i) => Str(values[i], $"{name}()");
            decimal N(int i) => Dec(values[i], $"{name}()");
            DateOnly? D(int i) => values[i] is DateOnly or null ? (DateOnly?)values[i] : throw Mismatch($"{name}() needs a date");
            int Count() => values.Length;

            switch (name)
            {
                case "UPPER": return S(0).ToUpperInvariant();
                case "LOWER": return S(0).ToLowerInvariant();
                case "TRIM" or "RTRIM": return S(0).TrimEnd(' ');
                case "LTRIM": return S(0).TrimStart(' ');
                case "ALLTRIM": return S(0).Trim(' ');
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
                case "STR": return FormatStr(values[0], Count() > 1 ? (int)N(1) : 10, Count() > 2 ? (int)N(2) : 0);
                case "VAL": return Val(S(0));
                case "ABS": return values[0] is double ad ? Math.Abs(ad) : Math.Abs(N(0));
                case "INT": return values[0] is double id ? Math.Truncate(id) : decimal.Truncate(N(0));
                case "IIF": return values[0] is NullValue ? values[2] : Bool(values[0], "IIF()") ? values[1] : values[2];
                case "DTOS": return D(0) is { } d ? d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "        ";
                case "YEAR": return D(0) is { } y ? (decimal)y.Year : 0m;
                case "MONTH": return D(0) is { } m ? (decimal)m.Month : 0m;
                case "DAY": return D(0) is { } dd ? (decimal)dd.Day : 0m;
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

        // STR(): right-justified, rounded half away from zero; asterisks when it doesn't fit.
        private static string FormatStr(object? value, int length, int decimals)
        {
            var text = value is double d && Math.Abs(d) >= 7.9e28
                ? d.ToString("E" + Math.Max(0, length - 7), CultureInfo.InvariantCulture)
                : Math.Round(Dec(value, "STR()"), decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture);
            return text.Length > length ? new string('*', length) : text.PadLeft(length);
        }

        // VAL(): the leading number (optional sign, digits, one decimal point); 0 when there isn't one.
        private static decimal Val(string s)
        {
            s = s.TrimStart(' ');
            var end = 0;
            var seenPoint = false;
            if (end < s.Length && s[end] is '-' or '+')
                end++;
            while (end < s.Length && (char.IsAsciiDigit(s[end]) || (s[end] == '.' && !seenPoint)))
            {
                seenPoint |= s[end] == '.';
                end++;
            }
            return decimal.TryParse(s[..end], NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0m;
        }
    }

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
                    throw new NotSupportedException($"'{name}->{ReadName()}' refers to another work area or a memory variable; only this table's own fields are supported.");
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
