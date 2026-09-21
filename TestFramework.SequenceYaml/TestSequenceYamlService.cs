using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;
using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TestFramework.SequenceYaml;

public sealed class TestSequenceYamlService
{
    /// <summary>Maximum collection nesting the editor will parse; see <see cref="RejectUnsupportedRoundTripSyntax"/>.</summary>
    private const int MaxNestingDepth = 64;

    // Values in variables, parameters and settings are typed the way YAML types them: an unquoted
    // 5.0 is a double, 7 an int, true a bool, and a quoted scalar is a string. Loading infers those
    // types, and saving quotes any string that would read back as something else, so a value keeps
    // its type across a round trip - which is what lets a plugin receive a number as a number.
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

    public TestSequence LoadFromFile(string filePath)
    {
        return Load(File.ReadAllText(filePath));
    }

    public async Task<TestSequence> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Load(await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false));
    }

    public TestSequence Load(string yaml)
    {
        RejectUnsupportedRoundTripSyntax(yaml);
        var dto = _deserializer.Deserialize<YamlTestSequence>(yaml) ?? new YamlTestSequence();
        EnsureSupportedSchema(dto.SchemaVersion);
        return dto.ToDomain();
    }

    public void SaveToFile(TestSequence sequence, string filePath)
    {
        var (fullPath, tempPath) = PrepareAtomicSave(filePath);
        try
        {
            File.WriteAllText(tempPath, Save(sequence));
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(tempPath);
        }
    }

    public async Task SaveToFileAsync(TestSequence sequence, string filePath, CancellationToken cancellationToken = default)
    {
        var (fullPath, tempPath) = PrepareAtomicSave(filePath);
        try
        {
            await File.WriteAllTextAsync(tempPath, Save(sequence), cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(tempPath);
        }
    }

    public string Save(TestSequence sequence)
    {
        EnsureSupportedSchema(sequence.SchemaVersion);
        return _serializer.Serialize(YamlTestSequence.FromDomain(sequence));
    }

    private static void EnsureSupportedSchema(int version)
    {
        if (version != 1) throw new InvalidDataException($"Unsupported sequence schemaVersion '{version}'. Expected 1.");
    }

    private static (string FullPath, string TempPath) PrepareAtomicSave(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        return (fullPath, Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp"));
    }

    private static void DeleteTemporaryFile(string tempPath)
    {
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Single streaming pre-scan over the token stream. Besides rejecting comments and aliases it
    /// bounds the collection nesting depth: the deserializer recurses per level, so a deeply nested
    /// document overflows the stack, and a StackOverflowException cannot be caught - it kills the
    /// process. Callers that load a whole directory would otherwise be unable to start at all.
    /// The scanner itself is a non-recursive state machine, so it is safe to run first.
    /// </summary>
    private static void RejectUnsupportedRoundTripSyntax(string yaml)
    {
        var scanner = new Scanner(new StringReader(yaml), skipComments: false);
        var depth = 0;
        while (scanner.MoveNext())
        {
            switch (scanner.Current)
            {
                case Comment:
                    throw new InvalidDataException("YAML comments are not supported by the visual editor because saving would discard them.");
                case Anchor:
                case AnchorAlias:
                    throw new InvalidDataException("YAML anchors and aliases are not supported by the visual editor because saving would flatten them.");
                case BlockMappingStart:
                case BlockSequenceStart:
                case FlowMappingStart:
                case FlowSequenceStart:
                    if (++depth > MaxNestingDepth)
                    {
                        throw new InvalidDataException($"YAML nesting is deeper than the supported limit of {MaxNestingDepth} levels.");
                    }

                    break;
                case BlockEnd:
                case FlowMappingEnd:
                case FlowSequenceEnd:
                    depth--;
                    break;
            }
        }
    }

    private sealed class YamlTestSequence
    {
        public int SchemaVersion { get; set; } = 1;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "New Test Sequence";

        public Dictionary<string, object?> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<YamlResourceRequirement> Requires { get; set; } = [];

        public List<YamlInstrument> Instruments { get; set; } = [];

        public List<YamlTransport> Transports { get; set; } = [];

        public List<YamlTestService> Services { get; set; } = [];

        public List<YamlTestItem> Items { get; set; } = [];

        public TestSequence ToDomain()
        {
            return new TestSequence
            {
                SchemaVersion = SchemaVersion,
                Id = Id,
                Name = Name,
                Variables = NormalizeDictionary(Variables),
                Requires = Requires.Select(requirement => requirement.ToDomain()).ToList(),
                Instruments = Instruments.Select(instrument => instrument.ToDomain()).ToList(),
                Transports = Transports.Select(transport => transport.ToDomain()).ToList(),
                Services = Services.Select(service => service.ToDomain()).ToList(),
                Items = Items.Select(item => item.ToDomain()).ToList()
            };
        }

        public static YamlTestSequence FromDomain(TestSequence sequence)
        {
            return new YamlTestSequence
            {
                SchemaVersion = sequence.SchemaVersion,
                Id = sequence.Id,
                Name = sequence.Name,
                Variables = sequence.Variables,
                Requires = sequence.Requires.Select(YamlResourceRequirement.FromDomain).ToList(),
                Instruments = sequence.Instruments.Select(YamlInstrument.FromDomain).ToList(),
                Transports = sequence.Transports.Select(YamlTransport.FromDomain).ToList(),
                Services = sequence.Services.Select(YamlTestService.FromDomain).ToList(),
                Items = sequence.Items.Select(YamlTestItem.FromDomain).ToList()
            };
        }
    }

    private sealed class YamlResourceRequirement
    {
        public string Alias { get; set; } = string.Empty;

        public ResourcePluginKind Kind { get; set; } = ResourcePluginKind.InstrumentDriver;

        public string? DriverId { get; set; }

        public string? MinimumDriverVersion { get; set; }

        public string? Description { get; set; }

        public ResourceRequirement ToDomain()
        {
            return new ResourceRequirement
            {
                Alias = Alias,
                Kind = Kind,
                DriverId = DriverId,
                MinimumDriverVersion = MinimumDriverVersion,
                Description = Description
            };
        }

        public static YamlResourceRequirement FromDomain(ResourceRequirement requirement)
        {
            return new YamlResourceRequirement
            {
                Alias = requirement.Alias,
                Kind = requirement.Kind,
                DriverId = requirement.DriverId,
                MinimumDriverVersion = requirement.MinimumDriverVersion,
                Description = requirement.Description
            };
        }
    }

    private sealed class YamlInstrument
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string DriverId { get; set; } = string.Empty;

        public string DriverVersion { get; set; } = "1.0.0";

        public string Resource { get; set; } = string.Empty;

        public Dictionary<string, object?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public InstrumentDefinition ToDomain()
        {
            return new InstrumentDefinition
            {
                Id = Id,
                DriverId = DriverId,
                DriverVersion = DriverVersion,
                Resource = Resource,
                Settings = NormalizeDictionary(Settings)
            };
        }

        public static YamlInstrument FromDomain(InstrumentDefinition instrument)
        {
            return new YamlInstrument
            {
                Id = instrument.Id,
                DriverId = instrument.DriverId,
                DriverVersion = instrument.DriverVersion,
                Resource = instrument.Resource,
                Settings = instrument.Settings
            };
        }
    }

    private sealed class YamlTransport
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string TransportId { get; set; } = string.Empty;

        public string TransportVersion { get; set; } = "1.0.0";

        public string? Channel { get; set; }

        public Dictionary<string, object?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public TransportDefinition ToDomain()
        {
            return new TransportDefinition
            {
                Id = Id,
                TransportId = TransportId,
                TransportVersion = TransportVersion,
                Channel = Channel,
                Settings = NormalizeDictionary(Settings)
            };
        }

        public static YamlTransport FromDomain(TransportDefinition transport)
        {
            return new YamlTransport
            {
                Id = transport.Id,
                TransportId = transport.TransportId,
                TransportVersion = transport.TransportVersion,
                Channel = transport.Channel,
                Settings = transport.Settings
            };
        }
    }

    private sealed class YamlTestService
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string ServiceId { get; set; } = string.Empty;

        public string ServiceVersion { get; set; } = "1.0.0";

        public string? Transport { get; set; }

        public Dictionary<string, object?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public TestServiceDefinition ToDomain()
        {
            return new TestServiceDefinition
            {
                Id = Id,
                ServiceId = ServiceId,
                ServiceVersion = ServiceVersion,
                Transport = Transport,
                Settings = NormalizeDictionary(Settings)
            };
        }

        public static YamlTestService FromDomain(TestServiceDefinition service)
        {
            return new YamlTestService
            {
                Id = service.Id,
                ServiceId = service.ServiceId,
                ServiceVersion = service.ServiceVersion,
                Transport = service.Transport,
                Settings = service.Settings
            };
        }
    }

    private sealed class YamlTestItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "New Test Item";

        public bool Enabled { get; set; } = true;

        public VerdictSource VerdictSource { get; set; } = new();

        public List<YamlTestStep> Init { get; set; } = [];

        public List<YamlTestStep> Main { get; set; } = [];

        public List<YamlTestStep> Cleanup { get; set; } = [];

        public TestItemDefinition ToDomain()
        {
            return new TestItemDefinition
            {
                Id = Id,
                Name = Name,
                Enabled = Enabled,
                VerdictSource = VerdictSource ?? new VerdictSource(),
                InitSteps = Init.Select(step => step.ToDomain()).ToList(),
                MainSteps = Main.Select(step => step.ToDomain()).ToList(),
                CleanupSteps = Cleanup.Select(step => step.ToDomain()).ToList()
            };
        }

        public static YamlTestItem FromDomain(TestItemDefinition item)
        {
            return new YamlTestItem
            {
                Id = item.Id,
                Name = item.Name,
                Enabled = item.Enabled,
                VerdictSource = item.VerdictSource,
                Init = item.InitSteps.Select(YamlTestStep.FromDomain).ToList(),
                Main = item.MainSteps.Select(YamlTestStep.FromDomain).ToList(),
                Cleanup = item.CleanupSteps.Select(YamlTestStep.FromDomain).ToList()
            };
        }
    }

    private sealed class YamlTestStep
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "New Step";

        public string PluginId { get; set; } = string.Empty;

        public string PluginVersion { get; set; } = "1.0.0";

        public bool Enabled { get; set; } = true;

        public int? TimeoutMs { get; set; }

        public string OnError { get; set; } = "stop";

        public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<VariableWriteDefinition> VariableWrites { get; set; } = [];

        public TestStepDefinition ToDomain()
        {
            return new TestStepDefinition
            {
                Id = Id,
                Name = Name,
                PluginId = PluginId,
                PluginVersion = PluginVersion,
                Enabled = Enabled,
                TimeoutMs = TimeoutMs,
                OnError = ParseErrorHandling(OnError),
                Parameters = NormalizeDictionary(Parameters),
                VariableWrites = VariableWrites.Select(NormalizeVariableWrite).ToList()
            };
        }

        public static YamlTestStep FromDomain(TestStepDefinition step)
        {
            return new YamlTestStep
            {
                Id = step.Id,
                Name = step.Name,
                PluginId = step.PluginId,
                PluginVersion = step.PluginVersion,
                Enabled = step.Enabled,
                TimeoutMs = step.TimeoutMs,
                OnError = FormatErrorHandling(step.OnError),
                Parameters = step.Parameters,
                VariableWrites = step.VariableWrites
            };
        }
    }

    private static VariableWriteDefinition NormalizeVariableWrite(VariableWriteDefinition write)
    {
        return new VariableWriteDefinition
        {
            Name = write.Name,
            OutputKey = write.OutputKey,
            Value = NormalizeValue(write.Value),
            WriteOnError = write.WriteOnError
        };
    }

    private static ErrorHandlingMode ParseErrorHandling(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "stop" => ErrorHandlingMode.Stop,
            "continue" => ErrorHandlingMode.Continue,
            "jumptocleanup" or "jump-to-cleanup" or "jump_to_cleanup" => ErrorHandlingMode.JumpToCleanup,
            _ => throw new InvalidDataException($"Unknown onError value '{value}'. Expected stop, continue, or jumpToCleanup.")
        };
    }

    private static string FormatErrorHandling(ErrorHandlingMode mode)
    {
        return mode switch
        {
            ErrorHandlingMode.Continue => "continue",
            ErrorHandlingMode.JumpToCleanup => "jumpToCleanup",
            _ => "stop"
        };
    }

    private static Dictionary<string, object?> NormalizeDictionary(IDictionary<string, object?>? source)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach (var (key, value) in source)
        {
            result[key] = NormalizeValue(value);
        }

        return result;
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is IDictionary<object, object?> objectDictionary)
        {
            return objectDictionary.ToDictionary(pair => Convert.ToString(pair.Key) ?? string.Empty, pair => NormalizeValue(pair.Value), StringComparer.OrdinalIgnoreCase);
        }

        if (value is IDictionary<string, object?> stringDictionary)
        {
            return NormalizeDictionary(stringDictionary);
        }

        if (value is IEnumerable<object?> list && value is not string)
        {
            return list.Select(NormalizeValue).ToList();
        }

        return value;
    }
}
