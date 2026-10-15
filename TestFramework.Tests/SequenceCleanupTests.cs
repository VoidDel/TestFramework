using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Core.Results;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// A sequence's own cleanup steps: they run after the items however the run ended, under the
/// reserved item id, and their verdict is the sequence's.
/// </summary>
public sealed class SequenceCleanupTests
{
    [Fact]
    public async Task Cleanup_RunsAfterTheLastItem_AndIsReportedAsOneMoreItem()
    {
        var plugin = new ScriptedPlugin();
        var observer = new RecordingObserver();
        var sequence = Sequence([Item("measure", Step("read"))], Step("open-contactor"));

        var result = await RunAsync(plugin, sequence, observer);

        Assert.Equal(TestVerdict.Pass, result.Verdict);
        Assert.Equal(["read", "open-contactor"], plugin.Executed);
        Assert.Single(result.ItemResults);

        var cleanup = Assert.IsType<TestItemRunResult>(result.Cleanup);
        Assert.Equal(TestSequence.CleanupItemId, cleanup.ItemId);
        Assert.Equal(TestVerdict.Pass, cleanup.Verdict);
        Assert.Equal(TestVerdict.Pass, Assert.Single(cleanup.CleanupResults).Verdict);
        Assert.Equal(["measure", TestSequence.CleanupItemId], observer.ItemsStarted);
        Assert.Equal((TestSequence.CleanupItemId, "open-contactor", StepSection.Cleanup), observer.StepsStarted.Last());
    }

    [Fact]
    public async Task Cleanup_RunsAfterAStopError_InsteadOfTheItemsThatFollow()
    {
        var plugin = new ScriptedPlugin();
        var failing = Step("fail", "Error");
        failing.OnError = ErrorHandlingMode.Stop;
        var sequence = Sequence([Item("first", failing), Item("second", Step("never"))], Step("open-contactor"));

        var result = await RunAsync(plugin, sequence);

        Assert.Equal(TestVerdict.Error, result.Verdict);
        Assert.Equal(["fail", "open-contactor"], plugin.Executed);
        Assert.Single(result.ItemResults);
        Assert.Equal(TestVerdict.Pass, result.Cleanup!.Verdict);
    }

    [Theory]
    [InlineData("Fail", TestVerdict.Fail)]
    [InlineData("Error", TestVerdict.Error)]
    public async Task Cleanup_ThatDoesNotSucceed_DecidesTheSequence(string verdict, TestVerdict expected)
    {
        // Every item passed; the contactor would not open. That DUT is not a pass.
        var sequence = Sequence([Item("measure", Step("read"))], Step("open-contactor", verdict));

        var result = await RunAsync(new ScriptedPlugin(), sequence);

        Assert.Equal(TestVerdict.Pass, Assert.Single(result.ItemResults).Verdict);
        Assert.Equal(expected, result.Cleanup!.Verdict);
        Assert.Equal(expected, result.Verdict);
    }

    [Fact]
    public async Task Cleanup_ReadsWhatTheItemsWrote()
    {
        var plugin = new ScriptedPlugin();
        var write = Step("read");
        write.VariableWrites.Add(new VariableWriteDefinition { Name = "charging", OutputKey = "calls" });
        var conditional = Step("stop-charger");
        conditional.RunIf = "${charging} == 1";
        var skipped = Step("not-needed");
        skipped.RunIf = "${charging} == 0";
        var sequence = Sequence([Item("measure", write)], conditional, skipped);

        var result = await RunAsync(plugin, sequence);

        Assert.Equal(["read", "stop-charger"], plugin.Executed);
        Assert.Equal([TestVerdict.Pass, TestVerdict.Skipped], result.Cleanup!.CleanupResults.Select(step => step.Verdict));
        Assert.Equal(TestVerdict.Pass, result.Verdict);
    }

    [Fact]
    public async Task Cleanup_RunsAfterCancellation_AfterTheItemsOwnCleanup()
    {
        var plugin = new ScriptedPlugin();
        var item = Item("charge", Step("wait", wait: true));
        item.CleanupSteps.Add(Step("item-cleanup"));
        var sequence = Sequence([item], Step("open-contactor"));
        using var cancellation = new CancellationTokenSource();
        var registry = new PluginRegistry();
        registry.Register(plugin);
        var runner = new TestSequenceRunner(registry) { CleanupGracePeriod = TimeSpan.FromSeconds(10) };

        var run = runner.RunAsync(sequence, cancellation.Token);
        await plugin.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var cancelled = await Assert.ThrowsAsync<TestSequenceCancelledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(TestVerdict.Cancelled, cancelled.Result.Verdict);
        Assert.Equal(["wait", "item-cleanup", "open-contactor"], plugin.Executed);
        Assert.Equal(TestVerdict.Pass, cancelled.Result.Cleanup!.Verdict);
        Assert.Equal(TestVerdict.Pass, Assert.Single(cancelled.Result.Cleanup.CleanupResults).Verdict);
    }

