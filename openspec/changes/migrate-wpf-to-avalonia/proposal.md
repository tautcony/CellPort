## Why

CellPort.App uses WPF and targets net10.0-windows, so opening the normal solution on macOS fails with NETSDK1100. The temporary CellPort.Desktop host only exposes a subset of features and creates two competing application entry points. Replace the WPF UI with one Avalonia application while retaining existing business protocols and Windows capabilities.

## What Changes

- Convert the canonical CellPort.App project to Avalonia with Windows and macOS host composition; remove the temporary Desktop host.
- Migrate all six pages, shared dialogs, navigation, themes, privacy, forwarding, refresh and cleanup behavior.
- Preserve SMS conversation/export/code copying, call controls/history, device diagnostics/USSD/phonebook, eSIM read/write/restart workflows, console history and settings/update checks.
- Keep Windows-only USB binding behind a platform boundary, with clear macOS alternatives and honest audio availability.
- Provide portable solution entry points, IDE run configuration, packaging scripts, CI builds and migration tests.
- **BREAKING**: WPF resources and UI types are replaced; app binaries retain the CellPort executable identity and settings schema.

## Capabilities

### New Capabilities
- `avalonia-desktop-host`: Single portable app, full page/workflow parity, platform composition and build/startup lifecycle.

### Modified Capabilities

None. Existing in-progress call-audio backend requirements remain tracked separately.

## Impact

CellPort.App, temporary CellPort.Desktop, MacOS/Windows composition, solution files, scripts, installers, CI, UI tests and documentation. Core AT/SMS/eSIM protocols remain shared. No new modem firmware, profile-download protocol or macOS media backend is introduced. Implementation is delegated to GPT-6 Luna at the user's request; root agent owns design and review.
