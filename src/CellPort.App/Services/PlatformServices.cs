using System.Diagnostics;
using Avalonia.Controls;
using CellPort.Core.Transport;
#if WINDOWS
using CellPort.Platform.Windows;
#else
using CellPort.Platform.MacOS;
#endif

namespace CellPort.App.Services;

public static class PlatformServices
{
    internal static Func<IAtChannelProvider>? ChannelProviderOverride { get; set; }
    internal static Func<IReadOnlyList<string>>? SerialPortsOverride { get; set; }
    internal static Func<IReadOnlyList<string>>? UsbDevicesOverride { get; set; }
    internal static Func<string>? DiagnosticsOverride { get; set; }
    public static bool CanBindWinUsb => OperatingSystem.IsWindows();

    public static IAtChannelProvider CreateChannelProvider() => ChannelProviderOverride?.Invoke() ??
#if WINDOWS
        new WindowsAtChannelProvider();
#else
        new MacAtChannelProvider();
#endif

    public static IReadOnlyList<string> EnumerateSerialPorts() => SerialPortsOverride?.Invoke() ??
#if WINDOWS
        SerialAtChannel.EnumerateCandidates().Select(x => x.PortName).ToArray();
#else
        MacAtChannelProvider.EnumeratePorts();
#endif

    public static IReadOnlyList<string> EnumerateKnownUsbDevices() => UsbDevicesOverride?.Invoke() ??
#if WINDOWS
        [];
#else
        MacAtChannelProvider.EnumerateKnownUsbDevices();
#endif

    public static string GetDiagnostics() => DiagnosticsOverride?.Invoke() ??
#if WINDOWS
        WindowsDiagnostics();
#else
        FormatMacDiagnostics(EnumerateKnownUsbDevices(), EnumerateSerialPorts());
#endif

    internal static string FormatMacSerialDiagnostics(IReadOnlyList<string> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        return ports.Count == 0
            ? "未发现串口。"
            : string.Join("\n", ports);
    }

    internal static string FormatMacDiagnostics(IReadOnlyList<string> usbDevices, IReadOnlyList<string> serialPorts)
    {
        ArgumentNullException.ThrowIfNull(usbDevices);
        ArgumentNullException.ThrowIfNull(serialPorts);
        if (usbDevices.Count > 0) return string.Join("\n", usbDevices);
        return serialPorts.Count > 0 ? string.Join("\n", serialPorts) : "未发现设备。";
    }

    internal static string DescribeConnectionFailure(string? selectedPort, IReadOnlyList<string> ports, IReadOnlyList<string>? usbDevices = null)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        if (selectedPort is not null && !ports.Contains(selectedPort, comparer))
            return $"串口已断开：{selectedPort}";

        if (ports.Count == 0)
        {
            if (OperatingSystem.IsMacOS() && usbDevices is not null)
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

    public static Task ShowDriverSetupAsync(Control owner)
    {
#if WINDOWS
        return DialogService.ShowAsync(owner, "如需 USB 直连，请在设备管理器中为 QDC507 的 MI_02 接口安装 WinUSB 驱动。选择“更新驱动程序”→“浏览我的电脑”→“从可用驱动程序列表选取”→“通用串行总线设备”→“WinUsb 设备”。", "WinUSB 驱动设置");
#else
        return Task.CompletedTask;
#endif
    }

    public static void OpenSystemDeviceManager()
    {
#if WINDOWS
        Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
#else
        var start = new ProcessStartInfo("open") { UseShellExecute = false };
        start.ArgumentList.Add("-a"); start.ArgumentList.Add("System Information");
        Process.Start(start);
#endif
    }

#if WINDOWS
    private static string WindowsDiagnostics()
    {
        try { return ModuleScanner.Scan().Describe(); }
        catch (Exception ex) { return $"Windows 设备扫描失败：{ex.Message}\n\n串口候选：\n{string.Join("\n", EnumerateSerialPorts())}"; }
    }
#endif

    internal static void ResetOverrides()
    {
        ChannelProviderOverride = null; SerialPortsOverride = null; UsbDevicesOverride = null; DiagnosticsOverride = null;
    }
}
