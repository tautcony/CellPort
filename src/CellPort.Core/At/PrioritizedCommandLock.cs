namespace CellPort.Core.At;

/// <summary>Bounded FIFO-within-priority async lock used to serialize complete AT exchanges.</summary>
internal sealed class PrioritizedCommandLock : IDisposable
{
    private const int MaximumQueuedCommands = 256;
    private readonly object _gate = new();
    private readonly SortedSet<Waiter> _waiters = new(WaiterComparer.Instance);
    private bool _held;
    private bool _disposed;
    private long _sequence;

    public async Task<Lease?> AcquireAsync(
        AtCommandPriority priority,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_held)
            {
                _held = true;
                return new Lease(this);
            }
            if (_waiters.Count >= MaximumQueuedCommands || timeout <= TimeSpan.Zero)
            {
                return null;
            }

            waiter = new Waiter(priority, _sequence++, cancellationToken);
            _waiters.Add(waiter);
        }

        try
        {
            return await waiter.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var grantedLease = AbandonOrTakeGranted(waiter);
            grantedLease?.Dispose();
            return null;
        }
        catch (OperationCanceledException)
        {
            var grantedLease = AbandonOrTakeGranted(waiter);
            grantedLease?.Dispose();
            throw;
        }
    }

    private Lease? AbandonOrTakeGranted(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.State == WaiterState.Queued)
            {
                waiter.State = WaiterState.Abandoned;
                _waiters.Remove(waiter);
                waiter.Completion.TrySetCanceled(waiter.CancellationToken);
                return null;
            }

            return waiter.State == WaiterState.Granted
                ? waiter.Completion.Task.GetAwaiter().GetResult()
                : null;
        }
    }

    private void Release()
    {
        Waiter? granted = null;
        lock (_gate)
        {
            while (_waiters.Count > 0)
            {
                var next = _waiters.Min!;
                _waiters.Remove(next);
                if (next.State != WaiterState.Queued)
                {
                    continue;
                }

                next.State = WaiterState.Granted;
                granted = next;
                break;
            }

            if (granted is null)
            {
                _held = false;
                return;
            }
        }

        granted.Completion.TrySetResult(new Lease(this));
    }

    public void Dispose()
    {
        List<Waiter> abandoned;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            abandoned = [.. _waiters];
            _waiters.Clear();
            foreach (var waiter in abandoned)
            {
                waiter.State = WaiterState.Abandoned;
            }
        }

        foreach (var waiter in abandoned)
        {
            waiter.Completion.TrySetException(new ObjectDisposedException(nameof(PrioritizedCommandLock)));
        }
    }

    internal sealed class Lease(PrioritizedCommandLock owner) : IDisposable
    {
        private PrioritizedCommandLock? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private sealed class Waiter(AtCommandPriority priority, long sequence, CancellationToken cancellationToken)
    {
        public AtCommandPriority Priority { get; } = priority;
        public long Sequence { get; } = sequence;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<Lease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WaiterState State { get; set; } = WaiterState.Queued;
    }

    private enum WaiterState
    {
        Queued,
        Granted,
        Abandoned,
    }

    private sealed class WaiterComparer : IComparer<Waiter>
    {
        public static WaiterComparer Instance { get; } = new();

        public int Compare(Waiter? left, Waiter? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var priority = left.Priority.CompareTo(right.Priority);
            return priority != 0 ? priority : left.Sequence.CompareTo(right.Sequence);
        }
    }
}
