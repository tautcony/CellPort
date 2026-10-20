## Purpose

Provide one desktop application with consistent module-management workflows on Windows and macOS, preserving user settings and separating platform capabilities from the common interface.

## ADDED Requirements

### Requirement: Portable canonical startup
The application SHALL build and start from the canonical application project on macOS without requiring Windows targeting packs. The default solution SHALL exclude Windows-only diagnostic hosts on non-Windows builds through a portable solution composition. The application SHALL support all six startup page names and remain responsive without attached hardware.

#### Scenario: Build and start on macOS
- **WHEN** a user builds the default solution and runs the canonical app on macOS
- **THEN** the build succeeds without NETSDK1100 and a six-page desktop window opens

### Requirement: Full management workflows
The application SHALL preserve SMS conversations, sending, refresh, CSV/JSON export, verification-code copy and dialing; call dialing, answering, hangup, DTMF, mute and history; device status, serial reconnection, diagnostics, USSD and phonebook copy; eSIM capability probing, profiles, enable/disable, nickname, deletion and optional restart/reconnect; console command execution, quick commands, history and unsolicited output; and settings, privacy, forwarding, refresh interval and update checks.

#### Scenario: Open each page while disconnected
- **WHEN** the user navigates through all six pages without a module
- **THEN** every page opens and provides a usable empty state with hardware actions guarded

#### Scenario: Destructive profile operation
- **WHEN** the user requests deletion or profile switching
- **THEN** the app awaits the existing confirmation sequence and cancellation prevents all writes

#### Scenario: Export inbox
- **WHEN** the user exports a populated SMS inbox and selects a destination
- **THEN** the chosen format is written using a native save picker and existing encoding rules

### Requirement: Platform capability isolation
The common interface SHALL NOT load Windows-only services on macOS. Windows USB diagnostics and binding SHALL remain available on Windows. Unsupported USB direct-control and media backends SHALL present accurate availability rather than simulated success.

#### Scenario: macOS device diagnostics
- **WHEN** the user opens device diagnostics on macOS
- **THEN** the app lists macOS serial candidates and provides macOS guidance without invoking WMI or WinUSB

### Requirement: Settings and lifecycle compatibility
The app SHALL retain the existing settings.json schema and honor saved theme, privacy, forwarding, refresh and port preferences. It SHALL cancel and await active operations, stop timers, detach service events and dispose owned modem resources on shutdown. Native dialogs SHALL be asynchronous and cancellable.

#### Scenario: Restart with saved preferences
- **WHEN** the user saves preferences and restarts
- **THEN** those preferences are restored without manual settings migration

#### Scenario: Close while operation is pending
- **WHEN** the window closes during a cancellable read or reconnect
- **THEN** the operation is cancelled and owned resources are released without an unhandled exception

### Requirement: Repeatable validation and packaging
The repository SHALL include portable UI regression coverage, macOS build/startup instructions, a runnable IDE configuration, self-contained macOS packaging and Windows publishing using the same canonical host.

#### Scenario: Package for macOS
- **WHEN** the macOS packaging script succeeds
- **THEN** its output is a launchable app bundle containing the canonical application and runtime
