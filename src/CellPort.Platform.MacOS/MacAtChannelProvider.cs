using System.Diagnostics;
using System.Text;
using CellPort.Core.Transport;
using CellPort.Transport.Serial;

namespace CellPort.Platform.MacOS;

/// <summary>Prefers a VID/PID- and location-matched libusb AT interface, then falls back to macOS callout devices.</summary>
public sealed class MacAtChannelProvider : IAtChannelProvider
{
    private readonly Func<IReadOnlyList<string>> _enumerate;
    private readonly Func<string, IAtChannel> _create;
    private readonly Func<IReadOnlyDictionary<string, MacUsbSerialIdentity>> _readUsbIdentities;
    private readonly Func<IReadOnlyList<MacUsbDeviceIdentity>> _readUsbDevices;
    private readonly Func<ILibUsbApi> _libUsbFactory;

    public MacAtChannelProvider() : this(
        EnumeratePorts,
        port => new SerialAtChannel(port),
        MacUsbSerialIdentityReader.GetPortIdentities,
        MacUsbSerialIdentityReader.GetUsbDeviceIdentities,
        () => new NativeLibUsbApi()) { }

    internal MacAtChannelProvider(
        Func<IReadOnlyList<string>> enumerate,
        Func<string, IAtChannel> create,
        Func<IReadOnlyDictionary<string, MacUsbSerialIdentity>>? readUsbIdentities = null,
        Func<IReadOnlyList<MacUsbDeviceIdentity>>? readUsbDevices = null,
        Func<ILibUsbApi>? libUsbFactory = null)
    {
        _enumerate = enumerate;
        _create = create;
        _readUsbIdentities = readUsbIdentities ?? (() => new Dictionary<string, MacUsbSerialIdentity>(StringComparer.Ordinal));
        _readUsbDevices = readUsbDevices ?? (() => []);
        _libUsbFactory = libUsbFactory ?? (() => new NativeLibUsbApi());
    }

    public StringComparer PortNameComparer => StringComparer.Ordinal;

    // USB-direct is optional: missing libusb, ambiguous targets or failed claims leave serial probing available.
    public async Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<MacUsbDeviceIdentity> registryDevices;
        try { registryDevices = _readUsbDevices(); }
        catch { registryDevices = []; }

        return await Task.Run(() => LibUsbAtChannel.TryOpen(_libUsbFactory(), registryDevices, ct), ct)
            .ConfigureAwait(false);
    }

    public IAtChannel CreateSerial(string portName) => _create(portName);

    public static IReadOnlyList<string> EnumerateKnownUsbDevices() => MacUsbSerialIdentityReader.GetUsbDeviceIdentities()
        .Where(device => ModuleIdentity.KnownIds.Contains((device.VendorId, device.ProductId)))
        .Select(device => $"{GetModuleName(device.VendorId, device.ProductId)} · {device.VendorId:X4}:{device.ProductId:X4}")
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static string GetModuleName(int vendorId, int productId) =>
        (vendorId, productId) == (ModuleIdentity.DjiVendorId, ModuleIdentity.DjiProductId) ? "QDC507" : "Quectel";

    public static IReadOnlyList<string> EnumeratePorts()
    {
        IEnumerable<string> directoryPorts;
        try { directoryPorts = [.. Directory.EnumerateFiles("/dev", "cu.*")]; }
        catch (IOException) { directoryPorts = []; }
        catch (UnauthorizedAccessException) { directoryPorts = []; }

        var registryPorts = MacUsbSerialIdentityReader.GetCalloutPorts();
        return MergeCalloutPorts(directoryPorts, registryPorts);
    }

    internal static IReadOnlyList<string> MergeCalloutPorts(
        IEnumerable<string> directoryPorts,
        IEnumerable<string> registryPorts) =>
        SelectCandidates(directoryPorts.Concat(registryPorts));

    internal static IReadOnlyList<string> SelectCandidates(IEnumerable<string> paths) =>
        paths.Where(p => p.StartsWith("/dev/cu.", StringComparison.Ordinal))
            .Where(p => !p.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Equals("/dev/cu.debug-console", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public async Task<string?> DetectSerialAsync(IReadOnlyList<string> knownPorts, CancellationToken ct = default)
    {
        // Enumerate away from the UI thread. Only reuse hints which still exist.
        var candidates = await Task.Run(_enumerate, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, MacUsbSerialIdentity> usbIdentities;
        try { usbIdentities = _readUsbIdentities(); }
        catch { usbIdentities = new Dictionary<string, MacUsbSerialIdentity>(StringComparer.Ordinal); }

        var (ordered, priorityCount) = OrderProbeCandidates(candidates, knownPorts, usbIdentities);

        // 已知模块端口/上次成功的端口串行优先探测；其余候选并发兜底，
        // 避免「端口多时靠后的候选永远轮不到」。
        return await AtPortProbe.FindAsync(
            _create, ordered, priorityCount, TimeSpan.FromSeconds(15),
            AtPortProbe.DefaultConcurrency, ct).ConfigureAwait(false);
    }

    internal static (IReadOnlyList<string> Ordered, int PriorityCount) OrderProbeCandidates(
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> knownPorts,
        IReadOnlyDictionary<string, MacUsbSerialIdentity> usbIdentities)
    {
        var modulePorts = candidates
            .Select((port, index) => (port, index))
            .Where(x => usbIdentities.TryGetValue(x.port, out var identity) && IsKnownModule(identity))
            .OrderBy(x => GetKnownIdPriority(usbIdentities[x.port]))
            .ThenBy(x => GetInterfacePriority(usbIdentities[x.port].InterfaceNumber))
            .ThenBy(x => x.index)
            .Select(x => x.port)
            .ToList();

        var priority = modulePorts
            .Concat(knownPorts.Where(p => candidates.Contains(p, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rest = candidates.Where(p => !priority.Contains(p, StringComparer.Ordinal));

        return ([.. priority, .. rest], priority.Count);
    }

    private static bool IsKnownModule(MacUsbSerialIdentity identity) =>
        ModuleIdentity.KnownIds.Contains((identity.VendorId, identity.ProductId));

    private static int GetKnownIdPriority(MacUsbSerialIdentity identity) =>
        Array.FindIndex(ModuleIdentity.KnownIds, id => id == (identity.VendorId, identity.ProductId));

    private static int GetInterfacePriority(int? interfaceNumber) => interfaceNumber switch
    {
        ModuleIdentity.AtInterfaceNumber => 0,
        ModuleIdentity.AtInterfaceNumberAlt => 1,
        null => 2,
        _ => 3,
    };
}

internal readonly record struct MacUsbSerialIdentity(int VendorId, int ProductId, int? InterfaceNumber, uint? LocationId = null);
internal readonly record struct MacUsbDeviceIdentity(int VendorId, int ProductId, uint? LocationId);
