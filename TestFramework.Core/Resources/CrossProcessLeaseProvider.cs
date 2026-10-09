namespace TestFramework.Core.Resources;

/// <summary>
/// Exclusive use of a named instrument across processes - and, with the directory on a network
/// share, across machines: one DMM behind a switch matrix that each station's own runner process
/// talks to.
///
/// A lease is a lock file under one directory, opened with no sharing. The operating system owns
/// the lock, so a process that crashes or is killed mid-step releases it with its handles; there is
/// no stale lock to clean up and no timeout to guess, which a "file exists" or a PID-in-a-file
/// scheme would both need. Waiting is polling, because there is no portable way to block on another
/// process's file handle; the interval is short next to any instrument transaction.
///
/// The lock files are left in place when released. Deleting one on release races another process
/// opening it - on Unix the second would lock a file that is already unlinked, and two processes
/// would each believe they hold the instrument.
/// </summary>
public sealed class CrossProcessLeaseProvider : IResourceLeaseProvider
{
    private readonly string _directory;
    private readonly TimeSpan _pollInterval;

    /// <param name="directory">
    /// Where the lock files live. Every process sharing an instrument must use the same directory,
    /// and the same lock name for that instrument.
    /// </param>
    public CrossProcessLeaseProvider(string directory, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(50);
    }

    public string Directory => _directory;

    public async Task<IDisposable> LeaseAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        System.IO.Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, FileNameFor(name));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
                return new Lease(handle);
            }
            catch (IOException)
            {
                // Held by another process (or another lease in this one): wait for it.
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The lock file for a name. Names are compared case-insensitively, like aliases, and anything
    /// that cannot be part of a file name is replaced, so "GPIB0::22" is a usable lock name.
    /// </summary>
    internal static string FileNameFor(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        invalid.Add(':');
        var safe = new string(name.Trim().ToLowerInvariant().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe + ".lock";
    }

    private sealed class Lease(FileStream handle) : IDisposable
    {
        private FileStream? _handle = handle;

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }
}
