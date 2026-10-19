namespace CellPort.Audio.Core.Resources;

public sealed record RuntimeAudioAvailability(bool IsAvailable, string StatusText)
{
    public static RuntimeAudioAvailability Evaluate(RuntimeBundleValidationResult? profile, bool hostBackendAvailable)
    {
        if (profile is null)
            return new(false, "通话音频：不可用（尚无已验证的设备运行时）");
        if (!profile.IsValid)
            return new(false, "通话音频：不可用（资源或设备匹配校验失败）");
        if (!profile.AudioEnableAllowed)
            return new(false, "通话音频：不可用（候选配置或验收证据未完成）");
        if (!hostBackendAvailable)
            return new(false, "通话音频：不可用（当前平台媒体后端未就绪）");
        return new(true, "通话音频：已就绪");
    }
}
