using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CellPort.Audio.Core.Models;

namespace CellPort.Platform.Linux;

public sealed record LinuxUsbInterface(
    string SysfsPath,
    string Name,
    int? InterfaceNumber,
    int InterfaceClass,
    int InterfaceSubclass,
    int InterfaceProtocol,
    string? Driver,
    ImmutableArray<string> DeviceNodes,
    bool IsAtControl,
    bool IsAdbControl);

public sealed record LinuxUsbDeviceRecord(
    string SysfsBusId,
    string PhysicalPath,
    ushort VendorId,
    ushort ProductId,
    string? ProductName,
    string? Serial,
    ImmutableArray<LinuxUsbInterface> Interfaces);

/// <summary>One USB parent and its associated control interfaces, discovered read-only from sysfs.</summary>
public sealed record LinuxModuleDevice(
    ModuleDeviceCandidate Candidate,
    string SysfsBusId,
    string PhysicalIdentityKey,
    string PhysicalPath,
    ushort VendorId,
    ushort ProductId,
    string? Serial,
    ImmutableArray<LinuxUsbInterface> Interfaces)
{
    public override string ToString() => "LinuxModuleDevice(physical identity redacted)";
}

public enum LinuxAtSelectionStatus
{
    Selected,
    NoChannel,
    Ambiguous,
    NodeUnavailable,
}

public sealed record LinuxAtChannelSelection(string DeviceNode, string PhysicalIdentityKey, string SysfsBusId);
public sealed record LinuxAtSelectionResult(LinuxAtSelectionStatus Status, LinuxAtChannelSelection? Selection);

public static class LinuxUsbDeviceMapper
{
    private static readonly HashSet<(ushort Vendor, ushort Product)> SupportedProducts =
    [
        (0x2CA3, 0x4006),
        (0x2C7C, 0x0125),
    ];

