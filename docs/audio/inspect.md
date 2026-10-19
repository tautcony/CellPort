# Read-only module inspection

Use the separate inspect path when recording a module baseline:

```powershell
cellport-diag inspect --port <currently verified AT port>
```

The port must be selected by the operator from the current PnP/USB identity view. The command does not call `ModemManager.ConnectAsync`, which runs normal SMS/call initialization.

Inspect sends only `AT`, `AT+CGMM`, `AT+CGMR`, and `AT+CLCC`. It does not issue call-control, USB-function, SIM, message-storage, reset, or audio commands. It omits phone numbers and raw AT output from its report.

`DEVICE_BUSY_DATA` means a complete CLCC response still contains non-voice occupancy. The command returns that status without sending `AT+CHUP` or attempting to make the list empty. `CALL_STATE_UNKNOWN` means the query failed or the list was malformed; it is not reported as idle.

Exit codes are `0` for a complete empty inspection, `2` for missing explicit port selection, `3` for an active voice call, `4` for non-voice occupancy, `5` for unknown call state, and `6` for control-channel failure.
