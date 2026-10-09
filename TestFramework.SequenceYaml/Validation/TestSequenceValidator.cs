using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Expressions;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;

namespace TestFramework.SequenceYaml.Validation;

public sealed class TestSequenceValidator
{
    private readonly IPluginRegistry? _pluginRegistry;
    private readonly IResourcePluginCatalog? _resourcePlugins;
    private readonly StationConfiguration? _station;
    private readonly ISequenceResolver? _sequences;
    private readonly StationConfiguration? _line;

    /// <param name="sequences">
    /// Resolves the sequences calls name, so a call to a file that does not exist - or with a
    /// parameter its target does not declare - is reported before the run. Without it, calls are
    /// checked only for what is visible in this file.
    /// </param>
    /// <param name="line">
    /// The resources the station's line shares, which satisfy a requirement the station itself
    /// does not bind and are valid targets of a step's <c>exclusive</c>.
    /// </param>
    public TestSequenceValidator(
        IPluginRegistry? pluginRegistry = null,
        IResourcePluginCatalog? resourcePlugins = null,
        StationConfiguration? station = null,
        ISequenceResolver? sequences = null,
        StationConfiguration? line = null)
    {
        _pluginRegistry = pluginRegistry;
        _resourcePlugins = resourcePlugins;
        _station = station;
        _sequences = sequences;
        _line = line;
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

        ValidateRequirements(sequence.Requires, issues);
        ValidateInstruments(sequence.Instruments, issues);
        ValidateTransports(sequence.Transports, sequence.Instruments, issues);
        ValidateServices(sequence.Services, sequence.Transports, issues);
        ValidateItems(sequence.Items, VariableValue.KnownIn(sequence), ResourceAliasesOf(sequence), issues);

        return issues;
    }

