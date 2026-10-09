using System.Runtime.CompilerServices;
using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Execution;

/// <summary>
/// Serialises calls into a step plugin that does not declare <see cref="ITestStepPlugin.IsThreadSafe"/>,
/// across every runner in the process.
///
/// A plugin is one instance shared by every step and every run, and several stations in one host
/// means several runners calling it at once. Most plugins were written for one bench - a field
/// holding the last frame, a session opened in <c>LoadSettings</c> - and fail in ways that look like
/// hardware faults when two stations interleave. Taking turns is slower only where a plugin has not
/// said it can do better, and it cannot corrupt a measurement.
///
/// Process-wide and keyed by the instance, because that is what is shared: two runners resolving
/// the same plugin get the same object out of the registry. A weak table, so the gate goes when the
/// plugin does.
/// </summary>
internal static class PluginExecutionGate
{
    private static readonly ConditionalWeakTable<ITestStepPlugin, SemaphoreSlim> Gates = new();

    /// <summary>
    /// Waits for the plugin's turn; dispose the result to give it up. Null for a thread-safe plugin,
    /// which never waits.
    /// </summary>
    public static async Task<IDisposable?> EnterAsync(ITestStepPlugin plugin, CancellationToken cancellationToken)
    {
        if (IsThreadSafe(plugin))
        {
            return null;
        }

        var gate = Gates.GetValue(plugin, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Turn(gate);
    }

    private static bool IsThreadSafe(ITestStepPlugin plugin)
    {
        try
        {
            return plugin.IsThreadSafe;
        }
        catch (Exception)
        {
            // A plugin that cannot answer is treated as one that has not thought about it.
            return false;
        }
    }

    private sealed class Turn(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
