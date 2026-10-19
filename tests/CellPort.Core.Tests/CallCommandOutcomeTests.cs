using System.Text;
using CellPort.Core.At;
using CellPort.Core.Services;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class CallCommandOutcomeTests
{
    [Fact]
    public async Task AtaOkDoesNotConfirmActiveNetworkState()
    {
        await using var engine = new AtEngine(new ReplyChannel(_ => "\r\nOK\r\n"));
        var calls = new CallService(engine);
        calls.HandleUnsolicited("RING");

        var outcome = await calls.AnswerCommandAsync();

        Assert.True(outcome.Accepted);
        Assert.Equal(AtCompletionStatus.Ok, outcome.AtCompletion);
        Assert.Equal(CallState.Ringing, calls.State);
        var statusText = CallStatePresentation.GetStatusText(calls);
        Assert.Contains("等待", statusText);
        Assert.DoesNotContain("通话中", statusText);
    }

    [Fact]
    public async Task ChupErrorLeavesHangupUnconfirmedAndRetainsCallForRefresh()
    {
        await using var engine = new AtEngine(new ReplyChannel(_ => "\r\nERROR\r\n"));
        var calls = new CallService(engine);
        calls.HandleUnsolicited("RING");
        calls.HandleUnsolicited("+CLIP: \"123\",129");

        var outcome = await calls.HangUpCommandAsync();

        Assert.False(outcome.Accepted);
        Assert.Equal(AtCompletionStatus.Error, outcome.AtCompletion);
        Assert.Equal(CallState.HangupUnconfirmed, calls.State);
        Assert.Equal("123", calls.PeerNumber);
        Assert.Empty(calls.History);
    }

    [Fact]
    public async Task FailedDialIsNotRetriedAutomatically()
    {
        var channel = new ReplyChannel(_ => "\r\nERROR\r\n");
        await using var engine = new AtEngine(channel);
        var calls = new CallService(engine);

        var outcome = await calls.DialCommandAsync("123");

        Assert.False(outcome.Accepted);
        Assert.Equal(CallState.Unknown, calls.State);
        Assert.Equal(new[] { "ATD123;" }, channel.Commands);
    }

    private sealed class ReplyChannel(Func<string, string> reply) : IAtChannel
    {
        private byte[] _pending = [];

        public string Description => "offline test channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen => true;
        public List<string> Commands { get; } = [];

        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var command = Encoding.ASCII.GetString(data.Span).TrimEnd('\r');
            Commands.Add(command);
            _pending = Encoding.ASCII.GetBytes(reply(command));
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            var count = Math.Min(buffer.Length, _pending.Length);
            _pending.AsMemory(0, count).CopyTo(buffer);
            _pending = _pending[count..];
            return Task.FromResult(count);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
