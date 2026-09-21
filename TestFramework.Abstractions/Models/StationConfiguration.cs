using TestFramework.Abstractions.Resources;

namespace TestFramework.Abstractions.Models;

/// <summary>
/// What one test station has, and where it is.
///
/// This is the other half of the split from <see cref="ResourceRequirement"/>: the sequence says it
/// needs a supply called psu, this says that on this bench psu is a Keysight on COM7. It lives with
/// the station - one file per bench, not per test - so moving a sequence between stations changes
/// nothing in the sequence, and re-porting a station changes one file rather than every sequence
/// that runs on it.
/// </summary>
public sealed class StationConfiguration
{
    public int SchemaVersion { get; set; } = 1;

    public string StationId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<StationResourceBinding> Resources { get; set; } = [];

    /// <summary>
    /// The binding for an alias, or null. Case-insensitive, like every other id in a sequence.
    /// </summary>
    public StationResourceBinding? Find(string alias) =>
        Resources.FirstOrDefault(binding => string.Equals(binding.Alias, alias, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One alias on a station, bound to a concrete driver at a concrete address.</summary>
public sealed class StationResourceBinding
{
    /// <summary>The alias a sequence's <see cref="ResourceRequirement"/> refers to.</summary>
    public string Alias { get; set; } = string.Empty;

    public ResourcePluginKind Kind { get; set; } = ResourcePluginKind.InstrumentDriver;

    public string DriverId { get; set; } = string.Empty;

    public string DriverVersion { get; set; } = "1.0.0";

    /// <summary>The address: "COM7", "can0", a VISA resource string.</summary>
    public string Resource { get; set; } = string.Empty;

    public Dictionary<string, object?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The instrument alias a transport runs over; ignored for the other kinds.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>The transport alias a service runs over; ignored for the other kinds.</summary>
    public string? Transport { get; set; }

    /// <summary>
    /// Whether this resource is opened once for the station and kept open across runs.
    ///
    /// True is the default and the reason the station scope exists: a USB-CAN adapter takes about a
    /// second to initialise, some instruments need to warm up, and some drivers fail outright when
    /// opened and closed repeatedly. Per-DUT open/close turns all three into a line-rate problem.
    ///
    /// False is for a resource that genuinely must start clean for every DUT - one holding
    /// per-unit state, or a driver that cannot be reset without a reconnect.
    /// </summary>
    public bool Shared { get; set; } = true;

    /// <summary>Projects this binding onto the definition the driver plugins already take.</summary>
    public InstrumentDefinition ToInstrumentDefinition() => new()
    {
        Id = Alias,
        DriverId = DriverId,
        DriverVersion = DriverVersion,
        Resource = Resource,
        Settings = new Dictionary<string, object?>(Settings, StringComparer.OrdinalIgnoreCase)
    };

    public TransportDefinition ToTransportDefinition() => new()
    {
        Id = Alias,
        TransportId = DriverId,
        TransportVersion = DriverVersion,
        Channel = Channel,
        Settings = new Dictionary<string, object?>(Settings, StringComparer.OrdinalIgnoreCase)
    };

    public TestServiceDefinition ToServiceDefinition() => new()
    {
        Id = Alias,
        ServiceId = DriverId,
        ServiceVersion = DriverVersion,
        Transport = Transport,
        Settings = new Dictionary<string, object?>(Settings, StringComparer.OrdinalIgnoreCase)
    };
}
