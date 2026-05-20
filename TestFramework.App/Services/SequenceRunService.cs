using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Resources;

namespace TestFramework.App.Services;

public sealed class SequenceRunService
{
    private readonly IPluginRegistry _pluginRegistry;
    private readonly ResourcePluginRegistry _resourcePluginRegistry;

    public SequenceRunService(
        IPluginRegistry pluginRegistry,
        ResourcePluginRegistry resourcePluginRegistry)
    {
        _pluginRegistry = pluginRegistry;
        _resourcePluginRegistry = resourcePluginRegistry;
    }

    public async Task<TestSequenceRunResult> RunAsync(
        TestSequence sequence,
        ITestExecutionObserver observer,
        CancellationToken cancellationToken = default)
    {
        await using var resources = await new RuntimeResourceBuilder(_resourcePluginRegistry)
            .BuildAsync(sequence, cancellationToken)
            .ConfigureAwait(false);

        var runner = new TestSequenceRunner(_pluginRegistry, observer, resources);
        return await runner.RunAsync(sequence, cancellationToken).ConfigureAwait(false);
    }
}
