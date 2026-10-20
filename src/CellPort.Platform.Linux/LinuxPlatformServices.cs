using System.Diagnostics;
using CellPort.Core.Transport;
using CellPort.Platform.Abstractions;

namespace CellPort.Platform.Linux;

public sealed class LinuxPlatformServices : IPlatformServices
{
    private readonly LinuxAtChannelProvider _channelProvider = new();

    public bool SupportsUsbDriverBinding => false;
    public string DeviceManagerButtonLabel => "打开 USB 设备目录";
    public string? UsbDriverSetupInstructions => null;

    public IAtChannelProvider CreateChannelProvider() => _channelProvider;
    public IReadOnlyList<string> EnumerateSerialPorts() => _channelProvider.EnumerateSerialPorts();
    public IReadOnlyList<string> EnumerateKnownUsbDevices() => _channelProvider.EnumerateKnownUsbDevices();

    public string GetDiagnostics() =>
        PlatformDiagnosticFormatter.FormatDeviceDiagnostics(EnumerateKnownUsbDevices(), EnumerateSerialPorts());

    public string DescribeConnectionFailure(string? selectedPort, IReadOnlyList<string> ports, IReadOnlyList<string>? usbDevices = null) =>
        PlatformDiagnosticFormatter.DescribeConnectionFailure(selectedPort, ports, usbDevices,
            StringComparer.Ordinal, useUsbDevicesWhenNoSerialPorts: false);

    public void OpenSystemDeviceManager()
    {
        var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add("/sys/bus/usb/devices");
        Process.Start(start);
    }
}
