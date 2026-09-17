using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;

namespace TestFramework.SequenceYaml.Validation;

public sealed class TestSequenceValidator
{
    private readonly IPluginRegistry? _pluginRegistry;
    private readonly IResourcePluginCatalog? _resourcePlugins;

    public TestSequenceValidator(
        IPluginRegistry? pluginRegistry = null,
        IResourcePluginCatalog? resourcePlugins = null)
    {
        _pluginRegistry = pluginRegistry;
        _resourcePlugins = resourcePlugins;
    }

    public IReadOnlyList<ValidationIssue> Validate(TestSequence sequence)
    {
        var issues = new List<ValidationIssue>();

        if (sequence.SchemaVersion != 1)
        {
            issues.Add(new ValidationIssue { Path = "schemaVersion", Message = "Only schemaVersion 1 is supported." });
        }

        if (string.IsNullOrWhiteSpace(sequence.Name))
        {
            issues.Add(new ValidationIssue { Path = "name", Message = "Sequence name is required." });
        }

        ValidateInstruments(sequence.Instruments, issues);
        ValidateTransports(sequence.Transports, sequence.Instruments, issues);
        ValidateServices(sequence.Services, sequence.Transports, issues);
        ValidateItems(sequence.Items, issues);

        return issues;
    }

    private void ValidateItems(IReadOnlyList<TestItemDefinition> items, ICollection<ValidationIssue> issues)
    {
        var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var item = items[itemIndex];
            var itemPath = $"items[{itemIndex}]";

            ValidateId(item.Id, itemPath, "Test item ID", itemIds, issues);

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                issues.Add(new ValidationIssue { Path = $"{itemPath}.name", Message = "Test item name is required." });
            }

            if (item.MainSteps.Count == 0)
            {
                issues.Add(new ValidationIssue { Path = $"{itemPath}.main", Message = "Main steps are required." });
            }

