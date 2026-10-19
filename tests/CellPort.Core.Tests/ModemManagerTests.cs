using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using CellPort.Core.Services;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

public class ModemManagerTests
{
    [Fact]
    public async Task UsbWinsOverExplicitSerialAndPreservesInitializationAndEvents()
    {
        var channel = new FakeChannel(AtChannelKind.UsbDirect);
        var provider = new FakeProvider { Usb = channel };
        await using var modem = new ModemManager(provider);
        var events = new List<bool>();
        var statusUpdates = 0;
        modem.ConnectionChanged += (_, connected) => events.Add(connected);
        modem.StatusUpdated += (_, _) => statusUpdates++;

        Assert.True(await modem.ConnectAsync("unused"));
        Assert.Equal(new[] { "usb" }, provider.Operations);
        Assert.True(modem.IsConnected);
        Assert.Same(channel, modem.Engine!.Channel);
        Assert.NotNull(modem.Sms);
        Assert.NotNull(modem.Calls);
        Assert.NotNull(modem.Info);
        Assert.NotNull(modem.Cusd);
        Assert.NotNull(modem.Phonebook);
        Assert.Equal(new[] { "AT", "AT+CMGF=0", "AT+CNMI=2,1,0,0,0",
            "AT+CPMS=\"SM\",\"SM\",\"SM\"", "AT+CLIP=1", "AT+CRC=1", "AT+CMOD=0" },
            channel.Commands.Take(7));
        Assert.Equal("AT+CMGL=4", channel.Commands.Last());
        Assert.Equal(1, statusUpdates);
        await modem.DisconnectAsync();
        Assert.Equal(new[] { false, true, false }, events);
        Assert.False(modem.IsConnected);
        Assert.Null(modem.Engine);
        Assert.Null(modem.Sms);
        Assert.Null(modem.Calls);
        Assert.True(channel.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPortBypassesDiscoveryAfterUsbUnavailableOrException(bool usbThrows)
    {
        var provider = new FakeProvider { UsbThrows = usbThrows };
        await using var modem = new ModemManager(provider);
        string? remembered = null;
        modem.RememberPort = port => remembered = port;
        Assert.True(await modem.ConnectAsync("/dev/serial/by-id/Example"));
        Assert.Equal(new[] { "usb", "serial:/dev/serial/by-id/Example" }, provider.Operations);
        Assert.Equal("/dev/serial/by-id/Example", remembered);
    }

    [Fact]
    public async Task AutoDiscoveryReceivesHistoryAndSuccessfulPortMovesToFrontWithLimit()
    {
        var provider = new FakeProvider { Detected = "COM8" };
        await using var modem = new ModemManager(provider);
        modem.PrimePortHints(["COM1", "COM2", "COM3", "COM4", "COM5"]);
        modem.RememberPort = _ => throw new IOException("settings unavailable");
        Assert.True(await modem.ConnectAsync());
        Assert.Equal(new[] { "usb", "detect", "serial:COM8" }, provider.Operations);
        Assert.Equal(new[] { "COM1", "COM2", "COM3", "COM4", "COM5" }, provider.LastHints);
        provider.Detected = null;
        Assert.False(await modem.ConnectAsync());
        Assert.Equal(new[] { "COM8", "COM1", "COM2", "COM3" }, provider.LastHints);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task PortIdentityUsesHostComparisonWithoutRewritingPaths(bool ignoreCase, int count)
    {
        var provider = new FakeProvider
        {
            PortNameComparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        };
        await using var modem = new ModemManager(provider);
        modem.PrimePortHints([null, "", "  ", "/dev/ttyA", "/dev/ttya"]);
        Assert.False(await modem.ConnectAsync());
        Assert.Equal(count, provider.LastHints.Count);
        Assert.Equal("/dev/ttya", provider.LastHints.Last());
        Assert.Equal(new[] { "usb", "detect" }, provider.Operations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOpenOrAtProbeDisposesChannelWithoutConnecting(bool openFails)
    {
        var channel = new FakeChannel(AtChannelKind.SerialPort) { OpenFails = openFails, ReplyOk = false };
        var provider = new FakeProvider { Serial = channel };
        await using var modem = new ModemManager(provider);
        var connected = false;
        modem.ConnectionChanged += (_, value) => connected |= value;
        Assert.False(await modem.ConnectAsync("COM8"));
        Assert.True(channel.Disposed);
        Assert.False(connected);
        Assert.Null(modem.Engine);
    }

    [Fact]
    public async Task PreCancelledConnectionDoesNotInvokePlatform()
    {
        var provider = new FakeProvider();
        await using var modem = new ModemManager(provider);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => modem.ConnectAsync(ct: cts.Token));
        Assert.Empty(provider.Operations);
    }

    [Fact]
    public void CoreHasNoPlatformImplementationDependency()
    {
        var core = typeof(ModemManager).Assembly;
        Assert.Equal(".NETCoreApp,Version=v9.0", core.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        Assert.DoesNotContain(core.GetReferencedAssemblies(), a =>
            a.Name is "System.Management" or "System.IO.Ports" or "PresentationFramework" ||
            a.Name!.StartsWith("CellPort.Platform.") || a.Name.StartsWith("CellPort.Transport."));
        Assert.DoesNotContain(core.GetTypes().SelectMany(t => t.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)),
            method => (method.Attributes & MethodAttributes.PinvokeImpl) != 0);
    }

    private sealed class FakeProvider : IAtChannelProvider
    {
        public StringComparer PortNameComparer { get; init; } = StringComparer.OrdinalIgnoreCase;
        public FakeChannel? Usb { get; init; }
        public FakeChannel? Serial { get; init; }
        public bool UsbThrows { get; init; }
        public string? Detected { get; set; }
        public List<string> Operations { get; } = [];
        public IReadOnlyList<string> LastHints { get; private set; } = [];

        public Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default)
        {
            Operations.Add("usb");
            if (UsbThrows) throw new IOException("USB unavailable");
            return Task.FromResult<IAtChannel?>(Usb);
        }

        public Task<string?> DetectSerialAsync(IReadOnlyList<string> knownPorts, CancellationToken ct = default)
        {
            Operations.Add("detect");
            LastHints = knownPorts.ToArray();
            return Task.FromResult(Detected);
        }

        public IAtChannel CreateSerial(string portName)
        {
            Operations.Add("serial:" + portName);
            return Serial ?? new FakeChannel(AtChannelKind.SerialPort);
        }
    }

    private sealed class FakeChannel(AtChannelKind kind) : IAtChannel
    {
        public string Description => "offline fake";
        public AtChannelKind Kind => kind;
        public bool IsOpen { get; private set; }
        public bool Disposed { get; private set; }
        public bool OpenFails { get; init; }
        public bool ReplyOk { get; init; } = true;
        public List<string> Commands { get; } = [];
        private byte[] _reply = [];

        public Task OpenAsync(CancellationToken ct = default)
        {
            if (OpenFails) throw new IOException("open failed");
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Commands.Add(Encoding.ASCII.GetString(data.Span).TrimEnd('\r'));
            _reply = Encoding.ASCII.GetBytes(ReplyOk ? "\r\nOK\r\n" : "\r\nERROR\r\n");
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            var size = Math.Min(buffer.Length, _reply.Length);
            _reply.AsMemory(0, size).CopyTo(buffer);
            _reply = _reply[size..];
            return Task.FromResult(size);
        }

        public async ValueTask DisposeAsync() { Disposed = true; await CloseAsync(); }
    }
}
