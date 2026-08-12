using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Resources;

namespace TestFramework.App.Services;

public sealed class SequenceRunService
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
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
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var resources = await new RuntimeResourceBuilder(_resourcePluginRegistry)
                .BuildAsync(sequence, cancellationToken)
                .ConfigureAwait(false);

            var runner = new TestSequenceRunner(_pluginRegistry, observer, resources);
            return await runner.RunAsync(sequence, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }
}
