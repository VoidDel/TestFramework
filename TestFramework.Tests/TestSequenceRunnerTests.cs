using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using Xunit;

namespace TestFramework.Tests;

public sealed class TestSequenceRunnerTests
{
    [Fact]
    public async Task RunAsync_JumpToCleanup_RunsCleanupAndSkipsRemainingMainSteps()
    {
        var registry = new PluginRegistry();
        registry.Register(new ResultPlugin("pass", TestVerdict.Pass));
        registry.Register(new ThrowingPlugin("throw"));

        var sequence = new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        Step("failing-main", "throw", ErrorHandlingMode.JumpToCleanup),
                        Step("skipped-main", "pass", ErrorHandlingMode.Stop)
                    ],
                    CleanupSteps =
                    [
                        Step("cleanup", "pass", ErrorHandlingMode.Stop)
                    ],
                    VerdictSource = new VerdictSource { StepId = "failing-main" }
                }
            ]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        var item = Assert.Single(result.ItemResults);

        Assert.Equal(TestVerdict.Error, item.Verdict);
        Assert.Single(item.MainResults);
        Assert.Equal("failing-main", item.MainResults[0].StepId);
        Assert.Single(item.CleanupResults);
        Assert.Equal("cleanup", item.CleanupResults[0].StepId);
    }

    private static TestStepDefinition Step(string id, string pluginId, ErrorHandlingMode onError)
    {
        return new TestStepDefinition
        {
            Id = id,
            Name = id,
            PluginId = pluginId,
            PluginVersion = "1.0.0",
            OnError = onError
        };
    }

    private sealed class ResultPlugin : ITestStepPlugin
    {
        private readonly TestVerdict _verdict;

        public ResultPlugin(string pluginId, TestVerdict verdict)
        {
            _verdict = verdict;
            Descriptor = new TestStepPluginDescriptor
            {
                PluginId = pluginId,
                DisplayName = pluginId,
                Version = new Version(1, 0, 0)
            };
        }

        public TestStepPluginDescriptor Descriptor { get; }

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TestStepResult { Verdict = _verdict });
        }
    }

    private sealed class ThrowingPlugin : ITestStepPlugin
    {
        public ThrowingPlugin(string pluginId)
        {
            Descriptor = new TestStepPluginDescriptor
            {
                PluginId = pluginId,
                DisplayName = pluginId,
                Version = new Version(1, 0, 0)
            };
        }

        public TestStepPluginDescriptor Descriptor { get; }

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }
    }
}
