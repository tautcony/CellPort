## Context

See proposal.md for motivation. The current WPF app has six code-behind pages (about 2,400 lines), synchronous WPF dialogs, theme resource dictionaries, and Windows-specific scanning in the shell/device page. Core already accepts IAtChannelProvider; MacAtChannelProvider and a minimal Avalonia 12.1.4 Desktop host were added in the first pass. The existing call-audio change has unfinished media backends; this migration must preserve its honest control-only state.

## Goals / Non-Goals

Goals: one canonical CellPort.App host, all existing UI workflows, native async OS integration, portable default builds, settings compatibility, cancellation/event cleanup, and repeatable UI validation.

Non-goals: new AT protocols, SM-DP+ downloads, new macOS/ Linux audio engines, claiming real-hardware verification without a module. Exact WPF control templates need not survive; page structure, labels and workflow behavior must.

## Decisions

1. Convert CellPort.App itself to Avalonia 12.1.4; remove CellPort.Desktop once the canonical app replaces it. Keep AssemblyName=CellPort for Windows installers and macOS bundle identity. A separate permanent Mac UI would duplicate every feature and settings implementation.
2. Keep a small code-only Avalonia UI with Fluent styles, shared semantic resources and reusable asynchronous dialogs. Existing code is predominantly imperative rendering, so this avoids a broad MVVM rewrite while removing every System.Windows UI dependency. Preserve namespaces CellPort.App.Views and AppSettings API to minimize changes to forwarding/business code. Each page implements IModuleAware and IDisposable; pages own cancellable work and detach subscriptions.
3. Shared contracts: IModuleAware.OnActivated(ModemManager), OnConnectionChanged(bool); DialogService.ShowAsync(Control owner,string message,string title="提示"), ConfirmAsync(Control owner,string message,string title="确认") returning bool, InputAsync(Control owner,string prompt,string initialText,string title="输入") returning nullable string. Closed dialogs are cancellation. Native storage and clipboard use TopLevel.GetTopLevel(control).StorageProvider/Clipboard. No synchronous modal wrappers.
4. Host platform composition is centralized in PlatformServices with CreateChannelProvider(), EnumerateSerialPorts() returning IReadOnlyList<string>, GetDiagnostics() returning string, ShowDriverSetupAsync(Control), OpenSystemDeviceManager(), and CanBindWinUsb. Windows implementation is conditionally compiled with Windows target/reference; non-Windows UI has no Windows dependency. Core and transport remain platform-neutral.
5. Keep one root `CellPort.slnx` containing the app, all platform adapters, tests, and diagnostic tools. Project target frameworks and conditional references preserve platform boundaries; non-Windows hosts can use `CellPort.Mac.slnf` as a filtered development view or set `EnableWindowsTargeting` to build the full solution. The Mac filter and shared Rider run configuration point at `CellPort.App`.
6. Preserve settings.json and existing AppSettings fields. Read the temporary desktop-settings.json only as optional first-run fallback if settings.json is absent, map theme values explicitly, and never overwrite existing settings with temporary defaults. Refresh interval and forwarding are wired through the shell.
7. Delegate implementation to three GPT-6 Luna agents with exclusive file ownership: host/settings/console/platform/build; SMS/eSIM; calls/device. Root designs and reviews only; fixes are routed back to Luna. No commits until requested.

## Risks / Trade-offs

- [Async confirmation changes allow connection state to change while a dialog is open] → Revalidate modem/session before writes and serialize page operations.
- [Lifecycle regressions during reconnect] → Page cancellation, disposal and synthetic-channel UI tests; stop refresh timer before closing.
- [Existing Windows behavior regresses] → Keep platform code isolated, cross-build/publish Windows host and run portable regression suites; Windows runtime hardware validation remains separate.
- [No real module is connected] → Validate UI and synthetic service behavior; explicitly record hardware limits.
- [Large parallel edits] → Agree shared contracts up front, exclusive files per agent and root review before final verification.

## Migration Plan

Inventory original workflow handlers before replacing files. Implement all six Avalonia pages and host contracts in parallel, integrate the canonical entry and solutions, remove old WPF files and the temporary Desktop project, update all launch/publish/docs references, then run core/platform/UI tests, Mac UI smoke and Windows cross-publish. Rollback is a normal source-control revert; settings format remains compatible. Keep the existing audio change open and untouched.
