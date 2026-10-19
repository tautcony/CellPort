using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using CellPort.Audio.Core.Contracts;
using CellPort.Audio.Core.Models;
using CellPort.Core.Transport;
using NAudio.CoreAudioApi;

namespace CellPort.Platform.Windows;

public enum WindowsAudioDataFlow
{
    Capture,
    Render,
}

/// <summary>Platform-neutral snapshot of one MMDevice endpoint and its PnP association.</summary>
public sealed record WindowsAudioEndpointInfo(
    string Id,
    string DisplayName,
    WindowsAudioDataFlow DataFlow,
    Guid? ContainerId,
    bool IsActive,
    string? FormatSummary);

public interface IWindowsAudioEndpointSource
{
    ImmutableArray<WindowsAudioEndpointInfo> Enumerate();
}

/// <summary>Maps MMDevice endpoints by physical PnP container identity, never by display name.</summary>
public static class WindowsAudioEndpointMapper
{
    public static ImmutableArray<AudioEndpointDescriptor> Map(
        IEnumerable<WindowsAudioEndpointInfo> endpoints,
        IReadOnlyDictionary<Guid, ModuleDeviceId> modulesByContainer,
        ModuleDeviceId? selectableModule = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(modulesByContainer);

        return [.. endpoints.Select(endpoint =>
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            var associatedModule = endpoint.ContainerId is { } container &&
                                   modulesByContainer.TryGetValue(container, out var module)
                ? module
                : null;
            var role = (endpoint.DataFlow, associatedModule is not null) switch
            {
                (WindowsAudioDataFlow.Capture, true) => AudioEndpointRole.ModuleCapture,
                (WindowsAudioDataFlow.Render, true) => AudioEndpointRole.ModulePlayback,
                (WindowsAudioDataFlow.Capture, false) => AudioEndpointRole.LocalMicrophoneCapture,
                _ => AudioEndpointRole.LocalPlayback,
            };

            // Without a physical association, the endpoint cannot safely be selected as
            // either a module route or a local route (it could form a hardware loopback).
            var available = endpoint.IsActive && endpoint.ContainerId is not null &&
                            (associatedModule is null || selectableModule is null || associatedModule == selectableModule) &&
                            !string.IsNullOrWhiteSpace(endpoint.FormatSummary);
            return new AudioEndpointDescriptor(
                new AudioEndpointId(endpoint.Id), endpoint.DisplayName, role,
                associatedModule, available, endpoint.FormatSummary);
        })];
    }

    public static AudioOperationError? ValidateSelection(
        AudioEndpointSelection selection,
        IEnumerable<AudioEndpointDescriptor> currentEndpoints)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(currentEndpoints);
        var endpoints = currentEndpoints.ToArray();
        var selectedIds = new[]
        {
            selection.LocalMicrophone, selection.LocalPlayback,
            selection.ModulePlayback, selection.ModuleCapture,
        };
        if (selectedIds.Select(endpoint => endpoint.Value).Distinct(StringComparer.Ordinal).Count() != 4)
            return Error(AudioErrorCode.EndpointMissing);

        var selected = new AudioEndpointDescriptor[4];
        for (var i = 0; i < selectedIds.Length; i++)
        {
            var matches = endpoints.Where(endpoint => endpoint.Id == selectedIds[i]).ToArray();
            if (matches.Length != 1 || !matches[0].IsAvailable)
                return Error(AudioErrorCode.EndpointMissing);
            selected[i] = matches[0];
        }

        if (selected[0].Role != AudioEndpointRole.LocalMicrophoneCapture ||
            selected[0].AssociatedModule is not null ||
            selected[1].Role != AudioEndpointRole.LocalPlayback ||
            selected[1].AssociatedModule is not null)
            return Error(AudioErrorCode.DeviceIdentityAmbiguous);

        var module = selected[2].AssociatedModule;
        if (module is null || selected[2].Role != AudioEndpointRole.ModulePlayback ||
            selected[3].Role != AudioEndpointRole.ModuleCapture || selected[3].AssociatedModule != module)
            return Error(AudioErrorCode.DeviceIdentityAmbiguous);

        return null;
    }

    private static AudioOperationError Error(AudioErrorCode code) =>
        new(code, AudioOperationStage.Routing, Retryable: true, AudioRecoveryStatus.None);
}

