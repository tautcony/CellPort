using CellPort.Audio.Core.Services;
using CellPort.Core.Services;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public sealed class CallStateFreshnessGateTests
{
    [Fact]
    public async Task ActiveSnapshotExpiresAndClosesOutgoingAudioIndependently()
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
        Assert.True(gate.IsOutgoingAuthorized);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.False(gate.IsOutgoingAuthorized);
    }

    [Fact]
    public void UnknownOrNonActiveSnapshotRevokesAuthorizationImmediately()
    {
        var tracker = new CallTracker();
        tracker.BeginModuleSession();
        using var gate = new CallStateFreshnessGate();
        var closeCount = 0;
        gate.OutgoingMustClose += (_, _) => closeCount++;
        var active = tracker.Apply(new ClccCallListResult(
            ClccQueryOutcome.Complete,
            [new ClccCallEntry(1, CallDirection.Outgoing, 0, 0, false, "<redacted>", 129, null, null)]));
        Assert.True(gate.Observe(active));

        var held = tracker.Apply(new ClccCallListResult(
            ClccQueryOutcome.Complete,
            [new ClccCallEntry(1, CallDirection.Outgoing, 1, 0, false, "<redacted>", 129, null, null)]));

        Assert.False(gate.Observe(held));
        Assert.False(gate.IsOutgoingAuthorized);
        Assert.Equal(1, closeCount);
    }
}
