# Supervisor host simulator

`tools/audio-supervisor` contains a dependency-free C11 protocol and recovery
simulator. It is a host-side implementation and test target; it does not load
modules, write USB attributes, contact a modem, or select an ADB device.

## Build and run

On a host with CMake and a C11 compiler:

```powershell
cmake -S tools/audio-supervisor -B build/audio-supervisor
cmake --build build/audio-supervisor --config Release
ctest --test-dir build/audio-supervisor -C Release --output-on-failure
```

The executable reads big-endian length-prefixed protocol frames on stdin and
writes one framed response for each request. The `simulator-only` profile
exercises in-memory owner state for protocol tests; it has no device, resources,
USB writes, or runtime helper. Any other profile returns `UNSUPPORTED_PROFILE`.
`tools/audio-supervisor/test-simulator.ps1` sends framed requests from two
clients and confirms owner contention returns `OPERATION_CONFLICT`, then checks
that protocol 2.0 returns `PROTOCOL_VERSION_UNSUPPORTED`. These results only
validate the host protocol model and do not approve the current QDC507 profile.

## Implemented invariants

- The request parser bounds frames at 65,536 bytes before allocating, accepts
  exactly one UTF-8 JSON object, rejects duplicate keys, unknown fields,
  unknown operations, incompatible major versions, invalid payloads, and any
  shell or command field.
- Ownership binds boot ID, session, owner token, and generation. A second
  owner cannot replace the current owner. `/proc/<pid>/stat` parsing locates
  start time after the final `)` so spaces and parentheses in `comm` do not
  shift fields. Process identity is accepted only when PID, start time,
  executable, argv, boot ID, and owner token all match.
- The original snapshot is created once and atomically renamed into place.
  Intent and last-applied records are separate atomic writes. A repeated
  prepare preserves the first snapshot. The bounded request history keeps 16
  request IDs and SHA-256 body digests; a changed body under a reused ID is
  rejected, and an evicted old sequence is stale rather than re-executed.
- Leases use supplied monotonic milliseconds: 45 seconds normally and one
  one-time 120-second initial-prepare grace. Unknown or non-empty occupancy
  leaves the owner locked in `restore_pending_call`; a configuration mismatch
  leaves `recovery_required`.
- The C transaction model accepts only an approved profile with the fixed
  `functions`, `audio_enable`, and `enable` fields. It records the boot ID,
  original values, intent and last-applied values before mutation. The fake
  device test applies `enable=0`, the profile function list and audio enable,
  then re-enables the gadget. Release first compares every current value with
  the last-applied snapshot, restores the exact originals, and verifies them.
  A boot change or external value change causes a no-write rejection; repeated
  release is idempotent after readback verification.
- The reconnect model has separate AT and UAC deadlines, a maximum of two
  retries, and a local-convergence terminal path. An incoming call during a
  committed re-enumeration goes directly to local convergence; if USB is
  consistent but restore cannot be verified, the result is
  `restore_pending_call`, with no wait for a host-side idle proof.
- The Linux runtime source uses a readiness pipe for daemon startup, `execv`
  rather than a shell, an explicit executable allowlist, bounded child waits,
  and allowlisted FIFO writes with deadlines. These Linux-only system calls
  require a separate target build and review before deployment.

## Recovery limit

The first release does not promise unattended recovery if the supervisor itself
is killed. A later connection can detect a remaining journal and report
`recovery_required`; it must not infer that cleanup completed. A shell trap is
not a substitute for this guarantee. Windows host tests exercise the lifecycle
model (client disconnect leaves the simulated supervisor running; a killed
supervisor with a journal is detected on reconnect), but do not execute the
Linux daemon or FIFO system calls.

The transaction tests inject fake read/write callbacks. They do not access
USB sysfs or prove target configfs ordering on hardware. The current device
still has non-voice CLCC occupancy. No supervisor, module, or USB experiment
is authorized by this simulator, and no G0-B source/license or target ABI
evidence is implied by a successful host build.
