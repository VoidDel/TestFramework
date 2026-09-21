using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Core.Variables;
using TestFramework.SequenceYaml;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// A dictionary inside a variable or a parameter value is the plugin's data, not a framework
/// namespace: <c>SOC</c> and <c>soc</c> are two signals there, not one written twice.
///
/// Four places copy these values - result snapshots, variable resolution, YAML loading and step
/// duplication - and each used to rebuild them case-insensitively. Depending on which was reached
/// that either threw out of the runner and lost the whole run result, or silently merged the two
/// entries. All four are covered here, because the rule is only worth anything if none of them
/// drifts back.
/// </summary>
public sealed class NestedDictionaryTests
{
    [Fact]
    public async Task RunAsync_PluginOutputWithCaseDistinctKeys_KeepsBothAndReturnsTheResult()
    {
        // Previously: ArgumentException out of RunAsync, raised from RunItemAsync's finally, with
        // no TestSequenceRunResult at all - the DUT's record lost over a legitimate signal map.
        var registry = new PluginRegistry();
        registry.Register(new SignalMapPlugin());

        var sequence = new TestSequence
        {
            Name = "Sequence",
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "read", Name = "Read", PluginId = "demo.signals", PluginVersion = "1.0.0",
                            VariableWrites = [new VariableWriteDefinition { Name = "signals", OutputKey = "signals" }]
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "read" }
                }
            ]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);

        Assert.Equal(TestVerdict.Pass, result.Verdict);
        var signals = Assert.IsType<Dictionary<string, object?>>(result.FinalVariables["signals"]);
        Assert.Equal(80, signals["SOC"]);
        Assert.Equal(81, signals["soc"]);
    }

    [Fact]
    public void ResolveValue_NestedDictionary_KeepsCaseDistinctKeys()
    {
        var variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["v"] = 1 };
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["frame"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["SOC"] = "${v}", ["soc"] = 2 }
        };

        var resolved = VariableResolver.ResolveDictionary(parameters, variables);

        var frame = Assert.IsType<Dictionary<string, object?>>(resolved["frame"]);
        Assert.Equal(2, frame.Count);
        Assert.Equal(1, frame["SOC"]);
        Assert.Equal(2, frame["soc"]);
    }

    [Fact]
    public void Load_NestedMappingWithCaseDistinctKeys_KeepsBoth()
    {
        var sequence = new TestSequenceYamlService().Load(SequenceWith("""
          variables:
            frame:
              SOC: 80
              soc: 81
          """));

        var frame = Assert.IsType<Dictionary<string, object?>>(sequence.Variables["frame"]);
        Assert.Equal(80, frame["SOC"]);
        Assert.Equal(81, frame["soc"]);
    }

    [Fact]
    public void Load_TopLevelVariablesDifferingOnlyByCase_IsRefused()
    {
        // The variable table itself IS case-insensitive, so these two are genuinely ambiguous:
        // keeping one would let file order decide which of the operator's values survives.
        var error = Assert.Throws<InvalidDataException>(() => new TestSequenceYamlService().Load(SequenceWith("""
          variables:
            SOC: 80
            soc: 81
          """)));

        Assert.Contains("soc", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clone_NestedDictionary_KeepsCaseDistinctKeys()
    {
        var step = new TestStepDefinition
        {
            Id = "s", Name = "S", PluginId = "p", PluginVersion = "1.0.0",
            Parameters =
            {
                ["frame"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["SOC"] = 80, ["soc"] = 81 }
            }
        };

        var frame = Assert.IsType<Dictionary<string, object?>>(step.Clone().Parameters["frame"]);

        Assert.Equal(2, frame.Count);
        Assert.Equal(80, frame["SOC"]);
        Assert.Equal(81, frame["soc"]);
    }

    private static string SequenceWith(string variablesBlock) => $"""
        schemaVersion: 1
        id: seq
        name: Sequence
        {variablesBlock}
        items:
          - id: item
            name: Item
            main:
              - id: main
                name: Main
                pluginId: demo.signals
                pluginVersion: 1.0.0
            verdictSource:
              stepId: main
        """;

    private sealed class SignalMapPlugin : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.signals",
            DisplayName = "Signals",
            Version = new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(Dictionary<string, object?>);
        public object CreateDefaultSettings() => new Dictionary<string, object?>();
        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => parameters;
        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) =>
            (IReadOnlyDictionary<string, object?>)settings;

        public Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TestStepResult
            {
                Verdict = TestVerdict.Pass,
                // A CAN signal map, where case is what tells two signals apart.
                Outputs = { ["signals"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["SOC"] = 80, ["soc"] = 81 } }
            });
    }
}
