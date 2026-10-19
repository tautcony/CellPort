using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace CellPort.Core.Transport;

public enum WindowsInterfaceFunction
{
    Unknown,
    AtControl,
    Adb,
    Audio,
    Data,
    Diagnostic,
}

public enum WindowsModuleResolutionStatus
{
    NotFound,
    Unique,
    Ambiguous,
    IdentityIncomplete,
}

/// <summary>Read-only PnP properties used to associate module interfaces with a physical USB device.</summary>
public sealed record WindowsPnpDeviceNode(
    string InstanceId,
    string? ParentInstanceId,
    Guid? ContainerId,
    ImmutableArray<string> LocationPaths,
    ImmutableArray<string> HardwareIds,
    string? FriendlyName);

public sealed record WindowsModuleInterface(
    string InstanceId,
    string? FriendlyName,
    int? InterfaceNumber,
    WindowsInterfaceFunction Function);

public sealed record WindowsModuleIdentity(
    Guid ContainerId,
    string ParentInstanceId,
    ImmutableArray<string> LocationPaths,
    ImmutableArray<WindowsModuleInterface> Interfaces)
{
    public ImmutableArray<WindowsModuleInterface> AtControlInterfaces =>
        [.. Interfaces.Where(item => item.Function == WindowsInterfaceFunction.AtControl)];
}

public sealed record WindowsModuleResolution(
    WindowsModuleResolutionStatus Status,
    WindowsModuleIdentity? Module,
    int CandidateCount);

/// <summary>
/// Resolves module PnP devnodes by physical container/topology identity, never by friendly name,
/// COM path, or a fixed interface number.
/// </summary>
public static class WindowsModuleIdentityResolver
{
    private static readonly Regex InterfaceNumberPattern = new("&MI_([0-9A-F]{2})(?:&|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AtLabelPattern = new(@"\bAT\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static WindowsModuleResolution Resolve(
        IEnumerable<WindowsPnpDeviceNode> deviceNodes,
        Guid? selectedContainerId = null)
    {
        ArgumentNullException.ThrowIfNull(deviceNodes);
        var nodes = deviceNodes.ToArray();
        var candidates = nodes
            .Where(IsCellPortModuleNode)
            .Where(node => selectedContainerId is null || node.ContainerId == selectedContainerId)
            .GroupBy(node => node.ContainerId)
            .Where(group => group.Key.HasValue)
            .Select(group => BuildIdentity(group.Key!.Value, group.ToArray()))
            .ToArray();

        if (candidates.Length == 0)
        {
            var incomplete = nodes.Any(IsCellPortModuleNode);
            return new WindowsModuleResolution(
                incomplete ? WindowsModuleResolutionStatus.IdentityIncomplete : WindowsModuleResolutionStatus.NotFound,
                null,
                0);
        }

        if (candidates.Length > 1)
        {
            return new WindowsModuleResolution(WindowsModuleResolutionStatus.Ambiguous, null, candidates.Length);
        }

        var candidate = candidates[0];
        if (candidate is null)
        {
            return new WindowsModuleResolution(WindowsModuleResolutionStatus.IdentityIncomplete, null, 1);
        }

        return new WindowsModuleResolution(WindowsModuleResolutionStatus.Unique, candidate, 1);
    }

    private static WindowsModuleIdentity? BuildIdentity(Guid containerId, WindowsPnpDeviceNode[] nodes)
    {
        var parents = nodes.Select(node => node.ParentInstanceId)
            .Where(parent => !string.IsNullOrWhiteSpace(parent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var locations = nodes.SelectMany(node => node.LocationPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        if (parents.Length != 1 || locations.IsEmpty || nodes.Any(node => node.LocationPaths.IsDefaultOrEmpty))
        {
            return null;
        }

        var interfaces = nodes.Select(node => new WindowsModuleInterface(
            node.InstanceId,
            node.FriendlyName,
            ParseInterfaceNumber(node.InstanceId, node.HardwareIds),
            ClassifyFunction(node.FriendlyName)))
            .OrderBy(item => item.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new WindowsModuleIdentity(containerId, parents[0]!, locations, interfaces);
    }

    private static bool IsCellPortModuleNode(WindowsPnpDeviceNode node)
    {
        var ids = (node.HardwareIds.IsDefault ? ImmutableArray<string>.Empty : node.HardwareIds)
            .Append(node.InstanceId);
        return ids.Any(id => id.Contains("VID_2CA3&PID_4006", StringComparison.OrdinalIgnoreCase) ||
                             id.Contains("VID_2C7C&PID_0125", StringComparison.OrdinalIgnoreCase));
    }

    private static int? ParseInterfaceNumber(string instanceId, ImmutableArray<string> hardwareIds)
    {
        var ids = (hardwareIds.IsDefault ? ImmutableArray<string>.Empty : hardwareIds).Prepend(instanceId);
        foreach (var id in ids)
        {
            var match = InterfaceNumberPattern.Match(id);
            if (match.Success && int.TryParse(match.Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var number))
            {
                return number;
            }
        }
        return null;
    }

    private static WindowsInterfaceFunction ClassifyFunction(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return WindowsInterfaceFunction.Unknown;
        if (friendlyName.Contains("ADB", StringComparison.OrdinalIgnoreCase)) return WindowsInterfaceFunction.Adb;
        if (AtLabelPattern.IsMatch(friendlyName)) return WindowsInterfaceFunction.AtControl;
        if (friendlyName.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
            friendlyName.Contains("Sound", StringComparison.OrdinalIgnoreCase)) return WindowsInterfaceFunction.Audio;
        if (friendlyName.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
            friendlyName.Contains("Network", StringComparison.OrdinalIgnoreCase)) return WindowsInterfaceFunction.Data;
        if (friendlyName.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase) ||
            friendlyName.Contains("DM Port", StringComparison.OrdinalIgnoreCase)) return WindowsInterfaceFunction.Diagnostic;
        return WindowsInterfaceFunction.Unknown;
    }
}
