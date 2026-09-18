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
    public async Task BuildAsync_PluginReadsAnEarlierResourceThroughTheReadOnlyScope()
    {
        // A transport routinely needs the instrument it sits on. The scope must let it read that,
        // while giving it no way to register into, or dispose, the host's container.
        var registry = new ResourcePluginRegistry();
        registry.RegisterInstrumentDriver(new NamedInstrumentPlugin());
        registry.RegisterTransport(new InstrumentBackedTransportPlugin());

        var sequence = new TestSequence
        {
            Instruments = [new InstrumentDefinition { Id = "dmm", DriverId = "demo.instrument", DriverVersion = "1.0.0" }],
            Transports = [new TransportDefinition { Id = "bus", TransportId = "demo.transport", TransportVersion = "1.0.0", Channel = "dmm" }]
        };

        await using var resources = await new RuntimeResourceBuilder(registry).BuildAsync(sequence);

        Assert.True(((ITransportProvider)resources).TryGet<string>("bus", out var transport));
        Assert.Equal("transport-over:dmm-instrument", transport);
    }

    [Fact]
    public async Task BuildAsync_ScopeHandedToPluginsCannotDisposeTheContainer()
    {
        // The contract is read-only, but the container behind it is disposable. A plugin must not
        // be able to reach that through a cast, or one plugin could release every other's resource.
        var registry = new ResourcePluginRegistry();
        registry.RegisterInstrumentDriver(new NamedInstrumentPlugin());
        registry.RegisterTransport(new ScopeCapturingTransportPlugin());

        var sequence = new TestSequence
        {
            Instruments = [new InstrumentDefinition { Id = "dmm", DriverId = "demo.instrument", DriverVersion = "1.0.0" }],
            Transports = [new TransportDefinition { Id = "bus", TransportId = "demo.capture", TransportVersion = "1.0.0" }]
        };

        await using var resources = await new RuntimeResourceBuilder(registry).BuildAsync(sequence);

        var scope = Assert.IsType<IResourceScope>(ScopeCapturingTransportPlugin.LastScope, exactMatch: false);
        Assert.NotSame(resources, scope);
        Assert.IsNotAssignableFrom<IDisposable>(scope);
        Assert.IsNotAssignableFrom<IAsyncDisposable>(scope);
        Assert.IsNotAssignableFrom<IDisposable>(scope.Instruments);
        Assert.Equal("dmm-instrument", scope.Instruments.GetRequired<string>("dmm"));
    }

    [Fact]
    public async Task RunAsync_ProvidersHandedToStepsCannotDisposeTheContainer()
    {
        var registry = new PluginRegistry();
        var plugin = new ProviderCapturingStepPlugin();
        registry.Register(plugin);
        var resources = new RuntimeResourceProvider();
        resources.RegisterService("svc", "service");
        var sequence = new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [new TestStepDefinition { Id = "capture", Name = "Capture", PluginId = "test.capture", PluginVersion = "1.0.0" }],
                    VerdictSource = new VerdictSource { StepId = "capture" }
                }
            ]
        };

        await new TestSequenceRunner(registry, resources: resources).RunAsync(sequence);

        Assert.NotNull(plugin.Services);
        Assert.IsNotAssignableFrom<IDisposable>(plugin.Services);
        Assert.IsNotAssignableFrom<IAsyncDisposable>(plugin.Services);
        Assert.Equal("service", plugin.Services!.GetRequired<string>("svc"));
    }

    private sealed class ScopeCapturingTransportPlugin : ITransportPlugin
    {
        public static IResourceScope? LastScope { get; private set; }

        public ResourcePluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.capture",
            DisplayName = "demo.capture",
            Version = new Version(1, 0, 0)
        };

        public Type TransportType => typeof(string);

        public Task<object> CreateAsync(TransportDefinition definition, IResourceScope resources, CancellationToken cancellationToken)
        {
            LastScope = resources;
            return Task.FromResult<object>("captured");
        }
    }

    private sealed class ProviderCapturingStepPlugin : ITestStepPlugin
    {
        public ITestServiceProvider? Services { get; private set; }

        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "test.capture",
            DisplayName = "Capture",
            Version = new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            Services = context.Services;
            return Task.FromResult(new TestStepResult { Verdict = TestVerdict.Pass });
        }
    }

    private sealed class NamedInstrumentPlugin : IInstrumentDriverPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.instrument",
            DisplayName = "demo.instrument",
            Version = new Version(1, 0, 0)
        };

        public Type InstrumentType => typeof(string);

        public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken)
        {
            return Task.FromResult<object>("dmm-instrument");
        }
    }

    private sealed class InstrumentBackedTransportPlugin : ITransportPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.transport",
            DisplayName = "demo.transport",
            Version = new Version(1, 0, 0)
        };

        public Type TransportType => typeof(string);

        public Task<object> CreateAsync(TransportDefinition definition, IResourceScope resources, CancellationToken cancellationToken)
        {
            var instrument = resources.Instruments.GetRequired<string>(definition.Channel!);
            return Task.FromResult<object>($"transport-over:{instrument}");
        }
    }

    [Fact]
    public async Task BuildAsync_DisposesCreatedResourcesWhenLaterResourceFails()
    {
        var disposable = new DisposableResource();
        var registry = new ResourcePluginRegistry();
        registry.RegisterInstrumentDriver(new DisposableInstrumentPlugin(disposable));
        registry.RegisterTransport(new FailingTransportPlugin());

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
                    TransportId = "failing.transport",
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

    [Fact]
    public async Task DisposeAsync_AttemptsAllResourcesAndAggregatesErrors()
    {
        var provider = new RuntimeResourceProvider();
        var instrument = new DisposableResource();
        provider.RegisterInstrument("instrument", instrument);
        provider.RegisterService("first", new FailingDisposable());
        provider.RegisterService("second", new FailingDisposable());

        var error = await Assert.ThrowsAsync<AggregateException>(() => provider.DisposeAsync().AsTask());
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(1, instrument.DisposeAsyncCount);
        await provider.DisposeAsync();
        Assert.Equal(1, instrument.DisposeAsyncCount);
    }

    [Fact]
    public void Dispose_AttemptsRemainingResourcesAfterFailure()
    {
        var provider = new RuntimeResourceProvider();
        var instrument = new DisposableResource();
        provider.RegisterService("broken", new FailingDisposable());
        provider.RegisterInstrument("instrument", instrument);
        Assert.Throws<AggregateException>(provider.Dispose);
        Assert.Equal(1, instrument.DisposeAsyncCount);
    }

    [Fact]
    public async Task BuildAsync_MissingPluginIsDetectedBeforeOpeningAnyResource()
    {
        var resource = new DisposableResource();
        var registry = new ResourcePluginRegistry();
        var plugin = new DisposableInstrumentPlugin(resource);
        registry.RegisterInstrumentDriver(plugin);
        var sequence = new TestSequence
        {
            Instruments = [new InstrumentDefinition { Id = "meter", DriverId = "fake.instrument" }],
            Transports = [new TransportDefinition { Id = "transport", TransportId = "missing" }]
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RuntimeResourceBuilder(registry).BuildAsync(sequence));
        Assert.Equal(0, plugin.CreateCount);
    }

    private sealed class FailingDisposable : IDisposable, IAsyncDisposable
    {
        public void Dispose() => throw new InvalidOperationException("cleanup failed");
        public ValueTask DisposeAsync() => throw new InvalidOperationException("cleanup failed");
    }

    private sealed class FailingTransportPlugin : ITransportPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new() { PluginId = "failing.transport", DisplayName = "Fail", Version = new(1, 0, 0) };
        public Type TransportType => typeof(object);
        public Task<object> CreateAsync(TransportDefinition definition, IResourceScope resources, CancellationToken cancellationToken)
            => throw new InvalidOperationException("creation failed");
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
        public int CreateCount { get; private set; }
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
            IResourceScope resources,
            CancellationToken cancellationToken)
        {
            CreateCount++;
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
