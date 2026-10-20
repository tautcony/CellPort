namespace CellPort.Core.Services;

/// <summary>Shared module-operation categories used by ModemManager and audio coordination.</summary>
public enum ModuleOperationKind
{
    ReadOnlyQuery,
    CallControl,
    SmsRead,
    SmsWrite,
    EsimModification,
    CfunChange,
    UsbNetworkChange,
    ModuleRestart,
    RawConsoleCommand,
}

public interface IModuleOperationLease : IAsyncDisposable;

public sealed record ModuleOperationAdmissionResult(
    bool Allowed,
    IModuleOperationLease? Lease,
    string? ErrorCode)
{
    public static ModuleOperationAdmissionResult Permit(IModuleOperationLease? lease = null) => new(true, lease, null);
    public static ModuleOperationAdmissionResult Reject(string errorCode) => new(false, null, errorCode);
}

/// <summary>Admission gate shared by modem operations and the audio session owner.</summary>
public interface IModuleOperationAdmission
{
    ValueTask<ModuleOperationAdmissionResult> TryAcquireAsync(
        ModuleOperationKind kind,
        bool rawCommandIsReadOnly = false,
        CancellationToken cancellationToken = default);
}
