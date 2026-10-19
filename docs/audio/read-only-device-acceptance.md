# Device matching and read-only acceptance

This procedure identifies a modem through its current USB parent chain and
records a non-mutating call-state baseline. Do not select a device by friendly
name alone. Do not use `adb -s ?` or an implicit first-device selection.

## Match the Windows USB device

1. Run `Get-PnpDevice -PresentOnly` and select the current Quectel AT control
   interface. Confirm the interface is present and its PnP status is `OK`.
2. Read `DEVPKEY_Device_Parent`, `DEVPKEY_Device_ContainerId`, and
   `DEVPKEY_Device_LocationPaths` for the AT interface and its parent composite
   USB devnode. Match the ADB interface and other module interfaces through
   that same parent/container/location evidence. A repeated friendly name is
   not a match.
3. Record those three values and the selected AT port privately before
   inspection. Compare the values again after inspection. A changed parent,
   container, or location means the module identity is no longer continuous.

Example read-only PowerShell lookup (replace the pattern with the currently
selected AT interface when more than one exists):

```powershell
$at = Get-PnpDevice -PresentOnly -Class Ports |
  Where-Object { $_.FriendlyName -like 'Quectel USB AT Port*' }
if (@($at).Count -ne 1) { throw 'Select one unambiguous current AT interface.' }
$parent = (Get-PnpDeviceProperty -InstanceId $at.InstanceId -KeyName DEVPKEY_Device_Parent).Data
foreach ($id in @($at.InstanceId, $parent)) {
  Get-PnpDeviceProperty -InstanceId $id -KeyName @(
    'DEVPKEY_Device_Parent',
    'DEVPKEY_Device_ContainerId',
    'DEVPKEY_Device_LocationPaths'
  ) | Select-Object InstanceId, KeyName, Data
}
```

The output contains host-specific identifiers. Keep it in a local acceptance
record; do not attach it to a diagnostic export.

## Record call and module state

Run `cellport-diag inspect --port <explicitly selected AT port>`. It sends only
`AT`, `AT+CGMM`, `AT+CGMR`, and `AT+CLCC`. A complete non-empty list is an
occupancy result; the inspector must not send `AT+CHUP` or change USB state.
Capture a module list only after the ADB transport has been physically matched
to the same USB parent. Use the current explicit transport ID for a read-only
`cat /proc/modules` query. Save the list privately before and after inspect and
compare it byte-for-byte. If ADB identity or transport selection is ambiguous
or unavailable, record the module comparison as incomplete.

## Current host observation

The current PnP view had one Quectel AT interface on COM8 and a Quectel ADB
interface under the same composite USB device. The independent inspector
returned `QDC507`, firmware `QDC507GLEFM21`, `DEVICE_BUSY_DATA`, a complete
CLCC response, zero voice calls, and two non-voice occupancy entries. No
command was sent to clear those entries or alter USB configuration.

`adb devices -l` did not return a device inventory during this acceptance
attempt. The Windows PnP and read-only AT observations are recorded, but no
ADB transport was selected and no `/proc/modules` before/after comparison was
made. Therefore this is not a completed 4.6 device acceptance and does not
establish a G0-A idle window.
