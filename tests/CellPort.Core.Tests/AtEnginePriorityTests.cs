using System.Collections.Concurrent;
using System.Text;
using CellPort.Core.At;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class AtEnginePriorityTests
{
    [Fact]
    public async Task QueuedCommandsRunByPriorityButNeverInterruptCurrentExchange()
    {
        var channel = new ScheduledReplyChannel(
            command => "\r\nOK\r\n",
            new Dictionary<string, TimeSpan> { ["AT+LONG"] = TimeSpan.FromMilliseconds(250) });
        await using var engine = new AtEngine(channel);
        var running = engine.ExecuteAsync("AT+LONG", TimeSpan.FromSeconds(2));
        await channel.WaitUntilWrittenAsync("AT+LONG");

        var background = engine.ExecuteAsync("AT+BACKGROUND", AtCommandPriority.Background,
            TimeSpan.FromSeconds(2));
        var callState = engine.ExecuteAsync("AT+CLCC", AtCommandPriority.CallState,
            TimeSpan.FromSeconds(2));
        var urgent = engine.ExecuteAsync("AT+URGENT", AtCommandPriority.Urgent,
            TimeSpan.FromSeconds(2));

        var results = await Task.WhenAll(running, background, callState, urgent);

        Assert.Equal(["AT+LONG", "AT+URGENT", "AT+CLCC", "AT+BACKGROUND"], channel.Commands);
        Assert.All(results,
            result => Assert.Equal(AtCompletionStatus.Ok, result.Completion));
    }

    [Fact]
    public async Task QueueTimeoutDoesNotSendCommandOrBlockFollowingWork()
    {
        var channel = new ScheduledReplyChannel(
            command => "\r\nOK\r\n",
            new Dictionary<string, TimeSpan> { ["AT+LONG"] = TimeSpan.FromMilliseconds(250) });
        await using var engine = new AtEngine(channel);
        var running = engine.ExecuteAsync("AT+LONG", TimeSpan.FromSeconds(2));
        await channel.WaitUntilWrittenAsync("AT+LONG");

        var expired = await engine.ExecuteAsync("AT+EXPIRED", AtCommandPriority.CallState,
            TimeSpan.FromMilliseconds(40));
        Assert.Equal(AtCompletionStatus.Timeout, expired.Completion);

        var after = engine.ExecuteAsync("AT+AFTER", AtCommandPriority.Interactive,
            TimeSpan.FromSeconds(2));
        await Task.WhenAll(running, after);
        Assert.DoesNotContain("AT+EXPIRED", channel.Commands);
        Assert.Equal(["AT+LONG", "AT+AFTER"], channel.Commands);
    }

    [Fact]
    public async Task UrgentCommandRequestedFromUrcWaitsUntilCurrentResponseCompletes()
    {
        var channel = new ScheduledReplyChannel(command => command == "AT+LONG"
            ? "\r\n+CIEV: 2,9\r\nOK\r\n"
            : "\r\nOK\r\n",
            new Dictionary<string, TimeSpan> { ["AT+LONG"] = TimeSpan.FromMilliseconds(150) });
        await using var engine = new AtEngine(channel);
        var followUp = new TaskCompletionSource<Task<AtResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.UnsolicitedReport += (_, _) => followUp.TrySetResult(
            engine.ExecuteAsync("AT+FOLLOWUP", AtCommandPriority.Urgent, TimeSpan.FromSeconds(2)));

        var first = await engine.ExecuteAsync("AT+LONG", TimeSpan.FromSeconds(2));
        var followUpTask = await followUp.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await followUpTask;

        Assert.Equal(AtCompletionStatus.Ok, first.Completion);
        Assert.Equal(AtCompletionStatus.Ok, second.Completion);
        Assert.Equal(["AT+LONG", "AT+FOLLOWUP"], channel.Commands);
    }

    private sealed class ScheduledReplyChannel(
        Func<string, string> response,
        IReadOnlyDictionary<string, TimeSpan> delays) : IAtChannel
    {
        private byte[] _pending = [];
        private string _currentCommand = string.Empty;
        private bool _delayConsumed;
        private readonly ConcurrentQueue<string> _commands = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _written = new();

        public string Description => "scheduled test channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen => true;
        public IReadOnlyList<string> Commands => [.. _commands];

        public async Task WaitUntilWrittenAsync(string command)
        {
            if (_commands.Contains(command)) return;
            var signal = _written.GetOrAdd(command,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (_commands.Contains(command)) signal.TrySetResult();
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            _currentCommand = Encoding.ASCII.GetString(data.Span).TrimEnd('\r');
            _delayConsumed = false;
            _commands.Enqueue(_currentCommand);
            _pending = Encoding.ASCII.GetBytes(response(_currentCommand));
            _written.GetOrAdd(_currentCommand,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            if (!_delayConsumed && delays.TryGetValue(_currentCommand, out var delay))
            {
                _delayConsumed = true;
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
