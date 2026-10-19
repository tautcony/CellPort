using CellPort.Audio.Core.Models;

namespace CellPort.Audio.Core.Contracts;

/// <summary>
/// The only module API allowed to mutate USB configuration. Implementations must
/// delegate these calls to the device supervisor and must not write USB sysfs
/// from host code or route helpers.
/// </summary>
public interface ISupervisorUsbTransaction
{
    ValueTask<AudioOperationResult<AudioSessionLease>> PrepareStandbyAsync(
        AudioPrepareRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<AudioRestoreReceipt>> ReleaseAsync(
        AudioSessionLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>Module-side protocol/runtime boundary; implementations own transport details.</summary>
public interface IModuleAudioRuntime : ISupervisorUsbTransaction
{
    ValueTask<AudioOperationResult<ModuleRuntimeStatus>> InspectAsync(
        ModuleDeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<ModuleRuntimeStatus>> GetStatusAsync(
        AudioSessionLease lease,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<ModuleRuntimeStatus>> StartRouteAsync(
        AudioMediaRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<ModuleRuntimeStatus>> StopRouteAsync(
        AudioSessionLease lease,
        long callGeneration,
        CancellationToken cancellationToken = default);

}
