using CellPort.Audio.Core.Models;
using CellPort.Audio.Core.Services;
using CellPort.Core.Services;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public sealed class AudioMediaActivationGateTests
{
    [Fact]
    public void BeginRequiresPermissionEndpointsRouteAndFreshConfirmedActiveVoiceCall()
    {
        var fixture = new Fixture();

        AssertRejected(fixture, permission: false, endpoints: true, route: true,
            AudioErrorCode.MicrophonePermissionDenied);
        AssertRejected(fixture, permission: true, endpoints: false, route: true,
            AudioErrorCode.EndpointMissing);
        AssertRejected(fixture, permission: true, endpoints: true, route: false,
            AudioErrorCode.RouteNotReady);

        var unknown = fixture.Tracker.Apply(new ClccCallListResult(ClccQueryOutcome.TimedOut, []));
        AssertRejected(fixture, unknown, AudioErrorCode.CallStateUnknown);

        fixture = new Fixture();
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        AssertRejected(fixture, fixture.ActiveCall, AudioErrorCode.CallStateUnknown);

        fixture = new Fixture();
        Assert.True(fixture.Gate.TryBegin(fixture.Lease, fixture.ActiveCall, true, true, true,
            out var generation, out var error));
        Assert.NotNull(generation);
        Assert.Null(error);
        Assert.True(fixture.Gate.HostMuted);
        Assert.Equal(AudioMediaState.StartingMuted, fixture.Lifecycle.MediaState);
        Assert.False(fixture.Lifecycle.CanAcceptMediaCallback(generation!));
    }

    [Fact]
    public void UplinkOpensOnlyAfterBothDirectionsAdvanceAndFailedAtMuteUsesHostFallback()
    {
        var fixture = new Fixture();
        var generation = fixture.Begin();

        Assert.False(fixture.Gate.ConfirmFrames(generation, fixture.ActiveCall, 0, 480));
        Assert.Equal(AudioMediaState.Stopped, fixture.Lifecycle.MediaState);
        Assert.True(fixture.Gate.HostMuted);

        generation = fixture.Begin();
        Assert.True(fixture.Gate.ConfirmFrames(generation, fixture.ActiveCall, 480, 480));
        Assert.False(fixture.Gate.HostMuted);
        Assert.Equal(AudioMediaState.Running, fixture.Lifecycle.MediaState);
        Assert.True(fixture.Lifecycle.CanAcceptMediaCallback(generation));

        Assert.True(fixture.Gate.ApplyUserMute(muted: true, atMuteCommandSucceeded: false));
        Assert.True(fixture.Gate.HostMuted);
        Assert.True(fixture.Gate.AtMuteFallbackActive);
        Assert.True(fixture.Gate.ApplyUserMute(muted: true, atMuteCommandSucceeded: true));
        Assert.True(fixture.Gate.HostMuted);
        Assert.False(fixture.Gate.AtMuteFallbackActive);
        Assert.True(fixture.Gate.ApplyUserMute(muted: false, atMuteCommandSucceeded: true));
        Assert.False(fixture.Gate.HostMuted);
    }

    [Fact]
    public void HangupDuringStartupStopsRouteAndPermissionRevocationInvalidatesCallbacks()
    {
        var fixture = new Fixture();
        var starting = fixture.Begin();
        var hangup = fixture.Tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete, []));

        Assert.False(fixture.Gate.ConfirmFrames(starting, hangup, 480, 480));
        Assert.True(fixture.Gate.HostMuted);
        Assert.Equal(AudioMediaState.Stopped, fixture.Lifecycle.MediaState);
        Assert.False(fixture.Lifecycle.CanAcceptMediaCallback(starting));

        var restarted = fixture.Begin(fixture.ActiveCall);
        Assert.True(fixture.Gate.ConfirmFrames(restarted, fixture.ActiveCall, 480, 480));
        fixture.Gate.RevokeMicrophonePermission();

        Assert.True(fixture.Gate.HostMuted);
        Assert.False(fixture.Gate.AtMuteFallbackActive);
        Assert.Equal(AudioMediaState.Stopped, fixture.Lifecycle.MediaState);
        Assert.False(fixture.Lifecycle.CanAcceptMediaCallback(restarted));
    }

    [Fact]
    public void StaleCallbackCannotStopAQuickRedialOrUnpluggedEndpointCanStopImmediately()
    {
        var fixture = new Fixture();
        var oldGeneration = fixture.Begin();
        Assert.True(fixture.Gate.ConfirmFrames(oldGeneration, fixture.ActiveCall, 480, 480));
        fixture.Gate.Stop();

        fixture.Tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete, []));
        var redial = fixture.Tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete,
            [Fixture.ActiveEntry(index: 2)]));
        var newGeneration = fixture.Begin(redial);
        Assert.True(fixture.Gate.ConfirmFrames(newGeneration, redial, 480, 480));

        Assert.False(fixture.Gate.ConfirmFrames(oldGeneration, redial, 480, 480));
        Assert.False(fixture.Gate.HostMuted);
        Assert.True(fixture.Lifecycle.CanAcceptMediaCallback(newGeneration));

        fixture.Gate.NotifyEndpointUnavailable();
        Assert.True(fixture.Gate.HostMuted);
        Assert.Equal(AudioMediaState.Stopped, fixture.Lifecycle.MediaState);
        Assert.False(fixture.Lifecycle.CanAcceptMediaCallback(newGeneration));
    }

    [Fact]
    public void RebuildIsMutedGenerationScopedAndLimitedToTwoAttemptsPerCall()
    {
        var fixture = new Fixture();
        var first = fixture.Begin();
        Assert.True(fixture.Gate.ConfirmFrames(first, fixture.ActiveCall, 480, 480));

        Assert.True(fixture.Gate.TryRebuild(fixture.ActiveCall, true, true, true, out var second, out var error));
        Assert.Null(error);
        Assert.NotNull(second);
        Assert.NotEqual(first.MediaGeneration, second!.MediaGeneration);
        Assert.Equal(1, fixture.Gate.RebuildAttempts);
        Assert.True(fixture.Gate.HostMuted);
        Assert.False(fixture.Lifecycle.CanAcceptMediaCallback(first));
        Assert.True(fixture.Gate.ConfirmFrames(second, fixture.ActiveCall, 480, 480));

        Assert.True(fixture.Gate.TryRebuild(fixture.ActiveCall, true, true, true, out var third, out error));
        Assert.Null(error);
        Assert.NotNull(third);
        Assert.Equal(2, fixture.Gate.RebuildAttempts);
        Assert.True(fixture.Gate.ConfirmFrames(third, fixture.ActiveCall, 480, 480));

        Assert.False(fixture.Gate.TryRebuild(fixture.ActiveCall, true, true, true, out var rejected, out error));
        Assert.Null(rejected);
        Assert.Equal(AudioErrorCode.RouteNotReady, error);
        Assert.Equal(2, fixture.Gate.RebuildAttempts);
        Assert.True(fixture.Gate.HostMuted);
        Assert.Equal(AudioMediaState.Stopped, fixture.Lifecycle.MediaState);
    }

    private static void AssertRejected(Fixture fixture, bool permission, bool endpoints, bool route,
        AudioErrorCode expected) =>
        AssertRejected(fixture, fixture.ActiveCall, expected, permission, endpoints, route);

    private static void AssertRejected(Fixture fixture, CallSnapshot snapshot, AudioErrorCode expected,
        bool permission = true, bool endpoints = true, bool route = true)
    {
        Assert.False(fixture.Gate.TryBegin(fixture.Lease, snapshot, permission, endpoints, route,
            out var generation, out var error));
        Assert.Null(generation);
        Assert.Equal(expected, error);
        Assert.True(fixture.Gate.HostMuted);
        Assert.Equal(AudioMediaState.Stopped, fixture.Lifecycle.MediaState);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Tracker = new CallTracker(Time);
            var moduleGeneration = Tracker.BeginModuleSession();
            Lease = new AudioSessionLease(Guid.NewGuid(), new ModuleDeviceId("fixture-module"), moduleGeneration);
            Lifecycle = new AudioSessionLifecycle();
            Assert.True(Lifecycle.CommitStandby(Lease));
            ActiveCall = Tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete,
                [ActiveEntry(index: 1)]));
            Gate = new AudioMediaActivationGate(Lifecycle, Time);
        }

        public ManualTimeProvider Time { get; } = new();
        public CallTracker Tracker { get; }
        public AudioSessionLease Lease { get; }
        public AudioSessionLifecycle Lifecycle { get; }
        public AudioMediaActivationGate Gate { get; }
        public CallSnapshot ActiveCall { get; }

        public AudioMediaGeneration Begin(CallSnapshot? snapshot = null)
        {
            Assert.True(Gate.TryBegin(Lease, snapshot ?? Tracker.Current, true, true, true,
                out var generation, out var error), error?.ToString());
            Assert.NotNull(generation);
            return generation!;
        }

        public static ClccCallEntry ActiveEntry(int index) =>
            new(index, CallDirection.Outgoing, 0, 0, false, "<redacted>", 129, null, null);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
