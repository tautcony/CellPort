using System.Diagnostics;
using System.Text;
using CellPort.Core.Transport;
using CellPort.Transport.Serial;

namespace CellPort.Platform.Linux;

/// <summary>Discovers Linux USB serial ports for supported modules and probes them for an AT channel.</summary>
public sealed class LinuxAtChannelProvider : IAtChannelProvider
{
    private readonly LinuxUsbDeviceCatalog _catalog;

    public LinuxAtChannelProvider() : this(new LinuxUsbDeviceCatalog()) { }

    public LinuxAtChannelProvider(LinuxUsbDeviceCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public StringComparer PortNameComparer => StringComparer.Ordinal;

    public Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IAtChannel?>(null);
    }

    public IAtChannel CreateSerial(string portName) => new SerialAtChannel(portName);

    public IReadOnlyList<string> EnumerateSerialPorts()
    {
        var modulePorts = GetModulePorts();
        var genericUsbPorts = SerialAtChannel.EnumerateCandidates()
            .Select(candidate => candidate.PortName)
            .Where(IsUsbSerialPort);
        return modulePorts.Concat(genericUsbPorts)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> EnumerateKnownUsbDevices() => _catalog.EnumerateModules()
        .Select(module => $"{GetModuleName(module.VendorId, module.ProductId)} · {module.VendorId:X4}:{module.ProductId:X4}")
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    public async Task<string?> DetectSerialAsync(IReadOnlyList<string> knownPorts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(knownPorts);
        var candidates = await Task.Run(EnumerateSerialPorts, ct).ConfigureAwait(false);

        // 高优先级：上次成功的端口 → 已知模块的 AT 节点；其余候选并发兜底。
        var priority = knownPorts.Where(port => candidates.Contains(port, StringComparer.Ordinal))
            .Concat(GetModulePorts().Where(port => candidates.Contains(port, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (priority.Count == 0 && candidates.Count == 0) return null;

        var ordered = priority
            .Concat(candidates.Where(port => !priority.Contains(port, StringComparer.Ordinal)))
            .ToArray();

        return await AtPortProbe.FindAsync(
            CreateSerial, ordered, priority.Count, TimeSpan.FromSeconds(15),
            AtPortProbe.DefaultConcurrency, ct).ConfigureAwait(false);
    }

    private IReadOnlyList<string> GetModulePorts() => _catalog.EnumerateModules()
        .SelectMany(module => module.Interfaces.Where(item => item.IsAtControl).SelectMany(item => item.DeviceNodes))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool IsUsbSerialPort(string path) =>
        path.StartsWith("/dev/ttyUSB", StringComparison.Ordinal) ||
        path.StartsWith("/dev/ttyACM", StringComparison.Ordinal);

    private static string GetModuleName(ushort vendorId, ushort productId) =>
        (vendorId, productId) == (0x2CA3, 0x4006) ? "QDC507" : "Quectel";
}
