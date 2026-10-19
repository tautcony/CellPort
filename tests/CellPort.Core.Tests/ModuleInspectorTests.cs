using System.Collections.Concurrent;
using System.Text;
using CellPort.Core.Services;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class ModuleInspectorTests
{
    [Fact]
    public async Task BusyDataInspectionSendsOnlyReadQueriesAndNeverHangsUp()
    {
        var channel = new RecordingChannel(command => command switch
        {
            "AT" => "\r\nOK\r\n",
            "AT+CGMM" => "\r\nQDC507\r\nOK\r\n",
            "AT+CGMR" => "\r\nQDC507GLEFM21\r\nOK\r\n",
            "AT+CLCC" => "\r\n+CLCC: 1,1,0,1,0,\"<redacted>\",128\r\n" +
                          "+CLCC: 2,1,0,1,0,\"<redacted>\",128\r\nOK\r\n",
            _ => "\r\nERROR\r\n",
        });

        var report = await ModuleInspector.InspectAsync(channel);

        Assert.Equal(ModuleInspectStatus.DeviceBusyData, report.Status);
        Assert.Equal("DEVICE_BUSY_DATA", report.Code);
        Assert.Equal(2, report.NonVoiceOccupancyCount);
        Assert.Equal(new[] { "AT", "AT+CGMM", "AT+CGMR", "AT+CLCC" }, channel.Commands);
        Assert.DoesNotContain(channel.Commands, command =>
            command.Contains("CHUP", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("ATD", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("USB", StringComparison.OrdinalIgnoreCase));
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task ErrorOrMalformedClccIsUnknownAndNeverTreatedAsIdle()
    {
        var channel = new RecordingChannel(command => command switch
        {
            "AT" or "AT+CGMM" or "AT+CGMR" => "\r\nOK\r\n",
            "AT+CLCC" => "\r\n+CLCC: 1,1,x,1,0\r\nOK\r\n",
            _ => "\r\nERROR\r\n",
        });

        var report = await ModuleInspector.InspectAsync(channel);

        Assert.Equal(ModuleInspectStatus.CallStateUnknown, report.Status);
        Assert.Equal("CALL_STATE_UNKNOWN", report.Code);
        Assert.Equal(ClccQueryOutcome.MalformedResponse, report.ClccOutcome);
    }

    private sealed class RecordingChannel(Func<string, string> response) : IAtChannel
    {
        private byte[] _pending = [];
        public string Description => "read-only inspector test channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen { get; private set; }
        public bool Disposed { get; private set; }
        public List<string> Commands { get; } = [];

        public Task OpenAsync(CancellationToken ct = default)
        {
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var command = Encoding.ASCII.GetString(data.Span).TrimEnd('\r');
            Commands.Add(command);
            _pending = Encoding.ASCII.GetBytes(response(command));
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            var count = Math.Min(buffer.Length, _pending.Length);
            _pending.AsMemory(0, count).CopyTo(buffer);
            _pending = _pending[count..];
            return Task.FromResult(count);
        }

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            await CloseAsync();
        }
    }
}