/// <summary>Real Windows MMDevice enumeration. Reads endpoint properties only; it never changes defaults or drivers.</summary>
public sealed class WindowsMmDeviceEndpointSource : IWindowsAudioEndpointSource
{
    internal static readonly PropertyKey ContainerIdKey = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    public static Guid? ReadContainerId(MMDevice device) =>
        device.Properties.TryGetValue(ContainerIdKey, out Guid value) && value != Guid.Empty ? value : null;

    public ImmutableArray<WindowsAudioEndpointInfo> Enumerate()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var collection = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All);
        var endpoints = ImmutableArray.CreateBuilder<WindowsAudioEndpointInfo>();
        foreach (var device in collection)
        {
            using (device)
            {
                var containerId = ReadContainerId(device);
                string? summary = null;
                if (device.State == DeviceState.Active)
                {
                    try
                    {
                        using var audioClient = device.CreateAudioClient();
                        var format = audioClient.MixFormat;
                        summary = format is null
                            ? null
                            : $"{format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample}-bit {format.Encoding}";
                    }
                    catch
                    {
                        // Keep the endpoint visible as unavailable so the UI can report it.
                    }
                }
                endpoints.Add(new WindowsAudioEndpointInfo(
                    device.ID,
                    device.FriendlyName,
                    device.DataFlow == DataFlow.Capture ? WindowsAudioDataFlow.Capture : WindowsAudioDataFlow.Render,
                    containerId,
                    device.State == DeviceState.Active,
                    summary));
            }
        }
        return endpoints.ToImmutable();
    }
}

