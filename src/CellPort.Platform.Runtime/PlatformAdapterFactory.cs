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

        // 平台程序集按 RID 分发，缺失时 Type.GetType 只会抛出不带上下文的
        // TypeLoadException。这里转成可读的诊断信息，便于用户判断是「安装包不完整」。
        Type? adapterType;
        try
        {
            adapterType = Type.GetType(adapterTypeName, throwOnError: false);
        }
        catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException(
                $"无法加载平台适配器：{adapterTypeName}。请确认安装包包含当前平台的程序集（{DescribePlatform()}）。", ex);
        }

        if (adapterType is null)
        {
            throw new InvalidOperationException(
                $"未找到当前平台的适配器：{adapterTypeName}。" +
                $"安装包可能缺少 {DescribePlatform()} 平台程序集，请重新安装完整版本。");
        }

        if (Activator.CreateInstance(adapterType) is not IPlatformServices services)
        {
            throw new InvalidOperationException(
                $"平台适配器 {adapterType.FullName} 未实现 {nameof(IPlatformServices)}。");
        }

        return services;
    }

    private static string DescribePlatform() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : "未知系统";
}
