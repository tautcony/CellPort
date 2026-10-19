# Windows audio operation and recovery guide

## Data occupancy before preparation

1. Keep the current data session intact. Do not send `AT+CHUP`, restart the
   modem, toggle USB functions, or force-clear a CLCC entry.
2. Run the explicit-port read-only inspection described in `inspect.md` and
   require a complete, fresh CLCC result. Any non-voice entry is
   `DEVICE_BUSY_DATA`; a failed, partial or stale query is `CALL_STATE_UNKNOWN`.
3. Leave audio in control-only/unavailable state. Retry preparation only after
   an independent fresh query proves the list empty and the module identity and
   generation still match.

## Network interruption during preparation

- Before supervisor USB commit, cancel preparation and keep USB untouched.
- After commit starts, the supervisor owns bounded local convergence. A lost
  ADB reply is an unknown outcome: query status using the same verified device
  identity when possible; do not replay with a new owner or issue a second
  USB write based on the missing reply.
- If control reconnects with a changed boot or physical parent, invalidate the
  old session. Do not use a stale transport ID or fall back to another modem.
- Keep call control available when possible; do not turn an audio recovery
  problem into an automatic network hangup.

## Restore failure

1. Stop media first. The log state must show `audio.media=Stopped`.
2. If occupancy is present or unknown, retain the journal and show
   `audio.module=RestorePendingCall`; do not report restored or hang up the
   network call to make restore possible.
3. If ownership, boot, or last-applied values conflict, retain the journal and
   show `audio.module=RecoveryRequired`. Do not overwrite external changes,
   unload a module, delete the session directory, or repeatedly retry writes.
4. Reconnect only to the same verified physical module and request read-only
   status/inspection first. Resume a restore only after the supervisor proves
   ownership and fresh idle state. A verified receipt must list the original
   values read back; otherwise keep recovery pending.

The first-release supervisor does not promise unattended recovery after the
supervisor itself is killed. Its next connection can identify an unfinished
journal, but a shell trap or host-process restart is not proof of restoration.
The current QDC507 runtime has not passed G0-B, so these steps describe required
operator behavior and simulator states; no production recovery UI is currently
available for this candidate device.

In the Windows app, `MainWindow` supplies one operation admission instance to
`ModemManager`. SMS writes, eSIM profile changes, CFUN/USB-network AT commands,
non-read-only console commands and call-control requests acquire that shared
gate. An owned audio session rejects conflicting SMS/eSIM/CFUN/network/restart
and raw-console mutations; safe read-only probes and call control remain
available. The gate's USB-network operation kind is reserved for supported
commands and future paths; it does not imply that the candidate profile is
approved.

## Simulated fault drill

Run:

```powershell
dotnet test tests/CellPort.Audio.Core.Tests/CellPort.Audio.Core.Tests.csproj -c Release --filter FaultDrillDiagnostics
```

The test runs media start, hangup/route stop, deferred restore, then verified
restore. It asserts that redacted diagnostics distinguish all three outcomes:

```text
audio.module=StandbyReady audio.media=Stopped audio.recovery=None
audio.module=RestorePendingCall audio.media=Stopped audio.recovery=RestorePendingCall
audio.module=Released audio.media=Stopped audio.recovery=Restored
```

The drill uses the lifecycle fake only. It validates state/log distinctions,
not a physical USB rollback or a device-side journal export.
