using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;
using TestFramework.Instruments;

namespace TestFramework.Plugins.Simulation;

public sealed class SimulatedPowerSupplyDriver : IInstrumentDriverPlugin
{
    public ResourcePluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "sim.psu",
        DisplayName = "模拟直流电源",
        Version = new Version(1, 0, 0),
        Category = "模拟仪器",
        Description = "一路直流输出，接一个电阻性负载（设置 LoadResistance，默认 100 Ω）：电流超过限流时转入恒流，超过过压保护时关断输出。"
    };

    public Type InstrumentType => typeof(SimulatedPowerSupply);

    public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new SimulatedPowerSupply(new SimulationSettings(definition.Id, definition.Settings)));
}

/// <summary>
/// A supply driving a resistive DUT: below the current limit it holds the set voltage, above it it
/// holds the limit and the voltage falls to what the resistance allows - the constant-current
/// corner a real supply shows. Over-voltage protection trips when the set voltage passes it with
/// the output on, and the output stays off until the protection is cleared.
/// </summary>
public sealed class SimulatedPowerSupply : IPowerSupply
{
    private readonly Lock _gate = new();
    private readonly string _identity;
    private readonly double _loadResistance;
    private double _voltage;
    private double _currentLimit = 1.0;
    private double? _overVoltage;
    private bool _output;
    private bool _tripped;

    internal SimulatedPowerSupply(SimulationSettings settings)
    {
        _identity = settings.Identity("SimPSU");
        _loadResistance = settings.Number("LoadResistance", 100.0);
        if (_loadResistance <= 0)
        {
            throw new ArgumentException($"Simulated instrument '{settings.Alias}': LoadResistance must be positive.");
        }
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_identity);

    public Task ResetAsync(CancellationToken cancellationToken = default) => Update(() =>
    {
        (_voltage, _currentLimit, _overVoltage, _output, _tripped) = (0, 1.0, null, false, false);
    });

    public Task SetVoltageAsync(double volts, CancellationToken cancellationToken = default) => Update(() => _voltage = Checked(volts, nameof(volts)));

    public Task SetCurrentLimitAsync(double amperes, CancellationToken cancellationToken = default) =>
        Update(() => _currentLimit = Checked(amperes, nameof(amperes)));

    public Task SetOverVoltageProtectionAsync(double? volts, CancellationToken cancellationToken = default) =>
        Update(() => _overVoltage = volts is { } limit ? Checked(limit, nameof(volts)) : null);

    public Task ClearProtectionAsync(CancellationToken cancellationToken = default) => Update(() => _tripped = false);

    public Task SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Update(() => _output = enabled);

    public Task<double> MeasureVoltageAsync(CancellationToken cancellationToken = default) => Read(() => Operating().Volts);

    public Task<double> MeasureCurrentAsync(CancellationToken cancellationToken = default) => Read(() => Operating().Amperes);

    public Task<PowerSupplyStatus> QueryStatusAsync(CancellationToken cancellationToken = default) => Read(() =>
    {
        var point = Operating();
        return new PowerSupplyStatus(_output, point.Mode, _tripped);
    });

    private (double Volts, double Amperes, RegulationMode Mode) Operating()
    {
        if (!_output)
        {
            return (0, 0, RegulationMode.Off);
        }

        var demanded = _voltage / _loadResistance;
        return demanded > _currentLimit
            ? (_currentLimit * _loadResistance, _currentLimit, RegulationMode.ConstantCurrent)
            : (_voltage, demanded, RegulationMode.ConstantVoltage);
    }

    private Task Update(Action change)
    {
        lock (_gate)
        {
            change();
            if (_output && _overVoltage is { } limit && _voltage > limit)
            {
                _tripped = true;
            }

            if (_tripped)
            {
                _output = false;
            }
        }

        return Task.CompletedTask;
    }

    private Task<T> Read<T>(Func<T> read)
    {
        lock (_gate)
        {
            return Task.FromResult(read());
        }
    }

    private static double Checked(double value, string name) =>
        double.IsFinite(value) && value >= 0 ? value : throw new ArgumentOutOfRangeException(name, value, "Must be a finite, non-negative number.");
}

public sealed class SimulatedElectronicLoadDriver : IInstrumentDriverPlugin
{
    public ResourcePluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "sim.load",
        DisplayName = "模拟电子负载",
        Version = new Version(1, 0, 0),
        Category = "模拟仪器",
        Description = "接一节电池：开路电压 SourceVoltage（默认 3.7 V），内阻 SourceResistance（默认 0.05 Ω），按恒流/恒压/恒阻/恒功率放电。"
    };

    public Type InstrumentType => typeof(SimulatedElectronicLoad);

    public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new SimulatedElectronicLoad(new SimulationSettings(definition.Id, definition.Settings)));
}

/// <summary>
/// A load discharging a cell with an open-circuit voltage and an internal resistance, so the
/// terminal voltage sags with current the way a real cell's does.
/// </summary>
public sealed class SimulatedElectronicLoad : IElectronicLoad
{
    private readonly Lock _gate = new();
    private readonly string _identity;
    private readonly double _openCircuit;
    private readonly double _internal;
    private LoadMode _mode = LoadMode.ConstantCurrent;
    private double _level;
    private bool _input;

    internal SimulatedElectronicLoad(SimulationSettings settings)
    {
        _identity = settings.Identity("SimLoad");
        _openCircuit = settings.Number("SourceVoltage", 3.7);
        _internal = settings.Number("SourceResistance", 0.05);
        if (_internal <= 0)
        {
            throw new ArgumentException($"Simulated instrument '{settings.Alias}': SourceResistance must be positive.");
        }
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_identity);

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            (_mode, _level, _input) = (LoadMode.ConstantCurrent, 0, false);
        }

        return Task.CompletedTask;
    }

    public Task SetModeAsync(LoadMode mode, double level, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown load mode.");
        }

        if (!double.IsFinite(level) || level < 0 || (mode == LoadMode.ConstantResistance && level == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"Not a valid level for {mode}.");
        }

        lock (_gate)
        {
            (_mode, _level) = (mode, level);
        }

        return Task.CompletedTask;
    }

    public Task SetInputEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _input = enabled;
        }

        return Task.CompletedTask;
    }

    public Task<double> MeasureVoltageAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_openCircuit - Current() * _internal);
        }
    }

    public Task<double> MeasureCurrentAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Current());
        }
    }

    /// <summary>The current the cell delivers in the present mode, never more than its short-circuit current.</summary>
    private double Current()
    {
        if (!_input)
        {
            return 0;
        }

        var shortCircuit = _openCircuit / _internal;
        var current = _mode switch
        {
            LoadMode.ConstantCurrent => _level,
            LoadMode.ConstantVoltage => (_openCircuit - _level) / _internal,
            LoadMode.ConstantResistance => _openCircuit / (_level + _internal),
            // P = (Voc - I·R)·I, the smaller root; past the cell's maximum power, that maximum.
            LoadMode.ConstantPower => _openCircuit * _openCircuit < 4 * _internal * _level
                ? shortCircuit / 2
                : (_openCircuit - Math.Sqrt(_openCircuit * _openCircuit - 4 * _internal * _level)) / (2 * _internal),
            _ => 0
        };

        return Math.Clamp(current, 0, shortCircuit);
    }
}
