namespace CellPort.Audio.Core.Models;

/// <summary>Stable, platform-neutral audio/runtime error codes.</summary>
public enum AudioErrorCode
{
    MalformedMessage,
    MessageTooLarge,
    ProtocolVersionUnsupported,
    RequestIdConflict,
    RequestSequenceStale,
    OwnerMismatch,
    BootMismatch,
    GenerationMismatch,
    OperationNotAllowed,
    OperationConflict,
    TransportUnavailable,
    DeviceNotFound,
    RootUnavailable,
    UnsupportedProfile,
    PayloadInvalid,
    DeviceIdentityAmbiguous,
    DeviceBusyData,
    CallStateUnknown,
    CalibrationFailed,
    RouteNotReady,
    UsbReenumerationTimeout,
    EndpointMissing,
    UnsupportedFormat,
    MicrophonePermissionDenied,
    MediaStalled,
    RestoreConflict,
    RestorePendingCall,
    RecoveryRequired,
}
