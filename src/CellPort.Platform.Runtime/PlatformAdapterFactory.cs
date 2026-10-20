using CellPort.Platform.Abstractions;

namespace CellPort.Platform.Runtime;

/// <summary>Creates the adapter packaged for the current operating system.</summary>
public static class PlatformAdapterFactory
{
    public static IPlatformServices CreateForCurrentOperatingSystem()
    {
        var adapterTypeName = OperatingSystem.IsWindows()
            ? "CellPort.Platform.Windows.WindowsPlatformServices, CellPort.Platform.Windows"
            : OperatingSystem.IsMacOS()
                ? "CellPort.Platform.MacOS.MacPlatformServices, CellPort.Platform.MacOS"
                : OperatingSystem.IsLinux()
                    ? "CellPort.Platform.Linux.LinuxPlatformServices, CellPort.Platform.Linux"
                    : throw new PlatformNotSupportedException("CellPort does not have a platform adapter for this operating system.");

        var adapterType = Type.GetType(adapterTypeName, throwOnError: true)!;
        if (Activator.CreateInstance(adapterType) is not IPlatformServices services)
            throw new InvalidOperationException($"Platform adapter {adapterType.FullName} does not implement {nameof(IPlatformServices)}.");
        return services;
    }
}
