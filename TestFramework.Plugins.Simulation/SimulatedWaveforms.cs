using System.Globalization;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;
using TestFramework.Instruments;

namespace TestFramework.Plugins.Simulation;

public sealed class SimulatedFunctionGeneratorDriver : IInstrumentDriverPlugin
{
    public ResourcePluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "sim.fgen",
        DisplayName = "模拟信号发生器",
        Version = new Version(1, 0, 0),
        Category = "模拟仪器",
        Description = "一路输出，按配置产生正弦、方波、三角、锯齿、脉冲或直流；模拟示波器可以把它接到某个通道上观察。"
    };

    public Type InstrumentType => typeof(SimulatedFunctionGenerator);

    public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new SimulatedFunctionGenerator(new SimulationSettings(definition.Id, definition.Settings)));
}

public sealed class SimulatedFunctionGenerator : IFunctionGenerator
{
    private readonly string _identity;
    private volatile WaveformSettings _settings = new(WaveformShape.Sine, 1000, 1);
    private volatile bool _output;

    internal SimulatedFunctionGenerator(SimulationSettings settings)
    {
        _identity = settings.Identity("SimFGEN");
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_identity);

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        _settings = new WaveformSettings(WaveformShape.Sine, 1000, 1);
        _output = false;
        return Task.CompletedTask;
    }

    public Task ConfigureAsync(WaveformSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(settings.FrequencyHz) || settings.FrequencyHz <= 0 && settings.Shape != WaveformShape.Dc)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.FrequencyHz, "The frequency must be positive.");
        }

        if (settings.DutyCyclePercent is <= 0 or >= 100)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.DutyCyclePercent, "The duty cycle must be between 0 and 100 %.");
        }

        _settings = settings;
        return Task.CompletedTask;
    }

    public Task SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _output = enabled;
        return Task.CompletedTask;
    }

    /// <summary>The output voltage at <paramref name="seconds"/> from an arbitrary origin; 0 V while the output is off.</summary>
    public double VoltageAt(double seconds)
    {
        if (!_output)
        {
            return 0;
        }

        var settings = _settings;
        var half = settings.AmplitudeVoltsPeakToPeak / 2;
        var phase = settings.Shape == WaveformShape.Dc ? 0 : seconds * settings.FrequencyHz - Math.Floor(seconds * settings.FrequencyHz);
        return settings.OffsetVolts + settings.Shape switch
        {
            WaveformShape.Sine => half * Math.Sin(2 * Math.PI * phase),
            WaveformShape.Square or WaveformShape.Pulse => phase < settings.DutyCyclePercent / 100 ? half : -half,
            WaveformShape.Triangle => phase < 0.5 ? -half + 4 * half * phase : 3 * half - 4 * half * phase,
            WaveformShape.RampUp => -half + 2 * half * phase,
            WaveformShape.RampDown => half - 2 * half * phase,
            _ => 0
        };
    }
}

public sealed class SimulatedOscilloscopeDriver : IInstrumentDriverPlugin
{
    public ResourcePluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "sim.scope",
        DisplayName = "模拟示波器",
        Version = new Version(1, 0, 0),
        Category = "模拟仪器",
        Description = "Inputs 块把通道接到一台模拟信号发生器（CH1: fgen）或一个直流电平（CH2: 3.3）；按配置的采样率、记录长度和边沿触发采集，测量由波形计算。"
    };

    public Type InstrumentType => typeof(SimulatedOscilloscope);

    public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken)
    {
        var settings = new SimulationSettings(definition.Id, definition.Settings);
        var inputs = new Dictionary<string, Func<double, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (channel, source) in settings.Map("Inputs"))
        {
            inputs[channel] = source switch
            {
                double or int or long => _ => Convert.ToDouble(source, CultureInfo.InvariantCulture),
                string alias when resources.Instruments.TryGet<SimulatedFunctionGenerator>(alias, out var generator) => generator.VoltageAt,
                _ => throw new InvalidOperationException(
                    $"Simulated oscilloscope '{definition.Id}': input {channel} must be a voltage or the alias of a simulated function generator opened before it, not '{source}'.")
            };
        }

        return Task.FromResult<object>(new SimulatedOscilloscope(settings, inputs));
    }
}

/// <summary>
/// A scope sampling the signals its inputs describe. Acquisition finds the first trigger crossing
/// after an arbitrary start, keeps a tenth of the record before it, clips each channel to its
/// range as a real front end would, and measures with <see cref="WaveformMath"/>.
/// </summary>
public sealed class SimulatedOscilloscope : IOscilloscope
{
    private const int MaxTriggerSearchSamples = 2_000_000;

