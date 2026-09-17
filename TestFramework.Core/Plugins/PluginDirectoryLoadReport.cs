using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;

namespace TestFramework.Core.Plugins;

/// <summary>
/// What one pass over a plugin directory produced, across both plugin kinds.
/// </summary>
public sealed class PluginDirectoryLoadReport
{
    public List<ITestStepPlugin> StepPlugins { get; } = [];

    public List<IInstrumentDriverPlugin> InstrumentDrivers { get; } = [];

    public List<ITransportPlugin> Transports { get; } = [];

    public List<ITestServicePlugin> Services { get; } = [];

    public List<PluginLoadFailure> Failures { get; } = [];

    /// <summary>
    /// The assembly each loaded plugin came from, keyed by plugin instance. Hosts use it to group
    /// plugins by their folder. Reference identity is deliberate: plugin types are not required to
    /// implement equality, and two instances of one plugin type are two plugins here.
    /// </summary>
    public Dictionary<object, string> PluginPaths { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The framework contract each scanned assembly declared, keyed by assembly path. Hosts use it
    /// to report what a plugin was built against and to adapt optional calls.
    /// </summary>
    public Dictionary<string, Version> AssemblyContracts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int LoadedCount => StepPlugins.Count + InstrumentDrivers.Count + Transports.Count + Services.Count;

    internal void AddFailure(string assemblyPath, Exception exception)
    {
        Failures.Add(new PluginLoadFailure
        {
            AssemblyPath = assemblyPath,
            Message = exception.Message,
            Exception = exception
        });
    }
}
