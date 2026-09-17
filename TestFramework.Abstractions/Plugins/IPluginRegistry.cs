namespace TestFramework.Abstractions.Plugins;

public interface IPluginRegistry
{
    IReadOnlyCollection<ITestStepPlugin> Plugins { get; }

    void Register(ITestStepPlugin plugin);

    ITestStepPlugin GetRequired(string pluginId, string? version = null);

    bool TryGet(string pluginId, out ITestStepPlugin plugin);

    bool TryGet(string pluginId, string? version, out ITestStepPlugin plugin);

    /// <summary>
    /// Resolves a plugin reference and reports how the version was satisfied. Prefer this over
    /// <see cref="TryGet(string, string?, out ITestStepPlugin)"/> wherever the caller can tell the
    /// user that a substituted version is running.
    /// </summary>
    bool TryResolve(string pluginId, string? version, out PluginResolution<ITestStepPlugin> resolution);

    /// <summary>
    /// Explains why <paramref name="pluginId"/> at <paramref name="version"/> cannot be resolved,
    /// naming the installed versions so a version mismatch is distinguishable from a missing plugin.
    /// </summary>
    string DescribeMissing(string pluginId, string? version);
}
