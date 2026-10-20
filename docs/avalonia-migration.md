# Avalonia migration and verification matrix

`CellPort.App` is the canonical Avalonia 12.1.4 desktop app for Windows and macOS; the former temporary `CellPort.Desktop` host is retired. `CellPort.sln` is the portable default solution; `CellPort.Windows.sln` contains Windows-only diagnostics and tools. macOS platform composition matches known module VID/PID with IOKit location and opens the libusb AT interface, with `/dev/cu.*` serial fallback. Windows retains module scanning, WinUSB and driver setup. The macOS scripts launch and publish the canonical `CellPort` executable in `CellPort.app`.

This matrix records the migrated workflows and automated verification. Headless and fake-channel tests verify UI wiring and disconnected states. A QDC507 was also connected on macOS through USB interface 2 and its modem/SIM/network status was read. eSIM profile writes, SMS/call workflows, other carriers, Windows runtime and audio-backend behavior remain unverified.

| Page | Preserved workflows in Avalonia | Automated coverage | Hardware / platform checks still needed |
|---|---|---|---|
| SMS | Conversation list and selection, incoming-message updates, refresh, send, verification-code copy, dial-from-message, CSV/JSON export using native file pickers, disconnected state | `SmsPageTests` for synthetic messages, controls and disconnected guards; Core SMS encoding/export tests | Real SMS receive/send, native save picker on macOS and Windows, forwarding delivery |
| Calls | Dial, answer, hang up, in-call DTMF, mute, incoming-number display, privacy masking in entry/history, call history, conservative `CallStatePresentation`, audio availability and media status | `CallsPageTests` connects a real `ModemManager` to a fake AT channel and checks dial, mute and DTMF commands; call-state presentation tests; event detachment on disposal | Real call command support, incoming URCs, network state transitions, call history against a module; no media backend is added and audio remains control-only |
| Device | All `DeviceStatus` fields, VID/PID USB match, libusb interface 2 with interface 3 fallback, serial candidates and selection, manual reconnect, refresh, USSD, SIM phonebook read and number copy | `DevicePageTests` checks status fields, disconnected guards and fake `+CPBR` phonebook UI; Mac platform tests cover native descriptor parsing, device selection, interface claim, transfer and cleanup | QDC507 AT connection and modem/SIM/network status verified on macOS; USSD, phonebook, native clipboard and Windows USB runtime remain unverified |
| eSIM | Probe and display EID/profile data, enable/disable, nickname editing, double-confirmed deletion, optional restart and reconnect | `EsimPageTests` for disconnected guards, simulated profile states and confirmation/cancellation; Core ES10c command/encoding tests | Read and write behavior on test eUICC cards; restart timing and serial reappearance on both operating systems |
| AT console | Command execution, quick commands, history, unsolicited output and result presentation | `HostTests` creates the console page through navigation; Core AT and command-admission tests | Real modem command responses, unsolicited event behavior and platform serial transport |
| Settings | Theme, privacy, auto-connect, refresh interval, port preference, forwarding destinations and update checks | `SettingsTests` checks original JSON fields and optional temporary-settings migration | Existing-user settings compatibility on Windows/macOS; real webhook delivery and GitHub update lookup |

## Audio capability boundary

The call UI keeps the existing audio availability evaluator and `CallAudioUiSnapshot`. It does not claim that media is active when no verified runtime is available. This migration adds no Windows, macOS or Linux media engine. The current user-facing state is call control available with media unavailable (control-only), and lifecycle diagnostics remain visible.

## Validation record

Final build, publish and connected-device checks completed on macOS:

- Canonical app (`src/CellPort.App`) Release build: 0 warnings, 0 errors.
- Portable default solution (`CellPort.sln`) Release build: 0 warnings, 0 errors.
- Windows solution (`CellPort.Windows.sln`) cross-targeted from macOS with `-p:EnableWindowsTargeting=true`: 0 warnings, 0 errors.
- Windows x64 framework-dependent and self-contained `dotnet publish` runs both succeeded. Each output contains `CellPort.exe` and the Core, Audio, Platform.Windows, and Transport.Serial assemblies.
- The self-contained `osx-arm64` `CellPort.app` passed code-signature and plist checks, launched, matched `2CA3:4006`, claimed interface 2, completed an AT probe and read QDC507, SIM-ready, operator, network and signal status.

Validation commands:

```bash
dotnet test CellPort.sln --no-restore -v:minimal
dotnet build CellPort.Windows.sln --no-restore -v:minimal
./scripts/publish-mac.sh osx-arm64
```

Results: Core 89/89, Audio.Core 39/39, App 25/25, macOS 18/18, and Linux 6/6 passed (177 tests total). `git diff --check`, Windows solution build, app bundle signature, plist and bundled libusb linkage checks also passed.

SMS send/receive, calls, USSD, phonebook, eSIM profile writes, Windows runtime and driver behavior, and any media/audio backend remain unverified. Calls currently provide control-only functionality; this migration does not add an audio backend.
