using CellPort.Core.Transport;

namespace CellPort.Platform.Abstractions;

/// <summary>Platform-specific device discovery, channel creation, diagnostics, and OS actions.</summary>
public interface IPlatformServices
{
    IAtChannelProvider CreateChannelProvider();
    IReadOnlyList<string> EnumerateSerialPorts();
    IReadOnlyList<string> EnumerateKnownUsbDevices();
    string GetDiagnostics();
    string DescribeConnectionFailure(string? selectedPort, IReadOnlyList<string> ports, IReadOnlyList<string>? usbDevices = null);

    bool SupportsUsbDriverBinding { get; }
    string DeviceManagerButtonLabel { get; }
    string? UsbDriverSetupInstructions { get; }
    void OpenSystemDeviceManager();
}

public static class PlatformDiagnosticFormatter
{
    public static string FormatSerialDiagnostics(IReadOnlyList<string> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        return ports.Count == 0 ? "未发现串口。" : string.Join("\n", ports);
    }

    public static string FormatDeviceDiagnostics(IReadOnlyList<string> usbDevices, IReadOnlyList<string> serialPorts)
    {
        ArgumentNullException.ThrowIfNull(usbDevices);
        ArgumentNullException.ThrowIfNull(serialPorts);
        if (usbDevices.Count > 0) return string.Join("\n", usbDevices);
        return serialPorts.Count > 0 ? string.Join("\n", serialPorts) : "未发现设备。";
    }

    public static string DescribeConnectionFailure(
        string? selectedPort,
        IReadOnlyList<string> ports,
        IReadOnlyList<string>? usbDevices,
        StringComparer portNameComparer,
        bool useUsbDevicesWhenNoSerialPorts)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(portNameComparer);

        if (selectedPort is not null && !ports.Contains(selectedPort, portNameComparer))
            return $"串口已断开：{selectedPort}";

        if (ports.Count == 0)
        {
            if (useUsbDevicesWhenNoSerialPorts && usbDevices is not null)
                return usbDevices.Count > 0 ? DescribeUsbFailure(usbDevices) : "未发现设备。";
            return "未发现串口。";
        }

        if (selectedPort is null && usbDevices is { Count: > 0 })
            return DescribeUsbFailure(usbDevices);

        return selectedPort is null
            ? $"检测到 {ports.Count} 个串口，模块未响应。"
            : $"串口 {selectedPort} 未响应。";
    }

    private static string DescribeUsbFailure(IReadOnlyList<string> devices)
    {
        if (devices.Count != 1) return "模块未响应。";
        var separator = devices[0].IndexOf('·');
        var name = separator > 0 ? devices[0][..separator].Trim() : "模块";
        return $"{name} 未响应。";
    }
}