    [Fact]
    public async Task Cleanup_IsSkippedAndSaysSo_WhenAStepWasAbandoned()
    {
        var plugin = new ScriptedPlugin();
        var stuck = Step("stuck", ignoreCancellation: true);
        stuck.TimeoutMs = 50;
        var sequence = Sequence([Item("charge", stuck)], Step("open-contactor"));
        var registry = new PluginRegistry();
        registry.Register(plugin);
        var runner = new TestSequenceRunner(registry);

        try
        {
            var result = await runner.RunAsync(sequence);

            Assert.True(result.HasPendingExecution);
            Assert.Equal(TestVerdict.Error, result.Verdict);
            Assert.DoesNotContain("open-contactor", plugin.Executed);
            var cleanup = Assert.IsType<TestItemRunResult>(result.Cleanup);
            Assert.Equal(TestVerdict.Skipped, cleanup.Verdict);
            var step = Assert.Single(cleanup.CleanupResults);
            Assert.Equal(TestVerdict.Skipped, step.Verdict);
            Assert.Contains("did not exit", step.SkipReason, StringComparison.Ordinal);
        }
        finally
        {
            plugin.Release.TrySetResult();
            await runner.PendingStepsCompletion;
        }
    }

    [Fact]
    public async Task Cleanup_RunsEveryStep_WhenOneErrorsUnderTheDefaultStopPolicy()
    {
        // Stop is every step's default policy; in a cleanup section it must not skip what follows.
        var plugin = new ScriptedPlugin();
        var stopCharger = Step("stop-charger", "Error");
        stopCharger.OnError = ErrorHandlingMode.Stop;
        var sequence = Sequence([Item("measure", Step("read"))], stopCharger, Step("open-contactor"));

        var result = await RunAsync(plugin, sequence);

        Assert.Equal(["read", "stop-charger", "open-contactor"], plugin.Executed);
        Assert.Equal([TestVerdict.Error, TestVerdict.Pass], result.Cleanup!.CleanupResults.Select(step => step.Verdict));
        Assert.Equal(TestVerdict.Error, result.Verdict);
    }

    [Fact]
    public async Task ItemCleanup_RunsEveryStep_AndItsStopPolicyStillStopsTheSequence()
    {
        var plugin = new ScriptedPlugin();
        var item = Item("charge", Step("read"));
        var restore = Step("restore", "Error");
        restore.OnError = ErrorHandlingMode.Stop;
        item.CleanupSteps.AddRange([restore, Step("release")]);
        var sequence = Sequence([item, Item("next", Step("never"))]);

        var result = await RunAsync(plugin, sequence);

        Assert.Equal(["read", "restore", "release"], plugin.Executed);
        Assert.Equal(TestVerdict.Error, Assert.Single(result.ItemResults).Verdict);
    }

