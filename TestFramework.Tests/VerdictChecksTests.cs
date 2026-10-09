using System.Text.RegularExpressions;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// TestStand-style numeric comparisons, and an item judged on several outputs at once.
/// </summary>
public sealed class VerdictChecksTests
{
    [Theory]
    [InlineData(NumericComparison.GELE, 3.0, 3.0, 3.6, null, TestVerdict.Pass)]
    [InlineData(NumericComparison.GELE, 3.7, 3.0, 3.6, null, TestVerdict.Fail)]
    [InlineData(NumericComparison.GTLT, 3.0, 3.0, 3.6, null, TestVerdict.Fail)]
    [InlineData(NumericComparison.GTLT, 3.3, 3.0, 3.6, null, TestVerdict.Pass)]
    [InlineData(NumericComparison.GTLT, 3.6, 3.0, 3.6, null, TestVerdict.Fail)]
    [InlineData(NumericComparison.GELT, 3.0, 3.0, 3.6, null, TestVerdict.Pass)]
    [InlineData(NumericComparison.GELT, 3.6, 3.0, 3.6, null, TestVerdict.Fail)]
    [InlineData(NumericComparison.GTLE, 3.0, 3.0, 3.6, null, TestVerdict.Fail)]
    [InlineData(NumericComparison.GTLE, 3.6, 3.0, 3.6, null, TestVerdict.Pass)]
    [InlineData(NumericComparison.GT, 3.0, 3.0, null, null, TestVerdict.Fail)]
    [InlineData(NumericComparison.GE, 3.0, 3.0, null, null, TestVerdict.Pass)]
    [InlineData(NumericComparison.LT, 3.6, null, 3.6, null, TestVerdict.Fail)]
    [InlineData(NumericComparison.LE, 3.6, null, 3.6, null, TestVerdict.Pass)]
    [InlineData(NumericComparison.EQ, 80.0, null, null, 80.0, TestVerdict.Pass)]
    [InlineData(NumericComparison.EQ, 79.0, null, null, 80.0, TestVerdict.Fail)]
    [InlineData(NumericComparison.NE, 0.0, null, null, 0.0, TestVerdict.Fail)]
    [InlineData(NumericComparison.NE, 3.0, null, null, 0.0, TestVerdict.Pass)]
    public async Task Comparison_JudgesByItsTestStandMeaning(
        NumericComparison comparison,
        double value,
        double? lower,
        double? upper,
        double? expected,
        TestVerdict verdict)
    {
        var source = Numeric("value", comparison, lower, upper);
        source.Expected = expected;

        var item = await RunAsync([Step("read", ("value", value))], source);

        Assert.Equal(verdict, item.Verdict);
        var record = Assert.Single(item.Measurements);
        Assert.Equal(comparison, record.Comparison);
        Assert.Equal(expected, record.Expected);
    }

