using System.Collections.Immutable;
using CellPort.Audio.Core.Models;
using CellPort.Core.Transport;
using CellPort.Platform.Linux;
using Xunit;

namespace CellPort.Platform.Linux.Tests;

public sealed class LinuxUsbDeviceCatalogTests
{
    [Fact]
    public void MapsAtAndAdbInterfacesByUsbParentAndUsesExplicitCurrentTransport()
    {
        var module = MapModule("1-2", "/sys/devices/platform/usb/1-2", null,
            At("1-2:3", 3, "/dev/ttyUSB3"), Adb("1-2:7", 7));
        Assert.Equal(ModuleIdentityStatus.Unverified, module.Candidate.IdentityStatus);
        Assert.True(module.Candidate.HasAtControlChannel);
        Assert.True(module.Candidate.HasAdbControlChannel);

        var at = LinuxUsbDeviceCatalog.SelectAtControlChannel(module, _ => true);
        Assert.Equal(LinuxAtSelectionStatus.Selected, at.Status);
        Assert.Equal("/dev/ttyUSB3", at.Selection!.DeviceNode);

        var adb = new LinuxAdbTransportSelector();
        adb.Update("List of devices attached\n? device product:qdc model:cellport device:qdc transport_id:42 usb:1-2\n",
            new Dictionary<string, string> { [module.SysfsBusId] = module.PhysicalIdentityKey });
        var selected = adb.Select(module);
        Assert.Equal(AdbSelectionStatus.Selected, selected.Status);
        Assert.Equal(42, selected.Selection!.TransportId);
        Assert.Null(selected.Selection.Serial);
        Assert.False(selected.Selection.SerialReliable);
        Assert.True(adb.IsCurrent(selected.Selection));
        Assert.Equal(new[] { "-t", "42", "shell", "id" },
            AdbTransportClient.BuildArguments(selected.Selection, ["shell", "id"]));
    }

    [Fact]
    public void DuplicateFriendlyNamesOnDifferentParentsRemainDistinctAndPortChangesInvalidateIdentity()
    {
        var first = MapModule("1-1", "/sys/devices/platform/usb/1-1", "serial-a", At("1-1:2", 2, "/dev/ttyUSB2"));
        var second = MapModule("1-2", "/sys/devices/platform/usb/1-2", "serial-b", At("1-2:4", 4, "/dev/ttyUSB4"));
        Assert.Equal(first.Candidate.DisplayName, second.Candidate.DisplayName);
        Assert.NotEqual(first.PhysicalIdentityKey, second.PhysicalIdentityKey);

        var moved = MapModule("1-3", "/sys/devices/platform/usb/1-3", "serial-a", At("1-3:11", 11, "/dev/ttyUSB11"));
        Assert.NotEqual(first.PhysicalIdentityKey, moved.PhysicalIdentityKey);
    }

    [Fact]
    public void InterfaceAndTtyNodeRenumberingDoesNotChangeUsbParentIdentity()
    {
        var first = MapModule("1-4", "/sys/devices/platform/usb/1-4", "serial-a",
            At("1-4:1", 1, "/dev/ttyUSB1"), Adb("1-4:5", 5));
        var renumbered = MapModule("1-4", "/sys/devices/platform/usb/1-4", "serial-a",
            At("1-4:8", 8, "/dev/ttyUSB8"), Adb("1-4:11", 11));

        Assert.Equal(first.PhysicalIdentityKey, renumbered.PhysicalIdentityKey);
        Assert.Equal("1-4:8", renumbered.Interfaces.Single(item => item.IsAtControl).Name);
        Assert.Equal("/dev/ttyUSB8",
            LinuxUsbDeviceCatalog.SelectAtControlChannel(renumbered, _ => true).Selection!.DeviceNode);
    }

    [Fact]
    public void AmbiguousOrUnassociatedAdbAndMissingAtNodesFailClosed()
    {
        var module = MapModule("1-5", "/sys/devices/platform/usb/1-5", "serial-a",
            At("1-5:2", 2, "/dev/ttyUSB2"), Adb("1-5:7", 7));
        var byLocation = new Dictionary<string, string> { [module.SysfsBusId] = module.PhysicalIdentityKey };
        var adb = new LinuxAdbTransportSelector();
        adb.Update("? device transport_id:1 usb:1-5\n? device transport_id:2 usb:1-5\n", byLocation);
        Assert.Equal(AdbSelectionStatus.Ambiguous, adb.Select(module).Status);

        adb.Update("? device transport_id:1\n", byLocation);
        Assert.Equal(AdbSelectionStatus.IdentityUnassociated, adb.Select(module).Status);

        adb.Update("? unauthorized transport_id:1 usb:1-5\n", byLocation);
        Assert.Equal(AdbSelectionStatus.TransportUnavailable, adb.Select(module).Status);

        Assert.Equal(LinuxAtSelectionStatus.NodeUnavailable,
            LinuxUsbDeviceCatalog.SelectAtControlChannel(module, _ => false).Status);
    }

    [Fact]
    public void DuplicateSerialsStayUnreliableAndOldTransportSelectionExpiresAfterRefresh()
    {
        var first = MapModule("1-1", "/sys/devices/usb/1-1", "same-serial", At("1-1:2", 2, "/dev/ttyUSB2"));
        var second = MapModule("1-2", "/sys/devices/usb/1-2", "same-serial", At("1-2:2", 2, "/dev/ttyUSB3"));
        var locations = new Dictionary<string, string>
        {
            [first.SysfsBusId] = first.PhysicalIdentityKey,
            [second.SysfsBusId] = second.PhysicalIdentityKey,
        };
        var adb = new LinuxAdbTransportSelector();
        adb.Update("same-serial device transport_id:42 usb:1-1\nsame-serial device transport_id:43 usb:1-2\n", locations);
        var selected = adb.Select(first);
        Assert.Equal(AdbSelectionStatus.Selected, selected.Status);
        Assert.False(selected.Selection!.SerialReliable);
        Assert.Null(selected.Selection.Serial);

        adb.Update("same-serial device transport_id:84 usb:1-1\nsame-serial device transport_id:85 usb:1-2\n", locations);
        Assert.False(adb.IsCurrent(selected.Selection));
        Assert.Equal(84, adb.Select(first).Selection!.TransportId);
    }

    [Fact]
    public void UnsupportedUsbProductsAreNotExposedAsCellPortModules()
    {
        var unsupported = new LinuxUsbDeviceRecord("1-6", "/sys/devices/platform/usb/1-6",
            0x1234, 0x5678, "Other device", null, []);
        Assert.Null(LinuxUsbDeviceMapper.Map(unsupported));
    }

    private static LinuxModuleDevice MapModule(string busId, string physicalPath, string? serial,
        params LinuxUsbInterface[] interfaces)
    {
        var record = new LinuxUsbDeviceRecord(busId, physicalPath, 0x2CA3, 0x4006,
            "CellPort modem", serial, [.. interfaces]);
        return LinuxUsbDeviceMapper.Map(record)!;
    }

    private static LinuxUsbInterface At(string name, int number, string node) => new(
        "/sys/bus/usb/devices/" + name, name, number, 0xFF, 0, 0, "option", [node],
        IsAtControl: true, IsAdbControl: false);

    private static LinuxUsbInterface Adb(string name, int number) => new(
        "/sys/bus/usb/devices/" + name, name, number, 0xFF, 0x42, 0x01, "", [],
        IsAtControl: false, IsAdbControl: true);
}
