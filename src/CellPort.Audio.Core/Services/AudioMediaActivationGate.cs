using CellPort.Audio.Core.Models;
using CellPort.Core.Services;

namespace CellPort.Audio.Core.Services;

/// <summary>
/// Fail-closed activation policy for a future host backend. It keeps the outgoing
/// host boundary muted through permission, endpoint, call-generation and first-frame
/// checks; the native backend remains responsible for applying HostMuted.
/// </summary>
public sealed class AudioMediaActivationGate
{
    public static readonly TimeSpan MaximumCallSnapshotAge = TimeSpan.FromSeconds(3);
    public const int MaximumRebuildsPerCall = 2;

    private readonly AudioSessionLifecycle _lifecycle;
    private readonly TimeProvider _timeProvider;
    private AudioSessionLease? _lease;
    private AudioMediaGeneration? _generation;
    private long _callGeneration;
    private int _rebuilds;
    private bool _permissionGranted;
    private bool _mediaRunning;
    private bool _userMuted;

    public AudioMediaActivationGate(AudioSessionLifecycle lifecycle, TimeProvider? timeProvider = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool HostMuted { get; private set; } = true;
    public bool AtMuteFallbackActive { get; private set; }
    public int RebuildAttempts => _rebuilds;
    public AudioMediaGeneration? CurrentGeneration => _generation;

    public bool TryBegin(
        AudioSessionLease lease,
        CallSnapshot callSnapshot,
        bool microphonePermissionGranted,
        bool endpointsReady,
        bool routeReady,
        out AudioMediaGeneration? generation,
        out AudioErrorCode? errorCode)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(callSnapshot);
        generation = null;
        errorCode = ValidateRequest(lease, callSnapshot, microphonePermissionGranted, endpointsReady, routeReady);
        HostMuted = true;
        if (errorCode is not null) return false;

        var call = callSnapshot.SelectedVoiceCall!;
        if (!_lifecycle.TryStartMedia(lease, call.Key.CallGeneration, out generation))
        {
            errorCode = AudioErrorCode.RouteNotReady;
            return false;
        }

        _lease = lease;
        _generation = generation;
        _callGeneration = call.Key.CallGeneration;
        _permissionGranted = true;
        _mediaRunning = false;
        _userMuted = false;
        _rebuilds = 0;
        return true;
    }

    /// <summary>Opens the host uplink only after both directions advance for this generation.</summary>
    public bool ConfirmFrames(AudioMediaGeneration generation, CallSnapshot freshCallSnapshot,
        long capturedFrames, long renderedFrames, bool endpointsReady = true)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(freshCallSnapshot);
        // A late callback from a prior route must not stop or mute the current route.
        if (!OwnsGeneration(generation)) return false;
        if (_lifecycle.MediaState != AudioMediaState.StartingMuted) return false;
        if (_lease is null ||
            ValidateRequest(_lease, freshCallSnapshot, _permissionGranted, endpointsReady, routeReady: true) is not null ||
            freshCallSnapshot.SelectedVoiceCall?.Key.CallGeneration != _callGeneration ||
            capturedFrames <= 0 || renderedFrames <= 0)
        {
            HostMuted = true;
            StopCurrentRoute();
            return false;
        }
        if (!_lifecycle.MarkMediaRunning(generation))
        {
            HostMuted = true;
            StopCurrentRoute();
            return false;
        }
        _mediaRunning = true;
        HostMuted = _userMuted || !_permissionGranted;
        AtMuteFallbackActive = false;
        return true;
    }

    /// <summary>
    /// Host mute is enforced regardless of the modem command result. This makes
    /// failed AT mute a host-side mute instead of an open uplink.
    /// </summary>
    public bool ApplyUserMute(bool muted, bool atMuteCommandSucceeded)
    {
        _userMuted = muted;
        if (!_mediaRunning || !_permissionGranted)
        {
            HostMuted = true;
            return false;
        }
        HostMuted = muted;
        AtMuteFallbackActive = muted && !atMuteCommandSucceeded;
        return true;
    }

    public void RevokeMicrophonePermission()
    {
        _permissionGranted = false;
        HostMuted = true;
        AtMuteFallbackActive = false;
        StopCurrentRoute();
    }

    /// <summary>Endpoint loss closes the uplink and invalidates all callbacks immediately.</summary>
    public void NotifyEndpointUnavailable()
    {
        HostMuted = true;
        StopCurrentRoute();
    }

    /// <summary>At most two muted rebuilds are allowed for the same call generation.</summary>
    public bool TryRebuild(
        CallSnapshot freshCallSnapshot,
        bool microphonePermissionGranted,
        bool endpointsReady,
        bool routeReady,
        out AudioMediaGeneration? generation,
        out AudioErrorCode? errorCode)
    {
        ArgumentNullException.ThrowIfNull(freshCallSnapshot);
        generation = null;
        HostMuted = true;
        if (_lease is null || _generation is null)
        {
            errorCode = AudioErrorCode.RouteNotReady;
            return false;
        }
        if (_rebuilds >= MaximumRebuildsPerCall)
        {
            errorCode = AudioErrorCode.RouteNotReady;
            StopCurrentRoute();
            return false;
        }
        errorCode = ValidateRequest(_lease, freshCallSnapshot, microphonePermissionGranted, endpointsReady, routeReady);
        if (errorCode is not null || freshCallSnapshot.SelectedVoiceCall?.Key.CallGeneration != _callGeneration)
        {
            errorCode ??= AudioErrorCode.CallStateUnknown;
            StopCurrentRoute();
            return false;
        }

        var lease = _lease;
        _lifecycle.StopMedia(lease);
        if (!_lifecycle.TryStartMedia(lease, _callGeneration, out generation))
        {
            errorCode = AudioErrorCode.RouteNotReady;
            _generation = null;
            _mediaRunning = false;
            return false;
        }
        ++_rebuilds;
        _generation = generation;
        _permissionGranted = true;
        _mediaRunning = false;
        _userMuted = false;
        AtMuteFallbackActive = false;
        errorCode = null;
        return true;
    }

    public void Stop()
    {
        HostMuted = true;
        AtMuteFallbackActive = false;
        StopCurrentRoute();
    }

    private AudioErrorCode? ValidateRequest(
        AudioSessionLease lease,
        CallSnapshot snapshot,
        bool permissionGranted,
        bool endpointsReady,
        bool routeReady)
    {
        if (!permissionGranted) return AudioErrorCode.MicrophonePermissionDenied;
        if (!endpointsReady) return AudioErrorCode.EndpointMissing;
        if (!routeReady) return AudioErrorCode.RouteNotReady;
        var age = _timeProvider.GetElapsedTime(snapshot.SampledAtTimestamp);
        var selected = snapshot.SelectedVoiceCall;
        if (snapshot.IsUnknown || snapshot.ModuleGeneration != lease.ModuleGeneration ||
            age < TimeSpan.Zero || age >= MaximumCallSnapshotAge || selected?.State != TrackedCallState.Active)
            return AudioErrorCode.CallStateUnknown;
        return null;
    }

    private bool OwnsGeneration(AudioMediaGeneration generation) =>
        _generation is not null && _generation == generation;

    private void StopCurrentRoute()
    {
        if (_lease is not null) _lifecycle.StopMedia(_lease);
        _generation = null;
        _mediaRunning = false;
    }
}
