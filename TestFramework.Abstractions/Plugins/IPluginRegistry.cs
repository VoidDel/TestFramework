namespace TestFramework.Abstractions.Plugins;

public interface IPluginRegistry
{
    IReadOnlyCollection<ITestStepPlugin> Plugins { get; }

    void Register(ITestStepPlugin plugin);

    ITestStepPlugin GetRequired(string pluginId, string? version = null);

    bool TryGet(string pluginId, out ITestStepPlugin plugin);

    bool TryGet(string pluginId, string? version, out ITestStepPlugin plugin);
}
