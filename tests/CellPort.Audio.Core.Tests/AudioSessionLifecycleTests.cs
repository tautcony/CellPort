using CellPort.Audio.Core.Models;
using CellPort.Audio.Core.Services;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public sealed class AudioSessionLifecycleTests
{
    private static AudioSessionLease NewLease(long generation = 4) =>
        new(Guid.NewGuid(), new ModuleDeviceId("fixture-module"), generation);

    [Fact]
    public void StandbyIsInertAndCallEndStopsOnlyTheMediaRoute()
    {
        var lifecycle = new AudioSessionLifecycle();
        var lease = NewLease();

        Assert.True(lifecycle.CommitStandby(lease));
        Assert.True(lifecycle.IsStandbyOnly);
        Assert.Equal(AudioMediaState.Stopped, lifecycle.MediaState);

        Assert.True(lifecycle.TryStartMedia(lease, 12, out var first));
        Assert.NotNull(first);
        Assert.False(lifecycle.CanAcceptMediaCallback(first!));
        Assert.True(lifecycle.MarkMediaRunning(first!));
        Assert.True(lifecycle.CanAcceptMediaCallback(first!));

        Assert.True(lifecycle.StopMedia(lease));
        Assert.Equal(ModuleAudioPhase.StandbyReady, lifecycle.ModulePhase);
        Assert.Equal(AudioMediaState.Stopped, lifecycle.MediaState);
        Assert.False(lifecycle.CanAcceptMediaCallback(first!));
        Assert.True(lifecycle.IsStandbyOnly);
    }

    [Fact]
    public void OldMediaGenerationCannotControlANewCallOrNewModuleSession()
    {
        var lifecycle = new AudioSessionLifecycle();
        var firstLease = NewLease();
        Assert.True(lifecycle.CommitStandby(firstLease));
        Assert.True(lifecycle.TryStartMedia(firstLease, 21, out var oldMedia));
        Assert.True(lifecycle.MarkMediaRunning(oldMedia!));
        Assert.True(lifecycle.StopMedia(firstLease));

        Assert.True(lifecycle.TryStartMedia(firstLease, 22, out var currentMedia));
        Assert.True(lifecycle.MarkMediaRunning(currentMedia!));
        Assert.False(lifecycle.CanAcceptMediaCallback(oldMedia!));
        Assert.True(lifecycle.CanAcceptMediaCallback(currentMedia!));
        Assert.False(lifecycle.StopMedia(firstLease with { SessionId = Guid.NewGuid() }));

        Assert.True(lifecycle.BeginRestore(firstLease));
        Assert.False(lifecycle.CompleteRestore(firstLease,
            new AudioRestoreReceipt(AudioRecoveryStatus.RestorePendingCall, false, [])));
        Assert.Equal(ModuleAudioPhase.RestorePendingCall, lifecycle.ModulePhase);
        Assert.False(lifecycle.CommitStandby(NewLease(5)));

        Assert.True(lifecycle.BeginRestore(firstLease));
        Assert.True(lifecycle.CompleteRestore(firstLease,
            new AudioRestoreReceipt(AudioRecoveryStatus.Restored, true, ["enable", "audio_enable"])));
        Assert.Equal(ModuleAudioPhase.Released, lifecycle.ModulePhase);

        var secondLease = NewLease(5);
        Assert.True(lifecycle.CommitStandby(secondLease));
        Assert.True(lifecycle.TryStartMedia(secondLease, 1, out var secondMedia));
        Assert.False(lifecycle.CanAcceptMediaCallback(currentMedia!));
        Assert.False(lifecycle.MarkMediaRunning(currentMedia!));
        Assert.True(lifecycle.MarkMediaRunning(secondMedia!));
    }

    [Fact]
    public void FaultDrillDiagnosticsSeparateStoppedMediaPendingRestoreAndVerifiedRestore()
    {
        var lifecycle = new AudioSessionLifecycle();
        var log = new List<string>();
        lifecycle.StateChanged += (_, diagnostic) => log.Add(diagnostic.ToLogLine());
        var lease = NewLease();

        Assert.True(lifecycle.CommitStandby(lease));
        Assert.True(lifecycle.TryStartMedia(lease, 31, out var media));
        Assert.True(lifecycle.MarkMediaRunning(media!));
        Assert.True(lifecycle.StopMedia(lease));
        Assert.True(lifecycle.BeginRestore(lease));
        Assert.False(lifecycle.CompleteRestore(lease,
            new AudioRestoreReceipt(AudioRecoveryStatus.RestorePendingCall, false, [])));
        Assert.True(lifecycle.BeginRestore(lease));
        Assert.True(lifecycle.CompleteRestore(lease,
            new AudioRestoreReceipt(AudioRecoveryStatus.Restored, true, ["enable", "audio_enable"])));

        Assert.Contains("audio.module=StandbyReady audio.media=Stopped audio.recovery=None", log);
        Assert.Contains("audio.module=RestorePendingCall audio.media=Stopped audio.recovery=RestorePendingCall", log);
        Assert.Contains("audio.module=Released audio.media=Stopped audio.recovery=Restored", log);
        Assert.DoesNotContain(log, entry => entry.Contains(lease.SessionId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRestoreVerificationNeverReportsSuccess()
    {
        var lifecycle = new AudioSessionLifecycle();
        var lease = NewLease();
        Assert.True(lifecycle.CommitStandby(lease));
        Assert.True(lifecycle.BeginRestore(lease));
        Assert.False(lifecycle.CompleteRestore(lease,
            new AudioRestoreReceipt(AudioRecoveryStatus.RecoveryRequired, false, [])));
        Assert.Equal(ModuleAudioPhase.RecoveryRequired, lifecycle.ModulePhase);
        Assert.Equal(AudioRecoveryStatus.RecoveryRequired, lifecycle.Diagnostic.RecoveryStatus);
    }
}
