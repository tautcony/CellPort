using System.Text;
using CellPort.Audio.Core.Services;
using CellPort.Core.At;
using CellPort.Core.Services;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public sealed class CallFreshnessQueueIntegrationTests
{
    [Fact]
    public async Task QueuedCallStateTimeoutStillClosesOutgoingGateWithoutDeadlock()
    {
        var tracker = new CallTracker();
        tracker.BeginModuleSession();
        var active = tracker.Apply(new ClccCallListResult(
            ClccQueryOutcome.Complete,
            [new ClccCallEntry(1, CallDirection.Outgoing, 0, 0, false, "<redacted>", 129, null, null)]));
        using var gate = new CallStateFreshnessGate();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.OutgoingMustClose += (_, _) => closed.TrySetResult();
        Assert.True(gate.Observe(active));

        var channel = new DelayedChannel(TimeSpan.FromMilliseconds(3400));
        await using var engine = new AtEngine(channel);
        var longCommand = engine.ExecuteAsync("AT+LONG", timeout: TimeSpan.FromSeconds(5));
        await channel.WaitUntilWrittenAsync();
        var queuedCallState = await engine.ExecuteAsync(
            "AT+CLCC",
            AtCommandPriority.CallState,
            timeout: TimeSpan.FromMilliseconds(80));

        Assert.Equal(AtCompletionStatus.Timeout, queuedCallState.Completion);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.False(gate.IsOutgoingAuthorized);
        Assert.False(longCommand.IsCompleted);

        var result = await longCommand;
        Assert.Equal(AtCompletionStatus.Ok, result.Completion);
    }

    private sealed class DelayedChannel(TimeSpan delay) : IAtChannel
    {
        private readonly TaskCompletionSource _written =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] _pending = [];
        private bool _delayed;

        public string Description => "delayed integration test channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen => true;

        public Task WaitUntilWrittenAsync() => _written.Task.WaitAsync(TimeSpan.FromSeconds(2));
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            _pending = Encoding.ASCII.GetBytes("\r\nOK\r\n");
            _written.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                await Task.Delay(delay, ct);
            }
            var count = Math.Min(buffer.Length, _pending.Length);
            _pending.AsMemory(0, count).CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
