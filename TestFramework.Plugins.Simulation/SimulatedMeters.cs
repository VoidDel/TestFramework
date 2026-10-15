using System.Globalization;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;
using TestFramework.Instruments;

namespace TestFramework.Plugins.Simulation;

public sealed class SimulatedMultimeterDriver : IInstrumentDriverPlugin
{
    public ResourcePluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "sim.dmm",
        DisplayName = "模拟万用表",
        Version = new Version(1, 0, 0),
        Category = "模拟仪器",
        Description = "各测量功能的读数取自同名设置（DcVoltage: 3.3、Resistance: 1000 …）；设置 Follows 为一台电源的别名时，直流电压与电流读那台电源的输出。"
    };

    public Type InstrumentType => typeof(SimulatedMultimeter);

    /// <summary>
    /// <c>Follows</c> is looked up through the scope, as a transport looks up its instrument: the
    /// supply must be bound before the meter, and it may be any driver's - the meter asks for the
    /// standard <see cref="IPowerSupply"/>, not for the simulated one.
    /// </summary>
    public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken)
    {
        var settings = new SimulationSettings(definition.Id, definition.Settings);
        IPowerSupply? follows = null;
        if (settings.Text("Follows") is { Length: > 0 } alias)
        {
            follows = resources.Instruments.TryGet<IPowerSupply>(alias, out var supply)
                ? supply
                : throw new InvalidOperationException(
                    $"Simulated multimeter '{definition.Id}' follows '{alias}', which is not a power supply opened before it.");
        }

        return Task.FromResult<object>(new SimulatedMultimeter(settings, follows));
    }
}

public sealed class SimulatedMultimeter : IDigitalMultimeter
{
    private readonly string _identity;
    private readonly Dictionary<MeasurementFunction, double> _readings = [];
    private readonly IPowerSupply? _follows;

    internal SimulatedMultimeter(SimulationSettings settings, IPowerSupply? follows)
    {
        _identity = settings.Identity("SimDMM");
        _follows = follows;
        foreach (var function in Enum.GetValues<MeasurementFunction>())
        {
            _readings[function] = settings.Number(function.ToString(), function == MeasurementFunction.Temperature ? 25.0 : 0.0);
        }
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_identity);

    public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<double> MeasureAsync(MeasurementFunction function, double? range = null, CancellationToken cancellationToken = default)
    {
        var reading = (function, _follows) switch
        {
            (MeasurementFunction.DcVoltage, { } supply) => await supply.MeasureVoltageAsync(cancellationToken).ConfigureAwait(false),
            (MeasurementFunction.DcCurrent, { } supply) => await supply.MeasureCurrentAsync(cancellationToken).ConfigureAwait(false),
            _ => _readings.TryGetValue(function, out var value)
                ? value
                : throw new ArgumentOutOfRangeException(nameof(function), function, "Unknown measurement function.")
        };

        // A real meter on a fixed range reports an overload rather than a number it cannot resolve.
        if (range is { } fullScale && Math.Abs(reading) > fullScale * 1.2)
        {
            throw new InvalidOperationException(
                $"Overload: {reading.ToString("G6", CultureInfo.InvariantCulture)} {MeasurementFunctions.UnitOf(function)} is beyond the {fullScale.ToString("G6", CultureInfo.InvariantCulture)} range.");
        }

        return reading;
    }
}

public sealed class SimulatedDataAcquisitionDriver : IInstrumentDriverPlugin
{
    public ResourcePluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "sim.daq",
        DisplayName = "模拟数据采集",
        Version = new Version(1, 0, 0),
        Category = "模拟仪器",
        Description = "每个通道读 Value（默认 3.3），按请求顺序每通道再加 Step；Channels 块可逐通道指定读数（\"105\": 3.25）。"
    };

    public Type InstrumentType => typeof(SimulatedDataAcquisition);

    public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new SimulatedDataAcquisition(new SimulationSettings(definition.Id, definition.Settings)));
}

public sealed class SimulatedDataAcquisition : IDataAcquisition
{
    private readonly string _identity;
    private readonly double _value;
    private readonly double _step;
    private readonly Dictionary<string, double> _channels = new(StringComparer.OrdinalIgnoreCase);

    internal SimulatedDataAcquisition(SimulationSettings settings)
    {
        _identity = settings.Identity("SimDAQ");
        _value = settings.Number("Value", 3.3);
        _step = settings.Number("Step", 0.0);
        var channels = settings.Map("Channels");
        var reader = new SimulationSettings(settings.Alias, channels);
        foreach (var channel in channels.Keys)
        {
            _channels[channel] = reader.Number(channel, _value);
        }
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_identity);

    public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, double>> ScanAsync(
        IReadOnlyList<string> channels,
        MeasurementFunction function,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var readings = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var index = 0; index < channels.Count; index++)
        {
            readings[channels[index]] = _channels.TryGetValue(channels[index], out var fixedValue) ? fixedValue : _value + index * _step;
        }

        return Task.FromResult<IReadOnlyDictionary<string, double>>(readings);
    }
}
