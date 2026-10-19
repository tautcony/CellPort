namespace CellPort.Core.Transport;

/// <summary>宿主提供的通道发现与创建边界；Core 不解析平台端口路径。</summary>
public interface IAtChannelProvider
{
    /// <summary>平台端口标识的相等规则，例如 Windows 使用 OrdinalIgnoreCase。</summary>
    StringComparer PortNameComparer { get; }

    /// <summary>尝试打开 USB 通道，不支持或不可用时返回 null。成功时所有权移交调用方。</summary>
    Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default);

    /// <summary>按历史提示发现串口；探测通道由实现释放，只返回原样的端口标识。</summary>
    Task<string?> DetectSerialAsync(IReadOnlyList<string> knownPorts, CancellationToken ct = default);

    /// <summary>创建尚未打开的串口通道，所有权移交调用方。</summary>
    IAtChannel CreateSerial(string portName);
}
