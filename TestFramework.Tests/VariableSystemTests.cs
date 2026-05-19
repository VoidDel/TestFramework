using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

public sealed class VariableSystemTests
{
    [Fact]
    public async Task RunAsync_ResolvesParametersAndWritesOutputsToVariables()
    {
        var registry = new PluginRegistry();
        registry.Register(new EchoPlugin());

        var sequence = new TestSequence
        {
            Variables =
            {
                ["targetVoltage"] = 12.5,
                ["dutSerial"] = "ABC123"
            },
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "echo",
                            Name = "Echo",
                            PluginId = "test.echo",
                            PluginVersion = "1.0.0",
                            Parameters =
                            {
                                ["value"] = "${targetVoltage}",
                                ["label"] = "DUT-${dutSerial}"
                            },
                            VariableWrites =
                            [
                                new VariableWriteDefinition { Name = "measuredVoltage", OutputKey = "value" },
                                new VariableWriteDefinition { Name = "finalLabel", OutputKey = "label" }
                            ]
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "echo" }
                }
            ]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        var step = Assert.Single(Assert.Single(result.ItemResults).MainResults);

        Assert.Equal(12.5, step.Outputs["value"]);
        Assert.Equal("DUT-ABC123", step.Outputs["label"]);
        Assert.Equal(12.5, step.WrittenVariables["measuredVoltage"]);
        Assert.Equal("DUT-ABC123", result.FinalVariables["finalLabel"]);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsVariableWrites()
    {
        var service = new TestSequenceYamlService();
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
                            Id = "step",
                            Name = "Step",
                            PluginId = "test.echo",
                            PluginVersion = "1.0.0",
                            VariableWrites =
                            [
                                new VariableWriteDefinition
                                {
                                    Name = "result",
                                    OutputKey = "value"
                                }
                            ]
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "step" }
                }
            ]
        };

        var loaded = service.Load(service.Save(sequence));
        var write = Assert.Single(Assert.Single(loaded.Items).MainSteps[0].VariableWrites);

        Assert.Equal("result", write.Name);
        Assert.Equal("value", write.OutputKey);
    }

    [Fact]
    public void Validate_ReportsInvalidVariableWrite()
    {
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
                            Id = "step",
                            Name = "Step",
                            PluginId = "test.echo",
                            PluginVersion = "1.0.0",
                            VariableWrites = [new VariableWriteDefinition()]
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "step" }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "items[0].main[0].variableWrites[0].name");
        Assert.Contains(issues, issue => issue.Path == "items[0].main[0].variableWrites[0]");
    }

    private sealed class EchoPlugin : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "test.echo",
            DisplayName = "Echo",
            Version = new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(IReadOnlyDictionary<string, object?>);

        public object CreateDefaultSettings() => new Dictionary<string, object?>();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => parameters;

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings)
        {
            return (IReadOnlyDictionary<string, object?>)settings;
        }

        public Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            var parameters = (IReadOnlyDictionary<string, object?>)settings;
            return Task.FromResult(new TestStepResult
            {
                Verdict = TestVerdict.Pass,
                Outputs =
                {
                    ["value"] = parameters["value"],
                    ["label"] = parameters["label"]
                }
            });
        }
    }
}
