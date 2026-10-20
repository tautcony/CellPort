using System.Text;
using System.Collections.Concurrent;
using CellPort.Core.Transport;

namespace CellPort.App.Tests;

internal sealed class CallsDeviceTestProvider(CallsDeviceTestChannel channel) : IAtChannelProvider
{
    public StringComparer PortNameComparer => StringComparer.OrdinalIgnoreCase;
    public Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default) => Task.FromResult<IAtChannel?>(null);
    public Task<string?> DetectSerialAsync(IReadOnlyList<string> knownPorts, CancellationToken ct = default) => Task.FromResult<string?>("fake-port");
    public IAtChannel CreateSerial(string portName) => channel;
}

internal sealed class CallsDeviceTestChannel : IAtChannel
{
    private readonly object _gate = new();
    private byte[] _reply = [];
    public string Description => "synthetic UI-test channel";
    public AtChannelKind Kind => AtChannelKind.SerialPort;
    public bool IsOpen { get; private set; }
    public bool ActiveCall { get; set; }
    public bool IncomingCall { get; set; }
    public ConcurrentQueue<string> Commands { get; } = new();

    public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
    public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var command = Encoding.ASCII.GetString(data.Span).TrimEnd('\r', '\n');
        lock (_gate)
        {
            Commands.Enqueue(command);
            var payload = command switch
            {
                var c when c.StartsWith("AT+CMGS=", StringComparison.Ordinal) => "> \r\n",
                var c when c.EndsWith('\u001A') => "+CMGS: 1\r\n",
                "AT+CLCC" when ActiveCall => "+CLCC: 1,0,0,0,0,\"+15551234\",145\r\n",
                "AT+CLCC" when IncomingCall => "+CLCC: 2,1,4,0,0,\"+15557654321\",145\r\n",
                "AT+CLCC" => string.Empty,
                "AT+CPBR=?" => "+CPBR: (1-50),20,14\r\n",
                "AT+CPBS=\"SM\"" => "+CPBS: \"SM\",1,50\r\n",
                "AT+CSCS?" => "+CSCS: \"GSM\"\r\n",
                "AT+CPBR=1,50" => "+CPBR: 1,\"5551234\",129,\"004A006F0068006E\"\r\n",
                _ => string.Empty,
            };
            _reply = Encoding.ASCII.GetBytes($"\r\n{payload}OK\r\n");
        }
        return Task.CompletedTask;
    }

    public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var count = Math.Min(buffer.Length, _reply.Length);
            _reply.AsMemory(0, count).CopyTo(buffer);
            _reply = _reply[count..];
            return Task.FromResult(count);
        }
    }

    public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
}
