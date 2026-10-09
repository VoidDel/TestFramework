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

    /// <summary>
    /// A digest of a sequence file's exact text, for <c>TestRunInfo.SequenceHash</c>:
    /// <c>sha256:</c> and 64 hex digits. Of the text as read, not of the loaded model - an edit that
    /// changes only spacing is still a different file, and the point is to identify the bytes.
    /// </summary>
    public static string ComputeHash(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(yaml));
        return "sha256:" + Convert.ToHexStringLower(digest);
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

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public string? Version { get; set; }

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
                Version = Version,
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
                Version = string.IsNullOrWhiteSpace(sequence.Version) ? null : sequence.Version,
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

        // The flow members are omitted while unset, so an item that uses none of them saves exactly
        // as it did before they existed.
        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public string? RunIf { get; set; }

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public YamlLoop? Loop { get; set; }

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public YamlRetry? Retry { get; set; }

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public YamlCall? Call { get; set; }

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections | DefaultValuesHandling.OmitNull)]
        public List<YamlTestItem> Items { get; set; } = [];

        // A group or a call has no verdict source and no steps; writing empty ones for it would
        // read as an item someone forgot to fill in. Null omits them; an ordinary item always has them.
        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public YamlVerdictSource? VerdictSource { get; set; } = new();

        // Omitted when empty, so an item without checks saves exactly as it did before they existed.
        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections | DefaultValuesHandling.OmitNull)]
        public List<YamlVerdictSource> Checks { get; set; } = [];

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public List<YamlTestStep>? Init { get; set; } = [];

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public List<YamlTestStep>? Main { get; set; } = [];

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public List<YamlTestStep>? Cleanup { get; set; } = [];

        public TestItemDefinition ToDomain()
        {
            return new TestItemDefinition
            {
                Id = Id,
                Name = Name,
                Enabled = Enabled,
                RunIf = RunIf,
                Loop = Loop?.ToDomain(),
                Retry = Retry?.ToDomain(),
                Call = Call?.ToDomain(),
                Items = (Items ?? []).Select(child => child.ToDomain()).ToList(),
                VerdictSource = (VerdictSource ?? new YamlVerdictSource()).ToDomain(),
                Checks = (Checks ?? []).Select(check => (check ?? new YamlVerdictSource()).ToDomain()).ToList(),
                InitSteps = (Init ?? []).Select(step => step.ToDomain()).ToList(),
                MainSteps = (Main ?? []).Select(step => step.ToDomain()).ToList(),
                CleanupSteps = (Cleanup ?? []).Select(step => step.ToDomain()).ToList()
            };
        }

        public static YamlTestItem FromDomain(TestItemDefinition item)
        {
            var container = item.IsGroup || item.IsCall;
            return new YamlTestItem
            {
                Id = item.Id,
                Name = item.Name,
                Enabled = item.Enabled,
                RunIf = string.IsNullOrWhiteSpace(item.RunIf) ? null : item.RunIf,
                Loop = item.Loop is null ? null : YamlLoop.FromDomain(item.Loop),
                Retry = item.Retry is null ? null : YamlRetry.FromDomain(item.Retry),
                Call = item.Call is null ? null : YamlCall.FromDomain(item.Call),
                Items = item.Items.Select(FromDomain).ToList(),
                VerdictSource = container ? null : YamlVerdictSource.FromDomain(item.VerdictSource),
                Checks = item.Checks.Select(YamlVerdictSource.FromDomain).ToList(),
                Init = container && item.InitSteps.Count == 0 ? null : item.InitSteps.Select(YamlTestStep.FromDomain).ToList(),
                Main = container && item.MainSteps.Count == 0 ? null : item.MainSteps.Select(YamlTestStep.FromDomain).ToList(),
                Cleanup = container && item.CleanupSteps.Count == 0 ? null : item.CleanupSteps.Select(YamlTestStep.FromDomain).ToList()
            };
        }
    }

    private sealed class YamlLoop
    {
        /// <summary>A number or an expression; YAML hands a bare number over as one, kept as its text.</summary>
        public object? Count { get; set; }

        public string Variable { get; set; } = "loopIndex";

        public LoopDefinition ToDomain() => new()
        {
            Count = Convert.ToString(Count, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            Variable = Variable
        };

        public static YamlLoop FromDomain(LoopDefinition loop) => new()
        {
            // A plain number is written back as one, so `count: 16` does not become `count: '16'`.
            Count = int.TryParse(loop.Count, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? number
                : loop.Count,
            Variable = loop.Variable
        };
    }

    private sealed class YamlRetry
    {
        public int MaxAttempts { get; set; } = 1;

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
        public int IntervalMs { get; set; }

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public string? Until { get; set; }

        public RetryDefinition ToDomain() => new() { MaxAttempts = MaxAttempts, IntervalMs = IntervalMs, Until = Until };

        public static YamlRetry FromDomain(RetryDefinition retry) => new()
        {
            MaxAttempts = retry.MaxAttempts,
            IntervalMs = retry.IntervalMs,
            Until = string.IsNullOrWhiteSpace(retry.Until) ? null : retry.Until
        };
    }

    private sealed class YamlCall
    {
        public string Path { get; set; } = string.Empty;

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections | DefaultValuesHandling.OmitNull)]
        public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public SequenceCallDefinition ToDomain() => new() { Path = Path, Parameters = NormalizeDictionary(Parameters) };

        public static YamlCall FromDomain(SequenceCallDefinition call) => new() { Path = call.Path, Parameters = call.Parameters };
    }

    /// <summary>
    /// The file shape of <see cref="TestFramework.Abstractions.Models.VerdictSource"/>. Members are
    /// in the model's order so the saved layout does not move.
    ///
    /// <c>lowerLimit</c> and <c>upperLimit</c> hold either a number or a <c>${variable}</c>, which
    /// the model keeps in two members. Anything else that is text - <c>abc</c>, <c>${1st}</c>,
    /// <c>.nan</c> - is kept as the reference too, so the file still opens, the validator says what
    /// is wrong with it, and saving writes back exactly what was read. <c>expected</c> works the same.
    ///
    /// The members added with comparisons and checks - <c>name</c>, <c>comparison</c>,
    /// <c>expected</c> - are omitted while they hold their defaults, so a file that does not use
    /// them saves byte for byte as it did before they existed, and a sequence under version control
    /// does not change on its first save.
    /// </summary>
    private sealed class YamlVerdictSource
    {
        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public string? Name { get; set; }

        public string? StepId { get; set; }

        public string? OutputKey { get; set; }

        public VerdictJudgeType JudgeType { get; set; } = VerdictJudgeType.PassFail;

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
        public NumericComparison Comparison { get; set; } = NumericComparison.GELE;

        public object? LowerLimit { get; set; }

        public object? UpperLimit { get; set; }

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public object? Expected { get; set; }

        public string? SourceUnit { get; set; }

        public string? Unit { get; set; }

        public StringJudgeMode StringMode { get; set; } = StringJudgeMode.Exact;

        public string? ExpectedString { get; set; }

        public VerdictSource ToDomain()
        {
            var (lower, lowerReference) = SplitLimit(LowerLimit, "lowerLimit");
            var (upper, upperReference) = SplitLimit(UpperLimit, "upperLimit");
            var (expected, expectedReference) = SplitLimit(Expected, "expected");
            return new VerdictSource
            {
                Name = Name,
                StepId = StepId,
                OutputKey = OutputKey,
                JudgeType = JudgeType,
                Comparison = Comparison,
                LowerLimit = lower,
                UpperLimit = upper,
                LowerLimitReference = lowerReference,
                UpperLimitReference = upperReference,
                Expected = expected,
                ExpectedReference = expectedReference,
                SourceUnit = SourceUnit,
                Unit = Unit,
                StringMode = StringMode,
                ExpectedString = ExpectedString
            };
        }

        public static YamlVerdictSource FromDomain(VerdictSource source)
        {
            return new YamlVerdictSource
            {
                Name = string.IsNullOrWhiteSpace(source.Name) ? null : source.Name,
                StepId = source.StepId,
                OutputKey = source.OutputKey,
                JudgeType = source.JudgeType,
                Comparison = source.Comparison,
                LowerLimit = string.IsNullOrWhiteSpace(source.LowerLimitReference) ? source.LowerLimit : source.LowerLimitReference,
                UpperLimit = string.IsNullOrWhiteSpace(source.UpperLimitReference) ? source.UpperLimit : source.UpperLimitReference,
                Expected = string.IsNullOrWhiteSpace(source.ExpectedReference) ? source.Expected : source.ExpectedReference,
                SourceUnit = source.SourceUnit,
                Unit = source.Unit,
                StringMode = source.StringMode,
                ExpectedString = source.ExpectedString
            };
        }

        private static (double? Literal, string? Reference) SplitLimit(object? value, string field)
        {
            return value switch
            {
                null => (null, null),
                string text when NumericValue.TryRead(text, out var number) => (number, null),
                string text => (null, text),
                // Kept even when not finite (1e999), so the validator reports it as before.
                double number => (number, null),
                _ when NumericValue.TryRead(value, out var number) => (number, null),
                _ => throw new InvalidDataException(
                    $"Verdict {field} must be a number or a ${{variable}} reference, but is '{value}'.")
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

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public string? RunIf { get; set; }

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public YamlRetry? Retry { get; set; }

        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections | DefaultValuesHandling.OmitNull)]
        public List<string> Exclusive { get; set; } = [];

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
                RunIf = RunIf,
                Retry = Retry?.ToDomain(),
                Exclusive = (Exclusive ?? []).ToList(),
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
                RunIf = string.IsNullOrWhiteSpace(step.RunIf) ? null : step.RunIf,
                Retry = step.Retry is null ? null : YamlRetry.FromDomain(step.Retry),
                Exclusive = step.Exclusive,
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

    /// <summary>
    /// Normalizes one of the file's top-level key/value blocks - <c>variables</c>, <c>parameters</c>
    /// or an instrument's <c>settings</c>. These are the framework's own namespaces and are read
    /// case-insensitively, so two keys differing only in case are genuinely ambiguous: merging them
    /// would let file order decide which of the operator's two values survives. Rejected, like
    /// every other way this file could be read differently from how it was written.
    /// </summary>
    private static Dictionary<string, object?> NormalizeDictionary(IDictionary<string, object?>? source)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        if (VariableValue.TryFindCaseInsensitiveCollision(source.Keys, out var collision))
        {
            throw new InvalidDataException(
                $"Key '{collision}' differs from an earlier key only by case. Variables, parameters and settings are matched case-insensitively, so the two cannot both be kept.");
        }

        foreach (var (key, value) in source)
        {
            result[key] = NormalizeValue(value);
        }

        return result;
    }

    /// <summary>
    /// A value inside one of those blocks. Dictionaries nested here are the plugin's own data, not
    /// a framework namespace, so they stay case-sensitive - see <see cref="VariableValue"/>. Making
    /// them case-insensitive turned a payload carrying both <c>SOC</c> and <c>soc</c> into an
    /// unhandled <see cref="ArgumentException"/> at load.
    /// </summary>
    private static object? NormalizeValue(object? value)
    {
        if (value is IDictionary<object, object?> objectDictionary)
        {
            var nested = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, item) in objectDictionary)
            {
                nested[Convert.ToString(key) ?? string.Empty] = NormalizeValue(item);
            }

            return nested;
        }

        if (value is IDictionary<string, object?> stringDictionary)
        {
            var nested = new Dictionary<string, object?>(VariableValue.ComparerOf(stringDictionary));
            foreach (var (key, item) in stringDictionary)
            {
                nested[key] = NormalizeValue(item);
            }

            return nested;
        }

        if (value is IEnumerable<object?> list && value is not string)
        {
            return list.Select(NormalizeValue).ToList();
        }

        return value;
    }
}