    /// <summary>
    /// Every alias a step could name in <c>exclusive</c>: what the sequence requires or opens
    /// itself, plus what the station and its line bind when they are known here.
    /// </summary>
    private HashSet<string> ResourceAliasesOf(TestSequence sequence)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        aliases.UnionWith(sequence.Requires.Select(requirement => requirement.Alias));
        aliases.UnionWith(sequence.Instruments.Select(instrument => instrument.Id));
        aliases.UnionWith(sequence.Transports.Select(transport => transport.Id));
        aliases.UnionWith(sequence.Services.Select(service => service.Id));
        aliases.UnionWith(_station?.Resources.Select(binding => binding.Alias) ?? []);
        aliases.UnionWith(_line?.Resources.Select(binding => binding.Alias) ?? []);
        aliases.Remove(string.Empty);
        return aliases;
    }

    /// <summary>
    /// Walks the items in execution order, carrying the set of variables that exist by the time
    /// each step runs.
    ///
    /// The set grows: a step's <c>variableWrites</c> defines a variable for everything after it.
    /// Seeding it from <c>sequence.Variables</c> and never extending it reported the sequence this
    /// feature exists for - measure in one step, use <c>${measured}</c> in the next - as referencing
    /// an undefined variable, an error severe enough to stop a run that would in fact have worked.
    ///
    /// It accumulates across items, not per item, because variables are sequence-scoped: an item
    /// reading what an earlier item wrote is legitimate. A reference to a variable written only by
    /// a <i>later</i> step stays an error, which is why a step's own writes are added after its
    /// parameters have been checked.
    /// </summary>
    private void ValidateItems(
        IReadOnlyList<TestItemDefinition> items,
        IReadOnlyDictionary<string, object?> knownVariables,
        ISet<string> resourceAliases,
        ICollection<ValidationIssue> issues)
    {
        var defined = new Dictionary<string, object?>(knownVariables, StringComparer.OrdinalIgnoreCase);
        var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateItemList(items, "items", defined, itemIds, resourceAliases, issues);
    }

    /// <summary>
    /// Items in execution order, a group's children in place: the tree runs depth first, so that is
    /// the order variables become defined in. Ids are unique across the whole tree, because a
    /// result names an item by its id and a report cannot tell two of the same apart.
    /// </summary>
    private void ValidateItemList(
        IReadOnlyList<TestItemDefinition> items,
        string listPath,
        Dictionary<string, object?> defined,
        ISet<string> itemIds,
        ISet<string> resourceAliases,
        ICollection<ValidationIssue> issues)
    {
        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            ValidateItem(items[itemIndex], $"{listPath}[{itemIndex}]", defined, itemIds, resourceAliases, issues);
        }
    }

    private void ValidateItem(
        TestItemDefinition item,
        string itemPath,
        Dictionary<string, object?> defined,
        ISet<string> itemIds,
        ISet<string> resourceAliases,
        ICollection<ValidationIssue> issues)
    {
        ValidateId(item.Id, itemPath, "Test item ID", itemIds, issues);

        if (string.IsNullOrWhiteSpace(item.Name))
        {
            issues.Add(new ValidationIssue { Path = $"{itemPath}.name", Message = "Test item name is required." });
        }

        ValidateExpression(item.RunIf, $"{itemPath}.runIf", "runIf", defined, issues);
        if (item.Loop is { } loop)
        {
            ValidateLoop(loop, $"{itemPath}.loop", defined, issues);
        }

        if (item.IsGroup || item.IsCall)
        {
            ValidateContainer(item, itemPath, defined, itemIds, resourceAliases, issues);
        }
        else
        {
            ValidateTestItem(item, itemPath, defined, resourceAliases, issues);
        }

        // Last: an until is evaluated after the attempt, when everything the item wrote exists.
        if (item.Retry is { } retry)
        {
            ValidateRetry(retry, $"{itemPath}.retry", defined, issues);
        }
    }

    private void ValidateTestItem(
        TestItemDefinition item,
        string itemPath,
        Dictionary<string, object?> defined,
        ISet<string> resourceAliases,
        ICollection<ValidationIssue> issues)
    {
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

        var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateSteps(item.InitSteps, $"{itemPath}.init", stepIds, defined, resourceAliases, issues);
        ValidateSteps(item.MainSteps, $"{itemPath}.main", stepIds, defined, resourceAliases, issues);
        ValidateSteps(item.CleanupSteps, $"{itemPath}.cleanup", stepIds, defined, resourceAliases, issues);

        // After the item's steps: the runner judges the item once they have all run, so a limit
        // may reference a variable one of them writes.
        ValidateVerdictSource(item.VerdictSource, $"{itemPath}.verdictSource", defined, issues);
        ValidateChecks(item, itemPath, defined, issues);
    }

    /// <summary>
    /// A group or a call. Either has no steps and no judgments of its own - its verdict is its
    /// children's - so a step or a check on one is a mistake the runner would silently ignore.
    /// </summary>
    private void ValidateContainer(
        TestItemDefinition item,
        string itemPath,
        Dictionary<string, object?> defined,
        ISet<string> itemIds,
        ISet<string> resourceAliases,
        ICollection<ValidationIssue> issues)
    {
        var kind = item.IsCall ? "call" : "group";
        if (item.IsGroup && item.IsCall)
        {
            issues.Add(new ValidationIssue { Path = itemPath, Message = "An item is either a group (items) or a call, not both." });
        }

        if (item.InitSteps.Count + item.MainSteps.Count + item.CleanupSteps.Count > 0)
        {
            issues.Add(new ValidationIssue { Path = itemPath, Message = $"A {kind} has no steps of its own; put them in a test item inside it." });
        }

        if (item.Checks.Count > 0 || !string.IsNullOrWhiteSpace(item.VerdictSource.StepId))
        {
            issues.Add(new ValidationIssue { Path = itemPath, Message = $"A {kind} is judged by its children; it has no verdict source or checks." });
        }

        if (item.IsGroup)
        {
            ValidateItemList(item.Items, $"{itemPath}.items", defined, itemIds, resourceAliases, issues);
        }

        if (item.Call is { } call)
        {
            ValidateCall(call, $"{itemPath}.call", defined, issues);
        }
    }

    /// <summary>
    /// A call's path and parameters. With a resolver, also that the sequence exists and that every
    /// parameter is one of its variables - a parameter it does not declare is set and never read,
    /// which is what a typo in its name looks like. Nothing the callee writes is visible afterwards,
    /// so it defines nothing in the caller.
    /// </summary>
    private void ValidateCall(
        SequenceCallDefinition call,
        string path,
        IReadOnlyDictionary<string, object?> defined,
        ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(call.Path))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.path", Message = "A call needs the path of the sequence it runs." });
            return;
        }

        foreach (var (name, value) in call.Parameters)
        {
            foreach (var reference in VariableReference.NamesIn(value).Where(reference => !defined.ContainsKey(reference)))
            {
                issues.Add(new ValidationIssue
                {
                    Path = $"{path}.parameters.{name}",
                    Message = $"Call parameter '{name}' references variable '{reference}', which is not defined."
                });
            }
        }

        if (_sequences is null)
        {
            return;
        }

        TestSequence callee;
        try
        {
            callee = _sequences.Resolve(call.Path);
        }
        catch (Exception ex)
        {
            issues.Add(new ValidationIssue { Path = $"{path}.path", Message = $"Called sequence '{call.Path}' cannot be loaded: {ex.Message}" });
            return;
        }

        foreach (var name in call.Parameters.Keys.Where(name => !callee.Variables.ContainsKey(name)))
        {
            issues.Add(new ValidationIssue
            {
                Path = $"{path}.parameters.{name}",
                Severity = ValidationSeverity.Warning,
                Message = $"Called sequence '{call.Path}' has no variable '{name}'; the parameter is set and never read."
            });
        }
    }

    /// <summary>
    /// A loop's count must parse, and its index variable must be a name a <c>${}</c> can reach.
    /// The index is defined from here on, as a whole number - which is what lets a step's Integer
    /// parameter take <c>${loopIndex}</c> without a type warning.
    /// </summary>
    private static void ValidateLoop(
        LoopDefinition loop,
        string path,
        Dictionary<string, object?> defined,
        ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(loop.Count))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.count", Message = "A loop needs a count." });
        }
        else
        {
            ValidateExpression(loop.Count, $"{path}.count", "loop count", defined, issues);
        }

        if (!VariableReference.IsWholeValueReference($"${{{loop.Variable}}}"))
        {
            issues.Add(new ValidationIssue
            {
                Path = $"{path}.variable",
                Message = $"Loop variable '{loop.Variable}' is not a valid variable name; it starts with a letter or underscore and continues with letters, digits, '_', '.' or '-'."
            });
            return;
        }

        defined[loop.Variable] = 0;
    }

    private static void ValidateRetry(
        RetryDefinition retry,
        string path,
        IReadOnlyDictionary<string, object?> defined,
        ICollection<ValidationIssue> issues)
    {
        if (retry.MaxAttempts < 1)
        {
            issues.Add(new ValidationIssue { Path = $"{path}.maxAttempts", Message = "maxAttempts counts the first attempt too, so it is at least 1." });
        }

        if (retry.IntervalMs < 0)
        {
            issues.Add(new ValidationIssue { Path = $"{path}.intervalMs", Message = "intervalMs cannot be negative." });
        }

        if (retry.MaxAttempts == 1 && string.IsNullOrWhiteSpace(retry.Until))
        {
            issues.Add(new ValidationIssue
            {
                Path = $"{path}.maxAttempts",
                Severity = ValidationSeverity.Warning,
                Message = "maxAttempts is 1, so this retry never runs anything again."
            });
        }

        ValidateExpression(retry.Until, $"{path}.until", "until", defined, issues);
    }

    /// <summary>
    /// An expression must parse and read only variables that exist where it is evaluated. Null or
    /// blank is fine - every expression in a sequence is optional.
    /// </summary>
    private static void ValidateExpression(
        string? text,
        string path,
        string field,
        IReadOnlyDictionary<string, object?> defined,
        ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!SequenceExpression.TryParse(text, out var expression, out var error))
        {
            issues.Add(new ValidationIssue { Path = path, Message = $"{field} is not a valid expression: {error}" });
            return;
        }

        foreach (var name in expression!.VariableNames.Where(name => !defined.ContainsKey(name)))
        {
            issues.Add(new ValidationIssue { Path = path, Message = $"{field} references variable '{name}', which is not defined." });
        }
    }

    /// <summary>
    /// Each check is held to the rules of the verdict source, plus what only a check needs: an
    /// output key, since it always judges an output, and a name its records do not share with
    /// another judgment of the same item - two judgments both called <c>value</c> would make a
    /// report unable to tell their records apart.
    /// </summary>
    private static void ValidateChecks(
        TestItemDefinition item,
        string itemPath,
        IReadOnlyDictionary<string, object?> definedVariables,
        ICollection<ValidationIssue> issues)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(item.VerdictSource.OutputKey))
        {
            names.Add(JudgmentName(item.VerdictSource));
        }

        for (var index = 0; index < item.Checks.Count; index++)
        {
            var check = item.Checks[index];
            var path = $"{itemPath}.checks[{index}]";

            if (string.IsNullOrWhiteSpace(check.StepId))
            {
                issues.Add(new ValidationIssue { Path = $"{path}.stepId", Message = "Check step is required." });
            }
            else if (item.MainSteps.All(step => !string.Equals(step.Id, check.StepId, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new ValidationIssue { Path = $"{path}.stepId", Message = "Check step must be in main steps." });
            }

            if (string.IsNullOrWhiteSpace(check.OutputKey))
            {
                // Numeric and string judgments already report this through ValidateVerdictSource.
                if (check.JudgeType == VerdictJudgeType.PassFail)
                {
                    issues.Add(new ValidationIssue { Path = $"{path}.outputKey", Message = "A check requires an output key." });
                }
            }
            else if (!names.Add(JudgmentName(check)))
            {
                issues.Add(new ValidationIssue
                {
                    Path = $"{path}.name",
                    Severity = ValidationSeverity.Warning,
                    Message = $"Another judgment of this item is also called '{JudgmentName(check)}'; give the check a name so their records can be told apart."
                });
            }

            ValidateVerdictSource(check, path, definedVariables, issues);
        }
    }

    private static string JudgmentName(VerdictSource source) =>
        string.IsNullOrWhiteSpace(source.Name) ? source.OutputKey?.Trim() ?? string.Empty : source.Name.Trim();

    private static void ValidateVerdictSource(
        VerdictSource source,
        string path,
        IReadOnlyDictionary<string, object?> definedVariables,
        ICollection<ValidationIssue> issues)
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

        if (source.JudgeType == VerdictJudgeType.Numeric)
        {
            ValidateNumericBounds(source, path, definedVariables, issues);
        }

        if (source.JudgeType == VerdictJudgeType.String &&
            string.IsNullOrEmpty(source.ExpectedString))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.expectedString", Message = "String verdict requires expectedString." });
        }
    }

    /// <summary>
    /// Checks that a numeric judgment has exactly the bounds its comparison reads, by the same
    /// <see cref="NumericComparisonRules"/> the runner uses.
    ///
    /// A bound the comparison does not read is a warning, not an error: it does no harm, but it is
    /// usually left over from switching comparison - an <c>EQ</c> still carrying the range it had
    /// as <c>GELE</c> - and whoever reads the file will take it as part of the test.
    /// </summary>
    private static void ValidateNumericBounds(
        VerdictSource source,
        string path,
        IReadOnlyDictionary<string, object?> definedVariables,
        ICollection<ValidationIssue> issues)
    {
        var comparison = source.Comparison;
        if (!Enum.IsDefined(comparison))
        {
            issues.Add(new ValidationIssue { Path = $"{path}.comparison", Message = $"Unknown numeric comparison '{(int)comparison}'." });
            return;
        }

        var hasLower = source.LowerLimit.HasValue || !string.IsNullOrWhiteSpace(source.LowerLimitReference);
        var hasUpper = source.UpperLimit.HasValue || !string.IsNullOrWhiteSpace(source.UpperLimitReference);
        var hasExpected = source.Expected.HasValue || !string.IsNullOrWhiteSpace(source.ExpectedReference);

        if (comparison == NumericComparison.GELE)
        {
            if (!hasLower && !hasUpper)
            {
                issues.Add(new ValidationIssue { Path = path, Message = "Numeric verdict requires lowerLimit or upperLimit." });
            }
        }
        else
        {
            if (comparison.UsesLowerLimit() && !hasLower)
            {
                issues.Add(new ValidationIssue { Path = $"{path}.lowerLimit", Message = $"Numeric comparison {comparison} requires lowerLimit." });
            }

            if (comparison.UsesUpperLimit() && !hasUpper)
            {
                issues.Add(new ValidationIssue { Path = $"{path}.upperLimit", Message = $"Numeric comparison {comparison} requires upperLimit." });
            }

            if (comparison.UsesExpected() && !hasExpected)
            {
                issues.Add(new ValidationIssue { Path = $"{path}.expected", Message = $"Numeric comparison {comparison} requires expected." });
            }
        }

        WarnUnused(comparison.UsesLowerLimit(), hasLower, $"{path}.lowerLimit", "lowerLimit", comparison, issues);
        WarnUnused(comparison.UsesUpperLimit(), hasUpper, $"{path}.upperLimit", "upperLimit", comparison, issues);
        WarnUnused(comparison.UsesExpected(), hasExpected, $"{path}.expected", "expected", comparison, issues);

        var lower = comparison.UsesLowerLimit()
            ? ValidateLimit(source.LowerLimit, source.LowerLimitReference, $"{path}.lowerLimit", "lowerLimit", definedVariables, issues)
            : null;
        var upper = comparison.UsesUpperLimit()
            ? ValidateLimit(source.UpperLimit, source.UpperLimitReference, $"{path}.upperLimit", "upperLimit", definedVariables, issues)
            : null;
        var expected = comparison.UsesExpected()
            ? ValidateLimit(source.Expected, source.ExpectedReference, $"{path}.expected", "expected", definedVariables, issues)
            : null;

        if (lower > upper)
        {
            issues.Add(new ValidationIssue { Path = $"{path}.lowerLimit", Message = "Numeric lowerLimit must be less than or equal to upperLimit." });
        }

        // EQ is exact. A fractional expected value is almost always a reading, which will not land
        // on it exactly after any conversion or instrument rounding; a tolerance band is what was meant.
        if (comparison.UsesExpected() && expected is { } value && value != Math.Floor(value))
        {
            issues.Add(new ValidationIssue
            {
                Path = $"{path}.expected",
                Severity = ValidationSeverity.Warning,
                Message = $"{comparison} compares exactly, and {value} is not a whole number; a measured value rarely lands on it. Use GELE with a tolerance band instead."
            });
        }
    }

    private static void WarnUnused(
        bool used,
        bool present,
        string path,
        string field,
        NumericComparison comparison,
        ICollection<ValidationIssue> issues)
    {
        if (!used && present)
        {
            issues.Add(new ValidationIssue
            {
                Path = path,
                Severity = ValidationSeverity.Warning,
                Message = $"Numeric comparison {comparison} does not use {field}; it is ignored."
            });
        }
    }

    /// <summary>
    /// Checks one numeric limit and returns its value when that is knowable before the run, so the
    /// caller can compare the two bounds.
    ///
    /// A reference is held to what the runner will do with it: it must be one whole
    /// <c>${variable}</c>, the variable must exist by the time the item is judged, and when its value
    /// is written in the file it must be a number. A variable some step writes has no knowable value
    /// and passes - what a plugin output carries is the plugin's business.
    /// </summary>
    private static double? ValidateLimit(
        double? literal,
        string? reference,
        string path,
        string field,
        IReadOnlyDictionary<string, object?> definedVariables,
        ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            if (literal is { } number && !double.IsFinite(number))
            {
                issues.Add(new ValidationIssue { Path = path, Message = $"Numeric {field} must be finite." });
                return null;
            }

            return literal;
        }

        if (literal.HasValue)
        {
            issues.Add(new ValidationIssue { Path = path, Message = $"Numeric {field} has both a value and a variable reference; keep one." });
        }

        if (!VariableReference.IsWholeValueReference(reference))
        {
            issues.Add(new ValidationIssue
            {
                Path = path,
                Message = $"Numeric {field} must be a number or a single ${{variable}} reference, but is '{reference}'."
            });
            return null;
        }

        var name = VariableReference.NamesIn(reference)[0];
        if (!definedVariables.TryGetValue(name, out var known))
        {
            issues.Add(new ValidationIssue { Path = path, Message = $"Numeric {field} references variable '{name}', which is not defined." });
            return null;
        }

        if (known is null)
        {
            return null;
        }

        if (!NumericValue.TryRead(known, out var value))
        {
            issues.Add(new ValidationIssue
            {
                Path = path,
                Message = $"Numeric {field} references variable '{name}', which holds '{known}' rather than a number."
            });
            return null;
        }

        return value;
    }

    /// <summary>
    /// Checks that every resource the sequence requires is bound on the configured station, and
    /// that whatever the station binds it to is a driver that exists.
    ///
    /// Binding failures belong here rather than in the runner: "this station has no supply on psu"
    /// is knowable the moment the sequence is opened, and finding it out mid-run means finding it
    /// out with a DUT already connected.
    ///
    /// With no station configured the binding check is skipped, because an editor validating a
    /// sequence it is about to send elsewhere cannot answer it - but the requirements themselves
    /// are still checked for being well-formed.
    /// </summary>
    private void ValidateRequirements(
        IReadOnlyList<ResourceRequirement> requirements,
        ICollection<ValidationIssue> issues)
    {
        if (requirements.Count == 0)
        {
            return;
        }

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < requirements.Count; index++)
        {
            var requirement = requirements[index];
            var path = $"requires[{index}]";

            if (!Enum.IsDefined(requirement.Kind))
            {
                issues.Add(new ValidationIssue { Path = $"{path}.kind", Message = $"Unknown resource kind '{(int)requirement.Kind}'." });
            }

            if (!string.IsNullOrWhiteSpace(requirement.Alias))
            {
                paths.TryAdd(requirement.Alias, path);
            }
        }

        // With a station configured, an alias it does not bind means this sequence cannot run here:
        // an error. With no station configured at all the host simply cannot answer the question -
        // an editor preparing a sequence for another bench is the ordinary case - so the same
        // finding is a warning, and the sequence is not refused over the host's own configuration.
        var severity = _station is null ? ValidationSeverity.Warning : ValidationSeverity.Error;

        foreach (var problem in StationBinding.Check(requirements, _station, _line))
        {
            issues.Add(new ValidationIssue
            {
                Path = paths.TryGetValue(problem.Alias, out var path) ? $"{path}.alias" : "requires",
                Severity = string.IsNullOrWhiteSpace(problem.Alias) ? ValidationSeverity.Error : severity,
                Message = problem.Message
            });
        }

        ValidateStationDrivers(requirements, issues, paths);
    }

    /// <summary>
    /// The station may bind an alias to a driver that is not installed on this host, which binding
    /// alone cannot see - it compares the sequence to the configuration, not to the plugin set.
    /// </summary>
    private void ValidateStationDrivers(
        IReadOnlyList<ResourceRequirement> requirements,
        ICollection<ValidationIssue> issues,
        IReadOnlyDictionary<string, string> paths)
    {
        if (_resourcePlugins is null || _station is null)
        {
            return;
        }

        foreach (var requirement in requirements)
        {
            var binding = _station.Find(requirement.Alias);
            if (binding is null || string.IsNullOrWhiteSpace(binding.DriverId) || binding.Kind != requirement.Kind)
            {
                continue;
            }

            var path = paths.TryGetValue(requirement.Alias, out var found) ? $"{found}.alias" : "requires";
            ValidateResourcePlugin(binding.Kind, binding.DriverId, binding.DriverVersion, path, issues);
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

    /// <summary>
    /// Validates a section's steps in order, extending <paramref name="definedVariables"/> with each
    /// step's <c>variableWrites</c> once that step's own parameters have been checked.
    /// </summary>
    private void ValidateSteps(
        IReadOnlyList<TestStepDefinition> steps,
        string path,
        ISet<string> stepIds,
        Dictionary<string, object?> definedVariables,
        ISet<string> resourceAliases,
        ICollection<ValidationIssue> issues)
    {
        for (var stepIndex = 0; stepIndex < steps.Count; stepIndex++)
        {
            var step = steps[stepIndex];
            var stepPath = $"{path}[{stepIndex}]";

            ValidateId(step.Id, stepPath, "Test step ID", stepIds, issues);
            ValidateExclusive(step.Exclusive, $"{stepPath}.exclusive", resourceAliases, issues);
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
                else
                {
                    if (resolution.IsSubstituted)
                    {
                        issues.Add(new ValidationIssue
                        {
                            Path = $"{stepPath}.pluginVersion",
                            Severity = ValidationSeverity.Warning,
                            Message = $"Test step plugin '{step.PluginId}' version '{step.PluginVersion}' is not installed; version '{resolution.ResolvedVersion}' will run instead."
                        });
                    }

                    // Against the version that will actually run, not the one the file asks for:
                    // a substituted 1.2 may declare a parameter the pinned 1.0 did not.
                    ValidateParameters(resolution.Plugin, step, stepPath, definedVariables, issues);
                }
            }

            ValidateExpression(step.RunIf, $"{stepPath}.runIf", "runIf", definedVariables, issues);
            ValidateVariableWrites(step.VariableWrites, $"{stepPath}.variableWrites", issues);

            // After this step's own parameters: a step cannot reference the variable it is about to
            // write, because the write happens once it has finished executing.
            // Defined from here on, but with no knowable type: what a plugin output carries is
            // the plugin's business, and a literal write may or may not execute.
            foreach (var write in step.VariableWrites.Where(write => !string.IsNullOrWhiteSpace(write.Name)))
            {
                definedVariables[write.Name] = null;
            }

            // After the writes: a poll's until reads what this very step just wrote.
            if (step.Retry is { } retry)
            {
                ValidateRetry(retry, $"{stepPath}.retry", definedVariables, issues);
            }
        }
    }

    /// <summary>
    /// Checks the step's parameter values against what its plugin declares.
    ///
    /// This is the point of declaring parameters at all: without it a wrong type or an out-of-range
    /// value passes validation, is saved, is reviewed, and fails part-way through a run - after the
    /// steps before it have already touched the DUT.
    ///
    /// A plugin that declares nothing is left alone, so this cannot make a previously valid sequence
    /// invalid; a plugin whose declaration throws is treated the same way, because a broken
    /// declaration is the plugin's bug and must not make the operator's sequence unusable.
    /// </summary>
    private static void ValidateParameters(
        ITestStepPlugin plugin,
        TestStepDefinition step,
        string stepPath,
        IReadOnlyDictionary<string, object?> definedVariables,
        ICollection<ValidationIssue> issues)
    {
        IReadOnlyList<StepParameterDescriptor> declared;
        try
        {
            declared = plugin.Parameters;
        }
        catch (Exception ex)
        {
            issues.Add(new ValidationIssue
            {
                Path = $"{stepPath}.pluginId",
                Severity = ValidationSeverity.Warning,
                Message = $"Test step plugin '{step.PluginId}' failed to describe its parameters: {ex.Message}"
            });
            return;
        }

        if (declared.Count == 0)
        {
            return;
        }

        foreach (var problem in StepParameterCheck.Check(declared, step.Parameters, definedVariables))
        {
            issues.Add(new ValidationIssue
            {
                Path = $"{stepPath}.parameters.{problem.ParameterName}",
                Severity = problem.IsWarning ? ValidationSeverity.Warning : ValidationSeverity.Error,
                Message = problem.Message
            });
        }
    }

    /// <summary>
    /// An <c>exclusive</c> alias must name a resource. At run time an alias nothing provides is a
    /// step error; here it is a warning, because the line's resources are often not known to the
    /// validator and a lease on them is exactly what the feature is for.
    /// </summary>
    private static void ValidateExclusive(
        IReadOnlyList<string> exclusive,
        string path,
        ISet<string> resourceAliases,
        ICollection<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < exclusive.Count; index++)
        {
            var alias = exclusive[index];
            var aliasPath = $"{path}[{index}]";
            if (string.IsNullOrWhiteSpace(alias))
            {
                issues.Add(new ValidationIssue { Path = aliasPath, Message = "An exclusive resource alias cannot be empty." });
                continue;
            }

            if (!seen.Add(alias.Trim()))
            {
                issues.Add(new ValidationIssue { Path = aliasPath, Severity = ValidationSeverity.Warning, Message = $"Exclusive alias '{alias}' is listed twice." });
                continue;
            }

            if (!resourceAliases.Contains(alias.Trim()))
            {
                issues.Add(new ValidationIssue
                {
                    Path = aliasPath,
                    Severity = ValidationSeverity.Warning,
                    Message = $"Exclusive alias '{alias}' is not a resource this sequence declares or the station binds; unless the line provides it, the step will fail."
                });
            }
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
