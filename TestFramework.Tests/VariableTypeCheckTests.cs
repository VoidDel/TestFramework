using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Plugins;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// Type checking across the variable boundary.
///
/// Declaring parameters buys one thing above all: a wrong type is reported while the sequence is
/// being edited instead of part-way through a run with a DUT connected. A <c>${variable}</c> used
/// to switch that off completely - the check was suspended and nothing replaced it - so the values
/// most likely to come from the line were the ones nobody checked. Where the variable's value is
/// knowable before the run it is now checked like a literal.
/// </summary>
public sealed class VariableTypeCheckTests
{
    [Fact]
    public void Validate_VariableHoldingTheWrongType_IsReportedBeforeTheRun()
    {
        var sequence = SequenceUsing("count", "${serial}");
        sequence.Variables["serial"] = "ABC123";

        var issue = Assert.Single(Validate(sequence));

        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Contains("serial", issue.Message, StringComparison.Ordinal);
        Assert.Contains("ABC123", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_VariableHoldingTheRightType_IsAccepted()
    {
        var sequence = SequenceUsing("count", "${retries}");
        sequence.Variables["retries"] = 3;

        Assert.Empty(Validate(sequence));
    }

    [Fact]
    public void Validate_IntegerParameterFedByAFractionalVariable_IsReported()
    {
        var sequence = SequenceUsing("count", "${retries}");
        sequence.Variables["retries"] = 2.5;

        var issue = Assert.Single(Validate(sequence));
        Assert.Contains("whole number", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EnumParameterFedByAValueOutsideTheChoices_IsReported()
    {
        var sequence = SequenceUsing("mode", "${mode}");
        sequence.Variables["mode"] = "sideways";

        var issue = Assert.Single(Validate(sequence));
        Assert.Contains("'fast'", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_VariableAStepReassigns_IsNotTypeChecked()
    {
        // The initial value says string, but a step writes it and only the plugin decides what
        // comes out - and whether that write even executes depends on enablement and error
        // policies. Guessing here would block a run that works.
        var sequence = SequenceUsing("count", "${measured}");
        sequence.Variables["measured"] = "not a number yet";
        sequence.Items[0].MainSteps[0].VariableWrites.Add(
            new VariableWriteDefinition { Name = "measured", OutputKey = "out" });

        Assert.Empty(Validate(sequence));
    }

    [Fact]
    public void Validate_ReferenceEmbeddedInText_IsNotTypeChecked()
    {
        // Embedded in text the result is text whatever the variable holds, so the variable's own
        // type says nothing about what the parameter receives.
        var sequence = SequenceUsing("label", "DUT-${retries}");
        sequence.Variables["retries"] = 3;

        Assert.Empty(Validate(sequence));
    }

    [Fact]
    public void Validate_RangeOfTheInitialValue_IsNotChecked()
    {
        // A limit belongs to the value a run produces; the initial value is not that, and a run may
        // well write an in-range value before the step is reached.
        var sequence = SequenceUsing("count", "${retries}");
        sequence.Variables["retries"] = 9999;

        Assert.Empty(Validate(sequence));
    }

    private static IReadOnlyList<ValidationIssue> Validate(TestSequence sequence)
    {
        var registry = new PluginRegistry();
        registry.Register(new TypedPlugin());
        return new TestSequenceValidator(registry).Validate(sequence);
    }

    private static TestSequence SequenceUsing(string parameter, object? value) => new()
    {
        Name = "Sequence",
        Items =
        [
            new TestItemDefinition
            {
                Id = "item",
                Name = "Item",
                MainSteps =
                [
                    new TestStepDefinition
                    {
                        Id = "step",
                        Name = "Step",
                        PluginId = "demo.typed",
                        PluginVersion = "1.0.0",
                        Parameters = { [parameter] = value }
                    }
                ],
                VerdictSource = new VerdictSource { StepId = "step" }
            }
        ]
    };

    private sealed class TypedPlugin : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.typed",
            DisplayName = "Typed",
            Version = new Version(1, 0, 0)
        };

        public IReadOnlyList<StepParameterDescriptor> Parameters =>
        [
            new StepParameterDescriptor { Name = "count", Kind = StepParameterKind.Integer, Minimum = 0, Maximum = 10 },
            new StepParameterDescriptor { Name = "label", Kind = StepParameterKind.String },
            new StepParameterDescriptor
            {
                Name = "mode",
                Kind = StepParameterKind.Enum,
                Choices = [new StepParameterChoice("fast"), new StepParameterChoice("slow")]
            }
        ];

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
