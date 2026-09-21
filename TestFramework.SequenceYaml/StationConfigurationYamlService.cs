using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TestFramework.SequenceYaml;

/// <summary>
/// Reads and writes a station's configuration file.
///
/// It is a separate file, and a separate service, because it has a different owner and a different
/// life: sequences are written by test engineers and travel between benches, while this describes
/// one bench and is edited when that bench is re-cabled. Putting station wiring inside a sequence
/// is what forced every sequence to be copied per station in the first place.
///
/// Settings values are typed the way YAML types them, exactly as in a sequence file, so a driver
/// receives a baud rate as a number rather than as text.
/// </summary>
public sealed class StationConfigurationYamlService
{
    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithQuotingNecessaryStrings()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .DisableAliases()
        .WithEventEmitter(next => new RoundTripScalarEventEmitter(next), where => where.OnBottom())
        .Build();

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .WithNodeTypeResolver(new PlainScalarTypeResolver(), where => where.OnTop())
        .Build();

    public StationConfiguration LoadFromFile(string filePath) =>
        Load(File.ReadAllText(filePath));

    public async Task<StationConfiguration> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default) =>
        Load(await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false));

    public StationConfiguration Load(string yaml)
    {
        var dto = _deserializer.Deserialize<YamlStation>(yaml) ?? new YamlStation();
        EnsureSupportedSchema(dto.SchemaVersion);
        return dto.ToDomain();
    }

    public string Save(StationConfiguration station)
    {
        ArgumentNullException.ThrowIfNull(station);
        EnsureSupportedSchema(station.SchemaVersion);
        return _serializer.Serialize(YamlStation.FromDomain(station));
    }

    public void SaveToFile(StationConfiguration station, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, Save(station));
    }

    private static void EnsureSupportedSchema(int version)
    {
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported station schemaVersion '{version}'. Expected 1.");
        }
    }

    private sealed class YamlStation
    {
        public int SchemaVersion { get; set; } = 1;

        public string StationId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public List<YamlStationResource> Resources { get; set; } = [];

        public StationConfiguration ToDomain() => new()
        {
            SchemaVersion = SchemaVersion,
            StationId = StationId,
            Name = Name,
            Resources = Resources.Select(resource => resource.ToDomain()).ToList()
        };

        public static YamlStation FromDomain(StationConfiguration station) => new()
        {
            SchemaVersion = station.SchemaVersion,
            StationId = station.StationId,
            Name = station.Name,
            Resources = station.Resources.Select(YamlStationResource.FromDomain).ToList()
        };
    }

    private sealed class YamlStationResource
    {
        public string Alias { get; set; } = string.Empty;

        public ResourcePluginKind Kind { get; set; } = ResourcePluginKind.InstrumentDriver;

        public string DriverId { get; set; } = string.Empty;

        public string DriverVersion { get; set; } = "1.0.0";

        public string Resource { get; set; } = string.Empty;

        public Dictionary<string, object?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Channel { get; set; }

        public string? Transport { get; set; }

        public bool Shared { get; set; } = true;

        public StationResourceBinding ToDomain() => new()
        {
            Alias = Alias,
            Kind = Kind,
            DriverId = DriverId,
            DriverVersion = DriverVersion,
            Resource = Resource,
            Settings = new Dictionary<string, object?>(Settings, StringComparer.OrdinalIgnoreCase),
            Channel = Channel,
            Transport = Transport,
            Shared = Shared
        };

        public static YamlStationResource FromDomain(StationResourceBinding binding) => new()
        {
            Alias = binding.Alias,
            Kind = binding.Kind,
            DriverId = binding.DriverId,
            DriverVersion = binding.DriverVersion,
            Resource = binding.Resource,
            Settings = binding.Settings,
            Channel = binding.Channel,
            Transport = binding.Transport,
            Shared = binding.Shared
        };
    }
}
