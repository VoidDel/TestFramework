using TestFramework.Abstractions.Expressions;
using Xunit;

namespace TestFramework.Tests;

public sealed class SequenceExpressionTests
{
    private static readonly Dictionary<string, object?> Variables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["soc"] = 96,
        ["mode"] = "charge",
        ["cells"] = new[] { 3.30, 3.31, 3.28 },
        ["count"] = 0,
        ["typed"] = "007",
        ["flag"] = true
    };

    [Theory]
    [InlineData("1 + 2 * 3", 7.0)]
    [InlineData("(1 + 2) * 3", 9.0)]
    [InlineData("-${soc} + 100", 4.0)]
    [InlineData("10 % 4", 2.0)]
    [InlineData("1e3 / 4", 250.0)]
    [InlineData("abs(-2.5)", 2.5)]
    [InlineData("round(3.14159, 2)", 3.14)]
    [InlineData("round(2.5)", 3.0)]
    [InlineData("count(${cells})", 3.0)]
    [InlineData("${cells}[1]", 3.31)]
    [InlineData("min(4, 2, 8)", 2.0)]
    public void Arithmetic_ComputesNumbers(string text, double expected)
    {
        var value = SequenceExpression.Parse(text).Evaluate(Variables);

        Assert.Equal(expected, Assert.IsType<double>(value), precision: 9);
    }

    [Fact]
    public void Aggregates_OverAListVariable_ComputeTheCellSpread()
    {
        var spread = SequenceExpression.Parse("max(${cells}) - min(${cells})").Evaluate(Variables);

        Assert.Equal(0.03, Assert.IsType<double>(spread), precision: 9);
        Assert.Equal(3.2966666666666664, (double)SequenceExpression.Parse("avg(${cells})").Evaluate(Variables)!, precision: 9);
    }

    [Theory]
    [InlineData("${soc} >= 95", true)]
    [InlineData("${soc} >= 95 && ${mode} == 'charge'", true)]
    [InlineData("${mode} != \"charge\" || ${soc} < 10", false)]
    [InlineData("!${flag}", false)]
    [InlineData("${typed} == 7", true)]
    [InlineData("'it''s' == \"it's\"", true)]
    [InlineData("${count} > 0 && 10 / ${count} > 1", false)]
    public void Conditions_EvaluateToBooleans(string text, bool expected)
    {
        Assert.Equal(expected, SequenceExpression.Parse(text).EvaluateCondition(Variables));
    }

    [Fact]
    public void VariableNames_ListsEveryReferenceOnce()
    {
        var expression = SequenceExpression.Parse("${a} + ${b} * ${A}");

        Assert.Equal(["a", "b"], expression.VariableNames);
    }

    [Theory]
    [InlineData("1 +", "Expected a value")]
    [InlineData("(1 + 2", "Expected ')'")]
    [InlineData("soc > 3", "is written ${soc}")]
    [InlineData("foo(1)", "Unknown function 'foo'")]
    [InlineData("${1st} + 1", "not a valid variable reference")]
    [InlineData("'open", "closing quote")]
    [InlineData("1 2", "Unexpected '2'")]
    public void SyntaxErrors_SayWhatIsWrongAndWhere(string text, string message)
    {
        var error = Assert.Throws<SequenceExpressionException>(() => SequenceExpression.Parse(text));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Position);
    }

    [Theory]
    [InlineData("${missing} + 1", "not defined")]
    [InlineData("1 / ${count}", "Division by zero")]
    [InlineData("${mode} > 3", "needs a number")]
    [InlineData("${soc} && true", "needs true or false")]
    [InlineData("${cells}[3]", "outside a list")]
    [InlineData("min(${cells}, 'x')", "needs a number")]
    public void EvaluationErrors_AreErrorsRatherThanGuesses(string text, string message)
    {
        var error = Assert.Throws<SequenceExpressionException>(() => SequenceExpression.Parse(text).Evaluate(Variables));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("${soc}")]
    [InlineData("${mode}")]
    public void Condition_MustBeABoolean_NeverTruthy(string text)
    {
        // A number or text as a condition is a mistake; treating 96 as "true" would hide it.
        Assert.Throws<SequenceExpressionException>(() => SequenceExpression.Parse(text).EvaluateCondition(Variables));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("${soc} - 90", 6)]
    public void Count_IsAWholeNumber(string text, int expected)
    {
        Assert.Equal(expected, SequenceExpression.Parse(text).EvaluateCount(Variables, maximum: 1000));
    }

    [Theory]
    [InlineData("2.5")]
    [InlineData("-1")]
    [InlineData("5000")]
    [InlineData("'three'")]
    public void Count_RefusesFractionsNegativesAndRunaways(string text)
    {
        Assert.Throws<SequenceExpressionException>(() => SequenceExpression.Parse(text).EvaluateCount(Variables, maximum: 1000));
    }

    [Fact]
    public void TryParse_ReportsInsteadOfThrowing()
    {
        Assert.False(SequenceExpression.TryParse("1 +", out var expression, out var error));
        Assert.Null(expression);
        Assert.Contains("Expected a value", error, StringComparison.Ordinal);
        Assert.False(SequenceExpression.TryParse("  ", out _, out _));
    }
}
