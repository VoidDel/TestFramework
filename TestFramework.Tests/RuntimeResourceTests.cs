using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using Xunit;

namespace TestFramework.Tests;

public sealed class RuntimeResourceTests
{
    [Fact]
    public async Task RunAsync_ProvidesRuntimeServicesToStep()
    {
        var registry = new PluginRegistry();
        registry.Register(new ServiceUsingStepPlugin());

        var resources = new RuntimeResourceProvider();
        resources.RegisterService("ecu1-uds", new FakeUdsClient("VIN123"));

        var sequence = new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "read-vin",
                            Name = "Read VIN",
                            PluginId = "uds.read-vin",
                            PluginVersion = "1.0.0",
                            Parameters = { ["service"] = "ecu1-uds" }
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "read-vin", OutputKey = "vinValid" }
                }
            ]
        };

        var result = await new TestSequenceRunner(registry, resources: resources).RunAsync(sequence);
        var step = Assert.Single(Assert.Single(result.ItemResults).MainResults);

        Assert.Equal(TestVerdict.Pass, result.Verdict);
        Assert.Equal("VIN123", step.Outputs["vin"]);
    }

    private interface IFakeUdsClient
    {
        string ReadVin();
    }

    private sealed class FakeUdsClient : IFakeUdsClient
    {
        private readonly string _vin;

        public FakeUdsClient(string vin)
        {
            _vin = vin;
        }

        public string ReadVin() => _vin;
    }

    private sealed class ServiceUsingStepPlugin : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "uds.read-vin",
            DisplayName = "Read VIN",
            Version = new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(Dictionary<string, object?>);

        public object CreateDefaultSettings() => new Dictionary<string, object?>();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters)
        {
            return parameters;
        }

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
            var serviceId = Convert.ToString(parameters["service"])!;
            var uds = context.Services.GetRequired<IFakeUdsClient>(serviceId);
            var vin = uds.ReadVin();

            return Task.FromResult(new TestStepResult
            {
                Verdict = TestVerdict.Pass,
                Outputs =
                {
                    ["vin"] = vin,
                    ["vinValid"] = !string.IsNullOrWhiteSpace(vin)
                }
            });
        }
    }
}
