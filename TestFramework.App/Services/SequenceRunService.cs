using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Resources;
using TestFramework.Abstractions.Resources;

namespace TestFramework.App.Services;

public sealed class SequenceRunService
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly IPluginRegistry _pluginRegistry;
    private readonly ResourcePluginRegistry _resourcePluginRegistry;
    private Task _pendingRecovery = Task.CompletedTask;
    private string? _recoveryError;

    public Task PendingRecovery => Volatile.Read(ref _pendingRecovery);

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
        if (!PendingRecovery.IsCompleted || _recoveryError is not null)
        {
            throw new InvalidOperationException(_recoveryError ?? "上次运行的插件仍未退出，暂不能重新运行。");
        }
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        RuntimeResourceProvider? resources = null;
        var deferredRelease = false;
        try
        {
            if (_recoveryError is not null) throw new InvalidOperationException(_recoveryError);
            var result = new TestSequenceRunResult
            {
                SequenceId = sequence.Id,
                SequenceName = sequence.Name,
                StartedAt = DateTimeOffset.Now,
                InitialVariables = new(sequence.Variables, StringComparer.OrdinalIgnoreCase),
                FinalVariables = new(sequence.Variables, StringComparer.OrdinalIgnoreCase)
            };
            TestSequenceRunner? runner = null;
            try
            {
                resources = await Task.Run(() => new RuntimeResourceBuilder(_resourcePluginRegistry)
                    .BuildAsync(sequence, cancellationToken), CancellationToken.None).ConfigureAwait(false);
                runner = new TestSequenceRunner(_pluginRegistry, observer, resources);
                result = await runner.RunAsync(sequence, cancellationToken).ConfigureAwait(false);
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
                result.Verdict = TestVerdict.Error;
                result.FinishedAt = DateTimeOffset.Now;
                result.ResourceErrors.Add(ex.ToString());
            }

            if (resources is not null)
            {
                if (runner is not null && !runner.PendingStepsCompletion.IsCompleted)
                {
                    deferredRelease = true;
                    Volatile.Write(ref _pendingRecovery, RecoverAsync(runner.PendingStepsCompletion, resources, observer));
                }
                else
                {
                    var error = await DisposeResourcesAsync(resources).ConfigureAwait(false);
                    if (error is not null)
                    {
                        result.ResourceErrors.Add(error);
                        if (result.Verdict != TestVerdict.Cancelled) result.Verdict = TestVerdict.Error;
                    }
                }
            }
            return result;
        }
        finally
        {
            if (!deferredRelease) _runLock.Release();
        }
    }

    private async Task RecoverAsync(Task pendingStep, RuntimeResourceProvider resources, ITestExecutionObserver observer)
    {
        try
        {
            await pendingStep.ConfigureAwait(false);
            var error = await DisposeResourcesAsync(resources).ConfigureAwait(false);
            observer.Log(error ?? "后台步骤已退出，资源清理完成。");
        }
        catch (Exception ex)
        {
            _recoveryError = ex.Message;
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
            _recoveryError = "资源清理失败，请检查设备并重启应用后再运行。" + ex;
            return _recoveryError;
        }
    }
}
