# Media quality and device-mode support

## Reported measurements

Record each metric per OS, CPU architecture, device firmware/profile, endpoint
selection and call direction. A successful build or endpoint enumeration is not
an audio quality result.

| Metric | Definition | Acceptance target |
|---|---|---|
| Added local latency | Capture callback timestamp through matching render callback, minus the loopback's measured baseline | Report median and p95; target p95 below 120 ms |
| Queue occupancy | Queued audio duration, sampled at each callback | Hard cap 120 ms; report high-water mark and drops |
| Underruns / overruns | Callback has insufficient input / queue rejects excess input | Count separately by direction; no sustained growth in a 60-minute run |
| Clock correction | Applied resampling ratio relative to nominal | Record ppm and bounds; verify ±100 and ±300 ppm scenarios |
| Stream stall | No advancing frames while the endpoint remains expected | Close uplink within one second; advancing silence is healthy |
| Resource use | Process memory, audio handles and active streams | Record start/end/high-water values and confirm no sustained growth |

Use software loopback for deterministic format, queue and drift coverage. Use a
physical headset run to report acoustic behavior. Neither result substitutes
for the other. Do not treat nonzero samples as proof that the far end heard
intelligible audio.

## Device-mode support record

| Mode | Status in this change | Evidence needed to change status |
|---|---|---|
| Wired headset | Not verified | Exact profile, paired local/module endpoints, both call directions, mute, permission and removal behavior |
| Built-in speaker / hands-free | Not verified | Separate acoustic/echo assessment and both call directions on the exact hardware |
| Bluetooth | Not verified | Pairing, route stability, format changes, both call directions and disconnect recovery |
| Acoustic echo cancellation | Not claimed | Measured AEC result for an identified implementation and device mode |

If a later acceptance run passes only for a wired headset, publish that mode
alone as verified. Keep hands-free, Bluetooth and AEC unverified until each has
its own evidence. Current QDC507 runtime/profile gates are still closed, so no
mode is currently supported by this repository.
