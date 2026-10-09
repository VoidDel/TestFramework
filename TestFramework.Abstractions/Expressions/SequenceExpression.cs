using System.Collections;
using System.Globalization;

namespace TestFramework.Abstractions.Expressions;

/// <summary>
/// The small expression language a sequence uses for conditions, loop counts, retry conditions and
/// computed values: <c>${soc} &gt;= 95</c>, <c>max(${cells}) - min(${cells})</c>,
/// <c>${mode} == 'charge' &amp;&amp; ${retries} &lt; 3</c>.
///
/// Variables are written <c>${name}</c>, exactly as in a parameter, so the validator and the
/// editors recognise a reference the same way wherever it appears. The language is deliberately
/// strict: a condition must be a boolean, never "truthy"; comparing text with a number is an error
/// rather than a guess; a division by zero is an error rather than infinity. A test that silently
/// took the wrong branch is worse than one that stops and says why.
///
/// Grammar, loosest binding first:
/// <code>
/// or      := and ('||' and)*
/// and     := not ('&amp;&amp;' not)*
/// not     := '!' not | compare
/// compare := sum (('==' | '!=' | '&lt;' | '&lt;=' | '&gt;' | '&gt;=') sum)?
/// sum     := product (('+' | '-') product)*
/// product := unary (('*' | '/' | '%') unary)*
/// unary   := '-' unary | postfix
/// postfix := primary ('[' or ']')*
/// primary := number | 'text' | "text" | true | false | null | ${name} | name '(' args ')' | '(' or ')'
/// </code>
///
/// Functions: <c>abs</c>, <c>round(x[, digits])</c>, <c>min</c>, <c>max</c>, <c>sum</c>,
/// <c>avg</c>, <c>count</c>. The aggregate ones take a list - a variable holding 80 cell voltages -
/// or several arguments.
/// </summary>
public sealed class SequenceExpression
{
    private readonly Node _root;

    private SequenceExpression(string text, Node root, IReadOnlyList<string> variableNames)
    {
        Text = text;
        _root = root;
        VariableNames = variableNames;
    }

    public string Text { get; }

    /// <summary>Every variable the expression reads, in order of first appearance.</summary>
    public IReadOnlyList<string> VariableNames { get; }

    /// <summary>Parses <paramref name="text"/>, throwing <see cref="SequenceExpressionException"/> when it is not valid.</summary>
    public static SequenceExpression Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        var root = parser.ParseAll();
        return new SequenceExpression(text, root, parser.VariableNames);
    }

    public static bool TryParse(string? text, out SequenceExpression? expression, out string? error)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Expression is empty.";
            return false;
        }

        try
        {
            expression = Parse(text);
            error = null;
            return true;
        }
        catch (SequenceExpressionException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The value: a <see cref="double"/>, <see cref="bool"/>, <see cref="string"/>, a list, or null.
    /// Numbers come back as double whatever type the variables held.
    /// </summary>
    public object? Evaluate(IDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return Normalize(_root.Evaluate(variables));
    }

    /// <summary>Evaluates a condition, which must come out as a boolean.</summary>
    public bool EvaluateCondition(IDictionary<string, object?> variables)
    {
        return Evaluate(variables) is bool result
            ? result
            : throw new SequenceExpressionException($"Condition '{Text}' does not evaluate to true or false.");
    }

    /// <summary>Evaluates a count, which must come out as a whole, non-negative number.</summary>
    public int EvaluateCount(IDictionary<string, object?> variables, int maximum)
    {
        var value = Evaluate(variables);
        if (value is not double number || number != Math.Floor(number) || number < 0)
        {
            throw new SequenceExpressionException($"Count '{Text}' must be a whole number of zero or more, but is '{Describe(value)}'.");
        }

        if (number > maximum)
        {
            throw new SequenceExpressionException($"Count '{Text}' is {number}, above the limit of {maximum}.");
        }

        return (int)number;
    }

    private static object? Normalize(object? value) => value switch
    {
        List<object?> list => list.AsReadOnly(),
        _ => value
    };

    internal static string Describe(object? value) => value switch
    {
        null => "null",
        string text => text,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IEnumerable => "a list",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    /// <summary>A variable's value in the expression's own types: numbers as double, lists as lists.</summary>
    internal static object? FromVariable(object? value)
    {
        return value switch
        {
            null => null,
            string or bool or double => value,
            float or decimal or int or long or short or byte or uint or ulong or ushort or sbyte =>
                Convert.ToDouble(value, CultureInfo.InvariantCulture),
            IDictionary dictionary => dictionary.Values.Cast<object?>().Select(FromVariable).ToList(),
            IEnumerable list => list.Cast<object?>().Select(FromVariable).ToList(),
            _ => value
        };
    }

    private abstract class Node
    {
        public abstract object? Evaluate(IDictionary<string, object?> variables);
    }

    private sealed class Literal(object? value) : Node
    {
        public override object? Evaluate(IDictionary<string, object?> variables) => value;
    }

    private sealed class Variable(string name) : Node
    {
        public override object? Evaluate(IDictionary<string, object?> variables)
        {
            return variables.TryGetValue(name, out var value)
                ? FromVariable(value)
                : throw new SequenceExpressionException($"Variable '{name}' is not defined.");
        }
    }

    private sealed class Unary(string op, Node operand) : Node
    {
        public override object? Evaluate(IDictionary<string, object?> variables)
        {
            var value = operand.Evaluate(variables);
            return op switch
            {
                "-" => -Operations.Number(value, "-"),
                _ => !Operations.Boolean(value, "!")
            };
        }
    }

    private sealed class Logical(string op, Node left, Node right) : Node
    {
        // Short-circuits, so ${count} > 0 && ${total} / ${count} > 3 never divides by zero.
        public override object? Evaluate(IDictionary<string, object?> variables)
        {
            var first = Operations.Boolean(left.Evaluate(variables), op);
            if (op == "&&" ? !first : first)
            {
                return first;
            }

            return Operations.Boolean(right.Evaluate(variables), op);
        }
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override object? Evaluate(IDictionary<string, object?> variables) =>
            Operations.Apply(op, left.Evaluate(variables), right.Evaluate(variables));
    }

    private sealed class Index(Node target, Node index) : Node
    {
        public override object? Evaluate(IDictionary<string, object?> variables)
        {
            if (target.Evaluate(variables) is not List<object?> list)
            {
                throw new SequenceExpressionException("Only a list can be indexed.");
            }

            var position = Operations.Number(index.Evaluate(variables), "[]");
            if (position != Math.Floor(position) || position < 0 || position >= list.Count)
            {
                throw new SequenceExpressionException($"Index {Describe(position)} is outside a list of {list.Count}.");
            }

            return list[(int)position];
        }
    }

    private sealed class Call(string name, IReadOnlyList<Node> arguments) : Node
    {
        public override object? Evaluate(IDictionary<string, object?> variables) =>
            Functions.Invoke(name, arguments.Select(argument => argument.Evaluate(variables)).ToList());
    }

    private static class Operations
    {
        public static double Number(object? value, string op)
        {
            return value switch
            {
                double number => number,
                string text when double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => throw new SequenceExpressionException($"Operator '{op}' needs a number, but got '{Describe(value)}'.")
            };
        }

        public static bool Boolean(object? value, string op)
        {
            return value is bool flag
                ? flag
                : throw new SequenceExpressionException($"Operator '{op}' needs true or false, but got '{Describe(value)}'.");
        }

        public static object? Apply(string op, object? left, object? right)
        {
            switch (op)
            {
                case "==":
                    return AreEqual(left, right);
                case "!=":
                    return !AreEqual(left, right);
                case "<":
                    return Number(left, op) < Number(right, op);
                case "<=":
                    return Number(left, op) <= Number(right, op);
                case ">":
                    return Number(left, op) > Number(right, op);
                case ">=":
                    return Number(left, op) >= Number(right, op);
                case "+" when left is string first && right is string second:
                    return first + second;
            }

            var a = Number(left, op);
            var b = Number(right, op);
            var result = op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" when b == 0 => throw new SequenceExpressionException("Division by zero."),
                "/" => a / b,
                "%" when b == 0 => throw new SequenceExpressionException("Division by zero."),
                "%" => a % b,
                _ => throw new SequenceExpressionException($"Unknown operator '{op}'.")
            };

            return double.IsFinite(result)
                ? result
                : throw new SequenceExpressionException($"'{Describe(a)} {op} {Describe(b)}' is not a finite number.");
        }

        /// <summary>
        /// Numbers compare as numbers - including a number held as text, which is what a value
        /// typed into a run parameter is - and anything else compares exactly. Text is never
        /// compared with a number by its characters: "007" equals 7.
        /// </summary>
        private static bool AreEqual(object? left, object? right)
        {
            if (left is double || right is double)
            {
                return TryNumber(left, out var a) && TryNumber(right, out var b) && a == b;
            }

            return Equals(left, right);
        }

        private static bool TryNumber(object? value, out double number)
        {
            switch (value)
            {
                case double existing:
                    number = existing;
                    return true;
                case string text:
                    return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                default:
                    number = 0;
                    return false;
            }
        }
    }

    private static class Functions
    {
        public static readonly IReadOnlySet<string> Names =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "abs", "round", "min", "max", "sum", "avg", "count" };

        public static object? Invoke(string name, IReadOnlyList<object?> arguments)
        {
            switch (name.ToLowerInvariant())
            {
                case "abs":
                    Expect(name, arguments, 1, 1);
                    return Math.Abs(Operations.Number(arguments[0], name));
                case "round":
                    Expect(name, arguments, 1, 2);
                    var digits = arguments.Count == 2 ? Operations.Number(arguments[1], name) : 0;
                    if (digits != Math.Floor(digits) || digits is < 0 or > 15)
                    {
                        throw new SequenceExpressionException("round() takes 0 to 15 decimal places.");
                    }

                    return Math.Round(Operations.Number(arguments[0], name), (int)digits, MidpointRounding.AwayFromZero);
                case "count":
                    // Every number in the language is a double; an int here would fail ==.
                    return (double)Flatten(name, arguments).Count;
            }

            var numbers = Flatten(name, arguments).Select(value => Operations.Number(value, name)).ToList();
            if (numbers.Count == 0)
            {
                throw new SequenceExpressionException($"{name}() of no values has no result.");
            }

            return name.ToLowerInvariant() switch
            {
                "min" => numbers.Min(),
                "max" => numbers.Max(),
                "sum" => numbers.Sum(),
                _ => numbers.Average()
            };
        }

        private static List<object?> Flatten(string name, IReadOnlyList<object?> arguments)
        {
            if (arguments.Count == 0)
            {
                throw new SequenceExpressionException($"{name}() needs at least one argument.");
            }

            return arguments.SelectMany(argument => argument is List<object?> list ? list : [argument]).ToList();
        }

        private static void Expect(string name, IReadOnlyList<object?> arguments, int minimum, int maximum)
        {
            if (arguments.Count < minimum || arguments.Count > maximum)
            {
                throw new SequenceExpressionException(minimum == maximum
                    ? $"{name}() takes {minimum} argument(s)."
                    : $"{name}() takes {minimum} to {maximum} arguments.");
            }
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly List<string> _variables = [];
        private int _position;

        public Parser(string text) => _text = text;

        public IReadOnlyList<string> VariableNames => _variables;

        public Node ParseAll()
        {
            var node = ParseOr();
            SkipSpace();
            if (_position < _text.Length)
            {
                throw Error($"Unexpected '{_text[_position]}'");
            }

            return node;
        }

        private Node ParseOr()
        {
            var node = ParseAnd();
            while (Accept("||"))
            {
                node = new Logical("||", node, ParseAnd());
            }

            return node;
        }

        private Node ParseAnd()
        {
            var node = ParseNot();
            while (Accept("&&"))
            {
                node = new Logical("&&", node, ParseNot());
            }

            return node;
        }

        private Node ParseNot()
        {
            if (Peek("!") && !Peek("!="))
            {
                Accept("!");
                return new Unary("!", ParseNot());
            }

            return ParseCompare();
        }

        private Node ParseCompare()
        {
            var node = ParseSum();
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
            {
                if (Accept(op))
                {
                    return new Binary(op, node, ParseSum());
                }
            }

            return node;
        }

        private Node ParseSum()
        {
            var node = ParseProduct();
            while (true)
            {
                if (Accept("+")) node = new Binary("+", node, ParseProduct());
                else if (Accept("-")) node = new Binary("-", node, ParseProduct());
                else return node;
            }
        }

        private Node ParseProduct()
        {
            var node = ParseUnary();
            while (true)
            {
                if (Accept("*")) node = new Binary("*", node, ParseUnary());
                else if (Accept("/")) node = new Binary("/", node, ParseUnary());
                else if (Accept("%")) node = new Binary("%", node, ParseUnary());
                else return node;
            }
        }

        private Node ParseUnary()
        {
            if (Accept("-"))
            {
                return new Unary("-", ParseUnary());
            }

            var node = ParsePrimary();
            while (Accept("["))
            {
                var index = ParseOr();
                Require("]");
                node = new Index(node, index);
            }

            return node;
        }

        private Node ParsePrimary()
        {
            SkipSpace();
            if (_position >= _text.Length)
            {
                throw Error("Expected a value");
            }

            var current = _text[_position];
            if (Accept("("))
            {
                var inner = ParseOr();
                Require(")");
                return inner;
            }

            if (current == '$')
            {
                return ParseVariable();
            }

            if (current is '\'' or '"')
            {
                return new Literal(ParseString(current));
            }

            if (char.IsDigit(current) || (current == '.' && _position + 1 < _text.Length && char.IsDigit(_text[_position + 1])))
            {
                return new Literal(ParseNumber());
            }

            if (char.IsLetter(current) || current == '_')
            {
                return ParseWord();
            }

            throw Error($"Unexpected '{current}'");
        }

        private Node ParseVariable()
        {
            var start = _position;
            if (!_text.AsSpan(_position).StartsWith("${", StringComparison.Ordinal))
            {
                throw Error("A variable is written ${name}");
            }

            var end = _text.IndexOf('}', _position);
            if (end < 0)
            {
                throw Error("A variable reference is missing its closing '}'");
            }

            var reference = _text[start..(end + 1)];
            if (!Models.VariableReference.IsWholeValueReference(reference))
            {
                throw Error($"'{reference}' is not a valid variable reference");
            }

            var name = reference[2..^1];
            if (!_variables.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                _variables.Add(name);
            }

            _position = end + 1;
            return new Variable(name);
        }

        private string ParseString(char quote)
        {
            var builder = new System.Text.StringBuilder();
            _position++;
            while (_position < _text.Length)
            {
                var current = _text[_position++];
                if (current != quote)
                {
                    builder.Append(current);
                    continue;
                }

                // A doubled quote is a literal quote: 'it''s'.
                if (_position < _text.Length && _text[_position] == quote)
                {
                    builder.Append(quote);
                    _position++;
                    continue;
                }

                return builder.ToString();
            }

            throw Error("Text is missing its closing quote");
        }

        private double ParseNumber()
        {
            var start = _position;
            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.'))
            {
                _position++;
            }

            if (_position < _text.Length && _text[_position] is 'e' or 'E')
            {
                _position++;
                if (_position < _text.Length && _text[_position] is '+' or '-')
                {
                    _position++;
                }

                while (_position < _text.Length && char.IsDigit(_text[_position]))
                {
                    _position++;
                }
            }

            var token = _text[start.._position];
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? number
                : throw Error($"'{token}' is not a number", start);
        }

        private Node ParseWord()
        {
            var start = _position;
            while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_'))
            {
                _position++;
            }

            var word = _text[start.._position];
            switch (word.ToLowerInvariant())
            {
                case "true":
                    return new Literal(true);
                case "false":
                    return new Literal(false);
                case "null":
                    return new Literal(null);
            }

            if (!Accept("("))
            {
                // The likeliest cause is a variable written without ${}: say so.
                throw Error($"'{word}' is not a value; a variable is written ${{{word}}}", start);
            }

            if (!Functions.Names.Contains(word))
            {
                throw Error($"Unknown function '{word}'", start);
            }

            var arguments = new List<Node>();
            if (!Accept(")"))
            {
                do
                {
                    arguments.Add(ParseOr());
                }
                while (Accept(","));

                Require(")");
            }

            return new Call(word, arguments);
        }

        private void SkipSpace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }

        private bool Peek(string token)
        {
            SkipSpace();
            return _text.AsSpan(_position).StartsWith(token, StringComparison.Ordinal);
        }

        private bool Accept(string token)
        {
            if (!Peek(token))
            {
                return false;
            }

            _position += token.Length;
            return true;
        }

        private void Require(string token)
        {
            if (!Accept(token))
            {
                throw Error($"Expected '{token}'");
            }
        }

        private SequenceExpressionException Error(string message, int? at = null)
        {
            var position = at ?? _position;
            return new SequenceExpressionException($"{message} at position {position + 1} of '{_text}'.", position);
        }
    }
}

public sealed class SequenceExpressionException : Exception
{
    public SequenceExpressionException(string message, int? position = null)
        : base(message)
    {
        Position = position;
    }

    /// <summary>Zero-based offset of the problem in the expression text, when it is a syntax error.</summary>
    public int? Position { get; }
}
