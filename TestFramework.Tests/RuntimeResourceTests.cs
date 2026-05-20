using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
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

    [Fact]
    public async Task BuildAsync_DisposesCreatedResourcesWhenLaterResourceFails()
    {
        var disposable = new DisposableResource();
        var registry = new ResourcePluginRegistry();
        registry.RegisterInstrumentDriver(new DisposableInstrumentPlugin(disposable));

        var sequence = new TestSequence
        {
            Instruments =
            [
                new InstrumentDefinition
                {
                    Id = "psu",
                    DriverId = "fake.instrument",
                    DriverVersion = "1.0.0"
                }
            ],
            Transports =
            [
                new TransportDefinition
                {
                    Id = "can",
                    TransportId = "missing.transport",
                    TransportVersion = "1.0.0"
                }
            ]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RuntimeResourceBuilder(registry).BuildAsync(sequence));

        Assert.Equal(1, disposable.DisposeAsyncCount);
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

    private sealed class DisposableResource : IAsyncDisposable
    {
        public int DisposeAsyncCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableInstrumentPlugin : IInstrumentDriverPlugin
    {
        private readonly DisposableResource _resource;

        public DisposableInstrumentPlugin(DisposableResource resource)
        {
            _resource = resource;
        }

        public ResourcePluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "fake.instrument",
            DisplayName = "Fake Instrument",
            Version = new Version(1, 0, 0)
        };

        public Type InstrumentType => typeof(DisposableResource);

        public Task<object> CreateAsync(
            InstrumentDefinition definition,
            RuntimeResourceProvider resources,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<object>(_resource);
        }
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
