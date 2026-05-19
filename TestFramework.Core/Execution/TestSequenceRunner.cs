using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Variables;

namespace TestFramework.Core.Execution;

public sealed class TestSequenceRunner
{
    private readonly IPluginRegistry _pluginRegistry;
    private readonly ITestExecutionObserver _observer;
    private readonly RuntimeResourceProvider _resources;

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

        var result = new TestSequenceRunResult
        {
            SequenceId = sequence.Id,
            SequenceName = sequence.Name,
            StartedAt = DateTimeOffset.Now,
            InitialVariables = new Dictionary<string, object?>(sequence.Variables, StringComparer.OrdinalIgnoreCase)
        };

        _observer.SequenceStarted(sequence);

        var variables = new Dictionary<string, object?>(sequence.Variables, StringComparer.OrdinalIgnoreCase);
        var stopSequence = false;

        foreach (var item in sequence.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!item.Enabled)
            {
                result.ItemResults.Add(CreateSkippedItemResult(item));
                continue;
            }

            var itemResult = await RunItemAsync(sequence, item, variables, cancellationToken).ConfigureAwait(false);
            result.ItemResults.Add(itemResult);

            if (itemResult.Verdict == TestVerdict.Error && HasStopError(item, itemResult))
            {
                stopSequence = true;
            }

            if (stopSequence)
            {
                break;
            }
        }

        result.FinishedAt = DateTimeOffset.Now;
        result.Verdict = AggregateSequenceVerdict(result.ItemResults);
        result.FinalVariables = new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);
        _observer.SequenceFinished(sequence, result);

        return result;
    }

    private async Task<TestItemRunResult> RunItemAsync(
        TestSequence sequence,
        TestItemDefinition item,
        IDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        var result = new TestItemRunResult
        {
            ItemId = item.Id,
            ItemName = item.Name,
            StartedAt = DateTimeOffset.Now
        };

        var stepResults = new Dictionary<string, TestStepResult>(StringComparer.OrdinalIgnoreCase);
        var flow = FlowDecision.Continue;

        _observer.ItemStarted(item);

        if (item.MainSteps.Count == 0)
        {
            result.Verdict = TestVerdict.Error;
            result.FinishedAt = DateTimeOffset.Now;
            _observer.ItemFinished(item, result);
            return result;
        }

        flow = await RunSectionAsync(sequence, item, StepSection.Init, item.InitSteps, result.InitResults, stepResults, variables, cancellationToken)
            .ConfigureAwait(false);

        if (flow == FlowDecision.Continue)
        {
            flow = await RunSectionAsync(sequence, item, StepSection.Main, item.MainSteps, result.MainResults, stepResults, variables, cancellationToken)
                .ConfigureAwait(false);
        }

        if (flow is FlowDecision.Continue or FlowDecision.JumpToCleanup)
        {
            await RunSectionAsync(sequence, item, StepSection.Cleanup, item.CleanupSteps, result.CleanupResults, stepResults, variables, cancellationToken)
                .ConfigureAwait(false);
        }

        result.VerdictSourceStepResult = ResolveVerdictSource(item, result.MainResults);
        result.Verdict = ResolveItemVerdict(item, result, flow);
        result.FinishedAt = DateTimeOffset.Now;
        result.VariablesAfter = new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);

        _observer.ItemFinished(item, result);
        return result;
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
            var stepResult = await RunStepAsync(sequence, item, step, stepResults, variables, cancellationToken).ConfigureAwait(false);
            ApplyVariableWrites(step, stepResult, variables);
            resultList.Add(stepResult);
            stepResults[step.Id] = stepResult;
            _observer.StepFinished(item, step, section, stepResult);

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

        try
        {
            var plugin = _pluginRegistry.GetRequired(step.PluginId, step.PluginVersion);
            var resolvedParameters = VariableResolver.ResolveDictionary(step.Parameters, variables);
            var settings = plugin.LoadSettings(resolvedParameters);
            var timeoutMs = step.TimeoutMs.GetValueOrDefault();
            using var timeoutCts = timeoutMs > 0
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
                Variables = variables,
                ResolvedParameters = resolvedParameters,
                PreviousStepResults = previousStepResults,
                Instruments = _resources,
                Transports = _resources,
                Services = _resources,
                Log = message => _observer.Log($"[{item.Name}/{step.Name}] {message}")
            };

            var result = await plugin.ExecuteAsync(context, settings, effectiveToken).ConfigureAwait(false);
            result.StepId = string.IsNullOrWhiteSpace(result.StepId) ? step.Id : result.StepId;
            result.StepName = string.IsNullOrWhiteSpace(result.StepName) ? step.Name : result.StepName;
            result.StartedAt = result.StartedAt == default ? startedAt : result.StartedAt;
            result.FinishedAt = result.FinishedAt == default ? DateTimeOffset.Now : result.FinishedAt;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && step.TimeoutMs.GetValueOrDefault() > 0)
        {
            return CreateErrorResult(step, startedAt, $"Step timed out after {step.TimeoutMs} ms.", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateErrorResult(step, startedAt, ex.Message, ex);
        }
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
                    continue;
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

        if (!string.IsNullOrWhiteSpace(item.VerdictSource.OutputKey) &&
            verdictSource.Outputs.TryGetValue(item.VerdictSource.OutputKey, out var value))
        {
            return ConvertOutputToVerdict(value);
        }

        return verdictSource.Verdict;
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
