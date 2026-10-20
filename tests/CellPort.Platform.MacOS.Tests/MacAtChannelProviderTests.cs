using System.Text;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Platform.MacOS.Tests;

public class MacAtChannelProviderTests
{
    [Fact]
    public void DiscoveryUsesCalloutPortsAndExcludesSystemDevices()
    {
        var ports = MacAtChannelProvider.SelectCandidates([
            "/dev/tty.usbserial-1", "/dev/cu.Bluetooth-Incoming-Port", "/dev/cu.debug-console",
            "/dev/cu.usbmodem2", "/dev/cu.usbserial-1", "/dev/cu.usbserial-1",
        ]);
        Assert.Equal(["/dev/cu.usbmodem2", "/dev/cu.usbserial-1"], ports);
    }

    [Fact]
    public void DiscoveryAcceptsCommonUsbSerialDriverPortNames()
    {
        var ports = MacAtChannelProvider.SelectCandidates([
            "/dev/cu.usbserial-FT123", "/dev/cu.usbmodem2101", "/dev/cu.SLAB_USBtoUART",
            "/dev/cu.wchusbserial1410", "/dev/cu.usbserial-AQ01",
        ]);

        Assert.Equal([
            "/dev/cu.SLAB_USBtoUART", "/dev/cu.usbmodem2101", "/dev/cu.usbserial-AQ01",
            "/dev/cu.usbserial-FT123", "/dev/cu.wchusbserial1410",
        ], ports);
    }

    [Fact]
    public void DirectoryAndIORegistryCalloutCandidatesMergeAndDeduplicate()
    {
        var ports = MacAtChannelProvider.MergeCalloutPorts(
            ["/dev/cu.directory", "/dev/cu.shared", "/dev/tty.not-callout", "/dev/cu.Bluetooth-Incoming-Port"],
            ["/dev/cu.registry-only", "/dev/cu.shared", "/dev/tty.registry-not-callout", "/dev/cu.debug-console"]);

        Assert.Equal(["/dev/cu.directory", "/dev/cu.registry-only", "/dev/cu.shared"], ports);
    }

