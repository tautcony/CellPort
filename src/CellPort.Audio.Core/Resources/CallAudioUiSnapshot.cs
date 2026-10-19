using CellPort.Core.Services;
using CellPort.Audio.Core.Models;
using CellPort.Audio.Core.Services;

namespace CellPort.Audio.Core.Resources;

/// <summary>One immutable UI view combining confirmed call state and audio-session readiness.</summary>
public sealed record CallAudioUiSnapshot(string CallStateText, string AudioStateText, string MediaPathText)
{
    public static CallAudioUiSnapshot Create(
        CallState callState,
        CallSnapshot callSnapshot,
        RuntimeAudioAvailability availability,
        AudioSessionLifecycleDiagnostic audioSession)
    {
        ArgumentNullException.ThrowIfNull(callSnapshot);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(audioSession);

        var callText = CallStatePresentation.GetStatusText(callState, callSnapshot);
        if (audioSession.ModulePhase == ModuleAudioPhase.RestorePendingCall)
            return new(callText, "通话音频：媒体已停，等待安全恢复", "当前仅通话控制；媒体未采集");
        if (audioSession.ModulePhase == ModuleAudioPhase.RecoveryRequired)
            return new(callText, "通话音频：媒体已停，需要恢复检查", "当前仅通话控制；媒体未采集");
        if (audioSession.ModulePhase == ModuleAudioPhase.Restoring)
            return new(callText, "通话音频：正在恢复", "当前仅通话控制；媒体未采集");
        if (!availability.IsAvailable)
            return new(callText, availability.StatusText, "通话控制可用；媒体通路不可用（仅控制）");

        var audioText = audioSession.ModulePhase switch
        {
            ModuleAudioPhase.StandbyReady when audioSession.MediaState == AudioMediaState.Running =>
                "通话音频：媒体运行中",
            ModuleAudioPhase.StandbyReady when audioSession.MediaState == AudioMediaState.StartingMuted =>
                "通话音频：媒体启动中（静音）",
            ModuleAudioPhase.StandbyReady => "通话音频：待机",
            ModuleAudioPhase.Restoring => "通话音频：正在恢复",
            ModuleAudioPhase.RestorePendingCall => "通话音频：媒体已停，等待安全恢复",
            ModuleAudioPhase.RecoveryRequired => "通话音频：媒体已停，需要恢复检查",
            ModuleAudioPhase.Released => "通话音频：已恢复",
            _ => "通话音频：未准备",
        };
        var pathText = audioSession.MediaState == AudioMediaState.Running
            ? "当前语音通话使用已选媒体端点"
            : "当前仅通话控制；媒体未采集";
        return new(callText, audioText, pathText);
    }
}
