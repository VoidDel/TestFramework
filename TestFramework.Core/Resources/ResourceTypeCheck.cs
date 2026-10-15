using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;

namespace TestFramework.Core.Resources;

/// <summary>
/// The two checks that keep a resource honest about what it is, both made when the scope is
/// built rather than at a step's first cast: a plugin's resource against the type the plugin
/// declares, and a run's resources against the classes the sequence requires.
/// </summary>
internal static class ResourceTypeCheck
{
    /// <summary>
    /// The resource a plugin created, refused when it is not the type the plugin declares. The
    /// declared type is what the validator checked a requirement's class against; a driver that
    /// returns something else would pass that check and fail the step that relied on it.
    /// </summary>
    public static object Created(object? resource, Type? declared, string pluginId, string alias)
    {
        if (resource is null)
        {
            throw new InvalidOperationException($"Resource plugin '{pluginId}' returned nothing for '{alias}'.");
        }

        if (declared is not null && !declared.IsInstanceOfType(resource))
        {
            throw new InvalidOperationException(
                $"Resource plugin '{pluginId}' declares that it creates '{declared.FullName}', but returned '{resource.GetType().FullName}' for '{alias}'.");
        }

        return resource;
    }

    /// <summary>
    /// Refuses a run whose resources are not of the classes its sequence requires. An alias the
    /// scope does not hold is left to the binding check that reports it, and to the step that
    /// would fail on it; this is about a resource that is there but is the wrong thing.
    /// </summary>
    public static void EnsureClasses(IEnumerable<ResourceRequirement> requirements, IResourceScope scope)
    {
        var problems = new List<string>();
        foreach (var requirement in requirements.Where(requirement => !string.IsNullOrWhiteSpace(requirement.Class)))
        {
            if (!TryFind(scope, requirement, out var resource) ||
                ResourceClasses.Implements(resource.GetType(), requirement.Class!))
            {
                continue;
            }

            var classes = ResourceClasses.Of(resource.GetType());
            problems.Add(
                $"'{requirement.Alias}' must be a {requirement.Class!.Trim()}, but is '{resource.GetType().FullName}'" +
                (classes.Count == 0 ? ", which implements no resource class." : $", which is {string.Join(", ", classes)}."));
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The station's resources do not match what the sequence requires: " + string.Join(" ", problems));
        }
    }

    private static bool TryFind(IResourceScope scope, ResourceRequirement requirement, out object resource) =>
        requirement.Kind switch
        {
            ResourcePluginKind.InstrumentDriver => scope.Instruments.TryGet(requirement.Alias, out resource),
            ResourcePluginKind.Transport => scope.Transports.TryGet(requirement.Alias, out resource),
            ResourcePluginKind.Service => scope.Services.TryGet(requirement.Alias, out resource),
            _ => Missing(out resource)
        };

    private static bool Missing(out object resource)
    {
        resource = null!;
        return false;
    }
}
