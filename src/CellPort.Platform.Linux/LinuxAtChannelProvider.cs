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
        var ordered = knownPorts.Where(port => candidates.Contains(port, StringComparer.Ordinal))
            .Concat(GetModulePorts().Where(port => candidates.Contains(port, StringComparer.Ordinal)))
            .Concat(candidates)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        foreach (var port in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) return null;
            try
            {
                await using var channel = CreateSerial(port);
                await channel.OpenAsync(timeout.Token).ConfigureAwait(false);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await channel.WriteAsync("AT\r"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                    var response = new StringBuilder();
                    var buffer = new byte[256];
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromMilliseconds(700))
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        var count = await channel.ReadAsync(buffer, TimeSpan.FromMilliseconds(150), timeout.Token)
                            .ConfigureAwait(false);
                        response.Append(Encoding.ASCII.GetString(buffer, 0, count));
                        if (response.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                            .Any(line => line.Trim() == "OK"))
                            return port;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException
                                      or InvalidOperationException)
            {
                // A busy, unplugged or non-AT port must not stop discovery of other ports.
            }
        }
        return null;
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
