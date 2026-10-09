using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Plugins.BasicSteps;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// Numeric verdicts over list and dictionary outputs, limits taken from variables, and what a
/// result records about the judgment and the plugin behind it.
/// </summary>
public sealed class MeasurementVerdictTests
{
    [Fact]
    public async Task ListOutput_IsJudgedPerElement_AndTheRecordNamesTheFailingCell()
    {
        var cells = Enumerable.Repeat(3.30, 80).ToArray();
        cells[41] = 2.95;

        var item = await RunAsync(cells, Numeric(lower: 3.0, upper: 3.6));

        Assert.Equal(TestVerdict.Fail, item.Verdict);
        Assert.Equal(80, item.Measurements.Count);
        var failing = Assert.Single(item.Measurements, measurement => measurement.Verdict == TestVerdict.Fail);
        Assert.Equal("cells[41]", failing.Name);
        Assert.Equal(41, failing.Index);
        Assert.Equal(2.95, failing.Value);
    }

    [Fact]
    public async Task AnotherStepFailing_StillLeavesEveryElementRecorded()
    {
        // A spread check failing the item must not cost the report the per-cell records: "which
        // cell was low" is the first thing anyone asks of a failed pack.
        var cells = Enumerable.Repeat(3.30, 80).ToArray();
        cells[17] = 2.95;
        var registry = new PluginRegistry();
        registry.Register(new OutputStep("probe.measure", new Version(1, 0, 0)));
        registry.Register(new LimitCheckStepPlugin());
        var item = new TestItemDefinition
        {
            Name = "Cells",
            MainSteps =
            [
                MeasureStep(cells),
                new TestStepDefinition
                {
                    Id = "spread",
                    Name = "spread",
                    PluginId = "basic.limit-check",
                    Parameters = { ["Value"] = 0.35, ["Min"] = 0.0, ["Max"] = 0.01 }
                }
            ],
            VerdictSource = Numeric(lower: 3.0, upper: 3.6)
        };

        var result = await new TestSequenceRunner(registry).RunAsync(new TestSequence { Items = [item] });
        var judged = Assert.Single(result.ItemResults);

        Assert.Equal(TestVerdict.Fail, judged.Verdict);
        Assert.Equal(80, judged.Measurements.Count);
        Assert.Equal("cells[17]", Assert.Single(judged.Measurements, m => m.Verdict == TestVerdict.Fail).Name);
    }

    [Fact]
    public async Task ListOutput_ConvertsEachElementIntoTheLimitUnit()
    {
        var source = Numeric(lower: 3.0, upper: 3.6);
        source.SourceUnit = "mV";
        source.Unit = "V";

        var item = await RunAsync(new List<int> { 3300, 3310, 3295 }, source);

        Assert.Equal(TestVerdict.Pass, item.Verdict);
        Assert.All(item.Measurements, measurement =>
        {
            Assert.Equal("V", measurement.Unit);
            Assert.Equal(3.0, measurement.LowerLimit);
            Assert.Equal(3.6, measurement.UpperLimit);
        });
        Assert.Equal(3.31, item.Measurements[1].Value!.Value, precision: 9);
    }

    [Fact]
    public async Task DictionaryOutput_NamesEachRecordByItsKey()
    {
        var signals = new Dictionary<string, double> { ["Cell01"] = 3.30, ["Cell02"] = 2.90 };

        var item = await RunAsync(signals, Numeric(lower: 3.0, upper: 3.6));

        Assert.Equal(TestVerdict.Fail, item.Verdict);
        Assert.Equal(["Cell01", "Cell02"], item.Measurements.Select(measurement => measurement.Name));
        Assert.Equal(TestVerdict.Fail, item.Measurements[1].Verdict);
    }

    [Fact]
    public async Task EmptyListOutput_IsInconclusive()
    {
        // No elements is no evidence; passing it would pass a DUT whose read returned nothing.
        var item = await RunAsync(Array.Empty<double>(), Numeric(lower: 3.0, upper: 3.6));

        Assert.Equal(TestVerdict.Inconclusive, item.Verdict);
        Assert.Empty(item.Measurements);
    }

