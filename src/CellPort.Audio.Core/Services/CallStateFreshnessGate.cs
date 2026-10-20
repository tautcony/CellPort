using CellPort.Core.Services;

namespace CellPort.Audio.Core.Services;

/// <summary>
/// Independently expires outgoing audio authorization when the selected active call snapshot
/// becomes older than three seconds. The owner handles OutgoingMustClose by muting the host.
/// </summary>
public sealed class CallStateFreshnessGate : IDisposable
{
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;
    private long _sampledAtTimestamp;
    private bool _authorized;
    private bool _disposed;

    public CallStateFreshnessGate(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = _timeProvider.CreateTimer(
            static state => ((CallStateFreshnessGate)state!).ExpireIfStale(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? OutgoingMustClose;

    public bool IsOutgoingAuthorized
    {
        get
        {
            var notify = false;
            lock (_gate)
            {
                if (!_authorized)
                {
                    return false;
                }
                if (_timeProvider.GetElapsedTime(_sampledAtTimestamp) < MaximumSnapshotAge)
                {
                    return true;
                }

                _authorized = false;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                notify = true;
            }

            if (notify)
            {
                NotifyClose();
            }
            return false;
        }
    }

    /// <summary>Arms the timer only for one complete, unique active ordinary voice call.</summary>
    public bool Observe(CallSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var selectedCall = snapshot.SelectedVoiceCall;
        var age = _timeProvider.GetElapsedTime(snapshot.SampledAtTimestamp);
        if (snapshot.IsUnknown || selectedCall is null || age >= MaximumSnapshotAge)
        {
            Revoke();
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sampledAtTimestamp = snapshot.SampledAtTimestamp;
            _authorized = true;
            _timer.Change(MaximumSnapshotAge - age, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    public void Revoke()
    {
        var notify = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            notify = _authorized;
            _authorized = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (notify)
        {
            NotifyClose();
        }
    }

    private void ExpireIfStale()
    {
        var notify = false;
        lock (_gate)
        {
            if (!_authorized)
            {
                return;
            }

            var elapsed = _timeProvider.GetElapsedTime(_sampledAtTimestamp);
            if (elapsed < MaximumSnapshotAge)
            {
                _timer.Change(MaximumSnapshotAge - elapsed, Timeout.InfiniteTimeSpan);
                return;
            }

            _authorized = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            notify = true;
        }

        if (notify)
        {
            NotifyClose();
        }
    }

    private void NotifyClose()
    {
        try
        {
            OutgoingMustClose?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // The authorization remains revoked even if a host mute callback fails.
        }
    }

    public void Dispose()
    {
        Revoke();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _authorized = false;
            _timer.Dispose();
        }
    }
}