    [Fact]
    public void SystemEnumerationReturnsOnlyUniqueCalloutPaths()
    {
        var ports = MacAtChannelProvider.EnumeratePorts();

        Assert.Equal(ports.Count, ports.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ports, port => Assert.StartsWith("/dev/cu.", port, StringComparison.Ordinal));
        Assert.DoesNotContain("/dev/cu.debug-console", ports);
        Assert.DoesNotContain(ports, port => port.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoDeviceReturnsWithoutCreatingAChannel()
    {
        var provider = new MacAtChannelProvider(
            () => [],
            _ => throw new Exception("No port should be opened"),
            readUsbDevices: () => [],
            libUsbFactory: () => new EmptyLibUsbApi());
        Assert.Null(await provider.TryOpenUsbAsync());
        Assert.Null(await provider.DetectSerialAsync(["COM6", "/dev/cu.missing"]));
    }

    [Fact]
    public async Task CachedPortIsProbedFirstAndReleasedBeforeReturn()
    {
        var opened = new List<string>();
        var channel = new FakeChannel(["AT\r\nO", "K\r\n"]);
        var provider = new MacAtChannelProvider(() => ["/dev/cu.a", "/dev/cu.b"], port =>
        {
            opened.Add(port);
            return channel;
        });
        Assert.Equal("/dev/cu.b", await provider.DetectSerialAsync(["/dev/cu.missing", "/dev/cu.b"]));
        Assert.Equal(["/dev/cu.b"], opened);
        Assert.True(channel.Disposed);
        Assert.Equal("AT\r", channel.LastWrite);
    }

    [Fact]
    public async Task KnownUsbModuleIdentityAndAtInterfaceArePrioritized()
    {
        var opened = new List<string>();
        var provider = new MacAtChannelProvider(
            () => ["/dev/cu.generic", "/dev/cu.mi04", "/dev/cu.mi03", "/dev/cu.mi02", "/dev/cu.dji"],
            port => { opened.Add(port); return new FakeChannel(["\r\nOK\r\n"]); },
            () => new Dictionary<string, MacUsbSerialIdentity>(StringComparer.Ordinal)
            {
                ["/dev/cu.generic"] = new(0x1234, 0x5678, null),
                ["/dev/cu.mi04"] = new(ModuleIdentity.QuectelVendorId, ModuleIdentity.QuectelProductId, 4),
                ["/dev/cu.mi03"] = new(ModuleIdentity.QuectelVendorId, ModuleIdentity.QuectelProductId, ModuleIdentity.AtInterfaceNumberAlt),
                ["/dev/cu.mi02"] = new(ModuleIdentity.QuectelVendorId, ModuleIdentity.QuectelProductId, ModuleIdentity.AtInterfaceNumber),
                ["/dev/cu.dji"] = new(ModuleIdentity.DjiVendorId, ModuleIdentity.DjiProductId, ModuleIdentity.AtInterfaceNumber),
            });

        Assert.Equal("/dev/cu.dji", await provider.DetectSerialAsync([]));
        Assert.Equal(["/dev/cu.dji"], opened);
    }

    [Fact]
    public async Task FailedKnownModuleCandidateFallsBackToGenericSerialCandidate()
    {
        var opened = new List<string>();
        var provider = new MacAtChannelProvider(
            () => ["/dev/cu.generic", "/dev/cu.module"],
            port =>
            {
                opened.Add(port);
                return port.EndsWith("module", StringComparison.Ordinal)
                    ? new FakeChannel([]) { OpenError = new UnauthorizedAccessException("busy") }
                    : new FakeChannel(["\r\nOK\r\n"]);
            },
            () => new Dictionary<string, MacUsbSerialIdentity>(StringComparer.Ordinal)
            {
                ["/dev/cu.module"] = new(ModuleIdentity.DjiVendorId, ModuleIdentity.DjiProductId, ModuleIdentity.AtInterfaceNumber),
            });

        Assert.Equal("/dev/cu.generic", await provider.DetectSerialAsync([]));
        Assert.Equal(["/dev/cu.module", "/dev/cu.generic"], opened);
    }

    [Fact]
    public async Task MissingUsbIdentityKeepsGenericSerialCandidatesAvailable()
    {
        var opened = new List<string>();
        var provider = new MacAtChannelProvider(
            () => ["/dev/cu.generic"],
            port => { opened.Add(port); return new FakeChannel(["\r\nOK\r\n"]); },
            () => new Dictionary<string, MacUsbSerialIdentity>(StringComparer.Ordinal));

        Assert.Equal("/dev/cu.generic", await provider.DetectSerialAsync([]));
        Assert.Equal(["/dev/cu.generic"], opened);
    }

    [Fact]
    public async Task BusyPortDoesNotPreventConnectingToNextPort()
    {
        var busy = new FakeChannel([]) { OpenError = new UnauthorizedAccessException("busy") };
        var available = new FakeChannel(["\r\nOK\r\n"]);
        var provider = new MacAtChannelProvider(() => ["/dev/cu.a", "/dev/cu.b"],
            port => port.EndsWith('a') ? busy : available);
        Assert.Equal("/dev/cu.b", await provider.DetectSerialAsync([]));
        Assert.True(busy.Disposed);
        Assert.True(available.Disposed);
    }

    [Fact]
    public async Task CancellationReleasesProbeAndPropagates()
    {
        using var cts = new CancellationTokenSource();
        var channel = new FakeChannel([]) { OnRead = cts.Cancel };
        var provider = new MacAtChannelProvider(() => ["/dev/cu.a"], _ => channel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DetectSerialAsync([], cts.Token));
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task EmbeddedOkIsNotAcceptedAsAnAtResponse()
    {
        using var cts = new CancellationTokenSource();
        var reads = 0;
        var channel = new FakeChannel(["NOT_OK\r\n"])
        {
            OnRead = () => { if (++reads > 1) cts.Cancel(); },
        };
        var provider = new MacAtChannelProvider(() => ["/dev/cu.a"], _ => channel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DetectSerialAsync([], cts.Token));
        Assert.True(channel.Disposed);
    }

    private sealed class FakeChannel(IEnumerable<string> responses) : IAtChannel
    {
        private readonly Queue<string> _responses = new(responses);
        public Exception? OpenError { get; init; }
        public Action? OnRead { get; init; }
        public bool Disposed { get; private set; }
        public string? LastWrite { get; private set; }
        public string Description => "test";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen { get; private set; }
        public Task OpenAsync(CancellationToken ct = default)
        {
            if (OpenError is not null) throw OpenError;
            IsOpen = true;
            return Task.CompletedTask;
        }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            LastWrite = Encoding.ASCII.GetString(data.Span);
            return Task.CompletedTask;
        }
        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            OnRead?.Invoke();
            ct.ThrowIfCancellationRequested();
            var bytes = Encoding.ASCII.GetBytes(_responses.Count > 0 ? _responses.Dequeue() : "");
            bytes.CopyTo(buffer);
            return Task.FromResult(bytes.Length);
        }
        public ValueTask DisposeAsync() { Disposed = true; IsOpen = false; return ValueTask.CompletedTask; }
    }

    private sealed class EmptyLibUsbApi : ILibUsbApi
    {
        public int TimeoutErrorCode => -7;
        public nint Init() => (nint)1;
        public ILibUsbDeviceList GetDeviceList(nint context) => new EmptyDeviceList();
        public int Open(LibUsbDevice device, out nint handle) { handle = 0; return -1; }
        public IReadOnlyList<LibUsbAltSetting> GetActiveConfiguration(nint handle) => [];
        public int ClaimInterface(nint handle, int interfaceNumber) => -1;
        public int SetAlternateSetting(nint handle, int interfaceNumber, int alternateSetting) => -1;
        public int ReleaseInterface(nint handle, int interfaceNumber) => 0;
        public int BulkTransfer(nint handle, byte endpoint, byte[] buffer, int length, out int transferred, uint timeoutMs)
        { transferred = 0; return -1; }
        public void Close(nint handle) { }
        public void Exit(nint context) { }
        public string ErrorName(int errorCode) => "error";
    }

    private sealed class EmptyDeviceList : ILibUsbDeviceList
    {
        public IReadOnlyList<LibUsbDevice> Devices { get; } = [];
        public void Dispose() { }
    }
}