/// <summary>Windows PnP + MMDevice implementation of the shared platform catalog contract.</summary>
public sealed class WindowsAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly Func<IReadOnlyList<WindowsPnpDeviceNode>> _enumeratePnp;
    private readonly IWindowsAudioEndpointSource _endpointSource;

    public WindowsAudioDeviceCatalog(
        Func<IReadOnlyList<WindowsPnpDeviceNode>>? enumeratePnp = null,
        IWindowsAudioEndpointSource? endpointSource = null)
    {
        _enumeratePnp = enumeratePnp ?? WindowsUsbDeviceEnumerator.EnumeratePresent;
        _endpointSource = endpointSource ?? new WindowsMmDeviceEndpointSource();
    }

    public ValueTask<AudioOperationResult<ImmutableArray<ModuleDeviceCandidate>>> EnumerateModulesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var nodes = _enumeratePnp();
            var containers = GetModuleContainerIds(nodes);
            var endpointSnapshot = _endpointSource.Enumerate();
            var candidates = containers.Select(containerId => BuildCandidate(nodes, containerId, endpointSnapshot))
                .ToImmutableArray();
            return ValueTask.FromResult(AudioOperationResult<ImmutableArray<ModuleDeviceCandidate>>.Success(candidates));
        }
        catch
        {
            return ValueTask.FromResult(AudioOperationResult<ImmutableArray<ModuleDeviceCandidate>>.Failure(
                Error(AudioErrorCode.DeviceNotFound, AudioOperationStage.DeviceDiscovery, retryable: true)));
        }
    }

    public ValueTask<AudioOperationResult<ModuleDeviceCandidate>> RevalidateModuleAsync(
        ModuleDeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var nodes = _enumeratePnp();
            var container = GetModuleContainerIds(nodes)
                .SingleOrDefault(candidate => CreateDeviceId(candidate) == deviceId);
            if (container == Guid.Empty)
            {
                return ValueTask.FromResult(AudioOperationResult<ModuleDeviceCandidate>.Failure(
                    Error(AudioErrorCode.DeviceNotFound, AudioOperationStage.DeviceDiscovery, retryable: true)));
            }
            return ValueTask.FromResult(AudioOperationResult<ModuleDeviceCandidate>.Success(
                BuildCandidate(nodes, container, _endpointSource.Enumerate())));
        }
        catch
        {
            return ValueTask.FromResult(AudioOperationResult<ModuleDeviceCandidate>.Failure(
                Error(AudioErrorCode.DeviceNotFound, AudioOperationStage.DeviceDiscovery, retryable: true)));
        }
    }

    public ValueTask<AudioOperationResult<ImmutableArray<AudioEndpointDescriptor>>> EnumerateEndpointsAsync(
        ModuleDeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var nodes = _enumeratePnp();
            var moduleMap = GetModuleContainerIds(nodes).ToDictionary(CreateDeviceId, id => id)
                .ToDictionary(pair => pair.Value, pair => pair.Key);
            var selected = moduleMap.FirstOrDefault(pair => pair.Value == deviceId);
            if (selected.Key == Guid.Empty)
            {
                return ValueTask.FromResult(AudioOperationResult<ImmutableArray<AudioEndpointDescriptor>>.Failure(
                    Error(AudioErrorCode.DeviceNotFound, AudioOperationStage.DeviceDiscovery, retryable: true)));
            }

            return ValueTask.FromResult(AudioOperationResult<ImmutableArray<AudioEndpointDescriptor>>.Success(
                WindowsAudioEndpointMapper.Map(_endpointSource.Enumerate(), moduleMap, deviceId)));
        }
        catch
        {
            return ValueTask.FromResult(AudioOperationResult<ImmutableArray<AudioEndpointDescriptor>>.Failure(
                Error(AudioErrorCode.EndpointMissing, AudioOperationStage.Routing, retryable: true)));
        }
    }

    public static ModuleDeviceId CreateDeviceId(Guid containerId)
    {
        if (containerId == Guid.Empty) throw new ArgumentException("Container identity is required.", nameof(containerId));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"CellPort.Windows.Module.{containerId:D}"));
        return new ModuleDeviceId("windows:" + Convert.ToHexString(digest.AsSpan(0, 16)));
    }

    public bool TryGetContainerId(ModuleDeviceId deviceId, out Guid containerId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        try
        {
            var match = GetModuleContainerIds(_enumeratePnp())
                .SingleOrDefault(candidate => CreateDeviceId(candidate) == deviceId);
            containerId = match;
            return match != Guid.Empty;
        }
        catch
        {
            containerId = Guid.Empty;
            return false;
        }
    }

    public IReadOnlyDictionary<Guid, ModuleDeviceId> GetKnownModuleContainers()
    {
        var nodes = _enumeratePnp();
        return GetModuleContainerIds(nodes).ToDictionary(container => container, CreateDeviceId);
    }

    private static ModuleDeviceCandidate BuildCandidate(
        IReadOnlyList<WindowsPnpDeviceNode> nodes,
        Guid containerId,
        ImmutableArray<WindowsAudioEndpointInfo> endpoints)
    {
        var resolution = WindowsModuleIdentityResolver.Resolve(nodes, containerId);
        var module = resolution.Module;
        var status = resolution.Status == WindowsModuleResolutionStatus.Unique
            ? ModuleIdentityStatus.Verified
            : ModuleIdentityStatus.Unverified;
        return new ModuleDeviceCandidate(
            CreateDeviceId(containerId),
            "CellPort USB module",
            status,
            module?.Interfaces.Any(item => item.Function == WindowsInterfaceFunction.AtControl) == true,
            module?.Interfaces.Any(item => item.Function == WindowsInterfaceFunction.Adb) == true,
            endpoints.Count(endpoint => endpoint.ContainerId == containerId));
    }

    private static Guid[] GetModuleContainerIds(IEnumerable<WindowsPnpDeviceNode> nodes) => nodes
        .Where(node => node.ContainerId is not null && IsModuleNode(node))
        .Select(node => node.ContainerId!.Value)
        .Distinct()
        .ToArray();

    private static bool IsModuleNode(WindowsPnpDeviceNode node) =>
        node.InstanceId.Contains("VID_2CA3&PID_4006", StringComparison.OrdinalIgnoreCase) ||
        node.InstanceId.Contains("VID_2C7C&PID_0125", StringComparison.OrdinalIgnoreCase) ||
        node.HardwareIds.Any(id => id.Contains("VID_2CA3&PID_4006", StringComparison.OrdinalIgnoreCase) ||
                                   id.Contains("VID_2C7C&PID_0125", StringComparison.OrdinalIgnoreCase));

    private static AudioOperationError Error(AudioErrorCode code, AudioOperationStage stage, bool retryable) =>
        new(code, stage, retryable, AudioRecoveryStatus.None);
}
