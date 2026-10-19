using CellPort.Core.Services;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class CallTrackerTests
{
    [Fact]
    public void ReusedIndexAfterConfirmedEmptyGetsANewCallGeneration()
    {
        var tracker = ConnectedTracker();
        var first = tracker.Apply(Snapshot(Entry(1, direction: 0, status: 0, mode: 0, number: "100")))
            .Calls.Single().Key;

        var empty = tracker.Apply(Snapshot());
        Assert.True(empty.IsConfirmedEmpty);

        var reused = tracker.Apply(Snapshot(Entry(1, direction: 1, status: 4, mode: 0, number: "200")))
            .Calls.Single().Key;

        Assert.Equal(first.ModuleGeneration, reused.ModuleGeneration);
        Assert.NotEqual(first.CallGeneration, reused.CallGeneration);
        Assert.Equal(1, reused.Index);
        Assert.Equal(CallDirection.Incoming, reused.Direction);
    }

    [Fact]
    public void MixedVoiceAndDataKeepsOccupancyButSelectsOnlyUniqueActiveVoice()
    {
        var tracker = ConnectedTracker();
        var snapshot = tracker.Apply(Snapshot(
            Entry(1, direction: 0, status: 0, mode: 0, number: "100"),
            Entry(2, direction: 1, status: 0, mode: 1, number: "<redacted>")));

        Assert.False(snapshot.IsUnknown);
        Assert.Equal(1, snapshot.NonVoiceOccupancyCount);
        Assert.NotNull(snapshot.SelectedVoiceCall);
        Assert.Equal(1, snapshot.SelectedVoiceCall!.Key.Index);
        Assert.Equal(2, snapshot.Calls.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void HeldAndWaitingCallsDoNotAuthorizeMedia(int status)
    {
        var tracker = ConnectedTracker();
        var snapshot = tracker.Apply(Snapshot(Entry(1, direction: 1, status, mode: 0, number: "100")));

        Assert.Null(snapshot.SelectedVoiceCall);
        Assert.Equal(status == 1 ? TrackedCallState.Held : TrackedCallState.Waiting,
            snapshot.Calls.Single().State);
    }

    [Fact]
    public void MultipleVoiceCallsDoNotSelectTheFirstActiveOne()
    {
        var tracker = ConnectedTracker();
        var snapshot = tracker.Apply(Snapshot(
            Entry(1, direction: 0, status: 0, mode: 0, number: "100"),
            Entry(2, direction: 1, status: 5, mode: 0, number: "200")));

        Assert.Null(snapshot.SelectedVoiceCall);
    }

    [Fact]
    public void UnknownQueryDoesNotBecomeIdleAndDisconnectInvalidatesContinuity()
    {
        var tracker = ConnectedTracker();
        var original = tracker.Apply(Snapshot(Entry(1, direction: 0, status: 0, mode: 0, number: "100")))
            .Calls.Single().Key;
        var failed = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.CommandError, []));

        Assert.True(failed.IsUnknown);
        Assert.False(failed.IsConfirmedEmpty);
        Assert.Null(failed.SelectedVoiceCall);

        var afterFailure = tracker.Apply(Snapshot(Entry(1, direction: 0, status: 0, mode: 0, number: "100")))
            .Calls.Single().Key;
        Assert.NotEqual(original.CallGeneration, afterFailure.CallGeneration);

        tracker.MarkModuleDisconnected();
        Assert.True(tracker.Current.IsUnknown);
        var newModuleGeneration = tracker.BeginModuleSession();
        var restored = tracker.Apply(Snapshot(Entry(1, direction: 0, status: 0, mode: 0, number: "100")))
            .Calls.Single().Key;

        Assert.NotEqual(original.ModuleGeneration, restored.ModuleGeneration);
        Assert.Equal(newModuleGeneration, restored.ModuleGeneration);
        Assert.NotEqual(original.CallGeneration, restored.CallGeneration);
    }

    [Fact]
    public void ChangingIdentityAtSameIndexStartsANewCallGeneration()
    {
        var tracker = ConnectedTracker();
        var first = tracker.Apply(Snapshot(Entry(1, direction: 0, status: 2, mode: 0, number: "100")))
            .Calls.Single().Key;
        var next = tracker.Apply(Snapshot(Entry(1, direction: 0, status: 0, mode: 0, number: "200")))
            .Calls.Single().Key;

        Assert.NotEqual(first.CallGeneration, next.CallGeneration);
    }

    private static CallTracker ConnectedTracker()
    {
        var tracker = new CallTracker();
        tracker.BeginModuleSession();
        return tracker;
    }

    private static ClccCallListResult Snapshot(params ClccCallEntry[] entries) =>
        new(ClccQueryOutcome.Complete, [.. entries]);

    private static ClccCallEntry Entry(int index, int direction, int status, int mode, string number) =>
        new(index,
            direction == 0 ? CallDirection.Outgoing : CallDirection.Incoming,
            status,
            mode,
            IsMultiparty: false,
            Number: number,
            NumberType: 129,
            Alpha: null,
            Priority: null);
}
