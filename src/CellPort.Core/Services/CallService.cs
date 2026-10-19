using System.Text.RegularExpressions;
using CellPort.Core.At;

namespace CellPort.Core.Services;

/// <summary>通话记录条目。</summary>
public sealed class CallRecord
{
    /// <summary>对方号码。</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>通话方向。</summary>
    public CallDirection Direction { get; set; }

    /// <summary>开始时间。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>通话时长。</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>是否未接。</summary>
    public bool Missed => Direction == CallDirection.Incoming && Duration is null or { TotalSeconds: <= 0 };
}

/// <summary>通话方向。</summary>
public enum CallDirection
{
    /// <summary>呼出。</summary>
    Outgoing,
    /// <summary>呼入。</summary>
    Incoming,
}

/// <summary>通话状态。</summary>
public enum CallState
{
    /// <summary>空闲。</summary>
    Idle,
    /// <summary>拨号中。</summary>
    Dialing,
    /// <summary>响铃（呼入或回铃）。</summary>
    Ringing,
    /// <summary>通话中。</summary>
    Active,
    /// <summary>已挂断。</summary>
    Ended,
    /// <summary>呼叫保持。</summary>
    Held,
    /// <summary>有数据占用，但没有确认的普通语音呼叫。</summary>
    DataOccupied,
    /// <summary>挂断命令已接受，仍等待网络状态确认。</summary>
    HangupPending,
    /// <summary>挂断结果未确认。</summary>
    HangupUnconfirmed,
    /// <summary>网络呼叫状态未知。</summary>
    Unknown,
}

public enum CallCommandKind
{
    Dial,
    Answer,
    HangUp,
}

public enum CallCommandStatus
{
    Accepted,
    Failed,
    InvalidInput,
}

/// <summary>AT command outcome only; it does not confirm network call state.</summary>
public sealed record CallCommandOutcome(
    CallCommandKind Command,
    CallCommandStatus Status,
    AtCompletionStatus? AtCompletion = null)
{
    public bool Accepted => Status == CallCommandStatus.Accepted;
}

/// <summary>
/// 语音通话服务。通过 AT 指令控制拨号、接听、挂断，并解析 URC 通话状态。
/// </summary>
public sealed class CallService
{
    private readonly AtEngine _engine;
    private readonly CallTracker _tracker;
    private readonly List<CallRecord> _history = [];
    private readonly object _lock = new();

    private CallState _state = CallState.Idle;
    private string? _peerNumber;
    private CallKey? _currentCallKey;
    private CallDirection? _currentDirection;
    private DateTime _callCreatedAt;
    private long? _activeStartedAt;
    private CallState? _hangupStatus;
    private int _refreshRequested;
    private int _refreshWorkerStarted;

    public CallService(AtEngine engine, CallTracker? tracker = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _tracker = tracker ?? new CallTracker();
    }

    /// <summary>通话状态变化。</summary>
    public event EventHandler<CallState>? StateChanged;

    /// <summary>收到来电。</summary>
    public event EventHandler<string>? IncomingCall;

    /// <summary>当前状态。</summary>
    public CallState State => _state;

    /// <summary>当前对端号码。</summary>
    public string? PeerNumber => _peerNumber;

    /// <summary>最近一次完整或未知的 CLCC 快照。</summary>
    public CallSnapshot CurrentSnapshot => _tracker.Current;

    /// <summary>通话记录。</summary>
    public IReadOnlyList<CallRecord> History
    {
        get
        {
            lock (_lock)
            {
                return [.. _history.OrderByDescending(c => c.Timestamp)];
            }
        }
    }

