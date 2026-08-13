using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Plugins;

public sealed class PluginLoadReport
{
    public List<ITestStepPlugin> LoadedPlugins { get; } = [];

    public Dictionary<ITestStepPlugin, string> PluginPaths { get; } = [];

    public List<PluginLoadFailure> Failures { get; } = [];
}
