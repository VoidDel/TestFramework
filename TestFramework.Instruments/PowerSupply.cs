using TestFramework.Abstractions.Resources;

namespace TestFramework.Instruments;

/// <summary>
/// One output of a programmable DC supply (IviDCPwr): set a voltage and a current limit, switch the
/// output, read back what it delivers. A battery simulator or a bidirectional source that can also
/// sink is a supply here and an <see cref="IElectronicLoad"/> as well.
/// </summary>
[ResourceClass(InstrumentClasses.PowerSupply)]
public interface IPowerSupply : IInstrument
{
    /// <summary>The voltage the output regulates to, in volts.</summary>
    Task SetVoltageAsync(double volts, CancellationToken cancellationToken = default);

    /// <summary>The current at which the output stops regulating voltage and limits current, in amperes.</summary>
    Task SetCurrentLimitAsync(double amperes, CancellationToken cancellationToken = default);

    /// <summary>
    /// The over-voltage protection threshold in volts, past which the output shuts down; null
    /// switches the protection off where the instrument allows it.
    /// </summary>
    Task SetOverVoltageProtectionAsync(double? volts, CancellationToken cancellationToken = default);

    /// <summary>Clears a tripped protection, so the output can be enabled again.</summary>
    Task ClearProtectionAsync(CancellationToken cancellationToken = default);

    Task SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>The voltage measured at the output, in volts.</summary>
    Task<double> MeasureVoltageAsync(CancellationToken cancellationToken = default);

    /// <summary>The current measured through the output, in amperes.</summary>
    Task<double> MeasureCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the output is on, how it is regulating, and whether a protection has tripped. A
    /// supply in constant current while charging a pack is the difference between "charging" and
    /// "the pack is pulling more than it should".
    /// </summary>
    Task<PowerSupplyStatus> QueryStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record PowerSupplyStatus(bool OutputEnabled, RegulationMode Mode, bool ProtectionTripped);

public enum RegulationMode
{
    /// <summary>The output is off.</summary>
    Off,

    ConstantVoltage,

    ConstantCurrent,

    /// <summary>On, but neither regulating - typically in transition, or a fault.</summary>
    Unregulated
}

/// <summary>
/// One input of a programmable DC electronic load: sink a set current, hold a voltage, act as a
/// resistance or draw a power - discharging a pack, or loading a DC-DC output.
/// </summary>
[ResourceClass(InstrumentClasses.ElectronicLoad)]
public interface IElectronicLoad : IInstrument
{
    /// <summary>
    /// The regulation mode and its level, in the mode's unit: amperes for constant current, volts
    /// for constant voltage, ohms for constant resistance, watts for constant power.
    /// </summary>
    Task SetModeAsync(LoadMode mode, double level, CancellationToken cancellationToken = default);

    Task SetInputEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>The voltage measured at the input, in volts.</summary>
    Task<double> MeasureVoltageAsync(CancellationToken cancellationToken = default);

    /// <summary>The current measured into the input, in amperes.</summary>
    Task<double> MeasureCurrentAsync(CancellationToken cancellationToken = default);
}

public enum LoadMode
{
    ConstantCurrent,
    ConstantVoltage,
    ConstantResistance,
    ConstantPower
}
