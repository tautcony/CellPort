# Call status and user-facing wording

The full, successfully completed `AT+CLCC` list is the source for confirmed network call state. `ATD`, `ATA`, and `AT+CHUP` returning `OK` only means that the module accepted a command. It does not confirm dialing progress, answer, media readiness, or call end.

CLCC `mode=0` entries are ordinary voice calls. `mode=1` entries and other modes are non-voice occupancy; they do not make the voice UI active and never authorize the microphone. A complete empty list confirms no calls. `ERROR`, timeout, transport failure, or malformed required fields produce an unknown snapshot, never an empty one.

The UI uses conservative labels:

- **语音通话中** appears only for one uniquely selected active voice entry.
- **仅数据占用；语音未接通** describes data or unsupported-mode entries without a voice call.
- **通话状态未知** describes a failed or stale observation. It does not authorize audio or USB changes.
- **来电，等待接听**, **拨号中，等待接通**, and **正在振铃，等待接通** describe pre-active network states.
- **通话保持；音频已暂停** and **多路语音状态不明确；音频已暂停** never select a different call automatically.
- **等待网络确认挂断…** and **挂断未确认** remain visible until a complete snapshot proves the target call absent.

Call direction is captured when the tracked call first appears. Connected duration begins on the first confirmed active snapshot and ends only when a later complete snapshot no longer contains that call. A call that never reaches confirmed active has no connected duration. URCs remain compatibility inputs and trigger a full snapshot refresh; an unmapped `+CIEV` value is not interpreted as active.