    /// <summary>初始化：开启来电显示与通话状态上报。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await SafeAsync("AT+CLIP=1", ct).ConfigureAwait(false);   // 来电号码显示
        await SafeAsync("AT+CRC=1", ct).ConfigureAwait(false);    // 区分语音/数据呼叫
        await SafeAsync("AT+CMOD=0", ct).ConfigureAwait(false);   // 单模
    }

    /// <summary>Reads and applies one full CLCC snapshot at call-state priority.</summary>
    public async Task<CallSnapshot> RefreshCallSnapshotAsync(CancellationToken ct = default)
    {
        if (!_tracker.IsModuleConnected)
        {
            return _tracker.Current;
        }
        var expectedModuleGeneration = _tracker.ModuleGeneration;
        AtResult result;
        try
        {
            result = await _engine.ExecuteAsync("AT+CLCC", AtCommandPriority.CallState,
                TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            result = new AtResult("AT+CLCC", false, [], string.Empty, TimeSpan.Zero,
                AtCompletionStatus.TransportFailure);
        }

        if (!_tracker.TryApply(ClccCallListParser.Parse(result), expectedModuleGeneration, out var snapshot))
        {
            return snapshot;
        }
        ApplySnapshot(snapshot);
        return snapshot;
    }

    /// <summary>拨号。</summary>
    internal async Task<bool> DialAsync(string number, CancellationToken ct = default) =>
        (await DialCommandAsync(number, ct).ConfigureAwait(false)).Accepted;

    internal async Task<CallCommandOutcome> DialCommandAsync(string number, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return new CallCommandOutcome(CallCommandKind.Dial, CallCommandStatus.InvalidInput);
        }

        var normalized = new string([.. number.Where(c => char.IsDigit(c) || c == '+' || c == '*'
            || c == '#')]);
        if (normalized.Length == 0)
        {
            return new CallCommandOutcome(CallCommandKind.Dial, CallCommandStatus.InvalidInput);
        }

        SetState(CallState.Dialing);
        _peerNumber = normalized;

        // 语音呼叫需带 ";"
        var r = await SafeResultAsync($"ATD{normalized};", ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (r is not { Succeeded: true })
        {
            SetState(CallState.Unknown);
            return new CallCommandOutcome(CallCommandKind.Dial, CallCommandStatus.Failed, r?.Completion);
        }

        return new CallCommandOutcome(CallCommandKind.Dial, CallCommandStatus.Accepted, r.Completion);
    }

    /// <summary>接听来电。</summary>
    internal async Task<bool> AnswerAsync(CancellationToken ct = default) =>
        (await AnswerCommandAsync(ct).ConfigureAwait(false)).Accepted;

    internal async Task<CallCommandOutcome> AnswerCommandAsync(CancellationToken ct = default)
    {
        var r = await SafeResultAsync("ATA", ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (r is { Succeeded: true })
        {
            return new CallCommandOutcome(CallCommandKind.Answer, CallCommandStatus.Accepted, r.Completion);
        }
        return new CallCommandOutcome(CallCommandKind.Answer, CallCommandStatus.Failed, r?.Completion);
    }

    /// <summary>挂断。</summary>
    internal async Task<bool> HangUpAsync(CancellationToken ct = default) =>
        (await HangUpCommandAsync(ct).ConfigureAwait(false)).Accepted;

    internal async Task<CallCommandOutcome> HangUpCommandAsync(CancellationToken ct = default)
    {
        var r = await SafeResultAsync("AT+CHUP", ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (r is not { Succeeded: true })
        {
            _hangupStatus = CallState.HangupUnconfirmed;
            SetState(CallState.HangupUnconfirmed);
            RequestSnapshotRefresh();
            return new CallCommandOutcome(CallCommandKind.HangUp, CallCommandStatus.Failed, r?.Completion);
        }

        _hangupStatus = CallState.HangupPending;
        SetState(CallState.HangupPending);
        RequestSnapshotRefresh();
        return new CallCommandOutcome(CallCommandKind.HangUp, CallCommandStatus.Accepted, r.Completion);
    }

    /// <summary>发送 DTMF（通话中按键）。</summary>
    internal async Task<bool> SendDtmfAsync(char digit, CancellationToken ct = default)
    {
        var r = await SafeAsync($"AT+VTS={digit}", ct).ConfigureAwait(false);
        return r is not null;
    }

    /// <summary>静音控制（部分固件支持）。</summary>
    internal async Task<bool> SetMuteAsync(bool mute, CancellationToken ct = default)
    {
        var r = await SafeAsync($"AT+CMUT={(mute ? 1 : 0)}", ct).ConfigureAwait(false);
        return r is not null;
    }

    /// <summary>
    /// 处理来自模块的主动上报。返回 true 表示已消费该行。
    /// </summary>
    public bool HandleUnsolicited(string line)
    {
        // RING —— 来电振铃
        if (line.Equals("RING", StringComparison.OrdinalIgnoreCase))
        {
            SetState(CallState.Ringing);
            RequestSnapshotRefresh();
            return true;
        }

        // +CLIP: "+8613800138000",145,...
        if (line.StartsWith("+CLIP:", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(line, "\"([^\"]+)\"");
            if (m.Success)
            {
                _peerNumber = m.Groups[1].Value;
                SetState(CallState.Ringing);
                IncomingCall?.Invoke(this, _peerNumber);
            }
            RequestSnapshotRefresh();
            return true;
        }

        // +CRC: 1,1  或  +CIEV 通话状态变化
        if (line.StartsWith("+CRC:", StringComparison.OrdinalIgnoreCase))
        {
            SetState(CallState.Ringing);
            RequestSnapshotRefresh();
            return true;
        }

        if (line.StartsWith("+CIEV:", StringComparison.OrdinalIgnoreCase))
        {
            SetState(CallState.Unknown);
            RequestSnapshotRefresh();
            return true;
        }

        // NO CARRIER —— 对方挂断
        if (line.StartsWith("NO CARRIER", StringComparison.OrdinalIgnoreCase))
        {
            SetState(CallState.Unknown);
            RequestSnapshotRefresh();
            return true;
        }

        // BUSY / NO ANSWER / NO DIALTONE
        if (line.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("NO ANSWER", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("NO DIALTONE", StringComparison.OrdinalIgnoreCase))
        {
            SetState(CallState.Unknown);
            RequestSnapshotRefresh();
            return true;
        }

        return false;
    }

    private void SetState(CallState state)
    {
        if (_state == state)
        {
            return;
        }
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void ApplySnapshot(CallSnapshot snapshot)
    {
        if (snapshot.IsUnknown)
        {
            SetState(_hangupStatus ?? CallState.Unknown);
            return;
        }

        if (_currentCallKey is not null && !snapshot.Calls.Any(call => call.Key == _currentCallKey))
        {
            RecordCurrentCall(snapshot.SampledAtTimestamp);
            _hangupStatus = null;
        }

        var voiceCalls = snapshot.Calls.Where(call => call.Entry.Mode == 0).ToArray();
        if (_hangupStatus.HasValue)
        {
            var targetStillPresent = _currentCallKey is not null
                ? snapshot.Calls.Any(call => call.Key == _currentCallKey)
                : voiceCalls.Length > 0;
            if (targetStillPresent)
            {
                SetState(_hangupStatus.Value);
                return;
            }
            _hangupStatus = null;
        }

        if (voiceCalls.Length == 0)
        {
            SetState(snapshot.Calls.IsEmpty ? CallState.Idle : CallState.DataOccupied);
            return;
        }

        if (voiceCalls.Length != 1)
        {
            SetState(CallState.Unknown);
            return;
        }

        var call = voiceCalls[0];
        if (_currentCallKey != call.Key)
        {
            _currentCallKey = call.Key;
            _currentDirection = call.Key.Direction;
            _callCreatedAt = DateTime.Now;
            _activeStartedAt = null;
            _peerNumber = call.Entry.Number ?? _peerNumber;
        }
        else if (!string.IsNullOrEmpty(call.Entry.Number))
        {
            _peerNumber = call.Entry.Number;
        }

        switch (call.State)
        {
            case TrackedCallState.Active:
                _activeStartedAt ??= snapshot.SampledAtTimestamp;
                SetState(CallState.Active);
                break;
            case TrackedCallState.Held:
                SetState(CallState.Held);
                break;
            case TrackedCallState.Dialing:
                SetState(CallState.Dialing);
                break;
            case TrackedCallState.Alerting:
            case TrackedCallState.Incoming:
            case TrackedCallState.Waiting:
                SetState(CallState.Ringing);
                break;
            default:
                SetState(CallState.Unknown);
                break;
        }
    }

    private void RecordCurrentCall(long endedAtTimestamp)
    {
        if (_currentCallKey is null)
        {
            return;
        }

        var duration = _activeStartedAt is long activeStartedAt
            ? _tracker.GetElapsedBetween(activeStartedAt, endedAtTimestamp)
            : (TimeSpan?)null;

        lock (_lock)
        {
            _history.Add(new CallRecord
            {
                Number = _peerNumber ?? string.Empty,
                Direction = _currentDirection ?? _currentCallKey.Direction,
                Timestamp = _callCreatedAt,
                Duration = duration,
            });
        }

        _currentCallKey = null;
        _currentDirection = null;
        _peerNumber = null;
        _activeStartedAt = null;
    }

    private void RequestSnapshotRefresh()
    {
        if (!_tracker.IsModuleConnected)
        {
            return;
        }

        Interlocked.Exchange(ref _refreshRequested, 1);
        if (Interlocked.CompareExchange(ref _refreshWorkerStarted, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _refreshRequested, 0) != 0)
                {
                    await RefreshCallSnapshotAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshWorkerStarted, 0);
                if (Volatile.Read(ref _refreshRequested) != 0)
                {
                    RequestSnapshotRefresh();
                }
            }
        });
    }

    private async Task<AtResult?> SafeAsync(string cmd, CancellationToken ct, TimeSpan? timeout = null)
    {
        var result = await SafeResultAsync(cmd, ct, timeout).ConfigureAwait(false);
        return result is { Succeeded: true } ? result : null;
    }

    private async Task<AtResult?> SafeResultAsync(string cmd, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            return await _engine.ExecuteAsync(cmd, timeout, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
