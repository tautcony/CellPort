using System.Collections.Immutable;
using System.Globalization;
using CellPort.Core.Transport;

namespace CellPort.Platform.Linux;

/// <summary>Associates adb's current USB location with the selected sysfs USB parent.</summary>
public sealed class LinuxAdbTransportSelector
{
    private ImmutableArray<AdbDeviceCandidate> _devices = [];
    private long _generation;

    public void Update(string adbDevicesLongOutput, IReadOnlyDictionary<string, string> physicalKeysByUsbLocation)
    {
        ArgumentNullException.ThrowIfNull(adbDevicesLongOutput);
        ArgumentNullException.ThrowIfNull(physicalKeysByUsbLocation);
        var devices = ImmutableArray.CreateBuilder<AdbDeviceCandidate>();
        foreach (var line in adbDevicesLongOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0].StartsWith('*') ||
                fields[0].Equals("List", StringComparison.OrdinalIgnoreCase)) continue;

            long? transportId = null;
            string? usbLocation = null;
            foreach (var field in fields.Skip(2))
            {
                if (field.StartsWith("transport_id:", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(field.AsSpan("transport_id:".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                    transportId = parsed;
                else if (field.StartsWith("usb:", StringComparison.OrdinalIgnoreCase))
                    usbLocation = field[4..];
            }

            var physicalKey = usbLocation is not null && physicalKeysByUsbLocation.TryGetValue(usbLocation, out var key)
                ? key
                : null;
            devices.Add(new AdbDeviceCandidate(fields[0], fields[1], transportId, physicalKey,
                !string.IsNullOrWhiteSpace(fields[0]) && fields[0] != "?"));
        }
        var duplicateSerials = devices.Where(device => device.SerialReliable)
            .GroupBy(device => device.Serial, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _devices = [.. devices.Select(device => device with
        {
            SerialReliable = device.SerialReliable && !duplicateSerials.Contains(device.Serial),
        })];
        _generation = checked(_generation + 1);
    }

    public AdbSelectionResult Select(LinuxModuleDevice module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var matches = _devices.Where(device => device.PhysicalIdentityKey == module.PhysicalIdentityKey).ToArray();
        if (matches.Length == 0) return new(AdbSelectionStatus.IdentityUnassociated, null);
        if (matches.Length > 1) return new(AdbSelectionStatus.Ambiguous, null);
        var device = matches[0];
        if (!device.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            return new(AdbSelectionStatus.TransportUnavailable, null);
        if (device.TransportId is not long transportId || transportId <= 0)
            return new(AdbSelectionStatus.TransportUnavailable, null);
        return new(AdbSelectionStatus.Selected,
            new AdbTransportSelection(transportId,
                device.SerialReliable ? device.Serial : null,
                device.SerialReliable,
                module.PhysicalIdentityKey,
                _generation));
    }

    public bool IsCurrent(AdbTransportSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.CatalogGeneration == _generation &&
               _devices.Any(device => device.TransportId == selection.TransportId &&
                   device.State.Equals("device", StringComparison.OrdinalIgnoreCase) &&
                   device.PhysicalIdentityKey == selection.PhysicalIdentityKey);
    }
}
