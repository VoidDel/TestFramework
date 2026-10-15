using TestFramework.Abstractions.Resources;

namespace TestFramework.Instruments;

/// <summary>
/// One output of a function generator (IviFgen): a standard waveform at a frequency, amplitude and
/// offset - a PWM input to a BMS, a wake-up pulse, a simulated sensor signal.
/// </summary>
[ResourceClass(InstrumentClasses.FunctionGenerator)]
public interface IFunctionGenerator : IInstrument
{
    /// <summary>
    /// The waveform the output produces once enabled. Configuring while the output is on changes the
    /// signal in place; a driver whose instrument cannot do that switches the output off and back.
    /// </summary>
    Task ConfigureAsync(WaveformSettings settings, CancellationToken cancellationToken = default);

    Task SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// A standard waveform. <see cref="AmplitudeVoltsPeakToPeak"/> is peak to peak, about
/// <see cref="OffsetVolts"/>; for <see cref="WaveformShape.Dc"/> only the offset counts.
/// <see cref="DutyCyclePercent"/> applies to square and pulse waves.
/// </summary>
public sealed record WaveformSettings(
    WaveformShape Shape,
    double FrequencyHz,
    double AmplitudeVoltsPeakToPeak,
    double OffsetVolts = 0,
    double DutyCyclePercent = 50);

public enum WaveformShape
{
    Sine,
    Square,
    Triangle,
    RampUp,
    RampDown,
    Pulse,
    Dc
}
