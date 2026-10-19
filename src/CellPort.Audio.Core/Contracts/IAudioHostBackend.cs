using System.Collections.Immutable;
using CellPort.Audio.Core.Models;

namespace CellPort.Audio.Core.Contracts;

/// <summary>Native host capture/render boundary; no OS audio types escape it.</summary>
public interface IAudioHostBackend
{
    ValueTask<AudioOperationResult<ImmutableArray<AudioEndpointDescriptor>>> EnumerateEndpointsAsync(
        ModuleDeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<AudioHostSession>> OpenMutedAsync(
        AudioEndpointSelection endpoints,
        bool microphonePermissionGranted,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult> SetOutgoingMutedAsync(
        AudioHostSession session,
        bool muted,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult> StopAsync(
        AudioHostSession session,
        CancellationToken cancellationToken = default);
}
