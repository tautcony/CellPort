namespace CellPort.Audio.Core.Models;

/// <summary>Error metadata suitable for structured UI and diagnostics.</summary>
public sealed record AudioOperationError(
    AudioErrorCode Code,
    AudioOperationStage Stage,
    bool Retryable,
    AudioRecoveryStatus RecoveryStatus);

/// <summary>Stage at which an operation failed or was rejected.</summary>
public enum AudioOperationStage
{
    Protocol,
    DeviceDiscovery,
    ProfileAdmission,
    ResourceValidation,
    Ownership,
    CallState,
    UsbTransaction,
    Reenumeration,
    Calibration,
    Routing,
    Media,
    Restore,
}

/// <summary>Recovery state reported independently from operation success.</summary>
public enum AudioRecoveryStatus
{
    None,
    NotRequired,
    Pending,
    RestorePendingCall,
    Restored,
    Conflict,
    RecoveryRequired,
    Unknown,
}
