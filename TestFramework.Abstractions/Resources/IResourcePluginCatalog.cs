using TestFramework.Abstractions.Plugins;

namespace TestFramework.Abstractions.Resources;

/// <summary>The three kinds of resource plugin a sequence can reference.</summary>
public enum ResourcePluginKind
{
    InstrumentDriver,
    Transport,
    Service
}

/// <summary>
/// Read-only lookup over the registered resource plugins. It lets validation report a missing or
/// substituted instrument driver, transport or service before a run starts, instead of surfacing it
/// as a runtime failure part-way through a sequence, without making the validation layer depend on
/// the concrete registry.
///
/// One method per kind would have to grow every time a resource kind is added; the kind is a
/// parameter so that stays a non-breaking change.
/// </summary>
public interface IResourcePluginCatalog
{
    bool TryResolve(
        ResourcePluginKind kind,
        string pluginId,
        string? version,
        out PluginVersionMatch match,
        out Version resolvedVersion);

    /// <summary>
    /// Explains why a reference cannot be resolved, naming the installed versions so a version
    /// mismatch is distinguishable from a plugin that was never installed.
    /// </summary>
    string DescribeMissing(ResourcePluginKind kind, string pluginId, string? version);

    /// <summary>
    /// The type the plugin a reference resolves to declares it creates - its <c>InstrumentType</c>,
    /// <c>TransportType</c> or <c>ServiceType</c> - so the validator can check a requirement's
    /// <see cref="Models.ResourceRequirement.Class"/> against it. Null when the reference does not
    /// resolve, or for a catalog that cannot say. Added in contract 1.2.
    /// </summary>
    Type? GetResourceType(ResourcePluginKind kind, string pluginId, string? version) => null;
}
