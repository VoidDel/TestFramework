using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Plugins;
using TestFramework.Plugin.Abstractions.UI;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// What the validator knows about <c>${variable}</c> references before a run starts.
///
/// Two things it used to get wrong: it did not know about variables a step creates through
/// <c>variableWrites</c>, so the pattern this feature exists for was reported as an error; and it
/// said nothing at all about a reference too malformed to be one, which the runner then passes to
/// the plugin as literal text.
/// </summary>
public sealed class VariableReferenceValidationTests
{
    [Fact]
    public void Validate_VariableWrittenByAnEarlierStep_IsAccepted()
    {
        // Measure in one step, use the measurement in the next. Previously an Error:
        // "references variable 'measured', which is not defined".
        var sequence = Sequence(
            Item("item", Step("s1", writes: "measured"), Step("s2", value: "${measured}")));

        Assert.Empty(Validate(sequence));
    }

    [Fact]
    public void Validate_VariableWrittenByAnEarlierItem_IsAccepted()
    {
        // Variables are sequence-scoped, so the set has to carry across items, not reset per item.
        var sequence = Sequence(
            Item("first", Step("s1", writes: "measured")),
            Item("second", Step("s2", value: "${measured}")));

        Assert.Empty(Validate(sequence));
    }

    [Fact]
    public void Validate_VariableWrittenByALaterStep_IsStillReported()
    {
        // The write happens after the step finishes, so reading it earlier never resolves. This is
        // the case the check exists for and it must survive the fix above.
        var sequence = Sequence(
            Item("item", Step("s1", value: "${measured}"), Step("s2", writes: "measured")));

        var issue = Assert.Single(Validate(sequence));
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Contains("measured", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_StepCannotReferenceTheVariableItWritesItself()
    {
        var sequence = Sequence(Item("item", Step("s1", value: "${own}", writes: "own")));

        var issue = Assert.Single(Validate(sequence));
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    [Theory]
    [InlineData("${1stReading}")] // a name cannot start with a digit
    [InlineData("${my var}")]     // nor contain a space
    [InlineData("${measured")]    // nor is this closed
    public void Validate_MalformedReference_IsWarnedAboutRatherThanPassedThroughInSilence(string text)
    {
        // The resolver leaves these alone and the plugin receives the characters, so nothing
        // downstream would ever object - and they are exactly the shape a mistyped name takes.
        var sequence = Sequence(Item("item", Step("s1", value: text)));

        var issue = Assert.Single(Validate(sequence));
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Contains("literal text", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WellFormedReferenceToADefinedVariable_IsNotWarnedAbout()
    {
        var sequence = Sequence(Item("item", Step("s1", value: "${defined}")));
        sequence.Variables["defined"] = "x";

        Assert.Empty(Validate(sequence));
    }

    [Theory]
    [InlineData("${measured}", true)]
    [InlineData("prefix-${measured}", true)]
    [InlineData("${1stReading}", false)]
    [InlineData("${my var}", false)]
    [InlineData("${measured", false)]
    [InlineData("plain", false)]
    public void IsVariableReference_AnswersTheSameAsTheFrameworkRule(string text, bool expected)
    {
        // The editor SDK used to decide this with a Contains("${") of its own, so it protected
        // ${my var} as a binding while the runner treated it as characters.
        Assert.Equal(expected, SettingsValueConverter.IsVariableReference(text));
        Assert.Equal(expected, VariableReference.IsReference(text));
    }

    private static IReadOnlyList<ValidationIssue> Validate(TestSequence sequence)
    {
        var registry = new PluginRegistry();
        registry.Register(new DeclaringPlugin());
        return new TestSequenceValidator(registry).Validate(sequence);
    }

    private static TestSequence Sequence(params TestItemDefinition[] items) => new()
    {
        Name = "Sequence",
        Items = [.. items]
    };

    private static TestItemDefinition Item(string id, params TestStepDefinition[] steps) => new()
    {
        Id = id,
        Name = id,
        MainSteps = [.. steps],
        VerdictSource = new VerdictSource { StepId = steps[^1].Id }
    };

    private static TestStepDefinition Step(string id, string value = "1", string? writes = null)
    {
        var step = new TestStepDefinition
        {
            Id = id,
            Name = id,
            PluginId = "demo.declaring",
            PluginVersion = "1.0.0",
            Parameters = { ["text"] = value }
        };

        if (writes is not null)
        {
            step.VariableWrites.Add(new VariableWriteDefinition { Name = writes, OutputKey = "out" });
        }

        return step;
    }

    /// <summary>Declares a parameter, which is what turns on the reference checks.</summary>
    private sealed class DeclaringPlugin : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.declaring",
            DisplayName = "Declaring",
            Version = new Version(1, 0, 0)
        };

        public IReadOnlyList<StepParameterDescriptor> Parameters =>
            [new StepParameterDescriptor { Name = "text", Kind = StepParameterKind.String }];

        public Type SettingsType => typeof(Dictionary<string, object?>);
        public object CreateDefaultSettings() => new Dictionary<string, object?>();
        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => parameters;
        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) =>
            (IReadOnlyDictionary<string, object?>)settings;

        public Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TestStepResult { Verdict = TestVerdict.Pass, Outputs = { ["out"] = 1 } });
    }
}