    [Theory]
    [InlineData(NumericComparison.GTLT, 3.0, null, "requires upperLimit")]
    [InlineData(NumericComparison.GELT, null, 3.6, "requires lowerLimit")]
    [InlineData(NumericComparison.GE, null, 3.6, "requires lowerLimit")]
    [InlineData(NumericComparison.LE, 3.0, null, "requires upperLimit")]
    [InlineData(NumericComparison.EQ, null, null, "requires expected")]
    public async Task Comparison_MissingABoundItReads_IsAnErrorRatherThanAPass(
        NumericComparison comparison,
        double? lower,
        double? upper,
        string message)
    {
        // An open interval with one bound would otherwise compare against nothing on the other side.
        var item = await RunAsync([Step("read", ("value", 3.3))], Numeric("value", comparison, lower, upper));

        Assert.Equal(TestVerdict.Error, item.Verdict);
        Assert.Contains(message, item.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comparison_IgnoresABoundItDoesNotRead()
    {
        // GT reads only the lower limit; a leftover upper limit must neither judge nor be recorded.
        var item = await RunAsync([Step("read", ("value", 9.0))], Numeric("value", NumericComparison.GT, 3.0, 3.6));

        Assert.Equal(TestVerdict.Pass, item.Verdict);
        Assert.Null(Assert.Single(item.Measurements).UpperLimit);
    }

    [Fact]
    public async Task Expected_ResolvesFromAVariable()
    {
        var source = Numeric("count", NumericComparison.EQ, null, null);
        source.ExpectedReference = "${cellCount}";

        var item = await RunAsync(
            [Step("read", ("count", 80))],
            source,
            new Dictionary<string, object?> { ["cellCount"] = 80 });

        Assert.Equal(TestVerdict.Pass, item.Verdict);
        Assert.Equal(80.0, Assert.Single(item.Measurements).Expected);
    }

    [Fact]
    public async Task Checks_JudgeOtherOutputsAgainstTheirOwnLimits()
    {
        var voltage = Numeric("voltage", NumericComparison.GELE, 3.0, 3.6);
        voltage.Name = "电压";
        var current = Numeric("current", NumericComparison.LE, null, 2.0);
        current.Name = "充电电流";
        current.Unit = "A";
        current.SourceUnit = "mA";

        var item = await RunAsync([Step("read", ("voltage", 3.3), ("current", 2500))], voltage, checks: [current]);

        Assert.Equal(TestVerdict.Fail, item.Verdict);
        Assert.Collection(
            item.Measurements,
            record =>
            {
                Assert.Equal("电压", record.Check);
                Assert.Equal(TestVerdict.Pass, record.Verdict);
            },
            record =>
            {
                Assert.Equal("充电电流", record.Check);
                Assert.Equal("充电电流", record.Name);
                Assert.Equal(2.5, record.Value);
                Assert.Equal("A", record.Unit);
                Assert.Equal(NumericComparison.LE, record.Comparison);
                Assert.Equal(TestVerdict.Fail, record.Verdict);
            });
    }

    [Fact]
    public async Task Checks_AllPassing_PassTheItem()
    {
        var item = await RunAsync(
            [Step("read", ("voltage", 3.3)), Step("temp", ("cells", new[] { 25.0, 26.0 }))],
            Numeric("voltage", NumericComparison.GELE, 3.0, 3.6),
            checks: [Named(Numeric("cells", NumericComparison.LT, null, 45.0, stepId: "temp"), "电芯温度")]);

        Assert.Equal(TestVerdict.Pass, item.Verdict);
        Assert.Equal(["voltage", "电芯温度[0]", "电芯温度[1]"], item.Measurements.Select(record => record.Name));
    }

    [Fact]
    public async Task Check_ThatCannotJudge_LeavesTheItemInconclusiveEvenWhenTheSourcePasses()
    {
        // Each judgment combines only its own records: the source's passing values must not
        // outvote a check whose value could not be read.
        var item = await RunAsync(
            [Step("read", ("voltage", 3.3), ("current", "n/a"))],
            Numeric("voltage", NumericComparison.GELE, 3.0, 3.6),
            checks: [Numeric("current", NumericComparison.LE, null, 2.0)]);

        Assert.Equal(TestVerdict.Inconclusive, item.Verdict);
    }

    [Fact]
    public async Task Check_WithoutItsOutput_IsAnErrorNamingTheCheck()
    {
        var item = await RunAsync(
            [Step("read", ("voltage", 3.3))],
            Numeric("voltage", NumericComparison.GELE, 3.0, 3.6),
            checks: [Named(Numeric("current", NumericComparison.LE, null, 2.0), "充电电流")]);

        Assert.Equal(TestVerdict.Error, item.Verdict);
        Assert.Contains("充电电流", item.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_OnAStepThatDidNotRun_IsAnError()
    {
        var item = await RunAsync(
            [Step("read", ("voltage", 3.3))],
            Numeric("voltage", NumericComparison.GELE, 3.0, 3.6),
            checks: [Numeric("current", NumericComparison.LE, null, 2.0, stepId: "missing")]);

        Assert.Equal(TestVerdict.Error, item.Verdict);
        Assert.Contains("'missing'", item.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ReplacesTheOwnVerdictOfTheStepItJudges_ButNotOfOthers()
    {
        // As for the verdict source: judging a step's output is what replaces that step's verdict.
        // A step nothing judges still fails the item on its own Fail.
        var judgedStep = Step("temp", ("cells", 30.0));
        judgedStep.Parameters["verdict"] = "Fail";
        var item = await RunAsync(
            [Step("read", ("voltage", 3.3)), judgedStep],
            Numeric("voltage", NumericComparison.GELE, 3.0, 3.6),
            checks: [Numeric("cells", NumericComparison.LT, null, 45.0, stepId: "temp")]);

        Assert.Equal(TestVerdict.Pass, item.Verdict);

        var unjudged = Step("other", ("x", 1.0));
        unjudged.Parameters["verdict"] = "Fail";
        var failing = await RunAsync(
            [Step("read", ("voltage", 3.3)), unjudged],
            Numeric("voltage", NumericComparison.GELE, 3.0, 3.6));

        Assert.Equal(TestVerdict.Fail, failing.Verdict);
    }

    [Fact]
    public void Yaml_ComparisonsAndChecks_RoundTrip()
    {
        var yaml = """
            schemaVersion: 1
            id: pack
            name: Pack
            variables:
              cellCount: 80
              currentMax: 2.0
            items:
            - id: pack
              name: Pack
              verdictSource:
                name: 电芯数
                stepId: read
                outputKey: count
                judgeType: Numeric
                comparison: EQ
                expected: ${cellCount}
              checks:
              - name: 充电电流
                stepId: read
                outputKey: current
                judgeType: Numeric
                comparison: LE
                upperLimit: ${currentMax}
                unit: A
              main:
              - id: read
                name: Read
                pluginId: probe.output
            """;
        var service = new TestSequenceYamlService();

        var saved = service.Save(service.Load(yaml));
        var item = service.Load(saved).Items[0];

        Assert.Equal("电芯数", item.VerdictSource.Name);
        Assert.Equal(NumericComparison.EQ, item.VerdictSource.Comparison);
        Assert.Equal("${cellCount}", item.VerdictSource.ExpectedReference);
        var check = Assert.Single(item.Checks);
        Assert.Equal("充电电流", check.Name);
        Assert.Equal(NumericComparison.LE, check.Comparison);
        Assert.Equal("${currentMax}", check.UpperLimitReference);
        Assert.Contains("comparison: EQ", saved, StringComparison.Ordinal);
        Assert.Contains("expected: ${cellCount}", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_ItemNotUsingTheNewKeys_SavesWithoutThem()
    {
        // A sequence under version control must not change on its first save after an upgrade.
        var sequence = new TestSequence
        {
            Name = "Plain",
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    VerdictSource = Numeric("value", NumericComparison.GELE, 3.0, 3.6),
                    MainSteps = [Step("read", ("value", 3.3))]
                }
            ]
        };

        var saved = new TestSequenceYamlService().Save(sequence);

        Assert.DoesNotMatch(new Regex(@"^\s*(checks|comparison|expected|name: null):", RegexOptions.Multiline), saved);
    }

    [Theory]
    [InlineData(NumericComparison.GTLT, 3.0, null, null, "items[0].verdictSource.upperLimit", "requires upperLimit")]
    [InlineData(NumericComparison.EQ, null, null, null, "items[0].verdictSource.expected", "requires expected")]
    public void Validator_ReportsABoundTheComparisonNeeds(
        NumericComparison comparison,
        double? lower,
        double? upper,
        double? expected,
        string path,
        string message)
    {
        var sequence = ValidationSequence(Numeric("value", comparison, lower, upper));
        sequence.Items[0].VerdictSource.Expected = expected;

        var issue = Assert.Single(new TestSequenceValidator().Validate(sequence), issue => issue.Path == path);

        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Contains(message, issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_WarnsAboutABoundTheComparisonIgnores()
    {
        var sequence = ValidationSequence(Numeric("value", NumericComparison.GT, 3.0, 3.6));

        var issue = Assert.Single(new TestSequenceValidator().Validate(sequence));

        Assert.Equal("items[0].verdictSource.upperLimit", issue.Path);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Validator_WarnsThatEqualityOnAFractionIsExact()
    {
        var source = Numeric("value", NumericComparison.EQ, null, null);
        source.Expected = 3.3;

        var issue = Assert.Single(new TestSequenceValidator().Validate(ValidationSequence(source)));

        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Contains("GELE", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_ReportsAnUnknownComparison()
    {
        var sequence = ValidationSequence(Numeric("value", (NumericComparison)99, 3.0, 3.6));

        Assert.Contains(new TestSequenceValidator().Validate(sequence), issue =>
            issue.Path == "items[0].verdictSource.comparison" && issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Validator_HoldsChecksToTheVerdictSourceRules()
    {
        var sequence = ValidationSequence(Numeric("value", NumericComparison.GELE, 3.0, 3.6));
        sequence.Items[0].Checks.Add(Numeric("current", NumericComparison.LE, null, null, stepId: "elsewhere"));
        var unbounded = Numeric("current", NumericComparison.LE, null, null);
        unbounded.UpperLimitReference = "${missing}";
        sequence.Items[0].Checks.Add(unbounded);
        sequence.Items[0].Checks.Add(new VerdictSource { StepId = "read" });

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "items[0].checks[0].stepId");
        Assert.Contains(issues, issue => issue.Path == "items[0].checks[0].upperLimit" && issue.Message.Contains("requires upperLimit"));
        Assert.Contains(issues, issue => issue.Path == "items[0].checks[1].upperLimit" && issue.Message.Contains("not defined"));
        Assert.Contains(issues, issue => issue.Path == "items[0].checks[2].outputKey");
    }

    [Fact]
    public void Validator_WarnsWhenTwoJudgmentsShareARecordName()
    {
        var sequence = ValidationSequence(Numeric("value", NumericComparison.GELE, 3.0, 3.6));
        sequence.Items[0].Checks.Add(Numeric("value", NumericComparison.LE, null, 9.0));

        var issue = Assert.Single(new TestSequenceValidator().Validate(sequence));

        Assert.Equal("items[0].checks[0].name", issue.Path);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    private static VerdictSource Numeric(
        string outputKey,
        NumericComparison comparison,
        double? lower,
        double? upper,
        string stepId = "read") => new()
    {
        StepId = stepId,
        OutputKey = outputKey,
        JudgeType = VerdictJudgeType.Numeric,
        Comparison = comparison,
        LowerLimit = lower,
        UpperLimit = upper
    };

    private static VerdictSource Named(VerdictSource source, string name)
    {
        source.Name = name;
        return source;
    }

    /// <summary>A step whose parameters come back as its outputs; <c>verdict</c> sets its own verdict.</summary>
    private static TestStepDefinition Step(string id, params (string Key, object? Value)[] outputs)
    {
        var step = new TestStepDefinition { Id = id, Name = id, PluginId = "probe.output" };
        foreach (var (key, value) in outputs)
        {
            step.Parameters[key] = value;
        }

        return step;
    }

    private static TestSequence ValidationSequence(VerdictSource source) => new()
    {
        Name = "Sequence",
        Items = [new TestItemDefinition { Id = "item", Name = "Item", VerdictSource = source, MainSteps = [Step("read")] }]
    };

    private static async Task<TestItemRunResult> RunAsync(
        List<TestStepDefinition> steps,
        VerdictSource source,
        Dictionary<string, object?>? variables = null,
        List<VerdictSource>? checks = null)
    {
        var registry = new PluginRegistry();
        registry.Register(new OutputPlugin());
        var sequence = new TestSequence
        {
            Variables = new Dictionary<string, object?>(variables ?? [], StringComparer.OrdinalIgnoreCase),
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = steps,
                    VerdictSource = source,
                    Checks = checks ?? []
                }
            ]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        return Assert.Single(result.ItemResults);
    }

    private sealed class OutputPlugin : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "probe.output",
            DisplayName = "probe.output",
            Version = new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new Dictionary<string, object?>();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) =>
            new Dictionary<string, object?>(parameters, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => (Dictionary<string, object?>)settings;

        public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            var values = (Dictionary<string, object?>)settings;
            var result = new TestStepResult
            {
                Verdict = values.TryGetValue("verdict", out var verdict)
                    ? Enum.Parse<TestVerdict>((string)verdict!)
                    : TestVerdict.Pass
            };
            foreach (var (key, value) in values.Where(pair => pair.Key != "verdict"))
            {
                result.Outputs[key] = value;
            }

            return Task.FromResult(result);
        }
    }
}
