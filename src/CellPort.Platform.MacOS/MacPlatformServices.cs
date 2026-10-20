using System.Diagnostics;
using CellPort.Core.Transport;
using CellPort.Platform.Abstractions;

namespace CellPort.Platform.MacOS;

public sealed class MacPlatformServices : IPlatformServices
{
    public bool SupportsUsbDriverBinding => false;
    public string DeviceManagerButtonLabel => "系统信息";
    public string? UsbDriverSetupInstructions => null;

    public IAtChannelProvider CreateChannelProvider() => new MacAtChannelProvider();
    public IReadOnlyList<string> EnumerateSerialPorts() => MacAtChannelProvider.EnumeratePorts();
    public IReadOnlyList<string> EnumerateKnownUsbDevices() => MacAtChannelProvider.EnumerateKnownUsbDevices();

    public string GetDiagnostics() =>
        PlatformDiagnosticFormatter.FormatDeviceDiagnostics(EnumerateKnownUsbDevices(), EnumerateSerialPorts());

    public string DescribeConnectionFailure(string? selectedPort, IReadOnlyList<string> ports, IReadOnlyList<string>? usbDevices = null) =>
        PlatformDiagnosticFormatter.DescribeConnectionFailure(selectedPort, ports, usbDevices,
            StringComparer.Ordinal, useUsbDevicesWhenNoSerialPorts: true);

    public void OpenSystemDeviceManager()
    {
        var start = new ProcessStartInfo("open") { UseShellExecute = false };
        start.ArgumentList.Add("-a");
        start.ArgumentList.Add("System Information");
        Process.Start(start);
    }
}
