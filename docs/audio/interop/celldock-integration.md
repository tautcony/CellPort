# CellDock and macOS interoperability handoff

## Versioned, platform-neutral contract

The v1 framing, JSON envelopes, operation allowlist, errors, state transitions,
and recovery rules are defined in [protocol-v1.md](../protocol-v1.md). Machine
readable vectors live in `interop/v1/protocol-vectors.json` and
`interop/v1/clcc-vectors.json`. The vectors use synthetic IDs and redacted
numbers. They cover empty, data-occupied, active voice, failed and partial CLCC
queries; incompatible major versions; candidate-profile rejection; and deferred
versus verified restore outcomes. `protocol-v1.schema.json` remains the
envelope schema and does not replace operation or ownership checks.

Swift implementations should model protocol fields as value types: `String`,
`UInt64`, `Bool`, optionals and enums with an unknown/error path. Keep the
four-byte big-endian frame length separate from the UTF-8 JSON body. Reject
duplicate keys, unknown fields and oversized frames before dispatch. Do not
import .NET, WPF, Windows handles, COM, or host audio endpoint types into the
wire model. `ownerToken` must be held as secret session state and omitted from
logs and exported diagnostics.

## CellDock component mapping

| CellDock component | Contract responsibility | Required guards |
|---|---|---|
| `ModuleVoiceRuntime` | inspect/status/prepare/renew/release protocol client | Verify protocol major, selected physical device, boot, owner and generation on each response; preserve unknown outcomes |
| `ModuleAudioSession` | module standby lease and per-call route generation | Standby opens no capture stream; call end stops the route only; old callback generations are discarded |
| `CUACProbe` | read-only USB parent/interface and endpoint association | Bind AT, ADB and audio interfaces through the same physical parent; fail on ambiguous identity or stale enumeration |
| `VoiceAudioService` | host endpoint selection, muted media startup, call-state gate and recovery presentation | Keep local playback/capture selection separate from module endpoints; require microphone permission and fresh unique active voice state |

On macOS, request microphone access only when the user explicitly starts
media, keep the app's audio entitlement and usage description aligned with that
path, and surface a denial as receive-only/unavailable without repeated prompts.
USB identity acceptance must record the selected device's parent identity,
interfaces, boot continuity, endpoint association, and any re-enumeration
result. A friendly product name or audio endpoint name alone is insufficient.

Do not assume the historical ModuleVoice helper supports the current protocol,
ABI, route-only semantics, or USB field ownership. Its source and redistribution
gates remain unresolved in `resource-inventory.md`; no legacy helper binary is
part of this handoff.

## Simulator results and external status

The host supervisor tests exercise a second owner attempting to acquire a live
session and require `OPERATION_CONFLICT`; they also reject protocol 2.0 with
`PROTOCOL_VERSION_UNSUPPORTED`. These tests validate the protocol model only.
They do not represent a CellDock implementation or a macOS device run.

External CellDock implementation and hardware receipt: **not provided**.
Status remains **external integration pending**. Do not advertise macOS
support based on these documents or the host simulator.
