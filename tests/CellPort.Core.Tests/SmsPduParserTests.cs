using System.Text;
using CellPort.Core.At;
using CellPort.Core.Services;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class SmsPduParserTests
{
    [Theory]
    [InlineData("+19545742336")]
    [InlineData("+1555123456")]
    public void ParsesDeliverPduWithCorrectSenderLengthAndText(string sender)
    {
        var message = SmsPduParser.Parse(BuildDeliverPdu(sender, "Test"));

        Assert.NotNull(message);
        Assert.Equal(sender, message.Number);
        Assert.Equal("Test", message.Body);
        Assert.Equal(SmsDirection.Incoming, message.Direction);
    }

    [Fact]
    public void ParsesSubmitPduAndPreservesOutgoingText()
    {
        var message = SmsPduParser.Parse(BuildSubmitPdu("+19545742336", "Test"));

        Assert.NotNull(message);
        Assert.Equal("+19545742336", message.Number);
        Assert.Equal("Test", message.Body);
        Assert.Equal(SmsDirection.Outgoing, message.Direction);
    }

    [Fact]
    public async Task RepeatedStoredSyncDoesNotAppendTheSameSimSlotAgain()
    {
        var pdu = BuildSubmitPdu("+19545742336", "Test");
        await using var channel = new SmsSyncChannel(pdu);
        await channel.OpenAsync();
        await using var engine = new AtEngine(channel);
        var service = new SmsService(engine);

        Assert.Equal(1, await service.SyncStoredAsync());
        Assert.Equal(0, await service.SyncStoredAsync());

        var message = Assert.Single(service.Inbox);
        Assert.Equal("Test", message.Body);
        Assert.Equal(SmsDirection.Outgoing, message.Direction);
    }

    [Fact]
    public async Task StoredMessageNotificationReadsAndPublishesItsSlotImmediately()
    {
        var pdu = BuildDeliverPdu("+19545742336", "Test");
        await using var channel = new SmsIndexChannel(pdu);
        await channel.OpenAsync();
        await using var engine = new AtEngine(channel);
        var service = new SmsService(engine);
        var received = new TaskCompletionSource<SmsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.MessageReceived += (_, message) => received.TrySetResult(message);

        service.FeedLine("+CMTI: \"SM\",7");

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Test", message.Body);
        Assert.Equal("+19545742336", message.Number);
        Assert.Equal(7, message.Index);
        Assert.Contains("AT+CMGR=7", channel.Commands);
        Assert.Single(service.Inbox);
    }

    [Fact]
    public async Task DirectPushAndStoredSyncDoNotAddTheSameMessageTwice()
    {
        var pdu = BuildDeliverPdu("+19545742336", "Test");
        await using var channel = new SmsSyncChannel(pdu, status: 0);
        await channel.OpenAsync();
        await using var engine = new AtEngine(channel);
        var service = new SmsService(engine);

        service.FeedLine("+CMT: ,23");
        var pushed = service.FeedLine(pdu);

        Assert.NotNull(pushed);
        Assert.Equal(0, await service.SyncStoredAsync());

        var message = Assert.Single(service.Inbox);
        Assert.Equal("Test", message.Body);
        Assert.Equal(-1, message.Index);
    }

    [Fact]
    public async Task SameTextReceivedAtDifferentTimesRemainsASeparateMessage()
    {
        var pushedPdu = BuildDeliverPdu("+19545742336", "Test", "42101000000000");
        var storedPdu = BuildDeliverPdu("+19545742336", "Test", "42101001000000");
        await using var channel = new SmsSyncChannel(storedPdu, status: 0);
        await channel.OpenAsync();
        await using var engine = new AtEngine(channel);
        var service = new SmsService(engine);

        service.FeedLine("+CMT: ,23");
        service.FeedLine(pushedPdu);

        Assert.Equal(1, await service.SyncStoredAsync());
        Assert.Equal(2, service.Inbox.Count);
    }

    private static string BuildDeliverPdu(string sender, string body, string timestamp = "42101000000000")
    {
        var digits = sender.TrimStart('+');
        var userData = Convert.ToHexString(SmsCodec.EncodeGsm7Bit(body));
        return "00" + "00" + $"{digits.Length:X2}" + (sender.StartsWith('+') ? "91" : "81") +
               EncodeBcd(digits) + "00" + "00" + timestamp +
               $"{body.Length:X2}" + userData;
    }

    private static string BuildSubmitPdu(string recipient, string body)
    {
        var digits = recipient.TrimStart('+');
        var userData = Convert.ToHexString(SmsCodec.EncodeGsm7Bit(body));
        return "00" + "01" + "00" + $"{digits.Length:X2}" + (recipient.StartsWith('+') ? "91" : "81") +
               EncodeBcd(digits) + "00" + "00" + $"{body.Length:X2}" + userData;
    }

    private static string EncodeBcd(string digits)
    {
        var result = new StringBuilder(digits.Length + 1);
        for (var i = 0; i < digits.Length; i += 2)
        {
            var low = digits[i];
            var high = i + 1 < digits.Length ? digits[i + 1] : 'F';
            result.Append(high).Append(low);
        }
        return result.ToString();
    }

    private sealed class SmsSyncChannel(string pdu, int status = 3) : IAtChannel
    {
        private byte[] _reply = [];
        public string Description => "SMS test channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen { get; private set; }

        public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var command = Encoding.ASCII.GetString(data.Span).TrimEnd('\r');
            var response = command == "AT+CMGL=4"
                ? $"\r\n{command}\r\n+CMGL: 7,{status},,{Convert.FromHexString(pdu).Length - 1}\r\n{pdu}\r\nOK\r\n"
                : $"\r\n{command}\r\nOK\r\n";
            _reply = Encoding.ASCII.GetBytes(response);
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            var size = Math.Min(buffer.Length, _reply.Length);
            _reply.AsMemory(0, size).CopyTo(buffer);
            _reply = _reply[size..];
            return Task.FromResult(size);
        }

        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }

    private sealed class SmsIndexChannel(string pdu) : IAtChannel
    {
        private byte[] _reply = [];
        public string Description => "SMS index test channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen { get; private set; }
        public List<string> Commands { get; } = [];

        public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var command = Encoding.ASCII.GetString(data.Span).TrimEnd('\r');
            Commands.Add(command);
            var length = Convert.FromHexString(pdu).Length - 1;
            _reply = Encoding.ASCII.GetBytes($"\r\n{command}\r\n+CMGR: 0,,{length}\r\n{pdu}\r\nOK\r\n");
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            var size = Math.Min(buffer.Length, _reply.Length);
            _reply.AsMemory(0, size).CopyTo(buffer);
            _reply = _reply[size..];
            return Task.FromResult(size);
        }

        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
