using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Expressions;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Resources;
using TestFramework.Core.Variables;
using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TestFramework.Core.Execution;

public sealed class TestSequenceRunner
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private readonly IPluginRegistry _pluginRegistry;
    private readonly ITestExecutionObserver _observer;
    private readonly RuntimeResourceProvider _defaultResources;
    private RuntimeResourceProvider _resources;
    private int _running;
    private bool _abandonedStep;

    // Plugin references already reported as version substitutions in this run. A sequence
    // usually reuses the same plugin across many steps, and the warning is about the
    // reference, not the step, so it is emitted once per reference.
    private readonly HashSet<string> _reportedSubstitutions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bounded window granted to an item's cleanup steps after the user cancels a run. Cancellation
    /// would otherwise leave the device in whatever state the interrupted step left it, unlike the
    /// Stop and JumpToCleanup error policies which both run cleanup. The bound keeps a stuck
    /// cleanup from hanging the stop indefinitely.
    /// </summary>
    public TimeSpan CleanupGracePeriod { get; init; } = TimeSpan.FromSeconds(30);

    // Hosts must await this before disposing resources or reusing plugin instances.
    public Task PendingStepsCompletion { get; private set; } = Task.CompletedTask;

    public TestSequenceRunner(
        IPluginRegistry pluginRegistry,
        ITestExecutionObserver? observer = null,
        RuntimeResourceProvider? resources = null)
    {
        _pluginRegistry = pluginRegistry;
        _observer = new SafeExecutionObserver(observer ?? new NullTestExecutionObserver());
        _defaultResources = resources ?? RuntimeResourceProvider.Empty;
        _resources = _defaultResources;
    }

    /// <summary>Runs a sequence against the resources this runner was constructed with.</summary>
    public Task<TestSequenceRunResult> RunAsync(TestSequence sequence, CancellationToken cancellationToken = default) =>
        RunGuardedAsync(sequence, null, cancellationToken);

    /// <summary>
    /// Runs a sequence against a scope opened for this run alone, keeping one runner alive across
    /// every run.
    ///
    /// This exists because the two ways of supplying resources have to compose. A station scope
    /// hands out a fresh <see cref="RuntimeResourceProvider"/> per run, and when resources can only
    /// arrive through the constructor a host has no choice but to build a new runner each time -
    /// which quietly discards the protection this class provides. <c>_running</c>,
    /// <see cref="PendingStepsCompletion"/> and the quarantine flag are per-instance, so a plugin
    /// abandoned by the previous run is unknown to the next runner, which then resolves the same
    /// singleton plugin instance out of the registry and calls <c>ExecuteAsync</c> on it a second
    /// time while the first call is still talking to the hardware.
    ///
    /// Passing the scope per run instead lets a host keep one runner for the life of the bench, so
    /// the gate covers the boundary it was always meant to cover: a run that follows an abandoned
    /// step is refused here rather than left to every host to re-derive.
    /// </summary>
    public Task<TestSequenceRunResult> RunAsync(
        TestSequence sequence,
        RuntimeResourceProvider resources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return RunGuardedAsync(sequence, resources, cancellationToken);
    }

    /// <summary>
    /// Runs a sequence and records <paramref name="runInfo"/> - the unit, operator, station and
    /// sequence file - in the result. <paramref name="resources"/> null uses the constructor's.
    /// </summary>
    public Task<TestSequenceRunResult> RunAsync(
        TestSequence sequence,
        RuntimeResourceProvider? resources,
        TestRunInfo runInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runInfo);
        return RunGuardedAsync(sequence, resources, cancellationToken, runInfo);
    }

    /// <summary>
    /// Finds the sequences that items with a <c>call</c> run. Without one, a call is an item error
    /// that says so.
    /// </summary>
    public ISequenceResolver? SequenceResolver { get; init; }

    /// <summary>How steps ask the operator; see <see cref="IOperatorInteraction"/>.</summary>
    public IOperatorInteraction Operator { get; init; } = NoOperatorInteraction.Instance;

    /// <summary>The most iterations one <c>loop</c> may run: a guard against a count variable holding nonsense.</summary>
    public const int MaxLoopIterations = 10_000;

    /// <summary>How deep calls may nest; past it the call is an error, which is how a sequence calling itself ends.</summary>
    public const int MaxCallDepth = 16;

    private TestRunInfo _runInfo = new();

    private async Task<TestSequenceRunResult> RunGuardedAsync(
        TestSequence sequence,
        RuntimeResourceProvider? resources,
        CancellationToken cancellationToken,
        TestRunInfo? runInfo = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("The previous run or its pending step has not finished.");
        }

        try
        {
            // Only after the gate is held: a refused run must not swap the scope out from under
            // the run that is still using it. A step already executing keeps the scope it captured
            // in its context, so an abandoned step is unaffected by a later run's resources.
            _resources = resources ?? _defaultResources;
            _abandonedStep = false;
            _reportedSubstitutions.Clear();
            _runInfo = runInfo?.Clone() ?? new TestRunInfo();
            return await RunCoreAsync(sequence, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PendingStepsCompletion = ReleaseRunnerAsync(PendingStepsCompletion);
        }
    }

    private async Task ReleaseRunnerAsync(Task pendingSteps)
    {
        await pendingSteps.ConfigureAwait(false);
        Volatile.Write(ref _running, 0);
    }

    private async Task<TestSequenceRunResult> RunCoreAsync(TestSequence sequence, CancellationToken cancellationToken)
    {
        var result = new TestSequenceRunResult
        {
            SequenceId = sequence.Id,
            SequenceName = sequence.Name,
            SequenceVersion = sequence.Version,
            RunInfo = _runInfo,
            FrameworkVersion = FrameworkBuild.Version,
            FrameworkContractVersion = FrameworkContract.Version.ToString(),
            StartedAt = DateTimeOffset.Now,
            InitialVariables = VariableSnapshot.Capture(sequence.Variables)
        };

        _observer.SequenceStarted(sequence);

        var variables = new Dictionary<string, object?>(sequence.Variables, StringComparer.OrdinalIgnoreCase);
        var cancelled = false;
        try
        {
            await RunItemsAsync(sequence, sequence.Items, result.ItemResults, variables, callDepth: 0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        result.FinishedAt = DateTimeOffset.Now;
        result.Verdict = cancelled ? TestVerdict.Cancelled : AggregateSequenceVerdict(result.ItemResults);
        result.HasPendingExecution = _abandonedStep;
        result.FinalVariables = VariableSnapshot.Capture(variables);
        _observer.SequenceFinished(sequence, result);

        if (cancelled) throw new TestSequenceCancelledException(result, cancellationToken);

        return result;
    }

    /// <summary>
    /// Runs items in order into <paramref name="results"/>. True means the sequence must stop - an
    /// error under a Stop policy, a quarantined step, or a flow definition that could not be
    /// evaluated - and the caller stops too, all the way up through groups and calls.
    /// </summary>
    private async Task<bool> RunItemsAsync(
        TestSequence sequence,
        IReadOnlyList<TestItemDefinition> items,
        List<TestItemRunResult> results,
        IDictionary<string, object?> variables,
        int callDepth,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await RunItemNodeAsync(sequence, item, results, variables, callDepth, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One item with its flow: enablement, then <c>runIf</c>, then <c>loop</c>, then <c>retry</c>
    /// around each iteration.
    ///
    /// A flow definition that cannot be evaluated - a <c>runIf</c> that is not a boolean, a loop
    /// count that is not a number - stops the sequence. Skipping the item would pass it by default,
    /// running it would ignore the author's condition, and either choice made silently is worse
    /// than an error that names the expression.
    /// </summary>
    private async Task<bool> RunItemNodeAsync(
        TestSequence sequence,
        TestItemDefinition item,
        List<TestItemRunResult> results,
        IDictionary<string, object?> variables,
        int callDepth,
        CancellationToken cancellationToken)
    {
        if (!item.Enabled)
        {
            results.Add(CreateSkippedItemResult(item, "Disabled."));
            return false;
        }

        if (!string.IsNullOrWhiteSpace(item.RunIf))
        {
            bool shouldRun;
            try
            {
                shouldRun = SequenceExpression.Parse(item.RunIf).EvaluateCondition(variables);
            }
            catch (SequenceExpressionException ex)
            {
                results.Add(CreateFlowErrorResult(item, $"runIf: {ex.Message}"));
                return true;
            }

            if (!shouldRun)
            {
                results.Add(CreateSkippedItemResult(item, $"runIf '{item.RunIf}' was false."));
                return false;
            }
        }

        if (item.Loop is not { } loop)
        {
            return await RunAttemptsAsync(sequence, item, results, variables, iteration: null, callDepth, cancellationToken)
                .ConfigureAwait(false);
        }

        int count;
        try
        {
            count = SequenceExpression.Parse(loop.Count).EvaluateCount(variables, MaxLoopIterations);
        }
        catch (SequenceExpressionException ex)
        {
            results.Add(CreateFlowErrorResult(item, $"loop count: {ex.Message}"));
            return true;
        }

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            variables[loop.Variable] = index;
            if (await RunAttemptsAsync(sequence, item, results, variables, index, callDepth, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs one iteration of an item, again while it fails and <c>retry</c> allows. Only the last
    /// attempt is in <paramref name="results"/>; the earlier ones hang off it, oldest first.
    ///
    /// An attempt is added to the results before it runs and moved off them only once it has
    /// finished, so a run cancelled mid-attempt still records the attempt that was interrupted.
    /// </summary>
    private async Task<bool> RunAttemptsAsync(
        TestSequence sequence,
        TestItemDefinition item,
        List<TestItemRunResult> results,
        IDictionary<string, object?> variables,
        int? iteration,
        int callDepth,
        CancellationToken cancellationToken)
    {
        var retry = item.Retry;
        var maxAttempts = Math.Max(1, retry?.MaxAttempts ?? 1);
        var previous = new List<TestItemRunResult>();
        for (var attempt = 1; ; attempt++)
        {
            var result = new TestItemRunResult
            {
                ItemId = item.Id,
                ItemName = item.Name,
                Iteration = iteration,
                Attempt = attempt,
                StartedAt = DateTimeOffset.Now
            };
            results.Add(result);

            var stop = await RunSingleItemAsync(sequence, item, result, variables, callDepth, cancellationToken).ConfigureAwait(false);

            // Only a Pass or a Fail is a candidate: an Error left a step part-way through, and an
            // item is not run again against hardware in that state. A Pass whose until is still
            // false has not succeeded either - it is a poll that has not finished.
            var retryNeeded = false;
            if (!stop && !_abandonedStep && result.Verdict is TestVerdict.Pass or TestVerdict.Fail)
            {
                var succeeded = result.Verdict == TestVerdict.Pass && UntilHolds(retry, result, variables, out stop);
                retryNeeded = !stop && !succeeded && attempt < maxAttempts;
                if (!stop && !succeeded && !retryNeeded && result.Verdict == TestVerdict.Pass)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.ErrorMessage = $"retry until '{retry!.Until}' was still false after {attempt} attempt(s).";

                    // The observer already saw this attempt finish as a Pass; a progress view left
                    // showing that would contradict the result.
                    _observer.ItemFinished(item, result);
                }
            }

            if (stop || !retryNeeded)
            {
                result.PreviousAttempts.AddRange(previous);
                return stop || _abandonedStep;
            }

            results.Remove(result);
            previous.Add(result);
            _observer.Log($"[{item.Name}] attempt {attempt} of {maxAttempts} ended {result.Verdict}; retrying.");
            if (retry!.IntervalMs > 0)
            {
                await Task.Delay(retry.IntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether a retry's <c>until</c> holds after an attempt; true when there is none. A condition
    /// that cannot be evaluated makes the attempt an Error and stops the sequence.
    /// </summary>
    private static bool UntilHolds(RetryDefinition? retry, TestItemRunResult result, IDictionary<string, object?> variables, out bool stop)
    {
        stop = false;
        if (string.IsNullOrWhiteSpace(retry?.Until))
        {
            return true;
        }

        try
        {
            return SequenceExpression.Parse(retry.Until).EvaluateCondition(variables);
        }
        catch (SequenceExpressionException ex)
        {
            result.Verdict = TestVerdict.Error;
            result.ErrorMessage = $"retry until: {ex.Message}";
            stop = true;
            return true;
        }
    }

    /// <summary>One attempt at one item, whichever kind it is. True means stop the sequence.</summary>
    private async Task<bool> RunSingleItemAsync(
        TestSequence sequence,
        TestItemDefinition item,
        TestItemRunResult result,
        IDictionary<string, object?> variables,
        int callDepth,
        CancellationToken cancellationToken)
    {
        if (item.IsGroup || item.IsCall)
        {
            return await RunContainerAsync(sequence, item, result, variables, callDepth, cancellationToken).ConfigureAwait(false);
        }

        await RunItemAsync(sequence, item, result, variables, cancellationToken).ConfigureAwait(false);
        return _abandonedStep ||
               (result.Verdict == TestVerdict.Error && (item.MainSteps.Count == 0 || HasStopError(item, result)));
    }

    /// <summary>
    /// A group or a call: its children run in order, and its verdict is theirs - Error over Fail over
    /// anything unjudged, exactly as for a whole sequence.
    /// </summary>
    private async Task<bool> RunContainerAsync(
        TestSequence sequence,
        TestItemDefinition item,
        TestItemRunResult result,
        IDictionary<string, object?> variables,
        int callDepth,
        CancellationToken cancellationToken)
    {
        _observer.ItemStarted(item);
        var stop = false;
        try
        {
            if (item.Call is { } call)
            {
                stop = await RunCallAsync(call, result, variables, callDepth, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                stop = await RunItemsAsync(sequence, item.Items, result.Children, variables, callDepth, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (result.ErrorMessage is null)
            {
                result.Verdict = AggregateSequenceVerdict(result.Children);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Verdict = TestVerdict.Cancelled;
            throw;
        }
        finally
        {
            result.FinishedAt = DateTimeOffset.Now;
            result.VariablesAfter = VariableSnapshot.Capture(variables);
            _observer.ItemFinished(item, result);
        }

        return stop;
    }

    /// <summary>
    /// Runs a called sequence's items as this item's children, in a variable scope of their own:
    /// the callee's variables, overridden by the call's parameters resolved in the caller's scope.
    /// Nothing the callee writes reaches the caller. Any failure to start the call - no resolver,
    /// no such sequence, nesting too deep, an unresolvable parameter - is an error that stops the
    /// sequence, because the author expected those tests to run.
    /// </summary>
    private async Task<bool> RunCallAsync(
        SequenceCallDefinition call,
        TestItemRunResult result,
        IDictionary<string, object?> callerVariables,
        int callDepth,
        CancellationToken cancellationToken)
    {
        result.CalledSequencePath = call.Path;
        TestSequence callee;
        Dictionary<string, object?> scope;
        try
        {
            if (SequenceResolver is null)
            {
                throw new InvalidOperationException("This runner has no sequence resolver, so it cannot run a call.");
            }

            if (callDepth >= MaxCallDepth)
            {
                throw new InvalidOperationException($"Calls are nested deeper than {MaxCallDepth}; a sequence probably calls itself.");
            }

            callee = SequenceResolver.Resolve(call.Path);
            scope = new Dictionary<string, object?>(callee.Variables, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in VariableResolver.ResolveDictionary(call.Parameters, callerVariables))
            {
                scope[name] = value;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Verdict = TestVerdict.Error;
            result.ErrorMessage = $"Call '{call.Path}': {ex.Message}";
            return true;
        }

        result.CalledSequenceId = callee.Id;
        return await RunItemsAsync(callee, callee.Items, result.Children, scope, callDepth + 1, cancellationToken)
            .ConfigureAwait(false);
    }

    private static TestItemRunResult CreateFlowErrorResult(TestItemDefinition item, string message)
    {
        var now = DateTimeOffset.Now;
        return new TestItemRunResult
        {
            ItemId = item.Id,
            ItemName = item.Name,
            Verdict = TestVerdict.Error,
            ErrorMessage = message,
            StartedAt = now,
            FinishedAt = now
        };
    }

    private async Task RunItemAsync(
        TestSequence sequence,
        TestItemDefinition item,
        TestItemRunResult result,
        IDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        var stepResults = new Dictionary<string, TestStepResult>(StringComparer.OrdinalIgnoreCase);
        var flow = FlowDecision.Continue;

        _observer.ItemStarted(item);

        try
        {
            if (item.MainSteps.Count == 0)
            {
                flow = FlowDecision.StopSequence;
            }
            else
            {
                flow = await RunSectionAsync(sequence, item, StepSection.Init, item.InitSteps, result.InitResults, stepResults, variables, cancellationToken)
                    .ConfigureAwait(false);

                if (flow == FlowDecision.Continue)
                {
                    flow = await RunSectionAsync(sequence, item, StepSection.Main, item.MainSteps, result.MainResults, stepResults, variables, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (!_abandonedStep && item.CleanupSteps.Count > 0)
            {
                var cleanupFlow = await RunSectionAsync(sequence, item, StepSection.Cleanup, item.CleanupSteps, result.CleanupResults, stepResults, variables, cancellationToken)
                    .ConfigureAwait(false);
                if (flow == FlowDecision.Continue && cleanupFlow != FlowDecision.Continue)
                {
                    flow = cleanupFlow;
                }
            }

            result.VerdictSourceStepResult = ResolveVerdictSource(item, result.MainResults);
            result.Verdict = ResolveItemVerdict(item, result, flow, variables);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Verdict = TestVerdict.Cancelled;
            await RunCleanupAfterCancellationAsync(sequence, item, result, stepResults, variables).ConfigureAwait(false);
            throw;
        }
        finally
        {
            result.FinishedAt = DateTimeOffset.Now;
            result.VariablesAfter = VariableSnapshot.Capture(variables);
            _observer.ItemFinished(item, result);
        }
    }

    /// <summary>
    /// Runs the item's cleanup steps after the user cancelled, within <see cref="CleanupGracePeriod"/>.
    /// The grace token is deliberately not linked to the caller's token, which is already cancelled
    /// and would abort every cleanup step at its first cancellation check. A step that was abandoned
    /// keeps cleanup suppressed, because its resources may still be in use by the pending plugin.
    /// </summary>
    private async Task RunCleanupAfterCancellationAsync(
        TestSequence sequence,
        TestItemDefinition item,
        TestItemRunResult result,
        Dictionary<string, TestStepResult> stepResults,
        IDictionary<string, object?> variables)
    {
        if (_abandonedStep || item.CleanupSteps.Count == 0 || result.CleanupResults.Count > 0)
        {
            return;
        }

        using var grace = new CancellationTokenSource(CleanupGracePeriod);
        try
        {
            await RunSectionAsync(sequence, item, StepSection.Cleanup, item.CleanupSteps, result.CleanupResults, stepResults, variables, grace.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The grace period elapsed or cleanup failed outright. The original cancellation is
            // rethrown by the caller and must not be replaced by this one.
        }
    }

    private async Task<FlowDecision> RunSectionAsync(
        TestSequence sequence,
        TestItemDefinition item,
        StepSection section,
        IReadOnlyList<TestStepDefinition> steps,
        IList<TestStepResult> resultList,
        Dictionary<string, TestStepResult> stepResults,
        IDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!step.Enabled)
            {
                var skipped = TestStepResult.Skipped(step.Id, step.Name, "Disabled.");
                resultList.Add(skipped);
                stepResults[step.Id] = skipped;
                continue;
            }

            var startedAt = DateTimeOffset.Now;
            TestStepResult stepResult;
            if (!TryEvaluateRunIf(step, variables, out var shouldRun, out var runIfError))
            {
                // Neither running nor skipping is what the author asked for; the step is an error,
                // and its OnError policy decides what follows.
                _observer.StepStarted(item, step, section);
                stepResult = CreateErrorResult(step, startedAt, $"runIf: {runIfError}", null);
            }
            else if (!shouldRun)
            {
                var skipped = TestStepResult.Skipped(step.Id, step.Name, $"runIf '{step.RunIf}' was false.");
                resultList.Add(skipped);
                stepResults[step.Id] = skipped;
                continue;
            }
            else
            {
                _observer.StepStarted(item, step, section);
                try
                {
                    stepResult = await RunStepWithRetryAsync(sequence, item, step, stepResults, variables, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    stepResult = CreateErrorResult(step, startedAt, "Step was cancelled.", ex);
                    stepResult.Verdict = TestVerdict.Cancelled;
                    resultList.Add(stepResult);
                    stepResults[step.Id] = stepResult;
                    _observer.StepFinished(item, step, section, stepResult);
                    throw;
                }
            }

            resultList.Add(stepResult);
            stepResults[step.Id] = stepResult;
            _observer.StepFinished(item, step, section, stepResult);

            if (_abandonedStep) return FlowDecision.StopSequence;

            if (stepResult.Verdict != TestVerdict.Error)
            {
                continue;
            }

            if (step.OnError == ErrorHandlingMode.Continue)
            {
                continue;
            }

            return step.OnError switch
            {
                ErrorHandlingMode.JumpToCleanup => FlowDecision.JumpToCleanup,
                ErrorHandlingMode.Stop => FlowDecision.StopSequence,
                _ => FlowDecision.StopSequence
            };
        }

        return FlowDecision.Continue;
    }

    /// <summary>
    /// The step's parameters with variables substituted - except those the plugin declares as
    /// <see cref="StepParameterKind.Expression"/>, which reach it as written: their <c>${}</c> are
    /// the expression's operands, and substituting a list variable would turn it into text.
    /// </summary>
    private static Dictionary<string, object?> ResolveParameters(
        ITestStepPlugin plugin,
        TestStepDefinition step,
        IDictionary<string, object?> variables)
    {
        HashSet<string> expressions;
        try
        {
            expressions = plugin.Parameters
                .Where(descriptor => descriptor.Kind == StepParameterKind.Expression)
                .Select(descriptor => descriptor.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A broken declaration is the plugin's bug; resolve everything, as before declarations.
            expressions = [];
        }

        if (expressions.Count == 0)
        {
            return VariableResolver.ResolveDictionary(step.Parameters, variables);
        }

        var substituted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in step.Parameters.Where(pair => !expressions.Contains(pair.Key)))
        {
            substituted[name] = value;
        }

        var resolved = VariableResolver.ResolveDictionary(substituted, variables);
        foreach (var (name, value) in step.Parameters.Where(pair => expressions.Contains(pair.Key)))
        {
            resolved[name] = value;
        }

        return resolved;
    }

    private static bool TryEvaluateRunIf(
        TestStepDefinition step,
        IDictionary<string, object?> variables,
        out bool shouldRun,
        out string? error)
    {
        shouldRun = true;
        error = null;
        if (string.IsNullOrWhiteSpace(step.RunIf))
        {
            return true;
        }

        try
        {
            shouldRun = SequenceExpression.Parse(step.RunIf).EvaluateCondition(variables);
            return true;
        }
        catch (SequenceExpressionException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Runs a step, again while it ends in Error or Fail - or while its <c>until</c> is false - and
    /// <c>retry</c> allows. Each attempt's variable writes apply before <c>until</c> is evaluated,
    /// which is what lets a step that reads SOC be polled until SOC is high enough.
    ///
    /// A quarantined attempt ends the retries at once: its plugin is still running, and calling it
    /// again is the concurrent use quarantine exists to prevent. A poll that runs out of attempts
    /// with its condition still false is a Fail, because what the author waited for never happened.
    /// </summary>
    private async Task<TestStepResult> RunStepWithRetryAsync(
        TestSequence sequence,
        TestItemDefinition item,
        TestStepDefinition step,
        IReadOnlyDictionary<string, TestStepResult> previousStepResults,
        IDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        var retry = step.Retry;
        var maxAttempts = Math.Max(1, retry?.MaxAttempts ?? 1);
        var failedAttempts = new List<TestStepResult>();
        for (var attempt = 1; ; attempt++)
        {
            var result = await RunStepAsync(sequence, item, step, previousStepResults, variables, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (!_abandonedStep) ApplyVariableWrites(step, result, variables);
            }
            catch (Exception ex)
            {
                result.Verdict = TestVerdict.Error;
                result.ErrorMessage = ex.Message;
                result.Exception = ex;
            }

            var failed = result.Verdict is TestVerdict.Error or TestVerdict.Fail;
            var succeeded = !failed;
            if (succeeded && !string.IsNullOrWhiteSpace(retry?.Until))
            {
                try
                {
                    succeeded = SequenceExpression.Parse(retry.Until).EvaluateCondition(variables);
                }
                catch (SequenceExpressionException ex)
                {
                    result.Verdict = TestVerdict.Error;
                    result.ErrorMessage = $"retry until: {ex.Message}";
                    return Finish(result, attempt, failedAttempts);
                }
            }

            if (succeeded || _abandonedStep || attempt >= maxAttempts)
            {
                if (!succeeded && !failed && !_abandonedStep)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.ErrorMessage = $"retry until '{retry!.Until}' was still false after {attempt} attempt(s).";
                }

                return Finish(result, attempt, failedAttempts);
            }

            if (failed)
            {
                failedAttempts.Add(result);
            }

            _observer.Log($"[{item.Name}/{step.Name}] attempt {attempt} of {maxAttempts} ended {result.Verdict}; retrying.");
            if (retry!.IntervalMs > 0)
            {
                await Task.Delay(retry.IntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }

        static TestStepResult Finish(TestStepResult result, int attempts, List<TestStepResult> failedAttempts)
        {
            result.Attempts = attempts;
            result.PreviousAttempts.AddRange(failedAttempts);
            return result;
        }
    }

    private async Task<TestStepResult> RunStepAsync(
        TestSequence sequence,
        TestItemDefinition item,
        TestStepDefinition step,
        IReadOnlyDictionary<string, TestStepResult> previousStepResults,
        IDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        CancellationTokenSource? timeoutCts = null;
        Task<TestStepResult>? execution = null;
        PluginResolution<ITestStepPlugin>? resolution = null;

        // Every result names the code that produced it once a plugin has been resolved, including
        // an error or a timeout - those are the results someone will want to trace.
        TestStepResult Stamped(TestStepResult stepResult)
        {
            stepResult.RequestedPluginVersion = step.PluginVersion;
            if (resolution is { } resolved)
            {
                stepResult.PluginId = resolved.Plugin.Descriptor.PluginId;
                stepResult.PluginVersion = resolved.ResolvedVersion.ToString();
            }

            return stepResult;
        }

        // How far the execution got, so a timeout spent queueing behind another station can say so
        // instead of blaming the plugin - and, just as much, so one that expired before the step was
        // even scheduled (a busy machine) does not claim to have been waiting for anything.
        var stage = ExecutionStage.NotStarted;
        var scope = _resources;
        var exclusive = step.Exclusive
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            resolution = ResolveStepPlugin(step);
            var plugin = resolution.Value.Plugin;
            var resolvedParameters = ResolveParameters(plugin, step, variables);
            if (step.TimeoutMs is <= 0) throw new ArgumentOutOfRangeException(nameof(step.TimeoutMs), "Timeout must be positive or omitted.");
            var timeoutMs = step.TimeoutMs.GetValueOrDefault();
            timeoutCts = timeoutMs > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            if (timeoutCts is not null)
            {
                timeoutCts.CancelAfter(timeoutMs);
            }

            var effectiveToken = timeoutCts?.Token ?? cancellationToken;
            var context = new TestStepExecutionContext
            {
                Sequence = sequence,
                Item = item,
                Step = step,
                Variables = new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase),
                ResolvedParameters = resolvedParameters,
                PreviousStepResults = previousStepResults,
                Instruments = _resources.Scope,
                Transports = _resources.Scope,
                Services = _resources.Scope,
                Log = message => _observer.Log($"[{item.Name}/{step.Name}] {message}"),
                Operator = Operator
            };

            // Include synchronous plugin code in the bounded wait and keep it off the UI thread.
            // The execution gate is released when the plugin actually returns - not when this run
            // stops waiting for it - so a call abandoned on timeout keeps other stations out of a
            // plugin that is not thread-safe until it has really finished.
            execution = Task.Run(async () =>
            {
                effectiveToken.ThrowIfCancellationRequested();

                // Leases first, in one global order (see RuntimeResourceProvider.LeaseAsync), then
                // the plugin gate. A gate holder therefore never waits for a lease, and lease holders
                // wait only for a gate whose holder is running, so two stations cannot hold one each
                // and wait for the other's. Released when the plugin returns, not when this run stops
                // waiting for it: an abandoned step is still using the instrument.
                stage = ExecutionStage.WaitingForLeases;
                using var leases = exclusive.Length == 0
                    ? null
                    : await scope.LeaseAsync(exclusive, effectiveToken).ConfigureAwait(false);
                stage = ExecutionStage.WaitingForGate;
                using var gate = await PluginExecutionGate.EnterAsync(plugin, effectiveToken).ConfigureAwait(false);
                stage = ExecutionStage.Running;
                var settings = plugin.LoadSettings(resolvedParameters);
                return await plugin.ExecuteAsync(context, settings, effectiveToken).ConfigureAwait(false);
            }, CancellationToken.None);
            var result = await execution.WaitAsync(effectiveToken).ConfigureAwait(false);
            effectiveToken.ThrowIfCancellationRequested();
            variables.Clear();
            foreach (var (name, value) in context.Variables) variables[name] = value;
            result.StepId = step.Id;
            result.StepName = step.Name;
            result.StartedAt = result.StartedAt == default ? startedAt : result.StartedAt;
            result.FinishedAt = result.FinishedAt == default ? DateTimeOffset.Now : result.FinishedAt;
            return Stamped(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (
            timeoutCts?.IsCancellationRequested == true &&
            ex.CancellationToken == timeoutCts.Token)
        {
            var message = stage switch
            {
                ExecutionStage.WaitingForLeases when exclusive.Length > 0 =>
                    $"Step timed out after {step.TimeoutMs} ms waiting for exclusive use of '{string.Join(", ", exclusive)}', which another run holds.",
                ExecutionStage.WaitingForGate =>
                    $"Step timed out after {step.TimeoutMs} ms waiting for plugin '{step.PluginId}', which another run is using; it is not thread-safe, so runs take turns.",
                _ => $"Step timed out after {step.TimeoutMs} ms."
            };
            return Stamped(CreateErrorResult(step, startedAt, message, ex));
        }
        catch (OperationCanceledException ex)
        {
            return Stamped(CreateErrorResult(step, startedAt, ex.Message, ex));
        }
        catch (Exception ex)
        {
            return Stamped(CreateErrorResult(step, startedAt, ex.Message, ex));
        }
        finally
        {
            if (execution is { IsCompleted: false })
            {
                // Give cooperative cancellation a short grace period before quarantining the run.
                try { await execution.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false); }
                catch (Exception) { /* The step error is already recorded; observe late faults below. */ }
            }
            if (execution is { IsCompleted: false })
            {
                _abandonedStep = true;
                PendingStepsCompletion = ObservePendingStepAsync(execution, timeoutCts);
            }
            else
            {
                _ = execution?.Exception;
                timeoutCts?.Dispose();
            }
        }
    }

    private static async Task ObservePendingStepAsync(Task execution, CancellationTokenSource? timeoutCts)
    {
        try { await execution.ConfigureAwait(false); }
        catch (Exception) { /* Timeout/cancellation has already been reported. */ }
        finally { timeoutCts?.Dispose(); }
    }

    /// <summary>
    /// Resolves the plugin a step names. When the exact version is gone and a compatible newer one
    /// is used instead, the substitution is logged: the sequence file no longer identifies the code
    /// that produced the results, and that has to be visible in the run log.
    /// </summary>
    private PluginResolution<ITestStepPlugin> ResolveStepPlugin(TestStepDefinition step)
    {
        if (!_pluginRegistry.TryResolve(step.PluginId, step.PluginVersion, out var resolution))
        {
            throw new InvalidOperationException(_pluginRegistry.DescribeMissing(step.PluginId, step.PluginVersion));
        }

        if (resolution.IsSubstituted && _reportedSubstitutions.Add($"{step.PluginId}@{step.PluginVersion}"))
        {
            _observer.Log(
                $"Plugin '{step.PluginId}' version '{step.PluginVersion}' is not installed; " +
                $"running version '{resolution.ResolvedVersion}' instead.");
        }

        return resolution;
    }

    private static TestStepResult CreateErrorResult(TestStepDefinition step, DateTimeOffset startedAt, string message, Exception? exception)
    {
        return new TestStepResult
        {
            StepId = step.Id,
            StepName = step.Name,
            Verdict = TestVerdict.Error,
            ErrorMessage = message,
            Exception = exception,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now
        };
    }

    private static void ApplyVariableWrites(
        TestStepDefinition step,
        TestStepResult stepResult,
        IDictionary<string, object?> variables)
    {
        foreach (var write in step.VariableWrites)
        {
            if (string.IsNullOrWhiteSpace(write.Name))
            {
                continue;
            }

            if (stepResult.Verdict == TestVerdict.Error && !write.WriteOnError)
            {
                continue;
            }

            object? value;
            if (!string.IsNullOrWhiteSpace(write.OutputKey))
            {
                if (!stepResult.Outputs.TryGetValue(write.OutputKey, out value))
                {
                    throw new InvalidOperationException($"Output '{write.OutputKey}' for variable '{write.Name}' was not produced.");
                }
            }
            else
            {
                value = VariableResolver.ResolveValue(write.Value, variables);
            }

            variables[write.Name] = value;
            stepResult.WrittenVariables[write.Name] = value;
        }
    }

    private static TestItemRunResult CreateSkippedItemResult(TestItemDefinition item, string reason)
    {
        var now = DateTimeOffset.Now;
        return new TestItemRunResult
        {
            ItemId = item.Id,
            ItemName = item.Name,
            Verdict = TestVerdict.Skipped,
            SkipReason = reason,
            StartedAt = now,
            FinishedAt = now
        };
    }

    private static TestStepResult? ResolveVerdictSource(TestItemDefinition item, IReadOnlyList<TestStepResult> mainResults)
    {
        if (!string.IsNullOrWhiteSpace(item.VerdictSource.StepId))
        {
            return mainResults.FirstOrDefault(result => string.Equals(result.StepId, item.VerdictSource.StepId, StringComparison.OrdinalIgnoreCase));
        }

        return mainResults.LastOrDefault(result => result.Verdict != TestVerdict.Skipped);
    }

    private static TestVerdict ResolveItemVerdict(
        TestItemDefinition item,
        TestItemRunResult result,
        FlowDecision flow,
        IDictionary<string, object?> variables)
    {
        if (flow == FlowDecision.StopSequence)
        {
            return TestVerdict.Error;
        }

        var allSteps = result.InitResults.Concat(result.MainResults).Concat(result.CleanupResults).ToList();
        if (allSteps.Any(step => step.Verdict == TestVerdict.Error))
        {
            return TestVerdict.Error;
        }

        var verdictSource = result.VerdictSourceStepResult;
        if (verdictSource is null)
        {
            return TestVerdict.Error;
        }

        // Judged first even when another step has already failed the item, so the measurements are
        // recorded either way: a cell-spread check failing must not leave the report unable to say
        // which cell was low. Every check is judged for the same reason.
        var judged = JudgeVerdictSource(item.VerdictSource, verdictSource, variables, result);
        if (judged == TestVerdict.Error)
        {
            return judged;
        }

        // Steps whose output the item judges: judging that output is what replaces the step's own
        // verdict, for a check exactly as for the verdict source.
        var judgedSteps = new List<TestStepResult>();
        if (!string.IsNullOrWhiteSpace(item.VerdictSource.OutputKey))
        {
            judgedSteps.Add(verdictSource);
        }

        var checkVerdicts = new List<TestVerdict>();
        foreach (var check in item.Checks)
        {
            var checkVerdict = JudgeCheck(check, result, variables, judgedSteps);
            if (checkVerdict == TestVerdict.Error)
            {
                return checkVerdict;
            }

            checkVerdicts.Add(checkVerdict);
        }

        // The judgments decide the item, but they cannot overrule another step that judged the
        // DUT bad. Two limit checks in one item is the natural way to write "voltage and current",
        // and letting the second one's Pass hide the first one's Fail ships a failing unit as good.
        if (allSteps.Any(step => step.Verdict == TestVerdict.Fail &&
                                 !judgedSteps.Any(judgedStep => ReferenceEquals(judgedStep, step)) &&
                                 !ReferenceEquals(step, verdictSource)))
        {
            return TestVerdict.Fail;
        }

        if (judged == TestVerdict.Fail || checkVerdicts.Contains(TestVerdict.Fail))
        {
            return TestVerdict.Fail;
        }

        // Pass only when every check passed too; a check that could not judge its value leaves the
        // item as unjudged as it is.
        return checkVerdicts.FirstOrDefault(verdict => verdict != TestVerdict.Pass, judged);
    }

    private static TestVerdict JudgeVerdictSource(
        VerdictSource source,
        TestStepResult verdictSource,
        IDictionary<string, object?> variables,
        TestItemRunResult result)
    {
        var outputKey = source.OutputKey;
        if (!string.IsNullOrWhiteSpace(outputKey))
        {
            if (!verdictSource.Outputs.TryGetValue(outputKey, out var value))
            {
                result.ErrorMessage = $"Step '{verdictSource.StepName}' did not produce verdict output '{outputKey}'.";
                return TestVerdict.Error;
            }

            return JudgeOutput(source, outputKey, value, variables, result);
        }

        return source.JudgeType == VerdictJudgeType.PassFail
            ? verdictSource.Verdict
            : TestVerdict.Inconclusive;
    }

    /// <summary>
    /// Judges one of <see cref="TestItemDefinition.Checks"/>. Anything that stops it being judged
    /// is a fault in the sequence and an Error, as for the verdict source: a check that silently
    /// dropped out would pass the item on the judgments that remain.
    /// </summary>
    private static TestVerdict JudgeCheck(
        VerdictSource check,
        TestItemRunResult result,
        IDictionary<string, object?> variables,
        ICollection<TestStepResult> judgedSteps)
    {
        var label = CheckName(check);
        if (string.IsNullOrWhiteSpace(check.OutputKey))
        {
            result.ErrorMessage = $"Check '{label}' needs an output key.";
            return TestVerdict.Error;
        }

        var step = result.MainResults.FirstOrDefault(candidate =>
            string.Equals(candidate.StepId, check.StepId, StringComparison.OrdinalIgnoreCase));
        if (step is null)
        {
            result.ErrorMessage = $"Check '{label}' judges step '{check.StepId}', which is not a main step that ran.";
            return TestVerdict.Error;
        }

        judgedSteps.Add(step);
        if (!step.Outputs.TryGetValue(check.OutputKey, out var value))
        {
            result.ErrorMessage = $"Step '{step.StepName}' did not produce output '{check.OutputKey}' for check '{label}'.";
            return TestVerdict.Error;
        }

        return JudgeOutput(check, check.OutputKey, value, variables, result);
    }

    /// <summary>What a judgment's records are called: its name, or its output key.</summary>
    private static string CheckName(VerdictSource source) =>
        string.IsNullOrWhiteSpace(source.Name) ? source.OutputKey ?? string.Empty : source.Name.Trim();

    /// <summary>
    /// Judges the configured output and records what it was judged on in
    /// <see cref="TestItemRunResult.Measurements"/>.
    /// </summary>
    private static TestVerdict JudgeOutput(
        VerdictSource source,
        string outputKey,
        object? value,
        IDictionary<string, object?> variables,
        TestItemRunResult result)
    {
        var check = CheckName(source);
        if (source.JudgeType == VerdictJudgeType.Numeric)
        {
            if (!TryResolveBounds(source, variables, out var bounds, out var error))
            {
                result.ErrorMessage = string.Equals(check, outputKey, StringComparison.Ordinal) ? error : $"Check '{check}': {error}";
                return TestVerdict.Error;
            }

            var records = ElementsOf(check, value)
                .Select(element => JudgeNumber(source, check, element.Name, element.Index, element.Value, bounds))
                .ToList();
            result.Measurements.AddRange(records);
            return CombineMeasurements(records);
        }

        var measurement = new MeasurementResult { Name = check, Check = check, RawValue = value };
        if (source.JudgeType == VerdictJudgeType.String)
        {
            measurement.ExpectedString = source.ExpectedString;
            measurement.Verdict = ResolveStringVerdict(source, value);
        }
        else
        {
            measurement.Verdict = ConvertOutputToVerdict(value);
        }

        result.Measurements.Add(measurement);
        return measurement.Verdict;
    }

    /// <summary>
    /// The values a numeric verdict judges. A list or dictionary output is judged element by
    /// element against the same limits, which is what lets one step that reads 80 cell voltages be
    /// one item instead of 80. Each element gets its own record, so a failure names the cell; a
    /// dictionary's keys - usually signal names - become the record names.
    /// </summary>
    private static IEnumerable<(string Name, int? Index, object? Value)> ElementsOf(string outputKey, object? value)
    {
        switch (value)
        {
            case string:
                yield return (outputKey, null, value);
                break;
            case IDictionary dictionary:
                var entryIndex = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    yield return (Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, entryIndex++, entry.Value);
                }

                break;
            case IEnumerable list:
                var elementIndex = 0;
                foreach (var element in list)
                {
                    yield return ($"{outputKey}[{elementIndex}]", elementIndex, element);
                    elementIndex++;
                }

                break;
            default:
                yield return (outputKey, null, value);
                break;
        }
    }

    private static MeasurementResult JudgeNumber(
        VerdictSource source,
        string check,
        string name,
        int? index,
        object? raw,
        NumericBounds bounds)
    {
        var measurement = new MeasurementResult
        {
            Name = name,
            Check = check,
            Index = index,
            RawValue = raw,
            Unit = string.IsNullOrWhiteSpace(source.Unit) ? null : source.Unit.Trim(),
            Comparison = source.Comparison,
            LowerLimit = bounds.Lower,
            UpperLimit = bounds.Upper,
            Expected = bounds.Expected
        };

        if (!UnitConverter.TryConvertToDouble(raw, source.SourceUnit, source.Unit, out var number, out var unit) ||
            !double.IsFinite(number))
        {
            measurement.Verdict = TestVerdict.Inconclusive;
            return measurement;
        }

        measurement.Value = number;
        measurement.Unit = unit;
        measurement.Verdict = source.Comparison.Passes(number, bounds.Lower, bounds.Upper, bounds.Expected)
            ? TestVerdict.Pass
            : TestVerdict.Fail;
        return measurement;
    }

    /// <summary>The bounds a numeric judgment compares against, resolved; only those its comparison reads.</summary>
    private readonly record struct NumericBounds(double? Lower, double? Upper, double? Expected);

    /// <summary>
    /// Any element failing fails the item; otherwise any element that could not be judged leaves it
    /// inconclusive. An output with no elements measured nothing, and passing it would pass a DUT on
    /// no evidence at all.
    /// </summary>
    private static TestVerdict CombineMeasurements(IReadOnlyCollection<MeasurementResult> measurements)
    {
        if (measurements.Count == 0)
        {
            return TestVerdict.Inconclusive;
        }

        if (measurements.Any(measurement => measurement.Verdict == TestVerdict.Fail))
        {
            return TestVerdict.Fail;
        }

        return measurements.All(measurement => measurement.Verdict == TestVerdict.Pass)
            ? TestVerdict.Pass
            : TestVerdict.Inconclusive;
    }

    /// <summary>
    /// The bounds as they stand when the item is judged - only those the comparison reads, so a
    /// leftover limit on an <c>EQ</c> cannot make it error. Every way this can fail is a fault in
    /// the sequence, not in the DUT, so it is an Error rather than a Fail: inverted limits fail every
    /// unit, and a missing or non-numeric bound would otherwise drop out of the comparison and pass
    /// every unit on what remains.
    /// </summary>
    private static bool TryResolveBounds(
        VerdictSource source,
        IDictionary<string, object?> variables,
        out NumericBounds bounds,
        out string? error)
    {
        bounds = default;
        var comparison = source.Comparison;
        if (!Enum.IsDefined(comparison))
        {
            error = $"Unknown numeric comparison '{(int)comparison}'.";
            return false;
        }

        double? lower = null, upper = null, expected = null;
        if ((comparison.UsesLowerLimit() &&
             !TryResolveLimit(source.LowerLimit, source.LowerLimitReference, "lowerLimit", variables, out lower, out error)) ||
            (comparison.UsesUpperLimit() &&
             !TryResolveLimit(source.UpperLimit, source.UpperLimitReference, "upperLimit", variables, out upper, out error)) ||
            (comparison.UsesExpected() &&
             !TryResolveLimit(source.Expected, source.ExpectedReference, "expected", variables, out expected, out error)))
        {
            return false;
        }

        error = MissingBound(comparison, lower, upper, expected);
        if (error is not null)
        {
            return false;
        }

        if (lower > upper)
        {
            error = $"Numeric lowerLimit {lower} is greater than upperLimit {upper}.";
            return false;
        }

        bounds = new NumericBounds(lower, upper, expected);
        return true;
    }

    private static string? MissingBound(NumericComparison comparison, double? lower, double? upper, double? expected)
    {
        if (comparison == NumericComparison.GELE)
        {
            return lower.HasValue || upper.HasValue ? null : "Numeric verdict requires lowerLimit or upperLimit.";
        }

        if (comparison.UsesLowerLimit() && !lower.HasValue)
        {
            return $"Numeric comparison {comparison} requires lowerLimit.";
        }

        if (comparison.UsesUpperLimit() && !upper.HasValue)
        {
            return $"Numeric comparison {comparison} requires upperLimit.";
        }

        return comparison.UsesExpected() && !expected.HasValue
            ? $"Numeric comparison {comparison} requires expected."
            : null;
    }

    private static bool TryResolveLimit(
        double? literal,
        string? reference,
        string field,
        IDictionary<string, object?> variables,
        out double? limit,
        out string? error)
    {
        error = null;
        limit = null;
        if (string.IsNullOrWhiteSpace(reference))
        {
            if (literal is { } number && !double.IsFinite(number))
            {
                error = $"Numeric {field} must be finite.";
                return false;
            }

            limit = literal;
            return true;
        }

        if (!VariableReference.IsWholeValueReference(reference))
        {
            error = $"Numeric {field} '{reference}' must be a single ${{variable}} reference.";
            return false;
        }

        var name = VariableReference.NamesIn(reference)[0];
        if (!variables.TryGetValue(name, out var value))
        {
            error = $"Numeric {field} references variable '{name}', which is not defined.";
            return false;
        }

        if (!NumericValue.TryRead(value, out var resolved))
        {
            error = $"Numeric {field} variable '{name}' must hold a finite number, but holds '{value}'.";
            return false;
        }

        limit = resolved;
        return true;
    }

    private static TestVerdict ResolveStringVerdict(VerdictSource source, object? value)
    {
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var expected = source.ExpectedString ?? string.Empty;
        try
        {
            var passed = source.StringMode switch
            {
                StringJudgeMode.Regex => Regex.IsMatch(text, expected, RegexOptions.None, RegexTimeout),
                _ => string.Equals(text, expected, StringComparison.Ordinal)
            };
            return passed ? TestVerdict.Pass : TestVerdict.Fail;
        }
        catch (ArgumentException)
        {
            return TestVerdict.Inconclusive;
        }
        catch (RegexMatchTimeoutException)
        {
            return TestVerdict.Inconclusive;
        }
    }

    private static TestVerdict ConvertOutputToVerdict(object? value)
    {
        return value switch
        {
            null => TestVerdict.Inconclusive,
            TestVerdict verdict => verdict,
            bool boolean => boolean ? TestVerdict.Pass : TestVerdict.Fail,
            string text when Enum.TryParse<TestVerdict>(text, true, out var verdict) => verdict,
            string text when bool.TryParse(text, out var boolean) => boolean ? TestVerdict.Pass : TestVerdict.Fail,
            _ => TestVerdict.Inconclusive
        };
    }

    private static TestVerdict AggregateSequenceVerdict(IEnumerable<TestItemRunResult> itemResults)
    {
        var materialized = itemResults.ToArray();
        if (materialized.Length == 0)
        {
            return TestVerdict.Inconclusive;
        }

        if (materialized.Any(item => item.Verdict == TestVerdict.Error))
        {
            return TestVerdict.Error;
        }

        if (materialized.Any(item => item.Verdict == TestVerdict.Fail))
        {
            return TestVerdict.Fail;
        }

        if (materialized.All(item => item.Verdict is TestVerdict.Pass or TestVerdict.Skipped))
        {
            return TestVerdict.Pass;
        }

        return TestVerdict.Inconclusive;
    }

    private static bool HasStopError(TestItemDefinition item, TestItemRunResult result)
    {
        return HasStopError(item.InitSteps, result.InitResults) ||
               HasStopError(item.MainSteps, result.MainResults) ||
               HasStopError(item.CleanupSteps, result.CleanupResults);
    }

    private static bool HasStopError(IReadOnlyList<TestStepDefinition> steps, IReadOnlyList<TestStepResult> results)
    {
        foreach (var result in results.Where(result => result.Verdict == TestVerdict.Error))
        {
            var definition = steps.FirstOrDefault(step => string.Equals(step.Id, result.StepId, StringComparison.OrdinalIgnoreCase));
            if (definition?.OnError == ErrorHandlingMode.Stop)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How far a step's execution got before it finished or was abandoned.</summary>
    private enum ExecutionStage
    {
        NotStarted,
        WaitingForLeases,
        WaitingForGate,
        Running
    }

    private enum FlowDecision
    {
        Continue,
        JumpToCleanup,
        StopSequence
    }
}
