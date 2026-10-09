using System.Runtime.CompilerServices;

namespace TestFramework.Core.Resources;

/// <summary>
/// Serialises <c>CreateAsync</c> calls into one resource plugin instance across the process.
///
/// A driver plugin is one instance for every station, and two stations opening at the same moment
/// call it at once. Vendor libraries behind a driver - a CAN card's SDK, a VISA implementation -
/// are not reliably safe to initialise concurrently, and the failure looks like a flaky instrument
/// rather than a race. Opening a resource is rare, so always taking turns costs nothing worth
/// having a declaration for.
/// </summary>
internal static class ResourceCreationGate
{
    private static readonly ConditionalWeakTable<object, SemaphoreSlim> Gates = new();

    public static async Task<IDisposable> EnterAsync(object plugin, CancellationToken cancellationToken)
    {
        var gate = Gates.GetValue(plugin, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Turn(gate);
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
