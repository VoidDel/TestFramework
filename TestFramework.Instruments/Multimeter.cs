using TestFramework.Abstractions.Resources;

namespace TestFramework.Instruments;

/// <summary>A digital multimeter (IviDmm): one reading of one function at a time.</summary>
[ResourceClass(InstrumentClasses.DigitalMultimeter)]
public interface IDigitalMultimeter : IInstrument
{
    /// <summary>
    /// One reading, in the function's unit (<see cref="MeasurementFunctions.UnitOf"/>).
    /// <paramref name="range"/> is the largest value expected, in the same unit; null asks for
    /// auto-ranging, which is slower but safe when the value is not known.
    /// </summary>
    Task<double> MeasureAsync(MeasurementFunction function, double? range = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// A scanning data acquisition unit: many channels read in one pass - 32 thermocouples, a stack of
/// cell taps. One call returns them all, because scanning them one by one through a multimeter
/// interface would multiply the switching time by the channel count.
/// </summary>
[ResourceClass(InstrumentClasses.DataAcquisition)]
public interface IDataAcquisition : IInstrument
{
    /// <summary>
    /// Reads <paramref name="channels"/> - in the instrument's own naming, "101" or "ai3" - with one
    /// function, and returns each channel's value in the function's unit, keyed by the channel as
    /// given and in the order given.
    /// </summary>
    Task<IReadOnlyDictionary<string, double>> ScanAsync(
        IReadOnlyList<string> channels,
        MeasurementFunction function,
        CancellationToken cancellationToken = default);
}

public enum MeasurementFunction
{
    DcVoltage,
    AcVoltage,
    DcCurrent,
    AcCurrent,
    Resistance,
    FourWireResistance,
    Frequency,
    Temperature
}

public static class MeasurementFunctions
{
    /// <summary>
    /// The unit a reading of <paramref name="function"/> is in, as the framework's unit table spells
    /// it - what a step reports so a limit in mV or kOhm converts without the sequence saying so.
    /// AC readings are RMS.
    /// </summary>
    public static string UnitOf(MeasurementFunction function) => function switch
    {
        MeasurementFunction.DcVoltage or MeasurementFunction.AcVoltage => "V",
        MeasurementFunction.DcCurrent or MeasurementFunction.AcCurrent => "A",
        MeasurementFunction.Resistance or MeasurementFunction.FourWireResistance => "Ohm",
        MeasurementFunction.Frequency => "Hz",
        MeasurementFunction.Temperature => "°C",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unknown measurement function.")
    };
}
