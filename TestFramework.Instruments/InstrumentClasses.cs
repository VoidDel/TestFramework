namespace TestFramework.Instruments;

/// <summary>
/// The standard resource classes, by the names a sequence writes in <c>requires[].class</c>.
///
/// Each is an interface in this package carrying a <c>ResourceClassAttribute</c> with one of these
/// names. They are modelled on the IVI Foundation's instrument classes (IviDCPwr, IviDmm, IviFgen,
/// IviScope) and keep only what every instrument of the class can do; a vendor's extras belong on
/// an interface of the driver's own, which a step reaches with <c>TryGet</c> after asking for the
/// standard one.
///
/// Conventions every class follows:
/// - values are in SI base units - volts, amperes, ohms, watts, hertz, seconds - and temperature in
///   degrees Celsius, the base of the framework's unit table, so a step's output converts to any
///   limit unit through <c>sourceUnit</c>;
/// - one resource is one channel: a three-output supply is three resources, each a channel view
///   over the instrument (see README, "按通道拆分"), so a step never takes a channel number for a
///   supply, load or generator. The oscilloscope and the data acquisition unit are the exceptions,
///   because their channels share one acquisition;
/// - every call takes a cancellation token, and a driver honours it - a step that is cancelled or
///   times out must not leave a command half-sent.
///
/// These interfaces are part of the plugin contract (1.2) and follow its rule: a member added
/// later carries a default implementation, and an existing member never changes.
/// </summary>
public static class InstrumentClasses
{
    public const string PowerSupply = "PowerSupply";
    public const string ElectronicLoad = "ElectronicLoad";
    public const string DigitalMultimeter = "DigitalMultimeter";
    public const string DataAcquisition = "DataAcquisition";
    public const string FunctionGenerator = "FunctionGenerator";
    public const string Oscilloscope = "Oscilloscope";
    public const string CanChannel = "CanChannel";
}

/// <summary>What every instrument can do, whatever its class.</summary>
public interface IInstrument
{
    /// <summary>
    /// Who the instrument says it is - for SCPI, the <c>*IDN?</c> reply: maker, model, serial,
    /// firmware. A step records it so a result can say which unit measured the DUT.
    /// </summary>
    Task<string> IdentifyAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the instrument to its default state - for SCPI, <c>*RST</c>.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
