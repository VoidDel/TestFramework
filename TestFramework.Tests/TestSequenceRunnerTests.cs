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

    [Fact]
    public async Task RunAsync_NumericVerdict_ConvertsUnitsBeforeComparingLimits()
    {
        var registry = new PluginRegistry();
        registry.Register(new OutputPlugin("measure", 5000, TestVerdict.Pass));

        var sequence = new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [Step("main", "measure", ErrorHandlingMode.Stop)],
                    VerdictSource = new VerdictSource
                    {
                        StepId = "main",
                        OutputKey = "value",
                        JudgeType = VerdictJudgeType.Numeric,
                        LowerLimit = 4.9,
                        UpperLimit = 5.1,
                        SourceUnit = "mV",
                        Unit = "V"
                    }
                }
            ]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);

        Assert.Equal(TestVerdict.Pass, Assert.Single(result.ItemResults).Verdict);
    }

    [Fact]
    public async Task RunAsync_StringVerdict_SupportsRegex()
    {
        var registry = new PluginRegistry();
        registry.Register(new OutputPlugin("read", "SN-12345", TestVerdict.Fail));

        var sequence = new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [Step("main", "read", ErrorHandlingMode.Stop)],
                    VerdictSource = new VerdictSource
                    {
                        StepId = "main",
                        OutputKey = "text",
                        JudgeType = VerdictJudgeType.String,
                        StringMode = StringJudgeMode.Regex,
                        ExpectedString = @"^SN-\d+$"
                    }
                }
            ]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);

        Assert.Equal(TestVerdict.Pass, Assert.Single(result.ItemResults).Verdict);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task RunAsync_NonFiniteNumericVerdict_IsInconclusive(double value)
    {
        var registry = new PluginRegistry();
        registry.Register(new OutputPlugin("measure", value, TestVerdict.Pass));
        var sequence = SequenceWithSingleStep("measure", ErrorHandlingMode.Stop);
        sequence.Items[0].VerdictSource = new VerdictSource
        {
            StepId = "main",
            OutputKey = "value",
            JudgeType = VerdictJudgeType.Numeric,
            LowerLimit = 0,
            UpperLimit = 10
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);

        Assert.Equal(TestVerdict.Inconclusive, Assert.Single(result.ItemResults).Verdict);
    }

    [Fact]
    public async Task RunAsync_PluginOperationCanceledException_BecomesStepError()
    {
        var registry = new PluginRegistry();
        registry.Register(new CancelingPlugin("cancel"));
        var sequence = SequenceWithSingleStep("cancel", ErrorHandlingMode.Continue);

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        var step = Assert.Single(Assert.Single(result.ItemResults).MainResults);

        Assert.Equal(TestVerdict.Error, step.Verdict);
        Assert.IsType<OperationCanceledException>(step.Exception);
    }

    [Fact]
    public async Task RunAsync_PluginOperationCanceledException_WithTimeout_IsNotMisreportedAsTimeout()
    {
        var registry = new PluginRegistry();
        registry.Register(new CancelingPlugin("cancel"));
        var sequence = SequenceWithSingleStep("cancel", ErrorHandlingMode.Continue);
        sequence.Items[0].MainSteps[0].TimeoutMs = 10_000;

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        var step = Assert.Single(Assert.Single(result.ItemResults).MainResults);

        Assert.Equal("plugin canceled itself", step.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_UserCancellation_IsRethrown()
    {
        var registry = new PluginRegistry();
        registry.Register(new WaitingPlugin("wait"));
        var sequence = SequenceWithSingleStep("wait", ErrorHandlingMode.Stop);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TestSequenceRunner(registry).RunAsync(sequence, cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_FrameworkTimeout_BecomesTimedOutStepError()
    {
        var registry = new PluginRegistry();
        registry.Register(new WaitingPlugin("wait"));
        var sequence = SequenceWithSingleStep("wait", ErrorHandlingMode.Continue);
        sequence.Items[0].MainSteps[0].TimeoutMs = 50;

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        var step = Assert.Single(Assert.Single(result.ItemResults).MainResults);

        Assert.Equal(TestVerdict.Error, step.Verdict);
        Assert.Contains("timed out after 50 ms", step.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_StopError_StillRunsCleanupAndStopsFollowingItems()
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
                    Name = "First",
                    MainSteps = [Step("failing-main", "throw", ErrorHandlingMode.Stop)],
                    CleanupSteps = [Step("cleanup", "pass", ErrorHandlingMode.Continue)],
                    VerdictSource = new VerdictSource { StepId = "failing-main" }
                },
                new TestItemDefinition
                {
                    Name = "Second",
                    MainSteps = [Step("never-runs", "pass", ErrorHandlingMode.Stop)],
                    VerdictSource = new VerdictSource { StepId = "never-runs" }
                }
            ]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);

        var first = Assert.Single(result.ItemResults);
        Assert.Equal("cleanup", Assert.Single(first.CleanupResults).StepId);
        Assert.Equal(TestVerdict.Error, first.Verdict);
    }

    [Fact]
    public async Task RunAsync_MissingMainSteps_StillRunsCleanup()
    {
        var registry = new PluginRegistry();
        registry.Register(new ResultPlugin("pass", TestVerdict.Pass));
        var sequence = new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Invalid item",
                    CleanupSteps = [Step("cleanup", "pass", ErrorHandlingMode.Continue)]
                }
            ]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        var item = Assert.Single(result.ItemResults);

        Assert.Equal(TestVerdict.Error, item.Verdict);
        Assert.Equal("cleanup", Assert.Single(item.CleanupResults).StepId);
    }

    [Fact]
    public async Task RunAsync_PathologicalRegex_ReturnsInconclusiveWithinBoundedTime()
    {
        var registry = new PluginRegistry();
        registry.Register(new OutputPlugin("read", new string('a', 30_000) + "!", TestVerdict.Pass));
        var sequence = SequenceWithSingleStep("read", ErrorHandlingMode.Stop);
        sequence.Items[0].VerdictSource = new VerdictSource
        {
            StepId = "main",
            OutputKey = "text",
            JudgeType = VerdictJudgeType.String,
            StringMode = StringJudgeMode.Regex,
            ExpectedString = "^(a+)+$"
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TestVerdict.Inconclusive, Assert.Single(result.ItemResults).Verdict);
    }

    [Fact]
    public async Task RunAsync_MissingVerdictOutput_IsErrorInsteadOfPass()
    {
        var registry = new PluginRegistry();
        registry.Register(new ResultPlugin("pass", TestVerdict.Pass));
        var sequence = SequenceWithSingleStep("pass", ErrorHandlingMode.Stop);
        sequence.Items[0].VerdictSource.OutputKey = "typo";
        Assert.Equal(TestVerdict.Error, (await new TestSequenceRunner(registry).RunAsync(sequence)).Verdict);
    }

    [Fact]
    public async Task RunAsync_CancellationPreservesCompletedAndInterruptedSteps()
    {
        var registry = new PluginRegistry();
        registry.Register(new ResultPlugin("pass", TestVerdict.Pass));
        var blocking = new BlockingPlugin();
        registry.Register(blocking);
        var sequence = SequenceWithSingleStep("pass", ErrorHandlingMode.Stop);
        sequence.Items[0].MainSteps[0].VariableWrites.Add(new VariableWriteDefinition { Name = "saved", Value = 42 });
        sequence.Items[0].MainSteps.Add(Step("wait", "blocking", ErrorHandlingMode.Stop));
        sequence.Items[0].CleanupSteps.Add(Step("cleanup", "pass", ErrorHandlingMode.Stop));
        using var cancellation = new CancellationTokenSource();
        var runner = new TestSequenceRunner(registry);
        var run = runner.RunAsync(sequence, cancellation.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        try
        {
            var error = await Assert.ThrowsAsync<TestSequenceCancelledException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(TestVerdict.Cancelled, error.Result.Verdict);
            var item = Assert.Single(error.Result.ItemResults);
            Assert.Equal(TestVerdict.Cancelled, item.Verdict);
            Assert.Equal(new[] { TestVerdict.Pass, TestVerdict.Cancelled }, item.MainResults.Select(step => step.Verdict));
            Assert.Equal(42, error.Result.FinalVariables["saved"]);
            Assert.Empty(item.CleanupResults);
            Assert.True(error.Result.FinishedAt >= error.Result.StartedAt);
        }
        finally
        {
            blocking.Release.TrySetResult();
            await runner.PendingStepsCompletion;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_UncooperativeTimeout_StopsSequenceAndDefersCleanup(bool synchronous)
    {
        var registry = new PluginRegistry();
        var blocking = new BlockingPlugin { BlockSynchronously = synchronous };
        registry.Register(blocking);
        registry.Register(new ResultPlugin("pass", TestVerdict.Pass));
        var sequence = SequenceWithSingleStep("blocking", ErrorHandlingMode.Continue);
        sequence.Items[0].MainSteps[0].TimeoutMs = 100;
        sequence.Items[0].MainSteps.Add(Step("next", "pass", ErrorHandlingMode.Stop));
        sequence.Items[0].CleanupSteps.Add(Step("cleanup", "pass", ErrorHandlingMode.Stop));
        var runner = new TestSequenceRunner(registry);
        try
        {
            var result = await runner.RunAsync(sequence).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(TestVerdict.Error, result.Verdict);
            Assert.True(result.HasPendingExecution);
            Assert.False(runner.PendingStepsCompletion.IsCompleted);
            var item = Assert.Single(result.ItemResults);
            Assert.Contains("timed out", Assert.Single(item.MainResults).ErrorMessage);
            Assert.Empty(item.CleanupResults);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(sequence));
        }
        finally
        {
            blocking.Release.TrySetResult();
            await runner.PendingStepsCompletion;
        }
    }

    [Fact]
    public async Task RunAsync_InvalidVariableWrite_BecomesStepErrorAndRunsCleanup()
    {
        var registry = new PluginRegistry();
        registry.Register(new ResultPlugin("pass", TestVerdict.Pass));
        var sequence = SequenceWithSingleStep("pass", ErrorHandlingMode.Stop);
        sequence.Items[0].MainSteps[0].VariableWrites.Add(new VariableWriteDefinition { Name = "missing", OutputKey = "typo" });
        sequence.Items[0].CleanupSteps.Add(Step("cleanup", "pass", ErrorHandlingMode.Stop));
        var result = await new TestSequenceRunner(registry).RunAsync(sequence);
        Assert.Equal(TestVerdict.Error, result.Verdict);
        Assert.Single(result.ItemResults[0].CleanupResults);
        Assert.Contains("typo", result.ItemResults[0].MainResults[0].ErrorMessage);
    }

    private sealed class BlockingPlugin : ResultPluginBase
    {
        public BlockingPlugin() : base("blocking") { }
        public bool BlockSynchronously { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (BlockSynchronously) Release.Task.GetAwaiter().GetResult();
            await Release.Task;
            return new TestStepResult { Verdict = TestVerdict.Pass };
        }
    }

    private static TestSequence SequenceWithSingleStep(string pluginId, ErrorHandlingMode onError)
    {
        return new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [Step("main", pluginId, onError)],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };
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

    private sealed class OutputPlugin : ITestStepPlugin
    {
        private readonly object? _value;
        private readonly TestVerdict _verdict;

        public OutputPlugin(string pluginId, object? value, TestVerdict verdict)
        {
            _value = value;
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
            return Task.FromResult(new TestStepResult
            {
                Verdict = _verdict,
                Outputs =
                {
                    ["value"] = _value,
                    ["text"] = _value
                }
            });
        }
    }

    private sealed class CancelingPlugin : ResultPluginBase
    {
        public CancelingPlugin(string pluginId) : base(pluginId)
        {
        }

        public override Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException("plugin canceled itself");
        }
    }

    private sealed class WaitingPlugin : ResultPluginBase
    {
        public WaitingPlugin(string pluginId) : base(pluginId)
        {
        }

        public override async Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TestStepResult { Verdict = TestVerdict.Pass };
        }
    }

    private abstract class ResultPluginBase : ITestStepPlugin
    {
        protected ResultPluginBase(string pluginId)
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

        public abstract Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken);
    }
}
