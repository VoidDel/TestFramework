namespace TestFramework.Abstractions.Models;

/// <summary>Why a requirement could not be bound to anything on the station.</summary>
public sealed class StationBindingProblem
{
    public required string Alias { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// Matches a sequence's <see cref="ResourceRequirement"/>s against a station's bindings.
///
/// One implementation, used by validation before a run and by the resource builder during one, so
/// that "this sequence can run here" means the same thing in both. If the two could disagree, the
/// disagreement would surface as a sequence that validates on the operator's screen and fails when
/// the line actually runs it, which is the failure this split exists to remove.
/// </summary>
public static class StationBinding
{
    /// <summary>
    /// The problems binding <paramref name="requirements"/> against <paramref name="station"/>.
    /// Empty means every requirement has somewhere to go. A null station means no station is
    /// configured, which is only a problem if the sequence requires something.
    /// </summary>
    public static IReadOnlyList<StationBindingProblem> Check(
        IReadOnlyList<ResourceRequirement> requirements,
        StationConfiguration? station)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        if (requirements.Count == 0)
        {
            return [];
        }

        var problems = new List<StationBindingProblem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requirement in requirements)
        {
            if (string.IsNullOrWhiteSpace(requirement.Alias))
            {
                problems.Add(new StationBindingProblem
                {
                    Alias = string.Empty,
                    Message = "A resource requirement needs an alias."
                });
                continue;
            }

            if (!seen.Add(requirement.Alias))
            {
                problems.Add(new StationBindingProblem
                {
                    Alias = requirement.Alias,
                    Message = $"Resource alias '{requirement.Alias}' is required more than once."
                });
                continue;
            }

            var binding = station?.Find(requirement.Alias);
            if (binding is null)
            {
                var purpose = string.IsNullOrWhiteSpace(requirement.Description)
                    ? string.Empty
                    : $" ({requirement.Description})";
                problems.Add(new StationBindingProblem
                {
                    Alias = requirement.Alias,
                    Message = station is null
                        ? $"This sequence needs resource '{requirement.Alias}'{purpose}, but no station is configured."
                        : $"Station '{StationName(station)}' has nothing bound to resource '{requirement.Alias}'{purpose}."
                });
                continue;
            }

            CheckBinding(requirement, binding, station!, problems);
        }

        return problems;
    }

    private static void CheckBinding(
        ResourceRequirement requirement,
        StationResourceBinding binding,
        StationConfiguration station,
        ICollection<StationBindingProblem> problems)
    {
        if (binding.Kind != requirement.Kind)
        {
            problems.Add(new StationBindingProblem
            {
                Alias = requirement.Alias,
                Message = $"Resource '{requirement.Alias}' must be a {Describe(requirement.Kind)}, but station '{StationName(station)}' binds it to a {Describe(binding.Kind)}."
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(binding.DriverId))
        {
            problems.Add(new StationBindingProblem
            {
                Alias = requirement.Alias,
                Message = $"Station '{StationName(station)}' binds resource '{requirement.Alias}' to no driver."
            });
            return;
        }

        // A sequence that names a driver is asking for that driver's behaviour, not merely for
        // something of the same kind, so a different one is refused rather than substituted.
        if (!string.IsNullOrWhiteSpace(requirement.DriverId) &&
            !string.Equals(requirement.DriverId, binding.DriverId, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new StationBindingProblem
            {
                Alias = requirement.Alias,
                Message = $"Resource '{requirement.Alias}' requires driver '{requirement.DriverId}', but station '{StationName(station)}' binds it to '{binding.DriverId}'."
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(requirement.MinimumDriverVersion))
        {
            return;
        }

        if (!Version.TryParse(requirement.MinimumDriverVersion, out var minimum))
        {
            problems.Add(new StationBindingProblem
            {
                Alias = requirement.Alias,
                Message = $"Resource '{requirement.Alias}' declares an unparseable minimum driver version '{requirement.MinimumDriverVersion}'."
            });
            return;
        }

        if (!Version.TryParse(binding.DriverVersion, out var bound))
        {
            problems.Add(new StationBindingProblem
            {
                Alias = requirement.Alias,
                Message = $"Station '{StationName(station)}' binds resource '{requirement.Alias}' to an unparseable driver version '{binding.DriverVersion}'."
            });
            return;
        }

        if (bound < minimum)
        {
            problems.Add(new StationBindingProblem
            {
                Alias = requirement.Alias,
                Message = $"Resource '{requirement.Alias}' needs driver '{binding.DriverId}' {minimum} or newer, but station '{StationName(station)}' has {bound}."
            });
        }
    }

    private static string StationName(StationConfiguration station) =>
        string.IsNullOrWhiteSpace(station.Name) ? station.StationId : station.Name;

    private static string Describe(Resources.ResourcePluginKind kind) => kind switch
    {
        Resources.ResourcePluginKind.InstrumentDriver => "instrument",
        Resources.ResourcePluginKind.Transport => "transport",
        Resources.ResourcePluginKind.Service => "service",
        _ => kind.ToString()
    };
}
