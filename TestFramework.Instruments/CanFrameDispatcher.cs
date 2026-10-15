using System.Threading.Channels;

namespace TestFramework.Instruments;

/// <summary>
/// The reader half of an <see cref="ICanChannel"/>, for a driver to build on: its receive loop
/// hands every frame to <see cref="Publish"/>, and its <c>OpenReader</c> returns
/// <see cref="OpenReader"/>. Each reader keeps what its filter matches from the moment it opens,
/// which is the guarantee the interface makes, without every driver writing it again.
///
/// A reader holds at most <c>capacity</c> frames; past that the oldest go, and
/// <see cref="Reader.DroppedFrames"/> says how many - a busy bus must not grow a forgotten reader
/// without bound, and a step that lost frames should be able to say so.
/// </summary>
public sealed class CanFrameDispatcher
{
    private readonly Lock _gate = new();
    private Reader[] _readers = [];

    public ICanReader OpenReader(CanFilter? filter = null, int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var reader = new Reader(this, filter ?? CanFilter.All, capacity);
        lock (_gate)
        {
            _readers = [.. _readers, reader];
        }

        return reader;
    }

    /// <summary>Delivers a received frame to every open reader whose filter matches it. Safe from any thread.</summary>
    public void Publish(CanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        foreach (var reader in Volatile.Read(ref _readers))
        {
            reader.Offer(frame);
        }
    }

    private void Remove(Reader reader)
    {
        lock (_gate)
        {
            _readers = _readers.Where(open => !ReferenceEquals(open, reader)).ToArray();
        }
    }

    public sealed class Reader : ICanReader
    {
        private readonly CanFrameDispatcher _owner;
        private readonly CanFilter _filter;
        private readonly Channel<CanFrame> _frames;
        private readonly int _capacity;
        private long _dropped;

        internal Reader(CanFrameDispatcher owner, CanFilter filter, int capacity)
        {
            _owner = owner;
            _filter = filter;
            _capacity = capacity;
            _frames = Channel.CreateUnbounded<CanFrame>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        }

        /// <summary>Frames this reader matched but had to discard because it was full.</summary>
        public long DroppedFrames => Interlocked.Read(ref _dropped);

        internal void Offer(CanFrame frame)
        {
            if (!_filter.Matches(frame))
            {
                return;
            }

            // Bounded by hand rather than with BoundedChannelFullMode.DropOldest, which drops
            // without telling anyone; the count is what lets a step report lost frames.
            while (_frames.Reader.Count >= _capacity && _frames.Reader.TryRead(out _))
            {
                Interlocked.Increment(ref _dropped);
            }

            _frames.Writer.TryWrite(frame);
        }

        public async Task<CanFrame?> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (_frames.Reader.TryRead(out var ready))
            {
                return ready;
            }

            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waiting.CancelAfter(timeout);
            try
            {
                return await _frames.Reader.ReadAsync(waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                throw new ObjectDisposedException(nameof(ICanReader));
            }
        }

        public void Dispose()
        {
            _owner.Remove(this);
            _frames.Writer.TryComplete();
        }
    }
}
