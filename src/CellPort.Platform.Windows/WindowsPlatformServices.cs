using System.Diagnostics;
using CellPort.Core.Transport;
using CellPort.Platform.Abstractions;
using CellPort.Transport.Serial;

namespace CellPort.Platform.Windows;

public sealed class WindowsPlatformServices : IPlatformServices
{
    public bool SupportsUsbDriverBinding => true;
    public string DeviceManagerButtonLabel => "打开设备管理器";
    public string? UsbDriverSetupInstructions =>
        "如需 USB 直连，请在设备管理器中为 QDC507 的 MI_02 接口安装 WinUSB 驱动。选择“更新驱动程序”→“浏览我的电脑”→“从可用驱动程序列表选取”→“通用串行总线设备”→“WinUsb 设备”。";

    public IAtChannelProvider CreateChannelProvider() => new WindowsAtChannelProvider();

    public IReadOnlyList<string> EnumerateSerialPorts() =>
        SerialAtChannel.EnumerateCandidates().Select(candidate => candidate.PortName).ToArray();

    public IReadOnlyList<string> EnumerateKnownUsbDevices() => [];

    public string GetDiagnostics()
    {
        try { return ModuleScanner.Scan().Describe(); }
        catch (Exception ex)
        {
            return $"Windows 设备扫描失败：{ex.Message}\n\n串口候选：\n{string.Join("\n", EnumerateSerialPorts())}";
        }
    }

    public string DescribeConnectionFailure(string? selectedPort, IReadOnlyList<string> ports, IReadOnlyList<string>? usbDevices = null) =>
        PlatformDiagnosticFormatter.DescribeConnectionFailure(selectedPort, ports, usbDevices,
            StringComparer.OrdinalIgnoreCase, useUsbDevicesWhenNoSerialPorts: false);

    public void OpenSystemDeviceManager() =>
        Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
}
