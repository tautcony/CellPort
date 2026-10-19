# Audio hardware acceptance record

Use one record for each run. Link it to the matching
compatibility-record-template.md record. A completed checklist is evidence
only for the listed configuration and test; it does not automatically certify
another OS, CPU, firmware, audio mode, or client.

## Run identity

| Field | Value |
|---|---|
| Acceptance ID | |
| Date/time and timezone | |
| Operator / reviewer | |
| Compatibility record ID | |
| Host OS and complete build | |
| Host CPU architecture/model | |
| Device model/revision | |
| Complete firmware build | |
| Kernel/module ABI | |
| Runtime manifest and component digests | |
| Audio mode and local endpoint types | |
| Test objective / task IDs | |

## Preconditions

- [ ] Device identity and selected control/audio endpoints are associated to the same physical parent.
- [ ] Current call state was read from a complete fresh CLCC response.
- [ ] Any required idle window and absence of another owner are confirmed.
- [ ] Applicable G0 gates and runtime provenance are reviewed.
- [ ] Starting USB fields and recovery journal state are recorded.
- [ ] Test phone number and other personal data are excluded from this record.

## Procedure and results

| Step | Action | Expected result | Actual result / evidence | Pass/fail/not run |
|---|---|---|---|---|
| | | | | |

## Measurements

| Metric | Target / method | Result | Evidence |
|---|---|---|---|
| Local added one-way latency p95 | <= 150 ms; exclude cellular network time | | |
| Maximum queued audio per direction | <= 120 ms | | |
| Clock drift / correction | Record ppm, watermarks, xruns | | |
| Media stall detection | Uplink closes after one second without frame progress | | |
| Call cycles | Record count and covered outcomes | | |
| Idle endurance | Record duration | | |
| Memory and handle trend | No unbounded growth | | |

## Recovery and privacy checks

| Check | Expected result | Actual result / evidence | Pass/fail/not run |
|---|---|---|---|
| USB original values and order | Exact captured values restored; no external value overwritten | | |
| AT/ADB/network interfaces | Correct target remains associated; lost connectivity is reported as unverified | | |
| Ownership and helper cleanup | Only this session's verified processes are affected | | |
| Failure or disconnect behavior | Media stops; recovery is complete, deferred, or explicitly required | | |
| Exported diagnostics | No PCM, full phone number, device serial, boot ID, owner token, or ADB credential | | |

## Result

- Overall result: not run / pass / fail / blocked
- Compatibility level supported by this run: candidate / experimental / verified / unsupported
- Unverified items and deviations:
- Recovery status:
- Follow-up issue or evidence path:
- Reviewer and review date:
