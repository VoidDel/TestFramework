using System.Collections.Concurrent;

namespace TestFramework.Core.Resources;

/// <summary>
/// One exclusive lock per resource alias, for steps that declare <c>exclusive</c>.
///
/// It belongs to the host that owns the resources rather than to one <see cref="RuntimeResourceProvider"/>,
/// because a line's resources can be rebuilt after a fault while a station is still using the
/// previous generation. Two generations with two lock tables would let two stations talk to the one
/// physical instrument at once - exactly during recovery, when it matters most.
/// </summary>
public sealed class ResourceLeaseTable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Waits for exclusive use of <paramref name="alias"/>; dispose the result to give it up.</summary>
    public async Task<IDisposable> LeaseAsync(string alias, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        var gate = _locks.GetOrAdd(alias, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
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
