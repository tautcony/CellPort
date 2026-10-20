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

    /// <summary>
    /// 已连接的已知模块摘要（与 macOS/Linux 保持一致的展示口径）。
    /// 通过 PnP 实例 ID / HardwareID 匹配 VID&amp;PID，缺失时返回空列表。
    /// </summary>
    public IReadOnlyList<string> EnumerateKnownUsbDevices()
    {
        try
        {
            return DescribeKnownModules(WindowsUsbDeviceEnumerator.EnumeratePresent());
        }
        catch (Exception)
        {
            // 枚举失败（权限/驱动异常）不应影响诊断页其它信息
            return [];
        }
    }

    internal static IReadOnlyList<string> DescribeKnownModules(IReadOnlyList<WindowsPnpDeviceNode> nodes)
    {
        var labels = new List<string>();
        foreach (var (vendorId, productId) in ModuleIdentity.KnownIds)
        {
            var marker = $"VID_{vendorId:X4}&PID_{productId:X4}";
            if (!nodes.Any(node => MatchesModule(node, marker)))
            {
                continue;
            }

            var name = (vendorId, productId) == (ModuleIdentity.DjiVendorId, ModuleIdentity.DjiProductId)
                ? "QDC507"
                : "Quectel";
            labels.Add($"{name} · {vendorId:X4}:{productId:X4}");
        }

        return labels;
    }

    private static bool MatchesModule(WindowsPnpDeviceNode node, string marker)
    {
        if (node.InstanceId.Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !node.HardwareIds.IsDefaultOrEmpty &&
               node.HardwareIds.Any(id => id.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

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
