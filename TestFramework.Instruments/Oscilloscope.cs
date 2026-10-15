using TestFramework.Abstractions.Resources;

namespace TestFramework.Instruments;

/// <summary>
/// A digital oscilloscope (IviScope): configure channels, the acquisition and the trigger, acquire,
/// then read waveforms or the instrument's own measurements of them.
///
/// Unlike the other classes it is one resource for all its channels, because they share one
/// trigger and one acquisition - "the pulse on CH2 while CH1 rises" is one capture, not two.
/// Channels are named as the instrument names them, "CH1" or "1".
/// </summary>
[ResourceClass(InstrumentClasses.Oscilloscope)]
public interface IOscilloscope : IInstrument
{
    Task ConfigureChannelAsync(string channel, ScopeChannelSettings settings, CancellationToken cancellationToken = default);

    Task ConfigureAcquisitionAsync(ScopeAcquisitionSettings settings, CancellationToken cancellationToken = default);

    Task ConfigureTriggerAsync(ScopeTriggerSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Arms, waits for the trigger and returns one record per channel asked for. A trigger that does
    /// not come within <paramref name="timeout"/> is a <see cref="TimeoutException"/>: no waveform is
    /// not a waveform of zeros.
    /// </summary>
    Task<IReadOnlyList<WaveformRecord>> AcquireAsync(
        IReadOnlyList<string> channels,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A measurement of the most recent acquisition on <paramref name="channel"/>, in its unit
    /// (<see cref="ScopeMeasurements.UnitOf"/>). A driver without one in the instrument can compute
    /// it with <see cref="WaveformMath"/>.
    /// </summary>
    Task<double> MeasureAsync(string channel, ScopeMeasurement measurement, CancellationToken cancellationToken = default);
}

/// <summary>
/// A channel's vertical setup. <see cref="RangeVolts"/> is the full-scale span across the screen;
/// <see cref="ProbeAttenuation"/> is 10 for a 10:1 probe, so readings come back at the probe tip.
/// </summary>
public sealed record ScopeChannelSettings(
    double RangeVolts,
    double OffsetVolts = 0,
    ScopeCoupling Coupling = ScopeCoupling.Dc,
    double ProbeAttenuation = 1,
    bool Enabled = true);

/// <summary>How fast and how long to sample: the record spans <c>RecordLength / SampleRateHz</c> seconds.</summary>
public sealed record ScopeAcquisitionSettings(double SampleRateHz, int RecordLength);

/// <summary>An edge trigger on <see cref="Source"/>, a channel name or "EXT".</summary>
public sealed record ScopeTriggerSettings(
    string Source,
    double LevelVolts,
    TriggerSlope Slope = TriggerSlope.Rising,
    TriggerMode Mode = TriggerMode.Normal);

public enum ScopeCoupling
{
    Dc,
    Ac,
    Ground
}

public enum TriggerSlope
{
    Rising,
    Falling
}

public enum TriggerMode
{
    /// <summary>Waits for a trigger, and times out without one.</summary>
    Normal,

    /// <summary>Acquires after a while even without a trigger - for a signal that is only a level.</summary>
    Auto
}

/// <summary>
/// One channel's capture: samples in volts, <see cref="SampleIntervalSeconds"/> apart, the first
/// taken <see cref="StartSeconds"/> from the trigger (negative for pre-trigger samples).
/// </summary>
public sealed record WaveformRecord(
    string Channel,
    IReadOnlyList<double> Samples,
    double SampleIntervalSeconds,
    double StartSeconds = 0);

public enum ScopeMeasurement
{
    Mean,
    Rms,
    PeakToPeak,
    Maximum,
    Minimum,
    Frequency,
    Period,

    /// <summary>10 % to 90 % of the swing, averaged over the record's rising edges.</summary>
    RiseTime,

    /// <summary>90 % to 10 % of the swing, averaged over the record's falling edges.</summary>
    FallTime,

    /// <summary>Time above the midpoint, from a rising crossing to the next falling one, averaged.</summary>
    PositiveWidth,

    /// <summary>The positive width as a percentage of the period.</summary>
    DutyCycle
}

public static class ScopeMeasurements
{
    /// <summary>The unit a measurement is in, as the framework's unit table spells it.</summary>
    public static string UnitOf(ScopeMeasurement measurement) => measurement switch
    {
        ScopeMeasurement.Mean or ScopeMeasurement.Rms or ScopeMeasurement.PeakToPeak or
            ScopeMeasurement.Maximum or ScopeMeasurement.Minimum => "V",
        ScopeMeasurement.Frequency => "Hz",
        ScopeMeasurement.Period or ScopeMeasurement.RiseTime or ScopeMeasurement.FallTime or
            ScopeMeasurement.PositiveWidth => "s",
        ScopeMeasurement.DutyCycle => "%",
        _ => throw new ArgumentOutOfRangeException(nameof(measurement), measurement, "Unknown scope measurement.")
    };
}