            if (string.IsNullOrWhiteSpace(item.VerdictSource.StepId))
            {
                issues.Add(new ValidationIssue { Path = $"{itemPath}.verdictSource.stepId", Message = "Verdict source step is required." });
            }
            else if (item.MainSteps.All(step => !string.Equals(step.Id, item.VerdictSource.StepId, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new ValidationIssue { Path = $"{itemPath}.verdictSource.stepId", Message = "Verdict source step must be in main steps." });
            }

            ValidateVerdictSource(item.VerdictSource, $"{itemPath}.verdictSource", issues);

            var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ValidateSteps(item.InitSteps, $"{itemPath}.init", stepIds, issues);
            ValidateSteps(item.MainSteps, $"{itemPath}.main", stepIds, issues);
            ValidateSteps(item.CleanupSteps, $"{itemPath}.cleanup", stepIds, issues);
        }
    }

    private static void ValidateVerdictSource(VerdictSource source, string path, ICollection<ValidationIssue> issues)
    {
        // YAML deserializes an out-of-range number straight into the enum, so a file can carry a
        // judge type the runner has no branch for. Without this it reaches execution and falls
        // through to a default that silently ignores the configured limits.
        if (!Enum.IsDefined(source.JudgeType))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.judgeType", Message = $"Unknown verdict judge type '{(int)source.JudgeType}'." });
        }

        if (!Enum.IsDefined(source.StringMode))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.stringMode", Message = $"Unknown string judge mode '{(int)source.StringMode}'." });
        }

        if (source.JudgeType is VerdictJudgeType.Numeric or VerdictJudgeType.String &&
            string.IsNullOrWhiteSpace(source.OutputKey))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.outputKey", Message = "Configured verdict requires an output key." });
        }

        if (source.JudgeType == VerdictJudgeType.Numeric &&
            !source.LowerLimit.HasValue &&
            !source.UpperLimit.HasValue)
        {
            issues.Add(new ValidationIssue { Path = path, Message = "Numeric verdict requires lowerLimit or upperLimit." });
        }

        if (source.JudgeType == VerdictJudgeType.Numeric &&
            source.LowerLimit.HasValue &&
            !double.IsFinite(source.LowerLimit.Value))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.lowerLimit", Message = "Numeric lowerLimit must be finite." });
        }

        if (source.JudgeType == VerdictJudgeType.Numeric &&
            source.UpperLimit.HasValue &&
            !double.IsFinite(source.UpperLimit.Value))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.upperLimit", Message = "Numeric upperLimit must be finite." });
        }

        if (source.JudgeType == VerdictJudgeType.Numeric &&
            source.LowerLimit.HasValue &&
            source.UpperLimit.HasValue &&
            source.LowerLimit.Value > source.UpperLimit.Value)
        {
            issues.Add(new ValidationIssue { Path = $"{path}.lowerLimit", Message = "Numeric lowerLimit must be less than or equal to upperLimit." });
        }

        if (source.JudgeType == VerdictJudgeType.String &&
            string.IsNullOrEmpty(source.ExpectedString))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.expectedString", Message = "String verdict requires expectedString." });
        }
    }

    private void ValidateInstruments(
        IReadOnlyList<InstrumentDefinition> instruments,
        ICollection<ValidationIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < instruments.Count; index++)
        {
            var instrument = instruments[index];
            var path = $"instruments[{index}]";
            ValidateId(instrument.Id, path, "Instrument ID", ids, issues);
            ValidateVersion(instrument.DriverVersion, $"{path}.driverVersion", issues);

            if (string.IsNullOrWhiteSpace(instrument.DriverId))
            {
                issues.Add(new ValidationIssue { Path = $"{path}.driverId", Message = "Instrument driver ID is required." });
            }
            else
            {
                ValidateResourcePlugin(ResourcePluginKind.InstrumentDriver, instrument.DriverId, instrument.DriverVersion, $"{path}.driverId", issues);
            }
        }
    }

    private void ValidateTransports(
        IReadOnlyList<TransportDefinition> transports,
        IReadOnlyList<InstrumentDefinition> instruments,
        ICollection<ValidationIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instrumentIds = instruments.Select(instrument => instrument.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < transports.Count; index++)
        {
            var transport = transports[index];
            var path = $"transports[{index}]";
            ValidateId(transport.Id, path, "Transport ID", ids, issues);
            ValidateVersion(transport.TransportVersion, $"{path}.transportVersion", issues);

            if (string.IsNullOrWhiteSpace(transport.TransportId))
            {
                issues.Add(new ValidationIssue { Path = $"{path}.transportId", Message = "Transport plugin ID is required." });
            }
            else
            {
                ValidateResourcePlugin(ResourcePluginKind.Transport, transport.TransportId, transport.TransportVersion, $"{path}.transportId", issues);
            }

            if (!string.IsNullOrWhiteSpace(transport.Channel) && !instrumentIds.Contains(transport.Channel))
            {
                issues.Add(new ValidationIssue { Path = $"{path}.channel", Message = $"Instrument channel '{transport.Channel}' was not found." });
            }
        }
    }

    private void ValidateServices(
        IReadOnlyList<TestServiceDefinition> services,
        IReadOnlyList<TransportDefinition> transports,
        ICollection<ValidationIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transportIds = transports.Select(transport => transport.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < services.Count; index++)
        {
            var service = services[index];
            var path = $"services[{index}]";
            ValidateId(service.Id, path, "Service ID", ids, issues);
            ValidateVersion(service.ServiceVersion, $"{path}.serviceVersion", issues);

            if (string.IsNullOrWhiteSpace(service.ServiceId))
            {
                issues.Add(new ValidationIssue { Path = $"{path}.serviceId", Message = "Service plugin ID is required." });
            }
            else
            {
                ValidateResourcePlugin(ResourcePluginKind.Service, service.ServiceId, service.ServiceVersion, $"{path}.serviceId", issues);
            }

            if (!string.IsNullOrWhiteSpace(service.Transport) && !transportIds.Contains(service.Transport))
            {
                issues.Add(new ValidationIssue { Path = $"{path}.transport", Message = $"Transport '{service.Transport}' was not found." });
            }
        }
    }

    /// <summary>
    /// Reports a resource plugin reference that cannot be resolved as an error, and one satisfied by
    /// a newer compatible version as a warning, so the operator learns about the substitution
    /// without the sequence being refused.
    /// </summary>
    private void ValidateResourcePlugin(
        ResourcePluginKind kind,
        string pluginId,
        string? version,
        string path,
        ICollection<ValidationIssue> issues)
    {
        if (_resourcePlugins is null)
        {
            return;
        }

        if (!_resourcePlugins.TryResolve(kind, pluginId, version, out var match, out var resolvedVersion))
        {
            issues.Add(new ValidationIssue { Path = path, Message = _resourcePlugins.DescribeMissing(kind, pluginId, version) });
        }
        else if (match == PluginVersionMatch.Compatible)
        {
            issues.Add(new ValidationIssue
            {
                Path = path,
                Severity = ValidationSeverity.Warning,
                Message = $"'{pluginId}' version '{version}' is not installed; version '{resolvedVersion}' will be used instead."
            });
        }
    }

    private static void ValidateId(
        string id,
        string path,
        string displayName,
        ISet<string> ids,
        ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.id", Message = $"{displayName} is required." });
        }
        else if (!ids.Add(id))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.id", Message = $"{displayName} is duplicated." });
        }
    }

    private void ValidateSteps(
        IReadOnlyList<TestStepDefinition> steps,
        string path,
        ISet<string> stepIds,
        ICollection<ValidationIssue> issues)
    {
        for (var stepIndex = 0; stepIndex < steps.Count; stepIndex++)
        {
            var step = steps[stepIndex];
            var stepPath = $"{path}[{stepIndex}]";

            ValidateId(step.Id, stepPath, "Test step ID", stepIds, issues);
            ValidateVersion(step.PluginVersion, $"{stepPath}.pluginVersion", issues);
            if (step.TimeoutMs is <= 0)
            {
                issues.Add(new ValidationIssue { Path = $"{stepPath}.timeoutMs", Message = "Timeout must be positive or omitted." });
            }

            if (string.IsNullOrWhiteSpace(step.Name))
            {
                issues.Add(new ValidationIssue { Path = $"{stepPath}.name", Message = "Test step name is required." });
            }

            if (string.IsNullOrWhiteSpace(step.PluginId))
            {
                issues.Add(new ValidationIssue { Path = $"{stepPath}.pluginId", Message = "Test step plugin ID is required." });
            }
            else if (_pluginRegistry is not null)
            {
                if (!_pluginRegistry.TryResolve(step.PluginId, step.PluginVersion, out var resolution))
                {
                    issues.Add(new ValidationIssue { Path = $"{stepPath}.pluginId", Message = _pluginRegistry.DescribeMissing(step.PluginId, step.PluginVersion) });
                }
                else if (resolution.IsSubstituted)
                {
                    issues.Add(new ValidationIssue
                    {
                        Path = $"{stepPath}.pluginVersion",
                        Severity = ValidationSeverity.Warning,
                        Message = $"Test step plugin '{step.PluginId}' version '{step.PluginVersion}' is not installed; version '{resolution.ResolvedVersion}' will run instead."
                    });
                }
            }

            ValidateVariableWrites(step.VariableWrites, $"{stepPath}.variableWrites", issues);
        }
    }

    private static void ValidateVersion(string? version, string path, ICollection<ValidationIssue> issues)
    {
        if (!Version.TryParse(version, out _))
        {
            issues.Add(new ValidationIssue { Path = path, Message = "A valid plugin version is required." });
        }
    }

    private static void ValidateVariableWrites(
        IReadOnlyList<VariableWriteDefinition> writes,
        string path,
        ICollection<ValidationIssue> issues)
    {
        for (var index = 0; index < writes.Count; index++)
        {
            var write = writes[index];
            var writePath = $"{path}[{index}]";

            if (string.IsNullOrWhiteSpace(write.Name))
            {
                issues.Add(new ValidationIssue { Path = $"{writePath}.name", Message = "Variable write name is required." });
            }

            if (string.IsNullOrWhiteSpace(write.OutputKey) && write.Value is null)
            {
                issues.Add(new ValidationIssue { Path = writePath, Message = "Variable write requires outputKey or value." });
            }
        }
    }
}