    [Theory]
    [InlineData(3.3, TestVerdict.Inconclusive)]
    [InlineData(2.0, TestVerdict.Fail)]
    public async Task UnreadableElement_IsInconclusive_UnlessAnotherElementFails(double other, TestVerdict expected)
    {
        var item = await RunAsync(new object?[] { other, "n/a" }, Numeric(lower: 3.0, upper: 3.6));

        Assert.Equal(expected, item.Verdict);
        Assert.Equal(TestVerdict.Inconclusive, item.Measurements[1].Verdict);
        Assert.Null(item.Measurements[1].Value);
    }

    [Fact]
    public async Task ScalarOutput_RecordsTheValueLimitsAndUnitItWasJudgedOn()
    {
        var source = Numeric(lower: 4.8, upper: 5.2);
        source.Unit = "V";

        var item = await RunAsync("5012 mV", source);

        Assert.Equal(TestVerdict.Pass, item.Verdict);
        var measurement = Assert.Single(item.Measurements);
        Assert.Equal("cells", measurement.Name);
        Assert.Null(measurement.Index);
        Assert.Equal("5012 mV", measurement.RawValue);
        Assert.Equal(5.012, measurement.Value!.Value, precision: 9);
        Assert.Equal("V", measurement.Unit);
        Assert.Equal(4.8, measurement.LowerLimit);
        Assert.Equal(5.2, measurement.UpperLimit);
    }

    [Fact]
    public async Task LimitReferences_ResolveFromVariables_AndAreRecordedAsResolved()
    {
        var source = Numeric(lower: null, upper: null);
        source.LowerLimitReference = "${cellMin}";
        source.UpperLimitReference = "${cellMax}";
        var variables = new Dictionary<string, object?> { ["cellMin"] = 3.0, ["cellMax"] = "3.6" };

        var item = await RunAsync(new[] { 3.3, 3.7 }, source, variables);

        Assert.Equal(TestVerdict.Fail, item.Verdict);
        Assert.All(item.Measurements, measurement =>
        {
            Assert.Equal(3.0, measurement.LowerLimit);
            Assert.Equal(3.6, measurement.UpperLimit);
        });
    }

    [Fact]
    public async Task LimitReference_TakesTheValueAStepWroteBeforeTheItemWasJudged()
    {
        var source = Numeric(lower: 3.0, upper: null);
        source.UpperLimitReference = "${cellMax}";
        var step = MeasureStep(new[] { 3.5 });
        step.VariableWrites.Add(new VariableWriteDefinition { Name = "cellMax", Value = 3.6 });
        var variables = new Dictionary<string, object?> { ["cellMax"] = 3.4 };

        var item = await RunAsync(step, source, variables);

        Assert.Equal(TestVerdict.Pass, item.Verdict);
        Assert.Equal(3.6, Assert.Single(item.Measurements).UpperLimit);
    }

