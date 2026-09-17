using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Variables;
using System.Text.RegularExpressions;

namespace TestFramework.Core.Execution;

public sealed class TestSequenceRunner
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private readonly IPluginRegistry _pluginRegistry;
    private readonly ITestExecutionObserver _observer;
    private readonly RuntimeResourceProvider _resources;
    private int _running;
    private bool _abandonedStep;

    // Hosts must await this before disposing resources or reusing plugin instances.
    public Task PendingStepsCompletion { get; private set; } = Task.CompletedTask;

    public TestSequenceRunner(
        IPluginRegistry pluginRegistry,
        ITestExecutionObserver? observer = null,
        RuntimeResourceProvider? resources = null)
    {
        _pluginRegistry = pluginRegistry;
        _observer = observer ?? new NullTestExecutionObserver();
        _resources = resources ?? RuntimeResourceProvider.Empty;
    }

    public async Task<TestSequenceRunResult> RunAsync(TestSequence sequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("The previous run or its pending step has not finished.");
        }

        try
        {
            _abandonedStep = false;
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
            StartedAt = DateTimeOffset.Now,
            InitialVariables = new Dictionary<string, object?>(sequence.Variables, StringComparer.OrdinalIgnoreCase)
        };

        _observer.SequenceStarted(sequence);

        var variables = new Dictionary<string, object?>(sequence.Variables, StringComparer.OrdinalIgnoreCase);
        var cancelled = false;
        try
        {
            foreach (var item in sequence.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.Enabled)
                {
                    result.ItemResults.Add(CreateSkippedItemResult(item));
                    continue;
                }

                var itemResult = new TestItemRunResult
                {
                    ItemId = item.Id,
                    ItemName = item.Name,
                    StartedAt = DateTimeOffset.Now
                };
                result.ItemResults.Add(itemResult);
                await RunItemAsync(sequence, item, itemResult, variables, cancellationToken).ConfigureAwait(false);

                if (_abandonedStep || (itemResult.Verdict == TestVerdict.Error &&
                    (item.MainSteps.Count == 0 || HasStopError(item, itemResult))))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        result.FinishedAt = DateTimeOffset.Now;
        result.Verdict = cancelled ? TestVerdict.Cancelled : AggregateSequenceVerdict(result.ItemResults);
        result.HasPendingExecution = _abandonedStep;
        result.FinalVariables = new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);
        _observer.SequenceFinished(sequence, result);

        if (cancelled) throw new TestSequenceCancelledException(result, cancellationToken);

        return result;
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
            result.Verdict = ResolveItemVerdict(item, result, flow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Verdict = TestVerdict.Cancelled;
            throw;
        }
        finally
        {
            result.FinishedAt = DateTimeOffset.Now;
            result.VariablesAfter = new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);
            _observer.ItemFinished(item, result);
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
                var skipped = TestStepResult.Skipped(step.Id, step.Name);
                resultList.Add(skipped);
                stepResults[step.Id] = skipped;
                continue;
            }

            _observer.StepStarted(item, step, section);
            var startedAt = DateTimeOffset.Now;
            TestStepResult stepResult;
            try
            {
                stepResult = await RunStepAsync(sequence, item, step, stepResults, variables, cancellationToken).ConfigureAwait(false);
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
            try
            {
                if (!_abandonedStep) ApplyVariableWrites(step, stepResult, variables);
            }
            catch (Exception ex)
            {
                stepResult.Verdict = TestVerdict.Error;
                stepResult.ErrorMessage = ex.Message;
                stepResult.Exception = ex;
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

        try
        {
            var plugin = _pluginRegistry.GetRequired(step.PluginId, step.PluginVersion);
            var resolvedParameters = VariableResolver.ResolveDictionary(step.Parameters, variables);
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
                Instruments = _resources,
                Transports = _resources,
                Services = _resources,
                Log = message => _observer.Log($"[{item.Name}/{step.Name}] {message}")
            };

            // Include synchronous plugin code in the bounded wait and keep it off the UI thread.
            execution = Task.Run(async () =>
            {
                effectiveToken.ThrowIfCancellationRequested();
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
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (
            timeoutCts?.IsCancellationRequested == true &&
            ex.CancellationToken == timeoutCts.Token)
        {
            return CreateErrorResult(step, startedAt, $"Step timed out after {step.TimeoutMs} ms.", ex);
        }
        catch (OperationCanceledException ex)
        {
            return CreateErrorResult(step, startedAt, ex.Message, ex);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(step, startedAt, ex.Message, ex);
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

    private static TestItemRunResult CreateSkippedItemResult(TestItemDefinition item)
    {
        var now = DateTimeOffset.Now;
        return new TestItemRunResult
        {
            ItemId = item.Id,
            ItemName = item.Name,
            Verdict = TestVerdict.Skipped,
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

    private static TestVerdict ResolveItemVerdict(TestItemDefinition item, TestItemRunResult result, FlowDecision flow)
    {
        if (flow == FlowDecision.StopSequence)
        {
            return TestVerdict.Error;
        }

        if (result.InitResults.Concat(result.MainResults).Concat(result.CleanupResults).Any(step => step.Verdict == TestVerdict.Error))
        {
            return TestVerdict.Error;
        }

        var verdictSource = result.VerdictSourceStepResult;
        if (verdictSource is null)
        {
            return TestVerdict.Error;
        }

        if (!string.IsNullOrWhiteSpace(item.VerdictSource.OutputKey))
        {
            return verdictSource.Outputs.TryGetValue(item.VerdictSource.OutputKey, out var value)
                ? ResolveConfiguredVerdict(item.VerdictSource, value)
                : TestVerdict.Error;
        }

        return item.VerdictSource.JudgeType == VerdictJudgeType.PassFail
            ? verdictSource.Verdict
            : TestVerdict.Inconclusive;
    }

    private static TestVerdict ResolveConfiguredVerdict(VerdictSource source, object? value)
    {
        return source.JudgeType switch
        {
            VerdictJudgeType.Numeric => ResolveNumericVerdict(source, value),
            VerdictJudgeType.String => ResolveStringVerdict(source, value),
            _ => ConvertOutputToVerdict(value)
        };
    }

    private static TestVerdict ResolveNumericVerdict(VerdictSource source, object? value)
    {
        if (!UnitConverter.TryConvertToDouble(value, source.SourceUnit, source.Unit, out var number))
        {
            return TestVerdict.Inconclusive;
        }

        if (!double.IsFinite(number))
        {
            return TestVerdict.Inconclusive;
        }

        if (source.LowerLimit.HasValue && number < source.LowerLimit.Value)
        {
            return TestVerdict.Fail;
        }

        if (source.UpperLimit.HasValue && number > source.UpperLimit.Value)
        {
            return TestVerdict.Fail;
        }

        return TestVerdict.Pass;
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

    private enum FlowDecision
    {
        Continue,
        JumpToCleanup,
        StopSequence
    }
}
