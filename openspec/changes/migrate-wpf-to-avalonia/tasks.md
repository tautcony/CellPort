## 1. Host and shared integration

- [x] 1.1 Convert CellPort.App to Avalonia, implement host composition, asynchronous dialogs and shared styles; verify the canonical project builds on macOS without Windows targeting.
- [x] 1.2 Migrate shell navigation, six startup page arguments, refresh timer, forwarding and cancellable shutdown; verify navigation and lifecycle with UI regression tests.
- [x] 1.3 Preserve settings schema and migrate temporary preferences when appropriate; migrate theme, privacy, webhook settings, update checks and console quick/history commands; verify persistence and console tests.

## 2. Full page migration

- [x] 2.1 Migrate SMS conversations, incoming updates, compose/send, refresh, verification-code copy, dialing and CSV/JSON native export; verify synthetic-message UI tests and empty states.
- [x] 2.2 Migrate eSIM probing, display, enable/disable, nickname, double-confirmed deletion and optional cancellable restart/reconnect; verify guarded disconnected state and cancellation/confirmation regressions.
- [x] 2.3 Migrate call keypad, controls, incoming number, privacy, history and honest audio state; verify synthetic call/page tests and event cleanup.
- [x] 2.4 Migrate device status, serial selection/reconnection, platform diagnostics/driver actions, USSD and phonebook copy; verify disconnected and simulated service states.

## 3. Build and delivery

- [x] 3.1 Make the default solution portable and retain a Windows diagnostic solution; remove temporary Desktop host, add canonical IDE run configuration and update scripts/CI/installer references; verify default solution build and Windows cross-publish.
- [x] 3.2 Update both READMEs, macOS notes, platform documentation and migration parity matrix; verify documented commands and absence of stale WPF/temporary-host launch instructions.
- [x] 3.3 Run core, audio, platform and new headless UI suites, visually smoke all six pages on macOS, package and launch final .app; record observed results and unverified hardware/platform limits.
