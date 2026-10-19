using CellPort.Audio.Core.Resources;
using CellPort.Audio.Core.Services;
using CellPort.Audio.Core.Models;
using CellPort.Core.Services;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public sealed class CallAudioUiSnapshotTests
{
    [Fact]
    public void DataOccupancyAndMissingResourcesStayControlOnly()
    {
        var tracker = new CallTracker();
        tracker.BeginModuleSession();
        var snapshot = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete,
            [new ClccCallEntry(1, CallDirection.Outgoing, 0, 1, false, null, 129, null, null)]));

        var ui = CallAudioUiSnapshot.Create(CallState.Active, snapshot, Unavailable(),
            Diagnostic(ModuleAudioPhase.Disconnected, AudioMediaState.Stopped, AudioRecoveryStatus.None));

        Assert.Equal("仅数据占用；语音未接通", ui.CallStateText);
        Assert.Equal("通话音频：不可用（资源未验证）", ui.AudioStateText);
        Assert.Equal("通话控制可用；媒体通路不可用（仅控制）", ui.MediaPathText);
    }

    [Fact]
    public void HangupUnconfirmedAndIncomingWithoutReadyMediaRemainExplicit()
    {
        var tracker = new CallTracker();
        tracker.BeginModuleSession();
        var incoming = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete,
            [new ClccCallEntry(2, CallDirection.Incoming, 4, 0, false, null, 129, null, null)]));

        var incomingUi = CallAudioUiSnapshot.Create(CallState.Ringing, incoming, Unavailable(),
            Diagnostic(ModuleAudioPhase.Disconnected, AudioMediaState.Stopped, AudioRecoveryStatus.None));
        var hangupUi = CallAudioUiSnapshot.Create(CallState.HangupUnconfirmed, incoming, Unavailable(),
            Diagnostic(ModuleAudioPhase.Disconnected, AudioMediaState.Stopped, AudioRecoveryStatus.None));

        Assert.Equal("来电，等待接听", incomingUi.CallStateText);
        Assert.Contains("不可用", incomingUi.AudioStateText);
        Assert.Equal("挂断未确认", hangupUi.CallStateText);
        Assert.Equal("通话控制可用；媒体通路不可用（仅控制）", hangupUi.MediaPathText);
    }

    [Fact]
    public void StandbyAndDeferredRestoreAreShownSeparatelyFromMediaRunning()
    {
        var tracker = new CallTracker();
        tracker.BeginModuleSession();
        var call = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete, []));
        var available = new RuntimeAudioAvailability(true, "通话音频：已就绪");

        var standby = CallAudioUiSnapshot.Create(CallState.Idle, call, available,
            Diagnostic(ModuleAudioPhase.StandbyReady, AudioMediaState.Stopped, AudioRecoveryStatus.None));
        var pending = CallAudioUiSnapshot.Create(CallState.Idle, call, available,
            Diagnostic(ModuleAudioPhase.RestorePendingCall, AudioMediaState.Stopped, AudioRecoveryStatus.RestorePendingCall));
        var pendingWithoutRuntime = CallAudioUiSnapshot.Create(CallState.Idle, call, Unavailable(),
            Diagnostic(ModuleAudioPhase.RestorePendingCall, AudioMediaState.Stopped, AudioRecoveryStatus.RestorePendingCall));

        Assert.Equal("通话音频：待机", standby.AudioStateText);
        Assert.Equal("当前仅通话控制；媒体未采集", standby.MediaPathText);
        Assert.Equal("通话音频：媒体已停，等待安全恢复", pending.AudioStateText);
        Assert.Equal(pending.AudioStateText, pendingWithoutRuntime.AudioStateText);
    }

    private static RuntimeAudioAvailability Unavailable() =>
        new(false, "通话音频：不可用（资源未验证）");

    private static AudioSessionLifecycleDiagnostic Diagnostic(
        ModuleAudioPhase phase, AudioMediaState media, AudioRecoveryStatus recovery) =>
        new(phase, media, recovery);
}
