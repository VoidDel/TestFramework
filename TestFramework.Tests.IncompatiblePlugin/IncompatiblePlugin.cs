using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;

// A contract far above anything this framework will provide, so the host must refuse the assembly.
[assembly: TestFrameworkPlugin("99.0")]

namespace TestFramework.Tests.IncompatiblePlugin;

/// <summary>
/// A plugin the host must never construct. The constructor throws so a test can prove the contract
/// check happened before instantiation rather than after.
/// </summary>
public sealed class IncompatibleStepPlugin : ITestStepPlugin
{
    public IncompatibleStepPlugin()
    {
        throw new InvalidOperationException("This plugin must never be instantiated by an incompatible host.");
    }

    public TestStepPluginDescriptor Descriptor => throw new NotSupportedException();

    public Type SettingsType => typeof(object);

    public object CreateDefaultSettings() => new();

    public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

    public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

    public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