    [Fact]
    public async Task Cleanup_ThatCannotJudge_LeavesTheSequenceUnjudged()
    {
        var sequence = Sequence([Item("measure", Step("read"))], Step("check-contactor", "Inconclusive"));

        var result = await RunAsync(new ScriptedPlugin(), sequence);

        Assert.Equal(TestVerdict.Inconclusive, result.Cleanup!.Verdict);
        Assert.Equal(TestVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public async Task Cleanup_CancelledWhileItRuns_FinishesTheStepsNotYetRun()
    {
        // The operator pressed Stop because the teardown was slow; the contactor must still open.
        var plugin = new ScriptedPlugin();
        var sequence = Sequence([], Step("discharge", wait: true), Step("open-contactor"));
        using var cancellation = new CancellationTokenSource();

        var run = Runner(plugin).RunAsync(sequence, cancellation.Token);
        await WaitUntilStartedAsync(plugin, "discharge");
        cancellation.Cancel();
        var cancelled = await Assert.ThrowsAsync<TestSequenceCancelledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(["discharge", "open-contactor"], plugin.Executed);
        var cleanup = cancelled.Result.Cleanup!;
        Assert.Equal(TestVerdict.Cancelled, cleanup.Verdict);
        Assert.Equal([TestVerdict.Cancelled, TestVerdict.Pass], cleanup.CleanupResults.Select(step => step.Verdict));
    }

    [Fact]
    public async Task ItemCleanup_CancelledWhileItRuns_FinishesTheStepsNotYetRun()
    {
        var plugin = new ScriptedPlugin();
        var item = Item("charge", Step("read"));
        item.CleanupSteps.AddRange([Step("discharge", wait: true), Step("release")]);
        var sequence = Sequence([item, Item("next", Step("never"))], Step("open-contactor"));
        using var cancellation = new CancellationTokenSource();

        var run = Runner(plugin).RunAsync(sequence, cancellation.Token);
        await WaitUntilStartedAsync(plugin, "discharge");
        cancellation.Cancel();
        var cancelled = await Assert.ThrowsAsync<TestSequenceCancelledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(["read", "discharge", "release", "open-contactor"], plugin.Executed);
        var itemResult = Assert.Single(cancelled.Result.ItemResults);
        Assert.Equal([TestVerdict.Cancelled, TestVerdict.Pass], itemResult.CleanupResults.Select(step => step.Verdict));
    }

    [Fact]
    public async Task Call_EndsWithTheCalleesCleanup_InTheCalleesScope_HoweverItsItemsEnded()
    {
        var plugin = new ScriptedPlugin();
        var energise = Step("energise", "Error");
        energise.OnError = ErrorHandlingMode.Stop;
        var deEnergise = Step("de-energise");
        deEnergise.RunIf = "${relay} == 1";
        var callee = Sequence([Item("inner", energise)], deEnergise);
        callee.Variables["relay"] = 1;
        var sequence = Sequence([CallItem("module"), Item("after", Step("never"))], Step("open-contactor"));

        var result = await Runner(plugin, callee).RunAsync(sequence);

        Assert.Equal(["energise", "de-energise", "open-contactor"], plugin.Executed);
        var call = Assert.Single(result.ItemResults);
        Assert.Equal(["inner", TestSequence.CleanupItemId], call.Children.Select(child => child.ItemId));
        Assert.Equal(TestVerdict.Pass, call.Children[^1].Verdict);
        Assert.Equal(TestVerdict.Error, call.Verdict);
    }

    [Fact]
    public async Task Call_WhoseCleanupFails_Fails()
    {
        var callee = Sequence([Item("inner", Step("read"))], Step("de-energise", "Fail"));

        var result = await Runner(new ScriptedPlugin(), callee).RunAsync(Sequence([CallItem("module")]));

        Assert.Equal(TestVerdict.Fail, Assert.Single(result.ItemResults).Verdict);
        Assert.Equal(TestVerdict.Fail, result.Verdict);
    }

    [Fact]
    public async Task Call_CancelledInside_RunsTheCalleesCleanupThenTheSequences()
    {
        var plugin = new ScriptedPlugin();
        var callee = Sequence([Item("inner", Step("wait", wait: true))], Step("de-energise"));
        using var cancellation = new CancellationTokenSource();

        var run = Runner(plugin, callee).RunAsync(Sequence([CallItem("module")], Step("open-contactor")), cancellation.Token);
        await WaitUntilStartedAsync(plugin, "wait");
        cancellation.Cancel();
        var cancelled = await Assert.ThrowsAsync<TestSequenceCancelledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(["wait", "de-energise", "open-contactor"], plugin.Executed);
        var call = Assert.Single(cancelled.Result.ItemResults);
        Assert.Equal(TestSequence.CleanupItemId, call.Children[^1].ItemId);
        Assert.Equal(TestVerdict.Pass, call.Children[^1].Verdict);
    }

    [Fact]
    public void Validator_ChecksCleanupStepsInTheScopeTheItemsLeave()
    {
        var write = Step("read");
        write.VariableWrites.Add(new VariableWriteDefinition { Name = "charging", OutputKey = "calls" });
        var known = Step("stop-charger");
        known.RunIf = "${charging} == 1";
        var unknown = Step("bad");
        unknown.RunIf = "${nobody} == 1";
        var sequence = Sequence([Item("measure", write)], known, unknown);

        var issue = Assert.Single(new TestSequenceValidator().Validate(sequence));

        Assert.Equal("cleanup[1].runIf", issue.Path);
        Assert.Contains("nobody", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_RefusesAnItemUsingTheReservedId()
    {
        var sequence = Sequence([Item(TestSequence.CleanupItemId, Step("read"))]);

        var issue = Assert.Single(new TestSequenceValidator().Validate(sequence));

        Assert.Equal("items[0].id", issue.Path);
        Assert.Contains("reserved", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_RoundTripsCleanup_AndOmitsItWhenThereIsNone()
    {
        var service = new TestSequenceYamlService();
        var without = service.Save(Sequence([Item("measure", Step("read"))])).ReplaceLineEndings("\n");
        var with = service.Save(Sequence([Item("measure", Step("read"))], Step("open-contactor"))).ReplaceLineEndings("\n");

        Assert.DoesNotContain("\ncleanup:", without, StringComparison.Ordinal);
        Assert.Contains("\ncleanup:\n- id: open-contactor", with, StringComparison.Ordinal);
        var reloaded = service.Load(with);
        Assert.Equal("open-contactor", Assert.Single(reloaded.CleanupSteps).Id);
        Assert.Empty(service.Load(without).CleanupSteps);
    }

    [Fact]
    public void ResultJson_RoundTripsTheCleanupRecord()
    {
        var result = new TestSequenceRunResult { SequenceId = "pack", Verdict = TestVerdict.Error };
        result.Cleanup = new TestItemRunResult { ItemId = TestSequence.CleanupItemId, ItemName = "Cleanup", Verdict = TestVerdict.Error };
        result.Cleanup.CleanupResults.Add(new TestStepResult { StepId = "open-contactor", Verdict = TestVerdict.Error, ErrorMessage = "stuck" });

        var read = TestResultJson.Deserialize(TestResultJson.Serialize(result));

        Assert.Equal(TestSequence.CleanupItemId, read.Cleanup!.ItemId);
        Assert.Equal("stuck", Assert.Single(read.Cleanup.CleanupResults).ErrorMessage);
        Assert.Null(TestResultJson.Deserialize(TestResultJson.Serialize(new TestSequenceRunResult())).Cleanup);
    }

    private static async Task<TestSequenceRunResult> RunAsync(ScriptedPlugin plugin, TestSequence sequence, ITestExecutionObserver? observer = null)
    {
        var registry = new PluginRegistry();
        registry.Register(plugin);
        return await new TestSequenceRunner(registry, observer).RunAsync(sequence);
    }

    private static TestSequenceRunner Runner(ScriptedPlugin plugin, TestSequence? callee = null)
    {
        var registry = new PluginRegistry();
        registry.Register(plugin);
        return new TestSequenceRunner(registry)
        {
            CleanupGracePeriod = TimeSpan.FromSeconds(10),
            SequenceResolver = callee is null ? null : new FixedResolver(callee)
        };
    }

    private static async Task WaitUntilStartedAsync(ScriptedPlugin plugin, string stepId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!plugin.Executed.Contains(stepId))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Step '{stepId}' did not start.");
            await Task.Delay(10);
        }
    }

    private static TestItemDefinition CallItem(string id) => new()
    {
        Id = id,
        Name = id,
        Call = new SequenceCallDefinition { Path = "module.yaml" }
    };

    private sealed class FixedResolver(TestSequence sequence) : ISequenceResolver
    {
        public TestSequence Resolve(string path) => sequence;
    }

    private static TestSequence Sequence(List<TestItemDefinition> items, params TestStepDefinition[] cleanup) =>
        new() { Name = "Sequence", Items = items, CleanupSteps = [.. cleanup] };

    private static TestItemDefinition Item(string id, TestStepDefinition step) => new()
    {
        Id = id,
        Name = id,
        MainSteps = [step],
        VerdictSource = new VerdictSource { StepId = step.Id }
    };

    private static TestStepDefinition Step(string id, string verdict = "Pass", bool wait = false, bool ignoreCancellation = false) => new()
    {
        Id = id,
        Name = id,
        PluginId = "probe.script",
        PluginVersion = "1.0.0",
        OnError = ErrorHandlingMode.Continue,
        Parameters = { ["verdict"] = verdict, ["wait"] = wait, ["ignoreCancellation"] = ignoreCancellation }
    };

    private sealed class RecordingObserver : ITestExecutionObserver
    {
        public List<string> ItemsStarted { get; } = [];

        public List<(string Item, string Step, StepSection Section)> StepsStarted { get; } = [];

        public void ItemStarted(TestItemDefinition item) => ItemsStarted.Add(item.Id);

        public void StepStarted(TestItemDefinition item, TestStepDefinition step, StepSection section) =>
            StepsStarted.Add((item.Id, step.Id, section));
    }

    /// <summary>
    /// Records the steps it ran, in order. <c>verdict</c> is returned, or thrown for "Error";
    /// <c>wait</c> blocks until cancelled; <c>ignoreCancellation</c> blocks until released, as a
    /// plugin stuck in a native call would.
    /// </summary>
    private sealed class ScriptedPlugin : ITestStepPlugin
    {
        private readonly List<string> _executed = [];

        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "probe.script",
            DisplayName = "probe.script",
            Version = new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(object);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Executed
        {
            get { lock (_executed) return [.. _executed]; }
        }

        public object CreateDefaultSettings() => new Dictionary<string, object?>();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) =>
            new Dictionary<string, object?>(parameters, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => (Dictionary<string, object?>)settings;

        public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            var values = (Dictionary<string, object?>)settings;
            lock (_executed) _executed.Add(context.Step.Id);
            Started.TrySetResult();

            if (values["ignoreCancellation"] is true)
            {
                await Release.Task;
            }
            else if (values["wait"] is true)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var verdict = Enum.Parse<TestVerdict>((string)values["verdict"]!);
            if (verdict == TestVerdict.Error)
            {
                throw new InvalidOperationException("scripted error");
            }

            return new TestStepResult { Verdict = verdict, Outputs = { ["calls"] = 1 } };
        }
    }
}
