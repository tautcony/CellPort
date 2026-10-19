using System.Collections.Concurrent;
using System.Text;
using CellPort.Core.At;
using CellPort.Core.Services;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class CallServiceSnapshotTests
{
    [Fact]
    public async Task OutgoingCallEndingFromActiveKeepsDirectionAndStartsDurationAtActiveSnapshot()
    {
        var clock = new ManualTimeProvider();
        var tracker = new CallTracker(clock);
        tracker.BeginModuleSession();
        var channel = new ScriptedChannel(
            "\r\n+CLCC: 1,0,2,0,0,\"123\",129\r\nOK\r\n",
            "\r\n+CLCC: 1,0,0,0,0,\"123\",129\r\nOK\r\n",
            "\r\nOK\r\n");
        await using var engine = new AtEngine(channel);
        var calls = new CallService(engine, tracker);

        await calls.RefreshCallSnapshotAsync();
        clock.Advance(TimeSpan.FromSeconds(7));
        await calls.RefreshCallSnapshotAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await calls.RefreshCallSnapshotAsync();

        var record = Assert.Single(calls.History);
        Assert.Equal(CallDirection.Outgoing, record.Direction);
        Assert.Equal(TimeSpan.FromSeconds(5), record.Duration);
    }

    [Fact]
    public async Task CallThatNeverReachesActiveHasNoConnectedDuration()
    {
        var clock = new ManualTimeProvider();
        var tracker = new CallTracker(clock);
        tracker.BeginModuleSession();
        var channel = new ScriptedChannel(
            "\r\n+CLCC: 1,1,4,0,0,\"123\",129\r\nOK\r\n",
            "\r\nOK\r\n");
        await using var engine = new AtEngine(channel);
        var calls = new CallService(engine, tracker);

        await calls.RefreshCallSnapshotAsync();
        clock.Advance(TimeSpan.FromSeconds(12));
        await calls.RefreshCallSnapshotAsync();

        var record = Assert.Single(calls.History);
        Assert.Equal(CallDirection.Incoming, record.Direction);
        Assert.Null(record.Duration);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    private sealed class ScriptedChannel(params string[] responses) : IAtChannel
    {
        private readonly ConcurrentQueue<string> _responses = new(responses);
        private byte[] _pending = [];
        public string Description => "scripted call snapshot channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            _pending = Encoding.ASCII.GetBytes(_responses.TryDequeue(out var next) ? next : "\r\nERROR\r\n");
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
