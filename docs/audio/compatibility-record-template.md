# Module audio compatibility record

Copy this file for each exact host/device/runtime/audio-mode combination.
Unknown fields stay explicitly unknown. A matching kernel release, visible
sound card, successful build, or endpoint enumeration is not a compatibility
approval.

## Identity and build

| Field | Value |
|---|---|
| Record ID | |
| Record date (UTC) | |
| Host OS and full version/build | |
| Host CPU architecture and model | |
| Host audio backend and version | |
| Device model and hardware revision | |
| Complete firmware version/build | |
| Device OS/version, if known | |
| Device CPU architecture | |
| Kernel release and complete build identity | |
| Kernel module ABI/vermagic or symbol version | |
| Device boot continuity evidence (identifier redacted) | |
| USB identity evidence (parent/topology; sensitive values redacted) | |
| Control transport and version | |

## Runtime and component fingerprint

Record every component required for the claimed capability. Include exact
version, byte size, SHA-256, source/provenance, license or redistribution
condition, ABI, and whether it is loaded or merely a candidate. Do not include
secrets, owner tokens, complete device serials, or credentials.

| Component | Exact version/build | ABI / architecture | Size (bytes) | SHA-256 | Source and license status | State (candidate/verified/loaded) |
|---|---|---|---:|---|---|---|
| Supervisor | | | | | | |
| Route helper | | | | | | |
| Kernel module(s) | | | | | | |
| Vendor library(ies) | | | | | | |
| Calibration component | | | | | | |
| Host application/audio backend | | | | | | |

## Compatibility classification

Select one current level and provide evidence. Levels are per exact OS, CPU,
firmware, runtime, and audio mode; they do not transfer between combinations.

| Level | Meaning |
|---|---|
| candidate | Collected observations only; no approval for device mutation or release support |
| experimental | Explicitly supervised experiment passed stated preconditions; not a support claim |
| verified | Required compatibility, recovery, call, media, endurance, and fault evidence passed for this exact combination |
| unsupported | A required check failed or the combination is known to be incompatible |

- Current level:
- Decision owner:
- Evidence links/paths:
- Allowed operations at this level:
- Release/support claim, if any:

## Gate and capability evidence

| Gate / capability | Required evidence | Result (not run/pass/fail/blocked) | Evidence reference | Notes |
|---|---|---|---|---|
| G0-A fresh complete empty CLCC and no competing owner | | | | |
| G0-B matching runtime source, ABI, license, digests, route-only contract | | | | |
| G0-C USB descriptors, correct configuration sequence, four endpoints | | | | |
| G0-D supervisor survival, bounded convergence, exact recovery | | | | |
| Read-only device identity and control/audio parent association | | | | |
| Exact USB snapshot and restoration | | | | |
| Calibration and route readiness | | | | |
| Windows/Linux/macOS host behavior | | | | |
| Headset two-way voice | | | | |
| Speakerphone and echo-cancellation quality | | | | |
| Bluetooth mode | | | | |
| Local added latency p95 (target <= 150 ms) | | | | |
| Queue bound, drift/xrun, memory and handle stability | | | | |
| Call-cycle, idle, and fault matrix | | | | |

## Unverified items and limits

List every unknown, not-run check, failed check, excluded operation, and
unsupported audio mode. A blank section is not evidence of a pass.

- 

## Decision

- Overall status: not assessed / candidate / experimental / verified / unsupported
- Supported OS/CPU/firmware/runtime/audio mode tuple:
- Explicitly excluded combinations and modes:
- Date of next review:
- Reviewer:
