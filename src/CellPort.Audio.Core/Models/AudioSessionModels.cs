using System.Collections.Immutable;
using CellPort.Core.Services;

namespace CellPort.Audio.Core.Models;

public enum ModuleAudioPhase
{
    Disconnected,
    Inspecting,
    Staging,
    RuntimeReady,
    UsbReenumerating,
    StandbyReady,
    Restoring,
    Released,
    RestorePendingCall,
    RecoveryRequired,
}

public enum ConfirmedCallState
{
    Idle,
    Dialing,
    Incoming,
    Alerting,
    Active,
    Held,
    Unknown,
}

public enum AudioMediaState
{
    Stopped,
    StartingMuted,
    Running,
    Failed,
}

public enum AudioStopReason
{
    CallEnded,
    CallStateUnknown,
    UserReleased,
    DeviceUnavailable,
    PermissionChanged,
    HostShutdown,
}

/// <summary>Opaque reference to a module-side audio session.</summary>
public sealed record AudioSessionLease(
    Guid SessionId,
    ModuleDeviceId DeviceId,
    long ModuleGeneration);

public sealed record AudioPrepareRequest(
    ModuleDeviceId DeviceId,
    string ProfileId);

/// <summary>
/// Confirmation from the call tracker; callers must supply only a fresh,
/// uniquely selected ordinary voice call. No phone number is included.
/// </summary>
public sealed record CallMediaAuthorization(
    ModuleDeviceId DeviceId,
    long ModuleGeneration,
    long CallGeneration,
    int ClccIndex,
    CallDirection Direction,
    TimeSpan SnapshotAge);

public sealed record AudioMediaRequest(
    AudioSessionLease Lease,
    CallMediaAuthorization Call,
    AudioEndpointSelection Endpoints);

public sealed record ModuleRuntimeStatus(
    ModuleAudioPhase Phase,
    long Generation,
    string? ProfileId,
    bool CompatibilityApproved,
    bool RouteReady,
    AudioRecoveryStatus RecoveryStatus,
    AudioErrorCode? LastErrorCode);

public sealed record AudioSessionSnapshot(
    ModuleDeviceId? DeviceId,
    Guid? SessionId,
    long ModuleGeneration,
    ModuleAudioPhase ModulePhase,
    ConfirmedCallState CallState,
    long? CallGeneration,
    TimeSpan? CallSnapshotAge,
    AudioMediaState MediaState,
    bool IsMuted,
    AudioRecoveryStatus RecoveryStatus,
    AudioErrorCode? LastErrorCode);

/// <summary>Receipt distinguishes verified restoration from deferred work.</summary>
public sealed record AudioRestoreReceipt(
    AudioRecoveryStatus Status,
    bool ValuesVerified,
    ImmutableArray<string> RestoredFields);

/// <summary>Opaque handle to opened host capture/render streams.</summary>
public sealed record AudioHostSession(
    Guid SessionId,
    AudioEndpointSelection Endpoints);
