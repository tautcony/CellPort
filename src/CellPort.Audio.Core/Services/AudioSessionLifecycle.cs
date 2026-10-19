using CellPort.Audio.Core.Models;

namespace CellPort.Audio.Core.Services;

/// <summary>A token carried by each media callback; stale callbacks cannot address a later route.</summary>
public sealed record AudioMediaGeneration(
    Guid SessionId,
    long ModuleGeneration,
    long CallGeneration,
    long MediaGeneration);

public sealed class AudioSessionLifecycleDiagnostic : EventArgs
{
    public AudioSessionLifecycleDiagnostic(ModuleAudioPhase modulePhase, AudioMediaState mediaState,
        AudioRecoveryStatus recoveryStatus)
    {
        ModulePhase = modulePhase;
        MediaState = mediaState;
        RecoveryStatus = recoveryStatus;
    }

    public ModuleAudioPhase ModulePhase { get; }
    public AudioMediaState MediaState { get; }
    public AudioRecoveryStatus RecoveryStatus { get; }

    public string ToLogLine() =>
        $"audio.module={ModulePhase} audio.media={MediaState} audio.recovery={RecoveryStatus}";
}

/// <summary>
/// Keeps the long-lived USB standby session separate from per-call host media.
/// This class has no capture or USB APIs: standby is inert, and only a currently
/// matching generation can accept media work.
/// </summary>
public sealed class AudioSessionLifecycle
{
    private readonly object _gate = new();
    private AudioSessionLease? _lease;
    private ModuleAudioPhase _modulePhase = ModuleAudioPhase.Disconnected;
    private AudioMediaState _mediaState = AudioMediaState.Stopped;
    private long _mediaGeneration;
    private long? _callGeneration;
    private AudioRecoveryStatus _recoveryStatus = AudioRecoveryStatus.None;

    /// <summary>Emits a redacted state snapshot without identifiers, numbers, or owner tokens.</summary>
    public event EventHandler<AudioSessionLifecycleDiagnostic>? StateChanged;

    public ModuleAudioPhase ModulePhase { get { lock (_gate) return _modulePhase; } }
    public AudioMediaState MediaState { get { lock (_gate) return _mediaState; } }
    public AudioSessionLifecycleDiagnostic Diagnostic
    {
        get { lock (_gate) return new(_modulePhase, _mediaState, _recoveryStatus); }
    }

    public bool IsStandbyOnly
    {
        get
        {
            lock (_gate)
                return _lease is not null && _modulePhase == ModuleAudioPhase.StandbyReady &&
                       _mediaState == AudioMediaState.Stopped;
        }
    }

    public bool CommitStandby(AudioSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (_lease is not null || lease.SessionId == Guid.Empty || lease.ModuleGeneration < 0)
                return false;
            _lease = lease;
            _modulePhase = ModuleAudioPhase.StandbyReady;
            _mediaState = AudioMediaState.Stopped;
            _callGeneration = null;
            _recoveryStatus = AudioRecoveryStatus.None;
            ++_mediaGeneration;
        }
        PublishDiagnostic();
        return true;
    }

    public bool TryStartMedia(AudioSessionLease lease, long callGeneration, out AudioMediaGeneration? generation)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!OwnsCurrentLease(lease) || _modulePhase != ModuleAudioPhase.StandbyReady ||
                _mediaState != AudioMediaState.Stopped || callGeneration < 0)
            {
                generation = null;
                return false;
            }

            _callGeneration = callGeneration;
            _mediaState = AudioMediaState.StartingMuted;
            generation = new AudioMediaGeneration(lease.SessionId, lease.ModuleGeneration,
                callGeneration, ++_mediaGeneration);
        }
        PublishDiagnostic();
        return true;
    }

    public bool MarkMediaRunning(AudioMediaGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (_gate)
        {
            if (!IsCurrentMediaGeneration(generation) || _mediaState != AudioMediaState.StartingMuted)
                return false;
            _mediaState = AudioMediaState.Running;
        }
        PublishDiagnostic();
        return true;
    }

    public bool CanAcceptMediaCallback(AudioMediaGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (_gate)
            return _mediaState == AudioMediaState.Running && IsCurrentMediaGeneration(generation);
    }

    /// <summary>Stops only the per-call route and invalidates every callback token.</summary>
    public bool StopMedia(AudioSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!OwnsCurrentLease(lease) || _modulePhase != ModuleAudioPhase.StandbyReady)
                return false;
            _mediaState = AudioMediaState.Stopped;
            _callGeneration = null;
            _recoveryStatus = AudioRecoveryStatus.None;
            ++_mediaGeneration;
        }
        PublishDiagnostic();
        return true;
    }

    public bool BeginRestore(AudioSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!OwnsCurrentLease(lease) || _modulePhase is ModuleAudioPhase.Released or ModuleAudioPhase.Disconnected)
                return false;
            _mediaState = AudioMediaState.Stopped;
            _callGeneration = null;
            _recoveryStatus = AudioRecoveryStatus.Pending;
            ++_mediaGeneration;
            _modulePhase = ModuleAudioPhase.Restoring;
        }
        PublishDiagnostic();
        return true;
    }

    /// <summary>Only a verified receipt clears ownership; deferred or conflicting restores remain visible.</summary>
    public bool CompleteRestore(AudioSessionLease lease, AudioRestoreReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(receipt);
        lock (_gate)
        {
            if (!OwnsCurrentLease(lease) || _modulePhase != ModuleAudioPhase.Restoring)
                return false;
            if (receipt.ValuesVerified && receipt.Status == AudioRecoveryStatus.Restored)
            {
                _lease = null;
                _modulePhase = ModuleAudioPhase.Released;
                _recoveryStatus = AudioRecoveryStatus.Restored;
            }
            else
            {
                _modulePhase = receipt.Status switch
                {
                    AudioRecoveryStatus.RestorePendingCall => ModuleAudioPhase.RestorePendingCall,
                    AudioRecoveryStatus.RecoveryRequired or AudioRecoveryStatus.Conflict => ModuleAudioPhase.RecoveryRequired,
                    _ => ModuleAudioPhase.Restoring,
                };
                _recoveryStatus = receipt.Status;
            }
        }
        PublishDiagnostic();
        return receipt.ValuesVerified && receipt.Status == AudioRecoveryStatus.Restored;
    }

    private bool OwnsCurrentLease(AudioSessionLease lease) =>
        _lease is not null && _lease.SessionId == lease.SessionId &&
        _lease.DeviceId == lease.DeviceId && _lease.ModuleGeneration == lease.ModuleGeneration;

    private bool IsCurrentMediaGeneration(AudioMediaGeneration generation) =>
        _lease is not null && _lease.SessionId == generation.SessionId &&
        _lease.ModuleGeneration == generation.ModuleGeneration && _callGeneration == generation.CallGeneration &&
        _mediaGeneration == generation.MediaGeneration;

    private void PublishDiagnostic()
    {
        var handler = StateChanged;
        if (handler is null) return;
        var diagnostic = Diagnostic;
        foreach (EventHandler<AudioSessionLifecycleDiagnostic> subscriber in handler.GetInvocationList())
        {
            try { subscriber(this, diagnostic); }
            catch { /* A logging subscriber cannot change session safety state. */ }
        }
    }
}
