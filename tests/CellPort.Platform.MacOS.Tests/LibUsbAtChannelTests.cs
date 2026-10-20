using CellPort.Core.Transport;
using CellPort.Platform.MacOS;
using Xunit;

namespace CellPort.Platform.MacOS.Tests;

public class LibUsbAtChannelTests
{
    private static readonly LibUsbDevice Dji = new((nint)1, ModuleIdentity.DjiVendorId, ModuleIdentity.DjiProductId, 0x02100000);
    private static readonly IReadOnlyList<MacUsbDeviceIdentity> MatchingRegistry = [new(ModuleIdentity.DjiVendorId, ModuleIdentity.DjiProductId, 0x02100000)];

    [Fact]
    public void BulkPairIgnoresInterruptAndRequiresBothDirections()
    {
        Assert.Equal(((byte)0x84, (byte)0x03), LibUsbAtChannel.FindBulkPair([
            new(0x85, 0x03), new(0x84, 0x02), new(0x03, 0x02)]));
        Assert.Null(LibUsbAtChannel.FindBulkPair([new(0x84, 0x02)]));
    }

    [Fact]
    public async Task NativeLibUsbClaimsQdcAtInterfaceWhenAvailable()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var registry = MacUsbSerialIdentityReader.GetUsbDeviceIdentities();
            if (!registry.Any(device => (device.VendorId, device.ProductId) == (ModuleIdentity.DjiVendorId, ModuleIdentity.DjiProductId))) return;
            var channel = LibUsbAtChannel.TryOpen(new NativeLibUsbApi(), registry, default);
            Assert.NotNull(channel);
            await channel!.DisposeAsync();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // libusb is optional in developer/test environments.
        }
    }

    [Fact]
    public void SelectsKnownDjiAndRejectsUnmappedMultipleKnownDevices()
    {
        var unknown = new LibUsbDevice((nint)2, 0x1234, 0x5678, 0x02100000);
        var otherKnown = new LibUsbDevice((nint)3, ModuleIdentity.QuectelVendorId, ModuleIdentity.QuectelProductId, 0x03100000);
        Assert.Equal(Dji, LibUsbAtChannel.SelectDevice([unknown, otherKnown, Dji], MatchingRegistry));
        Assert.Null(LibUsbAtChannel.SelectDevice([Dji, otherKnown], []));
        Assert.Equal(Dji, LibUsbAtChannel.SelectDevice([Dji], []));
    }

    [Fact]
    public async Task OpensInterfaceTwoWithBulkEndpointsAndOpenIsIdempotent()
    {
        var api = new FakeApi(
            [Dji],
            [new(2, 0, [new(0x85, 3), new(0x84, 2), new(0x03, 2)]), new(3, 0, [new(0x86, 2), new(0x04, 2)])]);
        var channel = LibUsbAtChannel.TryOpen(api, MatchingRegistry, default)!;
        Assert.NotNull(channel);
        await channel.OpenAsync();
        Assert.Equal([2], api.Claimed);
        await channel.DisposeAsync();
        Assert.Equal(1, api.ReleaseCount);
        Assert.Equal(1, api.CloseCount);
        Assert.Equal(1, api.ExitCount);
    }

    [Fact]
    public async Task UsesInterfaceThreeWhenInterfaceTwoHasNoBulkPair()
    {
        var api = new FakeApi([Dji], [new(2, 0, [new(0x84, 2)]), new(3, 0, [new(0x86, 2), new(0x04, 2)])]);
        var channel = LibUsbAtChannel.TryOpen(api, MatchingRegistry, default)!;
        Assert.Equal([3], api.Claimed);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task ReadWriteAndDisposeUseClaimedEndpoints()
    {
        var api = new FakeApi([Dji], [new(2, 0, [new(0x84, 2), new(0x03, 2)])]) { ReadChunks = new(new byte[][] { [0x2B, 0x51, 0x49, 0x4E, 0x44], [0x4F, 0x4B, 0x0D, 0x0A] }) };
        var channel = LibUsbAtChannel.TryOpen(api, MatchingRegistry, default)!;
        var output = new byte[8];
        Assert.Equal(5, await channel.ReadAsync(output, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(new byte[] { 0x2B, 0x51, 0x49, 0x4E, 0x44 }, output[..5]);
        Assert.Equal(4, await channel.ReadAsync(output, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(new byte[] { 0x4F, 0x4B, 0x0D, 0x0A }, output[..4]);
        await channel.WriteAsync(new byte[] { 0x41, 0x54, 0x0D });
        Assert.Equal(new byte[] { 0x41, 0x54, 0x0D }, api.Written);
        await channel.DisposeAsync();
        Assert.Equal(0x84, api.LastReadEndpoint);
        Assert.Equal(0x03, api.LastWriteEndpoint);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.OpenAsync());
    }

    private sealed class FakeApi(IReadOnlyList<LibUsbDevice> devices, IReadOnlyList<LibUsbAltSetting> settings) : ILibUsbApi
    {
        public int TimeoutErrorCode => -7;
        public List<int> Claimed { get; } = [];
        public int ReleaseCount { get; private set; }
        public int CloseCount { get; private set; }
        public int ExitCount { get; private set; }
        public byte LastReadEndpoint { get; private set; }
        public byte LastWriteEndpoint { get; private set; }
        public byte[] Written { get; private set; } = [];
        public Queue<byte[]> ReadChunks { get; init; } = new();
        public nint Init() => (nint)11;
        public ILibUsbDeviceList GetDeviceList(nint context) => new FakeList(devices);
        public int Open(LibUsbDevice device, out nint handle) { handle = (nint)12; return 0; }
        public IReadOnlyList<LibUsbAltSetting> GetActiveConfiguration(nint handle) => settings;
        public int ClaimInterface(nint handle, int interfaceNumber) { Claimed.Add(interfaceNumber); return 0; }
        public int SetAlternateSetting(nint handle, int interfaceNumber, int alternateSetting) => 0;
        public int ReleaseInterface(nint handle, int interfaceNumber) { ReleaseCount++; return 0; }
        public int BulkTransfer(nint handle, byte endpoint, byte[] buffer, int length, out int transferred, uint timeoutMs)
        {
            if ((endpoint & 0x80) != 0)
            {
                LastReadEndpoint = endpoint;
                var chunk = ReadChunks.Count > 0 ? ReadChunks.Dequeue() : [];
                transferred = Math.Min(length, chunk.Length);
                Array.Copy(chunk, buffer, transferred);
            }
            else { LastWriteEndpoint = endpoint; transferred = length; Written = buffer[..length]; }
            return 0;
        }
        public void Close(nint handle) => CloseCount++;
        public void Exit(nint context) => ExitCount++;
        public string ErrorName(int errorCode) => $"err {errorCode}";
    }
    private sealed class FakeList(IReadOnlyList<LibUsbDevice> devices) : ILibUsbDeviceList
    { public IReadOnlyList<LibUsbDevice> Devices { get; } = devices; public void Dispose() { } }
}
