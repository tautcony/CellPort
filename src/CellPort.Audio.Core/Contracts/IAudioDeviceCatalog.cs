using System.Collections.Immutable;
using CellPort.Audio.Core.Models;

namespace CellPort.Audio.Core.Contracts;

/// <summary>
/// Discovers modules and audio endpoints while keeping PnP, udev, CoreAudio,
/// PipeWire, and other host API types inside their platform adapters.
/// </summary>
public interface IAudioDeviceCatalog
{
    ValueTask<AudioOperationResult<ImmutableArray<ModuleDeviceCandidate>>> EnumerateModulesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<ModuleDeviceCandidate>> RevalidateModuleAsync(
        ModuleDeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask<AudioOperationResult<ImmutableArray<AudioEndpointDescriptor>>> EnumerateEndpointsAsync(
        ModuleDeviceId deviceId,
        CancellationToken cancellationToken = default);
}