    public static LinuxModuleDevice? Map(LinuxUsbDeviceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!SupportedProducts.Contains((record.VendorId, record.ProductId))) return null;
        var physicalKey = CreatePhysicalIdentityKey(record.VendorId, record.ProductId, record.PhysicalPath, record.Serial);
        var atCount = record.Interfaces.Count(item => item.IsAtControl);
        var adbCount = record.Interfaces.Count(item => item.IsAdbControl);
        var reliableSerial = !string.IsNullOrWhiteSpace(record.Serial) && record.Serial.Trim() != "?";
        var idDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(physicalKey))).ToLowerInvariant();
        var displayName = string.IsNullOrWhiteSpace(record.ProductName)
            ? $"USB {record.VendorId:X4}:{record.ProductId:X4}"
            : record.ProductName.Trim();
        var candidate = new ModuleDeviceCandidate(new ModuleDeviceId($"linux:{idDigest}"), displayName,
            reliableSerial ? ModuleIdentityStatus.Verified : ModuleIdentityStatus.Unverified,
            atCount == 1, adbCount == 1, AssociatedAudioEndpointCount: 0);
        return new LinuxModuleDevice(candidate, record.SysfsBusId, physicalKey, record.PhysicalPath,
            record.VendorId, record.ProductId, record.Serial, record.Interfaces);
    }

    private static string CreatePhysicalIdentityKey(ushort vendor, ushort product, string physicalPath, string? serial)
    {
        // The physical USB topology is deliberately part of the key: moving a device or
        // replacing it at an old port requires re-selection and new session evidence.
        var normalizedPath = physicalPath.Replace('\\', '/').TrimEnd('/');
        var canonical = $"{vendor:X4}:{product:X4}:{normalizedPath}:{serial?.Trim() ?? string.Empty}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

/// <summary>
/// Enumerates supported module USB parents and their interfaces without opening any device,
/// changing permissions, or interacting with system services.
/// </summary>
public sealed class LinuxUsbDeviceCatalog
{
    private readonly string _sysfsRoot;
    private readonly string _deviceRoot;

    public LinuxUsbDeviceCatalog(string sysfsRoot = "/sys/bus/usb/devices", string deviceRoot = "/dev")
    {
        _sysfsRoot = Path.GetFullPath(sysfsRoot);
        _deviceRoot = Path.GetFullPath(deviceRoot);
    }

    public ImmutableArray<LinuxModuleDevice> EnumerateModules()
    {
        if (!Directory.Exists(_sysfsRoot)) return [];

        var modules = ImmutableArray.CreateBuilder<LinuxModuleDevice>();
        foreach (var entry in Directory.EnumerateDirectories(_sysfsRoot).Order(StringComparer.Ordinal))
        {
            var busId = Path.GetFileName(entry);
            if (busId.Contains(':')) continue;
            if (!TryReadHex(Path.Combine(entry, "idVendor"), out var vendor) ||
                !TryReadHex(Path.Combine(entry, "idProduct"), out var product))
                continue;

            var physicalPath = ResolvePhysicalPath(entry);
            var serial = ReadTrimmed(Path.Combine(entry, "serial"));
            var interfaces = EnumerateInterfaces(entry, busId);
            var module = LinuxUsbDeviceMapper.Map(new LinuxUsbDeviceRecord(busId, physicalPath,
                vendor, product, ReadTrimmed(Path.Combine(entry, "product")), serial, interfaces));
            if (module is not null) modules.Add(module);
        }
        return modules.ToImmutable();
    }

    public static LinuxAtSelectionResult SelectAtControlChannel(LinuxModuleDevice module, Func<string, bool>? deviceNodeExists = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        deviceNodeExists ??= File.Exists;
        var nodes = module.Interfaces.Where(item => item.IsAtControl)
            .SelectMany(item => item.DeviceNodes).Distinct(StringComparer.Ordinal).ToArray();
        if (nodes.Length == 0) return new(LinuxAtSelectionStatus.NoChannel, null);
        if (nodes.Length != 1) return new(LinuxAtSelectionStatus.Ambiguous, null);
        if (!deviceNodeExists(nodes[0])) return new(LinuxAtSelectionStatus.NodeUnavailable, null);
        return new(LinuxAtSelectionStatus.Selected,
            new LinuxAtChannelSelection(nodes[0], module.PhysicalIdentityKey, module.SysfsBusId));
    }

    private ImmutableArray<LinuxUsbInterface> EnumerateInterfaces(string usbDevicePath, string busId)
    {
        var result = ImmutableArray.CreateBuilder<LinuxUsbInterface>();
        foreach (var path in Directory.EnumerateDirectories(usbDevicePath).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(busId + ":", StringComparison.Ordinal)) continue;
            if (!TryReadHex(Path.Combine(path, "bInterfaceClass"), out var interfaceClass) ||
                !TryReadHex(Path.Combine(path, "bInterfaceSubClass"), out var interfaceSubclass) ||
                !TryReadHex(Path.Combine(path, "bInterfaceProtocol"), out var interfaceProtocol))
                continue;

            var driver = ReadDriver(path);
            var isAdb = interfaceClass == 0xFF && interfaceSubclass == 0x42 && interfaceProtocol == 0x01;
            var isSerial = driver is "option" or "cdc_acm" or "usbserial" or "usbserial_generic";
            var deviceNodes = isSerial ? FindTtyNodes(path) : [];
            var isAt = isSerial && !deviceNodes.IsEmpty;
            result.Add(new LinuxUsbInterface(path, name,
                TryParseHex(Path.Combine(path, "bInterfaceNumber"), out var number) ? number : null,
                interfaceClass, interfaceSubclass, interfaceProtocol, driver, deviceNodes, isAt, isAdb));
        }
        return result.ToImmutable();
    }

    private ImmutableArray<string> FindTtyNodes(string interfacePath)
    {
        var nodes = ImmutableArray.CreateBuilder<string>();
        foreach (var child in Directory.EnumerateDirectories(interfacePath))
        {
            var ttyName = Path.GetFileName(child);
            if (!ttyName.StartsWith("tty", StringComparison.Ordinal)) continue;
            var nestedTty = Path.Combine(child, "tty");
            if (!Directory.Exists(nestedTty)) continue;
            foreach (var entry in Directory.EnumerateDirectories(nestedTty))
            {
                var nodeName = Path.GetFileName(entry);
                if (nodeName.StartsWith("tty", StringComparison.Ordinal))
                    nodes.Add(Path.Combine(_deviceRoot, nodeName));
            }
        }
        return nodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static string? ReadDriver(string interfacePath)
    {
        var driverLink = Path.Combine(interfacePath, "driver");
        try
        {
            var target = new DirectoryInfo(driverLink).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) return target.Name;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var uevent = ReadTrimmed(Path.Combine(interfacePath, "uevent"));
        if (uevent is null) return null;
        foreach (var line in uevent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("DRIVER=", StringComparison.Ordinal)) return line[7..].Trim();
        }
        return null;
    }

    private static string ResolvePhysicalPath(string path)
    {
        try
        {
            return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException) { return Path.GetFullPath(path); }
        catch (UnauthorizedAccessException) { return Path.GetFullPath(path); }
    }

    private static bool TryReadHex(string path, out ushort value) =>
        TryParseHex(path, out value);

    private static bool TryParseHex(string path, out ushort value)
    {
        value = 0;
        var text = ReadTrimmed(path);
        return text is not null && ushort.TryParse(text, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out value);
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
