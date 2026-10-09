using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Resources;

namespace TestFramework.Core.Execution;

/// <summary>
/// Runs sequences on one bench, one at a time, owning everything around a run that has to be right
/// for the next one to be safe.
///
/// <see cref="TestSequenceRunner"/> runs a sequence; it does not decide when a bench may run the
/// next one. Those decisions used to be every host's to re-derive, and each is easy to get wrong
/// quietly:
///
/// - <b>One runner for the life of the bench.</b> The runner's quarantine gate is per instance; a
///   new runner per run forgets the plugin the previous one abandoned and calls it again while it
///   is still driving the hardware.
/// - <b>An Error makes the station's shared resources suspect.</b> Something threw part-way through
///   a step, possibly mid-transaction on an instrument, and the next DUT must not inherit that
///   state, so the station scope is marked stale and rebuilt at the start of the next run. A
///   <c>Fail</c> costs no reconnection: a low reading says nothing about the CAN channel.
/// - <b>A plugin that has not exited keeps its resources alive</b>, and the bench refuses to run
///   until it has, because releasing them under it - or starting the next DUT against them - is
///   exactly what quarantine exists to prevent.
/// - <b>A cleanup that failed leaves the bench faulted.</b> The device state is unknown and the next
///   run would measure against it, so runs are refused until the host clears the fault, which it
///   should do only once someone has looked at the hardware.
///
/// A refusal is a <see cref="TestRunRefusedException"/> carrying the reason, so a host can word it
/// for its operators. Without a station, each run opens and closes what the sequence declares.
/// </summary>
public sealed class TestRunSession
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ResourcePluginRegistry _resourcePlugins;
    private readonly StationResourceHost? _station;
    private readonly ForwardingObserver _observer = new();
    private readonly TestSequenceRunner _runner;
    private Task _pendingRecovery = Task.CompletedTask;
    private string? _fault;

    public TestRunSession(
        IPluginRegistry plugins,
        ResourcePluginRegistry resourcePlugins,
        StationResourceHost? station = null,
        TestRunSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(resourcePlugins);
        _resourcePlugins = resourcePlugins;
        _station = station;
        options ??= new TestRunSessionOptions();
        _runner = new TestSequenceRunner(plugins, _observer)
        {
            SequenceResolver = options.SequenceResolver,
            Operator = options.Operator ?? NoOperatorInteraction.Instance,
            CleanupGracePeriod = options.CleanupGracePeriod ?? TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>Completes when a plugin abandoned by the last run has exited and its resources are released.</summary>
    public Task PendingRecovery => Volatile.Read(ref _pendingRecovery);

    /// <summary>Why the bench refuses to run until <see cref="ClearFault"/>; null when it is not faulted.</summary>
    public string? Fault => Volatile.Read(ref _fault);

    /// <summary>
    /// Accepts runs again after a cleanup failure. Call it once the hardware has been checked -
    /// it is the host's word that the device is in a known state, not a retry button.
    /// </summary>
    public void ClearFault()
    {
        Volatile.Write(ref _fault, null);
        _station?.Invalidate();
    }

    /// <summary>
    /// Runs <paramref name="sequence"/> and returns its result, which always comes back - cancelled,
    /// errored or not - because it is the record of what the DUT was asked to do. Throws
    /// <see cref="TestRunRefusedException"/> without touching anything when the bench may not run.
    /// </summary>
    public async Task<TestSequenceRunResult> RunAsync(
        TestSequence sequence,
        TestRunInfo? runInfo = null,
        ITestExecutionObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ThrowIfRefused();

        // Refused rather than queued: a second run on a bench that is already running is a host
        // bug, and quietly waiting would start a DUT the operator thought had been turned away.
        if (!await _runLock.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            throw new TestRunRefusedException(TestRunRefusal.RunInProgress, "A run is already in progress on this bench.");
        }

        var deferredRelease = false;
        try
        {
            ThrowIfRefused();
            var info = runInfo ?? new TestRunInfo();
            var result = new TestSequenceRunResult
            {
                SequenceId = sequence.Id,
                SequenceName = sequence.Name,
                SequenceVersion = sequence.Version,
                RunInfo = info.Clone(),
                FrameworkVersion = FrameworkBuild.Version,
                FrameworkContractVersion = FrameworkContract.Version.ToString(),
                StartedAt = DateTimeOffset.Now,
                InitialVariables = new Dictionary<string, object?>(sequence.Variables, StringComparer.OrdinalIgnoreCase),
                FinalVariables = new Dictionary<string, object?>(sequence.Variables, StringComparer.OrdinalIgnoreCase)
            };

            RuntimeResourceProvider? resources = null;
            try
            {
                resources = await Task.Run(() => _station is null
                    ? new RuntimeResourceBuilder(_resourcePlugins).BuildAsync(sequence, cancellationToken)
                    : _station.BeginRunAsync(sequence, cancellationToken), CancellationToken.None).ConfigureAwait(false);

                // Attached under the run lock, so it changes only between runs. Deliberately left
                // attached afterwards: a plugin that outlived its timeout keeps logging, and those
                // lines belong to the run that started it.
                _observer.Attach(observer);
                result = await _runner.RunAsync(sequence, resources, info, cancellationToken).ConfigureAwait(false);
            }
            catch (TestSequenceCancelledException ex)
            {
                result = ex.Result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.Verdict = TestVerdict.Cancelled;
                result.FinishedAt = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                // Opening the resources failed, or the runner refused: either way nothing was
                // measured, and the result says why.
                result.Verdict = TestVerdict.Error;
                result.FinishedAt = DateTimeOffset.Now;
                result.ResourceErrors.Add(ex.ToString());
            }

            // Decided on the verdict, not on whether an exception reached here: a step that threw
            // comes back from the runner as an Error result, and that is the CAN-channel-died case.
            if (result.Verdict == TestVerdict.Error)
            {
                _station?.Invalidate();
            }

            if (resources is not null)
            {
                if (!_runner.PendingStepsCompletion.IsCompleted)
                {
                    deferredRelease = true;
                    Volatile.Write(ref _pendingRecovery, RecoverAsync(_runner.PendingStepsCompletion, resources, observer));
                }
                else if (await DisposeResourcesAsync(resources).ConfigureAwait(false) is { } error)
                {
                    result.ResourceErrors.Add(error);
                    if (result.Verdict != TestVerdict.Cancelled)
                    {
                        result.Verdict = TestVerdict.Error;
                    }
                }
            }

            return result;
        }
        finally
        {
            // Held until recovery finishes when a step was abandoned, so the next caller is turned
            // away before anything is opened for it.
            if (!deferredRelease)
            {
                _runLock.Release();
            }
        }
    }

    private void ThrowIfRefused()
    {
        if (Fault is { } fault)
        {
            throw new TestRunRefusedException(TestRunRefusal.Faulted, fault);
        }

        if (!PendingRecovery.IsCompleted)
        {
            throw new TestRunRefusedException(
                TestRunRefusal.PluginStillRunning,
                "A plugin from the previous run has not exited yet; its resources stay open until it does.");
        }
    }

    private async Task RecoverAsync(Task pendingStep, RuntimeResourceProvider resources, ITestExecutionObserver? observer)
    {
        try
        {
            await pendingStep.ConfigureAwait(false);
            var error = await DisposeResourcesAsync(resources).ConfigureAwait(false);
            observer?.Log(error ?? "The abandoned step has exited; its resources are released.");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, $"Recovering from the abandoned step failed: {ex.Message}");
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<string?> DisposeResourcesAsync(RuntimeResourceProvider resources)
    {
        try
        {
            await resources.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            var fault = $"Releasing the run's resources failed, so the device state is unknown. Check the hardware, then clear the fault. {ex}";
            Volatile.Write(ref _fault, fault);
            _station?.Invalidate();
            return fault;
        }
    }

    /// <summary>
    /// Forwards to the observer of the run in progress. The runner takes its observer once, at
    /// construction, while a caller supplies one per run.
    /// </summary>
    private sealed class ForwardingObserver : ITestExecutionObserver
    {
        private ITestExecutionObserver? _target;

        public void Attach(ITestExecutionObserver? observer) => Volatile.Write(ref _target, observer);

        private ITestExecutionObserver? Target => Volatile.Read(ref _target);

        public void SequenceStarted(TestSequence sequence) => Target?.SequenceStarted(sequence);

        public void SequenceFinished(TestSequence sequence, TestSequenceRunResult result) => Target?.SequenceFinished(sequence, result);

        public void ItemStarted(TestItemDefinition item) => Target?.ItemStarted(item);

        public void ItemFinished(TestItemDefinition item, TestItemRunResult result) => Target?.ItemFinished(item, result);

        public void StepStarted(TestItemDefinition item, TestStepDefinition step, StepSection section) =>
            Target?.StepStarted(item, step, section);

        public void StepFinished(TestItemDefinition item, TestStepDefinition step, StepSection section, TestStepResult result) =>
            Target?.StepFinished(item, step, section, result);

        public void Log(string message) => Target?.Log(message);
    }
}

public sealed class TestRunSessionOptions
{
    public ISequenceResolver? SequenceResolver { get; init; }

    public IOperatorInteraction? Operator { get; init; }

    public TimeSpan? CleanupGracePeriod { get; init; }
}

public enum TestRunRefusal
{
    /// <summary>Another run on this bench has not returned yet.</summary>
    RunInProgress,

    /// <summary>A plugin abandoned by the previous run is still executing.</summary>
    PluginStillRunning,

    /// <summary>A cleanup failed and the host has not cleared the fault.</summary>
    Faulted
}

/// <summary>A run the bench would not start; nothing was opened or measured.</summary>
public sealed class TestRunRefusedException : InvalidOperationException
{
    public TestRunRefusedException(TestRunRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public TestRunRefusal Reason { get; }
}
