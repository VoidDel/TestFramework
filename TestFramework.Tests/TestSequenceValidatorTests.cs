using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Plugins;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

public sealed class TestSequenceValidatorTests
{
    [Fact]
    public void Validate_ReportsMissingPluginVersion()
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(1, 0, 0)));

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
                            Id = "main",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "2.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var issue = Assert.Single(new TestSequenceValidator(registry).Validate(sequence));

        Assert.Equal("items[0].main[0].pluginId", issue.Path);
        Assert.Contains("2.0.0", issue.Message);
    }

    [Fact]
    public void Validate_ReportsBrokenServiceTransportReference()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Services =
            [
                new TestServiceDefinition
                {
                    Id = "ecu1-uds",
                    ServiceId = "uds.client",
                    Transport = "missing-transport"
                }
            ],
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "main",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "services[0].transport");
    }

    [Fact]
    public void Validate_ReportsDuplicatedStepIdAcrossSections()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    InitSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "shared",
                            Name = "Init",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "shared",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "shared" }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue =>
            issue.Path == "items[0].main[0].id" &&
            issue.Message.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AllowsStandaloneTransportAndService()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Transports =
            [
                new TransportDefinition
                {
                    Id = "psu1-tcp",
                    TransportId = "tcp.client"
                }
            ],
            Services =
            [
                new TestServiceDefinition
                {
                    Id = "crc16",
                    ServiceId = "crc16.calculator"
                }
            ],
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "main",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.DoesNotContain(issues, issue => issue.Path is "transports[0].channel" or "services[0].transport");
    }

    [Fact]
    public void Validate_ReportsBrokenTransportChannelReferenceWhenChannelIsSpecified()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Transports =
            [
                new TransportDefinition
                {
                    Id = "isotp",
                    TransportId = "isotp",
                    Channel = "missing-can"
                }
            ],
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "main",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "transports[0].channel");
    }

    private sealed class StubPlugin : ITestStepPlugin
    {
        public StubPlugin(string pluginId, Version version)
        {
            Descriptor = new TestStepPluginDescriptor
            {
                PluginId = pluginId,
                DisplayName = pluginId,
                Version = version
            };
        }

        public TestStepPluginDescriptor Descriptor { get; }

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TestStepResult { Verdict = TestVerdict.Pass });
        }
    }
}
