# Candidate device record: QDC507

This is a candidate-only record of the read-only baseline documented on
2026-10-19 in the OpenSpec design. It does not authorize a USB change, driver
load, runtime deployment, call test, or audio support claim.

## Identity and build

| Field | Observed value |
|---|---|
| Record date | 2026-10-19 |
| Host OS | Windows, kernel 10.0.26300.0 |
| Host CPU architecture/model | Not recorded |
| Host ADB | 35.0.1-11580240 |
| Device model | QDC507 |
| Reported firmware | QDC507GLEFM21; complete firmware/build suffix not obtained |
| Device CPU architecture | armv7l |
| Kernel | 3.18.44 #1 PREEMPT, Thu Jun 20 11:08:23 CST 2024 |
| Kernel build / module ABI verification | Not verified; matching release is not compatibility evidence |
| ADB serial | Question mark; unreliable and not a stable identity |
| Boot continuity | Read twice consistently during the baseline; boot identifier omitted |
| USB original values | functions=diag,serial,rmnet,ffs; enable=1; audio_enable=0 |

## Candidate component fingerprints

These digests were observed on the device as read-only fingerprints. Presence
does not establish source approval, ABI compatibility, calibration readiness,
or permission to load or redistribute the component.

| Component | Observed version/name | SHA-256 | State |
|---|---|---|---|
| Vendor audio library | libql_lib_audio.so.1.0.0 | 5c684c59bdb2663de45d7e02e10ac68b06e60a1ad6778787a87ea9312e037255 | Candidate fingerprint; ABI/source not approved |
| Calibration utility | alsaucm_test | e55995d9d2a5b8233f3e720005de9548a8b7e55507dc07ab8a2555b4ea57c37e | Candidate fingerprint; presence does not prove calibration service is running |
| APR / voice modules | No approved device-loaded component | Not recorded | Not loaded or approved |
| Route helper / supervisor | No v1 helper evidence | Not recorded | Not deployed |

## Support classification and gates

- **Current level: candidate (unverified).**
- **Release support: none.** No Windows, Linux, or macOS audio support claim is approved.
- G0-A: blocked for USB preparation. The recorded complete CLCC response had
  two stat=0, mode=1 entries. They are non-voice occupancy; they must not be
  cleared automatically.
- G0-B: not passed. Matching runtime source/build, license, ABI and route-only
  helper evidence are absent.
- The audited MaVo v0.1.2 bridge is built for ARMv7 EABI5 soft-float with a
  glibc loader (`/lib/ld-linux.so.3`); the current module's user-space loader,
  libc, float ABI and exact target libraries have not been verified. The
  bridge also writes `audio_enable` on start and stop, so it cannot satisfy
  the route-only capability even if its user-space ABI matches.
- The external module report describes an APR plus `qdc507_afe.ko` build, but
  the candidate v1 manifest contains `qdc507_voice.ko`. The report's APR hash
  also differs from the artifact hash in that manifest. See
  [resource inventory](resource-inventory.md); neither binary is approved for
  packaging or loading.
- `alsaucm_test` was observed as a candidate calibration utility, but there
  is no readiness contract or successful calibration evidence. A filename or
  process presence does not satisfy calibration readiness.
- G0-C: not passed. No UAC enablement, USB audio descriptor verification, or
  host endpoint enumeration was performed.
- G0-D: not passed. No supervisor, bounded recovery, or exact restoration
  experiment was performed.
- Audio: not tested. No driver was loaded, no USB fields were changed, and no
  call or recording was made.
- Complete firmware build and host CPU model remain unverified.
- The v2 admission policy leaves candidate and experimental profiles
  unavailable for audio. It permits enablement only after a validated
  manifest, exact target match, route-only helper evidence, source/ABI/
  calibration/hardware evidence and exact resource hashes are all present.
  The Calls page surfaces this state as “通话音频：不可用” while no verified
  profile and host media backend are connected. A successful offline import
  alone does not change that state. `cellport-diag audio-status` reports
  `UNSUPPORTED_PROFILE` with `audioAvailable=false` until those gates pass.

Only read-only PnP, selected ADB reads, and AT+CGMM/CGMR/CLCC observations are
part of this baseline. The original USB values were reread unchanged. This
record intentionally contains no phone number, device serial, boot identifier,
or owner token.

Use compatibility-record-template.md for a new exact compatibility decision
and acceptance-record-template.md for any future supervised hardware run.