    private readonly Lock _gate = new();
    private readonly string _identity;
    private readonly IReadOnlyDictionary<string, Func<double, double>> _inputs;
    private readonly Dictionary<string, ScopeChannelSettings> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WaveformRecord> _last = new(StringComparer.OrdinalIgnoreCase);
    private ScopeAcquisitionSettings _acquisition = new(1_000_000, 1000);
    private ScopeTriggerSettings? _trigger;

    internal SimulatedOscilloscope(SimulationSettings settings, IReadOnlyDictionary<string, Func<double, double>> inputs)
    {
        _identity = settings.Identity("SimScope");
        _inputs = inputs;
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_identity);

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _channels.Clear();
            _last.Clear();
            _acquisition = new ScopeAcquisitionSettings(1_000_000, 1000);
            _trigger = null;
        }

        return Task.CompletedTask;
    }

    public Task ConfigureChannelAsync(string channel, ScopeChannelSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(settings.RangeVolts) || settings.RangeVolts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.RangeVolts, "The range must be positive.");
        }

        lock (_gate)
        {
            _channels[channel] = settings;
        }

        return Task.CompletedTask;
    }

    public Task ConfigureAcquisitionAsync(ScopeAcquisitionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(settings.SampleRateHz) || settings.SampleRateHz <= 0 || settings.RecordLength is < 2 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The sample rate must be positive and the record 2 to 10 000 000 samples long.");
        }

        lock (_gate)
        {
            _acquisition = settings;
        }

        return Task.CompletedTask;
    }

    public Task ConfigureTriggerAsync(ScopeTriggerSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _trigger = settings;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WaveformRecord>> AcquireAsync(
        IReadOnlyList<string> channels,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        lock (_gate)
        {
            var interval = 1.0 / _acquisition.SampleRateHz;
            var pre = _acquisition.RecordLength / 10;
            var origin = TriggerTime(interval) - pre * interval;

            var records = new List<WaveformRecord>();
            foreach (var channel in channels)
            {
                var signal = Signal(channel);
                var setup = _channels.GetValueOrDefault(channel) ?? new ScopeChannelSettings(20.0);
                var (low, high) = (setup.OffsetVolts - setup.RangeVolts / 2, setup.OffsetVolts + setup.RangeVolts / 2);
                var samples = new double[_acquisition.RecordLength];
                for (var index = 0; index < samples.Length; index++)
                {
                    var value = setup.Coupling == ScopeCoupling.Ground ? 0 : signal(origin + index * interval);
                    samples[index] = Math.Clamp(value, low, high);
                }

                var record = new WaveformRecord(channel, samples, interval, -pre * interval);
                _last[channel] = record;
                records.Add(record);
            }

            return Task.FromResult<IReadOnlyList<WaveformRecord>>(records);
        }
    }

    public Task<double> MeasureAsync(string channel, ScopeMeasurement measurement, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _last.TryGetValue(channel, out var record)
                ? Task.FromResult(WaveformMath.Measure(record, measurement))
                : throw new InvalidOperationException($"Channel '{channel}' has not been acquired yet.");
        }
    }

    private Func<double, double> Signal(string channel) => _inputs.TryGetValue(channel, out var signal) ? signal : _ => 0.0;

    /// <summary>
    /// The first time the trigger source crosses its level in the configured direction, searched
    /// sample by sample from zero. Without a trigger, or in auto mode without a crossing, zero; in
    /// normal mode without a crossing, a timeout, as a real scope would never trigger.
    /// </summary>
    private double TriggerTime(double interval)
    {
        if (_trigger is not { } trigger)
        {
            return 0;
        }

        var source = Signal(trigger.Source);
        var previous = source(0);
        for (var index = 1; index < MaxTriggerSearchSamples; index++)
        {
            var value = source(index * interval);
            var crossed = trigger.Slope == TriggerSlope.Rising
                ? previous < trigger.LevelVolts && value >= trigger.LevelVolts
                : previous > trigger.LevelVolts && value <= trigger.LevelVolts;
            if (crossed)
            {
                return index * interval;
            }

            previous = value;
        }

        return trigger.Mode == TriggerMode.Auto
            ? 0
            : throw new TimeoutException($"The trigger on {trigger.Source} at {trigger.LevelVolts.ToString("G4", CultureInfo.InvariantCulture)} V never came.");
    }
}
