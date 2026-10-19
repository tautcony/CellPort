namespace CellPort.Audio.Core.Services;

/// <summary>Applies a bounded resampling correction from a measured clock-rate difference.</summary>
public sealed class AudioClockDriftController
{
    public AudioClockDriftController(int maximumCorrectionPpm = 500)
    {
        if (maximumCorrectionPpm is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumCorrectionPpm));
        MaximumCorrectionPpm = maximumCorrectionPpm;
    }

    public int MaximumCorrectionPpm { get; }
    public double CorrectionPpm { get; private set; }
    public double ResamplingRatio { get; private set; } = 1.0;

    /// <summary>Clamps the measured input/render clock delta before it reaches the resampler.</summary>
    public double Update(double measuredDriftPpm)
    {
        if (!double.IsFinite(measuredDriftPpm)) throw new ArgumentOutOfRangeException(nameof(measuredDriftPpm));
        CorrectionPpm = Math.Clamp(measuredDriftPpm, -MaximumCorrectionPpm, MaximumCorrectionPpm);
        ResamplingRatio = 1.0 + (CorrectionPpm / 1_000_000.0);
        return ResamplingRatio;
    }
}

public enum AudioMediaDirection
{
    Capture,
    Playback,
}

public sealed record AudioMediaHealthSnapshot(
    bool CaptureAdvancing,
    bool PlaybackAdvancing,
    AudioMediaDirection? StalledDirection,
    bool OutgoingMustClose);

/// <summary>
/// Detects stopped frame positions independently for capture and playback.
/// Sample values do not affect health: advancing zero-valued silence is healthy.
/// </summary>
public sealed class AudioMediaHealthMonitor
{
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider;
    private long _lastCaptureTimestamp;
    private long _lastPlaybackTimestamp;
    private long _captureFramePosition;
    private long _playbackFramePosition;
    private volatile bool _captureAvailable = true;
    private volatile bool _playbackAvailable = true;
    private volatile bool _started;
    private int _outgoingClosed;

    public AudioMediaHealthMonitor(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public event EventHandler? OutgoingMustClose;

    public void Start(long captureFramePosition = 0, long playbackFramePosition = 0)
    {
        if (captureFramePosition < 0) throw new ArgumentOutOfRangeException(nameof(captureFramePosition));
        if (playbackFramePosition < 0) throw new ArgumentOutOfRangeException(nameof(playbackFramePosition));
        var now = _timeProvider.GetTimestamp();
        Interlocked.Exchange(ref _captureFramePosition, captureFramePosition);
        Interlocked.Exchange(ref _playbackFramePosition, playbackFramePosition);
        Interlocked.Exchange(ref _lastCaptureTimestamp, now);
        Interlocked.Exchange(ref _lastPlaybackTimestamp, now);
        _captureAvailable = true;
        _playbackAvailable = true;
        Interlocked.Exchange(ref _outgoingClosed, 0);
        _started = true;
    }

    public void ObserveCaptureFramePosition(long framePosition) =>
        Observe(ref _captureFramePosition, ref _lastCaptureTimestamp, framePosition);

    public void ObservePlaybackFramePosition(long framePosition) =>
        Observe(ref _playbackFramePosition, ref _lastPlaybackTimestamp, framePosition);

    public void SetEndpointAvailable(AudioMediaDirection direction, bool available)
    {
        if (direction == AudioMediaDirection.Capture) _captureAvailable = available;
        else _playbackAvailable = available;
        if (!available) _ = Evaluate();
    }

    public AudioMediaHealthSnapshot Evaluate()
    {
        if (!_started) return new(false, false, null, false);
        var now = _timeProvider.GetTimestamp();
        var captureAdvancing = _captureAvailable &&
            _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastCaptureTimestamp), now) < StallLimit;
        var playbackAdvancing = _playbackAvailable &&
            _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastPlaybackTimestamp), now) < StallLimit;
        AudioMediaDirection? stalled = !_captureAvailable || !captureAdvancing
            ? AudioMediaDirection.Capture
            : !_playbackAvailable || !playbackAdvancing ? AudioMediaDirection.Playback : null;
        if (stalled is not null && Interlocked.Exchange(ref _outgoingClosed, 1) == 0)
        {
            NotifyClose();
        }
        return new(captureAdvancing, playbackAdvancing, stalled, Volatile.Read(ref _outgoingClosed) != 0);
    }

    private void Observe(ref long lastPosition, ref long lastTimestamp, long framePosition)
    {
        if (framePosition < 0) throw new ArgumentOutOfRangeException(nameof(framePosition));
        if (!_started) return;
        while (true)
        {
            var previous = Interlocked.Read(ref lastPosition);
            if (framePosition <= previous) return;
            if (Interlocked.CompareExchange(ref lastPosition, framePosition, previous) == previous) break;
        }
        Interlocked.Exchange(ref lastTimestamp, _timeProvider.GetTimestamp());
    }

    private void NotifyClose()
    {
        try { OutgoingMustClose?.Invoke(this, EventArgs.Empty); }
        catch { /* A host mute callback cannot reopen a stalled stream. */ }
    }
}
