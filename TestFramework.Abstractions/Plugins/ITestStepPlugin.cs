using TestFramework.Abstractions.Execution;

namespace TestFramework.Abstractions.Plugins;

public interface ITestStepPlugin
{
    // Plugin instances are reused. The application serializes sequence runs by default;
    // hosts that execute multiple runners concurrently must provide thread-safe plugins.
    TestStepPluginDescriptor Descriptor { get; }

    Type SettingsType { get; }

    object CreateDefaultSettings();

    object LoadSettings(IReadOnlyDictionary<string, object?> parameters);

    IReadOnlyDictionary<string, object?> SaveSettings(object settings);

    Task<TestStepResult> ExecuteAsync(
        TestStepExecutionContext context,
        object settings,
        CancellationToken cancellationToken);
}
