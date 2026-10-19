using System.Collections.Immutable;

namespace CellPort.Core.Services;

public enum TrackedCallState
{
    Active,
    Held,
    Dialing,
    Alerting,
    Incoming,
    Waiting,
    Unknown,
}

/// <summary>Identity for one call within one module connection and call generation.</summary>
public sealed record CallKey(
    long ModuleGeneration,
    long CallGeneration,
    int Index,
    CallDirection Direction);

public sealed record TrackedCall(CallKey Key, ClccCallEntry Entry, TrackedCallState State);

/// <summary>
/// Complete immutable view of one CLCC observation. A failed observation never proves idle.
/// SampledAtTimestamp uses the injected TimeProvider's monotonic timestamp domain.
/// </summary>
public sealed record CallSnapshot(
    ClccQueryOutcome QueryOutcome,
    bool IsModuleConnected,
    long ModuleGeneration,
    long SampledAtTimestamp,
    ImmutableArray<TrackedCall> Calls,
    TrackedCall? SelectedVoiceCall,
    int NonVoiceOccupancyCount,
    string? Diagnostic = null)
{
    public bool IsUnknown => !IsModuleConnected || QueryOutcome != ClccQueryOutcome.Complete;
    public bool IsConfirmedEmpty => !IsUnknown && Calls.IsEmpty;
    public bool HasCallOrUnknownOccupancy => IsUnknown || !Calls.IsEmpty;
}

/// <summary>
/// Tracks CLCC entries across snapshots and invalidates identities across module reconnects.
/// It never chooses among multiple ordinary voice calls.
/// </summary>
public sealed class CallTracker(TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<int, TrackedCall> _knownCalls = [];
    private long _moduleGeneration;
    private long _nextCallGeneration;
    private bool _isModuleConnected;
    private CallSnapshot _current = new(
        ClccQueryOutcome.TransportFailure, false, 0, 0,
        ImmutableArray<TrackedCall>.Empty, null, 0, "No module session has been started.");

    public long BeginModuleSession()
    {
        lock (_gate)
        {
            _moduleGeneration = checked(_moduleGeneration + 1);
            _isModuleConnected = true;
            _knownCalls.Clear();
            _current = UnknownSnapshot(ClccQueryOutcome.TransportFailure,
                "A fresh module session needs a new CLCC snapshot.");
            return _moduleGeneration;
        }
    }

    public bool IsModuleConnected
    {
        get
        {
            lock (_gate)
            {
                return _isModuleConnected;
            }
        }
    }

    public long ModuleGeneration
    {
        get
        {
            lock (_gate)
            {
                return _moduleGeneration;
            }
        }
    }

    public void MarkModuleDisconnected()
    {
        lock (_gate)
        {
            _isModuleConnected = false;
            _knownCalls.Clear();
            _current = UnknownSnapshot(ClccQueryOutcome.TransportFailure,
                "Module disconnected; call continuity is no longer known.");
        }
    }

    public CallSnapshot Apply(ClccCallListResult query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            if (!_isModuleConnected)
            {
                _current = UnknownSnapshot(ClccQueryOutcome.TransportFailure,
                    "Cannot apply CLCC data without an active module session.");
                return _current;
            }

            return ApplyLocked(query);
        }
    }

    /// <summary>Applies a query only if it belongs to the same module generation that issued it.</summary>
    public bool TryApply(ClccCallListResult query, long expectedModuleGeneration, out CallSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            if (!_isModuleConnected || _moduleGeneration != expectedModuleGeneration)
            {
                snapshot = _current;
                return false;
            }

            snapshot = ApplyLocked(query);
            return true;
        }
    }

    private CallSnapshot ApplyLocked(ClccCallListResult query)
    {

            if (!query.IsComplete)
            {
                // The tracker cannot know whether a call ended and an index was reused
                // during an incomplete observation, so no prior identity may be reused.
                _knownCalls.Clear();
                _current = UnknownSnapshot(query.Outcome, query.Diagnostic);
                return _current;
            }

            var indices = new HashSet<int>();
            foreach (var entry in query.Entries)
            {
                if (!indices.Add(entry.Index))
                {
                    _knownCalls.Clear();
                    _current = UnknownSnapshot(ClccQueryOutcome.MalformedResponse,
                        "CLCC contains a duplicate call index.");
                    return _current;
                }
            }

            var nextKnown = new Dictionary<int, TrackedCall>();
            var calls = ImmutableArray.CreateBuilder<TrackedCall>(query.Entries.Length);
            foreach (var entry in query.Entries)
            {
                TrackedCall tracked;
                if (_knownCalls.TryGetValue(entry.Index, out var prior) && IsSameCall(prior.Entry, entry))
                {
                    tracked = prior with { Entry = entry, State = MapState(entry.Status) };
                }
                else
                {
                    var key = new CallKey(
                        _moduleGeneration,
                        checked(++_nextCallGeneration),
                        entry.Index,
                        entry.Direction);
                    tracked = new TrackedCall(key, entry, MapState(entry.Status));
                }

                nextKnown.Add(entry.Index, tracked);
                calls.Add(tracked);
            }

            _knownCalls.Clear();
            foreach (var (index, tracked) in nextKnown)
            {
                _knownCalls.Add(index, tracked);
            }

            var completedCalls = calls.ToImmutable();
            var voiceCalls = completedCalls.Where(call => call.Entry.Mode == 0).ToArray();
            var selected = voiceCalls.Length == 1 && voiceCalls[0].State == TrackedCallState.Active
                ? voiceCalls[0]
                : null;

            _current = new CallSnapshot(
                ClccQueryOutcome.Complete,
                true,
                _moduleGeneration,
                _timeProvider.GetTimestamp(),
                completedCalls,
                selected,
                completedCalls.Count(call => call.Entry.IsNonVoiceOccupancy));
            return _current;
    }

    public CallSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public TimeSpan GetAge(CallSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return _timeProvider.GetElapsedTime(snapshot.SampledAtTimestamp);
    }

    public TimeSpan GetElapsedBetween(long startTimestamp, long endTimestamp) =>
        _timeProvider.GetElapsedTime(startTimestamp, endTimestamp);

    private CallSnapshot UnknownSnapshot(ClccQueryOutcome outcome, string? diagnostic) => new(
        outcome,
        _isModuleConnected,
        _moduleGeneration,
        _timeProvider.GetTimestamp(),
        ImmutableArray<TrackedCall>.Empty,
        null,
        0,
        diagnostic);

    private static bool IsSameCall(ClccCallEntry prior, ClccCallEntry next) =>
        prior.Direction == next.Direction &&
        prior.Mode == next.Mode &&
        (string.IsNullOrEmpty(prior.Number) || string.IsNullOrEmpty(next.Number) ||
         string.Equals(prior.Number, next.Number, StringComparison.Ordinal));

    private static TrackedCallState MapState(int status) => status switch
    {
        0 => TrackedCallState.Active,
        1 => TrackedCallState.Held,
        2 => TrackedCallState.Dialing,
        3 => TrackedCallState.Alerting,
        4 => TrackedCallState.Incoming,
        5 => TrackedCallState.Waiting,
        _ => TrackedCallState.Unknown,
    };
}
