namespace CellPort.Core.Services;

/// <summary>Conservative user-visible wording for confirmed call snapshots and command state.</summary>
public static class CallStatePresentation
{
    public static string GetStatusText(CallService? calls)
    {
        if (calls is null)
        {
            return "空闲";
        }

        return GetStatusText(calls.State, calls.CurrentSnapshot);
    }

    public static string GetStatusText(CallState state, CallSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (state == CallState.HangupUnconfirmed)
        {
            return "挂断未确认";
        }
        if (state == CallState.HangupPending)
        {
            return "等待网络确认挂断…";
        }
        if (snapshot.IsUnknown)
        {
            return state switch
            {
                CallState.Dialing => "拨号中，等待网络确认",
                CallState.Ringing => "响铃，等待网络确认",
                _ => "通话状态未知",
            };
        }

        var voiceCalls = snapshot.Calls.Where(call => call.Entry.Mode == 0).ToArray();
        if (voiceCalls.Length == 0)
        {
            return snapshot.Calls.IsEmpty ? "空闲" : "仅数据占用；语音未接通";
        }
        if (voiceCalls.Length > 1)
        {
            return "多路语音状态不明确；音频已暂停";
        }

        return voiceCalls[0].State switch
        {
            TrackedCallState.Active when snapshot.SelectedVoiceCall is not null => "语音通话中",
            TrackedCallState.Held => "通话保持；音频已暂停",
            TrackedCallState.Dialing => "拨号中，等待接通",
            TrackedCallState.Alerting => "正在振铃，等待接通",
            TrackedCallState.Incoming or TrackedCallState.Waiting => "来电，等待接听",
            _ => "通话状态未知",
        };
    }
}
