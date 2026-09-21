using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Variables;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// Substitution itself: what <c>${}</c> means, how to write one that is not one, and what stops the
/// resolver when the structure it is given has no bottom.
/// </summary>
public sealed class VariableResolverTests
{
    private static Dictionary<string, object?> Variables() =>
        new(StringComparer.OrdinalIgnoreCase) { ["voltage"] = 12.5, ["serial"] = "ABC" };

    [Fact]
    public void ResolveValue_WholeValueReference_KeepsTheVariableType()
    {
        Assert.Equal(12.5, VariableResolver.ResolveValue("${voltage}", Variables()));
    }

    [Fact]
    public void ResolveValue_EmbeddedReference_BecomesText()
    {
        Assert.Equal("DUT-ABC", VariableResolver.ResolveValue("DUT-${serial}", Variables()));
    }

    [Fact]
    public void ResolveValue_EscapedReference_ProducesTheLiteralText()
    {
        // Without an escape a parameter simply could not carry the characters "${voltage}", and the
        // only way to get them through was to misspell the name - which is not a way.
        Assert.Equal("${voltage}", VariableResolver.ResolveValue("$${voltage}", Variables()));
    }

    [Fact]
    public void ResolveValue_EscapeMixedWithARealReference_ResolvesOnlyTheReference()
    {
        Assert.Equal("${voltage} is 12.5", VariableResolver.ResolveValue("$${voltage} is ${voltage}", Variables()));
    }

    [Theory]
    [InlineData("$$5.00")]      // two dollars, no brace: not an escape
    [InlineData("a $$ b")]
    [InlineData("100$$")]
    public void ResolveValue_DollarsNotBeforeABrace_AreLeftAlone(string text)
    {
        // The escape is deliberately "$$" only directly before a brace. Escaping every "$$" would
        // have changed the meaning of values that already contain one.
        Assert.Equal(text, VariableResolver.ResolveValue(text, Variables()));
    }

    [Fact]
    public void IsReference_EscapeIsNotAReference()
    {
        Assert.False(VariableReference.IsReference("$${voltage}"));
        Assert.Empty(VariableReference.NamesIn("$${voltage}"));
        Assert.True(VariableReference.IsReference("${voltage}"));
    }

    [Fact]
    public void HasMalformedReference_TellsATypoApartFromAnEscape()
    {
        Assert.True(VariableReference.HasMalformedReference("${1stReading}"));
        Assert.True(VariableReference.HasMalformedReference("${my var}"));
        Assert.True(VariableReference.HasMalformedReference("${voltage"));

        Assert.False(VariableReference.HasMalformedReference("$${voltage}"));
        Assert.False(VariableReference.HasMalformedReference("${voltage}"));
        Assert.False(VariableReference.HasMalformedReference("no dollars here"));
    }

    [Fact]
    public void ResolveValue_UndefinedVariable_IsReported()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => VariableResolver.ResolveValue("${missing}", Variables()));

        Assert.Contains("missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveValue_StructureThatContainsItself_IsReportedInsteadOfKillingTheProcess()
    {
        // A plugin's SaveSettings decides what a host writes into a step's parameters. A structure
        // that contains itself would recurse until the stack ran out, and a StackOverflowException
        // cannot be caught: it takes the process, and whatever the DUT was in the middle of, with
        // it. The bound matches the loader's, so nothing that loads from a file can hit this.
        var looping = new List<object?>();
        looping.Add(looping);

        var error = Assert.Throws<InvalidOperationException>(
            () => VariableResolver.ResolveValue(looping, Variables()));

        Assert.Contains("nesting", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveValue_DeepButFiniteStructure_StillResolves()
    {
        // 60 levels: under the bound, so a legitimately deep file is not refused.
        object? nested = "${serial}";
        for (var depth = 0; depth < 60; depth++)
        {
            nested = new List<object?> { nested };
        }

        var resolved = VariableResolver.ResolveValue(nested, Variables());

        for (var depth = 0; depth < 60; depth++)
        {
            resolved = Assert.IsType<List<object?>>(resolved)[0];
        }

        Assert.Equal("ABC", resolved);
    }
}
