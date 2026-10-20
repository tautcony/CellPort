using CellPort.Core.Transport;
using CellPort.Platform.Abstractions;
using CellPort.Platform.Runtime;

namespace CellPort.App.Services;

/// <summary>UI-facing access to the platform adapter selected by the platform layer.</summary>
public static class PlatformServices
{
    internal static Func<IAtChannelProvider>? ChannelProviderOverride { get; set; }
    internal static Func<IReadOnlyList<string>>? SerialPortsOverride { get; set; }
    internal static Func<IReadOnlyList<string>>? UsbDevicesOverride { get; set; }
    internal static Func<string>? DiagnosticsOverride { get; set; }

    private static IPlatformServices? _platform;
    private static IPlatformServices Platform => _platform ??= PlatformAdapterFactory.CreateForCurrentOperatingSystem();

    public static bool SupportsUsbDriverBinding => Platform.SupportsUsbDriverBinding;
    public static string DeviceManagerButtonLabel => Platform.DeviceManagerButtonLabel;

    public static IAtChannelProvider CreateChannelProvider() => ChannelProviderOverride?.Invoke() ?? Platform.CreateChannelProvider();
    public static IReadOnlyList<string> EnumerateSerialPorts() => SerialPortsOverride?.Invoke() ?? Platform.EnumerateSerialPorts();
    public static IReadOnlyList<string> EnumerateKnownUsbDevices() => UsbDevicesOverride?.Invoke() ?? Platform.EnumerateKnownUsbDevices();
    public static string GetDiagnostics() => DiagnosticsOverride?.Invoke() ?? Platform.GetDiagnostics();

    public static string DescribeConnectionFailure(string? selectedPort, IReadOnlyList<string> ports, IReadOnlyList<string>? usbDevices = null) =>
        Platform.DescribeConnectionFailure(selectedPort, ports, usbDevices);

    public static string? GetDriverSetupInstructions() => Platform.UsbDriverSetupInstructions;
    public static void OpenSystemDeviceManager() => Platform.OpenSystemDeviceManager();

    internal static void ResetOverrides()
    {
        ChannelProviderOverride = null;
        SerialPortsOverride = null;
        UsbDevicesOverride = null;
        DiagnosticsOverride = null;
        _platform = null;
    }
}
