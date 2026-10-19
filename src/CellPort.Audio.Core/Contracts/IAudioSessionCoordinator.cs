using CellPort.Audio.Core.Models;

namespace CellPort.Audio.Core.Contracts;

/// <summary>Coordinates module standby, call authorization, host media and recovery.</summary>
public interface IAudioSessionCoordinator
{
    ValueTask<AudioOperationResult<AudioSessionSnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<AudioSessionSnapshot>> PrepareStandbyAsync(
        AudioPrepareRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<AudioSessionSnapshot>> StartMediaAsync(
        AudioMediaRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<AudioSessionSnapshot>> SetMutedAsync(
        bool muted,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<AudioSessionSnapshot>> StopMediaAsync(
        AudioStopReason reason,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<AudioRestoreReceipt>> ReleaseAsync(
        CancellationToken cancellationToken = default);
}