    [Theory]
    [InlineData("${missing}", "not defined")]
    [InlineData("${label}", "finite number")]
    [InlineData("about ${cellMax}", "single ${variable}")]
    public async Task UnusableLimitReference_IsAnItemErrorThatSaysWhy(string reference, string expectedMessage)
    {
        var source = Numeric(lower: 3.0, upper: null);
        source.UpperLimitReference = reference;
        var variables = new Dictionary<string, object?> { ["label"] = "high", ["cellMax"] = 3.6 };

        var item = await RunAsync(new[] { 3.3 }, source, variables);

        Assert.Equal(TestVerdict.Error, item.Verdict);
        Assert.Contains(expectedMessage, item.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(item.Measurements);
    }

    [Fact]
    public async Task InvertedLimitsFromVariables_AreAnErrorRatherThanFailingEveryUnit()
    {
        var source = Numeric(lower: null, upper: null);
        source.LowerLimitReference = "${cellMin}";
        source.UpperLimitReference = "${cellMax}";
        var variables = new Dictionary<string, object?> { ["cellMin"] = 3.6, ["cellMax"] = 3.0 };

        var item = await RunAsync(new[] { 3.3 }, source, variables);

        Assert.Equal(TestVerdict.Error, item.Verdict);
        Assert.Contains("greater than upperLimit", item.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingVerdictOutput_IsAnItemErrorThatSaysWhy()
    {
        var source = Numeric(lower: 3.0, upper: 3.6);
        source.OutputKey = "absent";

        var item = await RunAsync(new[] { 3.3 }, source);

        Assert.Equal(TestVerdict.Error, item.Verdict);
        Assert.Contains("'absent'", item.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StepResult_RecordsThePluginVersionThatActuallyRan()
    {
        // The sequence pins 1.0.0, only 1.2.0 is installed: the result must say 1.2.0, because that
        // is the code that produced the measurement.
        var registry = new PluginRegistry();
        registry.Register(new OutputStep("probe.measure", new Version(1, 2, 0)));
        registry.Register(new OutputStep("probe.throw", new Version(1, 0, 0), throws: true));
        var item = new TestItemDefinition
        {
            Name = "Item",
            MainSteps =
            [
                MeasureStep(3.3),
                new TestStepDefinition { Id = "broken", Name = "broken", PluginId = "probe.throw", OnError = ErrorHandlingMode.Continue }
            ],
            VerdictSource = new VerdictSource { StepId = "measure" }
        };

        var result = await new TestSequenceRunner(registry).RunAsync(new TestSequence { Items = [item] });
        var steps = Assert.Single(result.ItemResults).MainResults;

        Assert.Equal("probe.measure", steps[0].PluginId);
        Assert.Equal("1.2.0", steps[0].PluginVersion);
        Assert.Equal(TestVerdict.Error, steps[1].Verdict);
        Assert.Equal("1.0.0", steps[1].PluginVersion);
    }

    [Fact]
    public void Yaml_LimitReference_RoundTripsThroughTheSameKey()
    {
        var yaml = """
            schemaVersion: 1
            name: Cells
            variables:
              cellMin: 3.0
            items:
            - id: cells
              name: Cell voltages
              verdictSource:
                stepId: read
                outputKey: cells
                judgeType: Numeric
                lowerLimit: ${cellMin}
                upperLimit: 3.6
              main:
              - id: read
                name: Read
                pluginId: probe.measure
            """;
        var service = new TestSequenceYamlService();

        var loaded = service.Load(yaml);
        var source = loaded.Items[0].VerdictSource;
        var saved = service.Save(loaded);
        var reloaded = service.Load(saved).Items[0].VerdictSource;

        Assert.Null(source.LowerLimit);
        Assert.Equal("${cellMin}", source.LowerLimitReference);
        Assert.Equal(3.6, source.UpperLimit);
        Assert.Contains("lowerLimit: ${cellMin}", saved, StringComparison.Ordinal);
        Assert.Equal("${cellMin}", reloaded.LowerLimitReference);
        Assert.Equal(3.6, reloaded.UpperLimit);
    }

    [Fact]
    public void Yaml_UnusableLimitText_StillLoads_AndTheValidatorReportsIt()
    {
        var yaml = """
            schemaVersion: 1
            name: Cells
            items:
            - id: cells
              name: Cell voltages
              verdictSource:
                stepId: read
                outputKey: cells
                judgeType: Numeric
                lowerLimit: abc
              main:
              - id: read
                name: Read
                pluginId: probe.measure
            """;

        var sequence = new TestSequenceYamlService().Load(yaml);
        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Equal("abc", sequence.Items[0].VerdictSource.LowerLimitReference);
        Assert.Contains(issues, issue =>
            issue.Path == "items[0].verdictSource.lowerLimit" &&
            issue.Message.Contains("must be a number or a single", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("${missing}", "not defined")]
    [InlineData("${label}", "rather than a number")]
    public void Validator_RejectsALimitReferenceTheRunnerCouldNotUse(string reference, string expectedMessage)
    {
        var sequence = ValidationSequence(reference);
        sequence.Variables["label"] = "high";

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue =>
            issue.Path == "items[0].verdictSource.upperLimit" &&
            issue.Message.Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_AcceptsALimitWrittenByAStepOfTheSameItem()
    {
        var sequence = ValidationSequence("${cellMax}");
        sequence.Items[0].MainSteps[0].VariableWrites.Add(new VariableWriteDefinition { Name = "cellMax", OutputKey = "max" });

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.DoesNotContain(issues, issue => issue.Path.StartsWith("items[0].verdictSource", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_ReportsInvertedLimitsKnowableFromTheFile()
    {
        var sequence = ValidationSequence("${cellMax}");
        sequence.Variables["cellMax"] = 2.5;

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue =>
            issue.Path == "items[0].verdictSource.lowerLimit" &&
            issue.Message.Contains("less than or equal", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_ReportsALimitGivenBothAsValueAndReference()
    {
        var sequence = ValidationSequence("${cellMax}");
        sequence.Variables["cellMax"] = 3.6;
        sequence.Items[0].VerdictSource.UpperLimit = 3.6;

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue =>
            issue.Path == "items[0].verdictSource.upperLimit" &&
            issue.Message.Contains("keep one", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_CountsAReferenceAsALimit()
    {
        var sequence = ValidationSequence("${cellMax}");
        sequence.Variables["cellMax"] = 3.6;
        sequence.Items[0].VerdictSource.LowerLimit = null;

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.DoesNotContain(issues, issue => issue.Path.StartsWith("items[0].verdictSource", StringComparison.Ordinal));
    }

    private static VerdictSource Numeric(double? lower, double? upper) => new()
    {
        StepId = "measure",
        OutputKey = "cells",
        JudgeType = VerdictJudgeType.Numeric,
        LowerLimit = lower,
        UpperLimit = upper
    };

    private static TestSequence ValidationSequence(string upperReference) => new()
    {
        Name = "Sequence",
        Items =
        [
            new TestItemDefinition
            {
                Id = "cells",
                Name = "Cells",
                MainSteps = [MeasureStep(3.3)],
                VerdictSource = new VerdictSource
                {
                    StepId = "measure",
                    OutputKey = "cells",
                    JudgeType = VerdictJudgeType.Numeric,
                    LowerLimit = 3.0,
                    UpperLimitReference = upperReference
                }
            }
        ]
    };

    private static TestStepDefinition MeasureStep(object? output) => new()
    {
        Id = "measure",
        Name = "measure",
        PluginId = "probe.measure",
        PluginVersion = "1.0.0",
        Parameters = { ["output"] = output }
    };

    private static Task<TestItemRunResult> RunAsync(
        object? output,
        VerdictSource source,
        Dictionary<string, object?>? variables = null) =>
        RunAsync(MeasureStep(output), source, variables);

    private static async Task<TestItemRunResult> RunAsync(
        TestStepDefinition step,
        VerdictSource source,
        Dictionary<string, object?>? variables = null)
    {
        var registry = new PluginRegistry();
        registry.Register(new OutputStep("probe.measure", new Version(1, 0, 0)));
        var sequence = new TestSequence
        {
            Variables = new Dictionary<string, object?>(variables ?? [], StringComparer.OrdinalIgnoreCase),
            Items = [new TestItemDefinition { Name = "Cells", MainSteps = [step], VerdictSource = source }]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        return Assert.Single(result.ItemResults);
    }

    /// <summary>Returns its <c>output</c> parameter, unchanged, as the output <c>cells</c>.</summary>
    private sealed class OutputStep(string pluginId, Version version, bool throws = false) : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = pluginId,
            DisplayName = pluginId,
            Version = version
        };

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) =>
            parameters.TryGetValue("output", out var output) ? new Box(output) : new Box(null);

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            if (throws)
            {
                throw new InvalidOperationException("instrument not responding");
            }

            return Task.FromResult(new TestStepResult
            {
                Verdict = TestVerdict.Pass,
                Outputs = { ["cells"] = ((Box)settings).Value }
            });
        }

        private sealed record Box(object? Value);
    }
}
