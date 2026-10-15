using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// The unit table a numeric judgment converts with: what it knows, how it matches a symbol, what
/// a host may add, and what the validator says about a unit before a run.
/// </summary>
public sealed class MeasurementUnitsTests
{
    [Theory]
    [InlineData(25.0, "°C", "C", 25.0)]
    [InlineData(25.0, "℃", "degC", 25.0)]
    [InlineData(25.0, "C", "K", 298.15)]
    [InlineData(298.15, "K", "°C", 25.0)]
    [InlineData(1500.0, "mAh", "Ah", 1.5)]
    [InlineData(2.5, "kWh", "Wh", 2500.0)]
    [InlineData(3600.0, "J", "Wh", 1.0)]
    [InlineData(1.5, "h", "min", 90.0)]
    [InlineData(250.0, "mOhm", "Ohm", 0.25)]
    [InlineData(2.0, "MOhm", "kOhm", 2000.0)]
    [InlineData(2.0, "MΩ", "kOhm", 2000.0)]
    [InlineData(10.0, "ohm", "mOhm", 10000.0)]
    [InlineData(500.0, "ppm", "%", 0.05)]
    [InlineData(4.7, "µF", "nF", 4700.0)]
    [InlineData(3.3, "V", "", 3.3)]
    public void TryConvert_ConvertsWithinADimension(double value, string from, string to, double expected)
    {
        Assert.True(MeasurementUnits.TryConvert(value, from, to, out var converted));
        Assert.Equal(expected, converted, precision: 9);
    }

    [Theory]
    [InlineData("Ah", "V")]
    [InlineData("mV", "ms")]
    [InlineData("furlong", "m")]
    [InlineData("", "V")]
    public void TryConvert_RefusesAcrossDimensionsAndUnknownUnits(string from, string to)
    {
        Assert.False(MeasurementUnits.TryConvert(1.0, from, to, out _));
    }

    [Fact]
    public void Symbols_MatchExactlyFirst_AndCaseInsensitivelyOnlyWhenUnambiguous()
    {
        // mOhm and MOhm are both real and a thousand million apart: a reading of "mohm" must not
        // pick one. "ohm" can only be Ohm, so it is.
        Assert.True(MeasurementUnits.IsKnown("mOhm"));
        Assert.True(MeasurementUnits.IsKnown("MOhm"));
        Assert.False(MeasurementUnits.IsKnown("mohm"));
        Assert.False(MeasurementUnits.IsKnown("MOHM"));
        Assert.True(MeasurementUnits.IsKnown("ohm"));
        Assert.True(MeasurementUnits.IsKnown(null));

        Assert.Equal("Ohm", MeasurementUnits.Normalize("ohm"));
        Assert.Equal("uV", MeasurementUnits.Normalize("µV"));
        Assert.Equal("°C", MeasurementUnits.Normalize("℃"));
        Assert.Equal("furlong", MeasurementUnits.Normalize(" furlong "));
        Assert.Equal("resistance", MeasurementUnits.DimensionOf("kΩ"));
        Assert.Null(MeasurementUnits.DimensionOf("mohm"));
    }

    [Fact]
    public void Register_AddsAUnitEverySequenceMayThenUse()
    {
        MeasurementUnits.Register("Nm", "torque", 1.0);
        MeasurementUnits.Register("mNm", "torque", 0.001);

        Assert.True(MeasurementUnits.TryConvert(1500.0, "mNm", "Nm", out var converted));
        Assert.Equal(1.5, converted, precision: 9);
        Assert.Contains("mNm", MeasurementUnits.Known);
    }

    [Fact]
    public void Register_RefusesToChangeWhatAUnitMeans()
    {
        // Registering what is already there is a no-op, so two plugins may each register Ah.
        MeasurementUnits.Register("mV", "voltage", 0.001);

        var scale = Assert.Throws<InvalidOperationException>(() => MeasurementUnits.Register("mV", "voltage", 1.0));
        Assert.Contains("already defined", scale.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => MeasurementUnits.Register("V", "current", 1.0));

        Assert.Throws<ArgumentException>(() => MeasurementUnits.Register(" ", "voltage", 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementUnits.Register("bad", "voltage", 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementUnits.Register("bad", "voltage", 1.0, double.NaN));
    }

    [Fact]
    public void Validator_WarnsAboutAUnitNobodyKnows()
    {
        var sequence = SequenceJudging(sourceUnit: null, unit: "furlong");

        var issue = Assert.Single(new TestSequenceValidator().Validate(sequence));

        Assert.Equal("items[0].verdictSource.unit", issue.Path);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Contains("furlong", issue.Message, StringComparison.Ordinal);
        Assert.Contains("MeasurementUnits.Register", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_RejectsUnitsOfDifferentDimensions()
    {
        var sequence = SequenceJudging(sourceUnit: "mV", unit: "ms");

        var issue = Assert.Single(new TestSequenceValidator().Validate(sequence));

        Assert.Equal("items[0].verdictSource.unit", issue.Path);
        Assert.True(issue.IsError);
        Assert.Contains("voltage", issue.Message, StringComparison.Ordinal);
        Assert.Contains("time", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_AcceptsKnownUnitsOfOneDimension()
    {
        Assert.Empty(new TestSequenceValidator().Validate(SequenceJudging(sourceUnit: "mAh", unit: "Ah")));
        Assert.Empty(new TestSequenceValidator().Validate(SequenceJudging(sourceUnit: null, unit: null)));
    }

    [Theory]
    [InlineData("25.3 °C", "C", 20.0, 30.0, 25.3, "C")]
    [InlineData("1500 mAh", "Ah", 1.0, 2.0, 1.5, "Ah")]
    [InlineData("250 mΩ", "mOhm", 100.0, 300.0, 250.0, "mOhm")]
    public async Task Judgment_ConvertsAnInstrumentsUnitIntoTheLimits(
        string reading, string unit, double lower, double upper, double expectedValue, string expectedUnit)
    {
        var registry = new PluginRegistry();
        registry.Register(new OutputPlugin(reading));
        var sequence = SequenceJudging(sourceUnit: null, unit: unit, lower, upper);

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        var item = Assert.Single(result.ItemResults);

        Assert.Equal(TestVerdict.Pass, item.Verdict);
        var measurement = Assert.Single(item.Measurements);
        Assert.Equal(expectedValue, measurement.Value!.Value, precision: 9);
        Assert.Equal(expectedUnit, measurement.Unit);
    }

    private static TestSequence SequenceJudging(string? sourceUnit, string? unit, double lower = 0.0, double upper = 10.0)
    {
        var step = new TestStepDefinition { Id = "read", Name = "Read", PluginId = "probe.output", PluginVersion = "1.0.0" };
        return new TestSequence
        {
            Name = "Sequence",
            Items =
            [
                new TestItemDefinition
                {
                    Id = "item",
                    Name = "Item",
                    MainSteps = [step],
                    VerdictSource = new VerdictSource
                    {
                        StepId = step.Id,
                        OutputKey = "value",
                        JudgeType = VerdictJudgeType.Numeric,
                        LowerLimit = lower,
                        UpperLimit = upper,
                        SourceUnit = sourceUnit,
                        Unit = unit
                    }
                }
            ]
        };
    }

    private sealed class OutputPlugin(object? value) : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "probe.output",
            DisplayName = "probe.output",
            Version = new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken) =>
            Task.FromResult(new TestStepResult { Verdict = TestVerdict.Pass, Outputs = { ["value"] = value } });
    }
}
