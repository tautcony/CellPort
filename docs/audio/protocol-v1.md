# Module control protocol v1

## Framing and compatibility

- Transport is the selected ADB connection's standard input/output. PCM never
  travels through this control protocol.
- Each frame has a four-byte unsigned big-endian length followed by exactly that
  many UTF-8 JSON bytes. The length counts the JSON body, not the prefix.
- The body length must be between 1 and 65,536 bytes. Reject an oversized length
  before allocating its body. Reject truncated frames, invalid UTF-8, invalid
  JSON, duplicate object keys, trailing body bytes, and a second JSON value.
- Every message has protocolVersion 1.0. A v1 implementation rejects an
  unknown major version with PROTOCOL_VERSION_UNSUPPORTED and performs no
  operation. Unknown fields and operation values are rejected; payload fields
  are an allowlist, never a shell command, argv vector, path to execute, or
  arbitrary device write.
- protocol-v1.schema.json defines the request and response envelope. The
  receiver must also apply the operation-specific and ownership checks below;
  schema validity alone never authorizes a device mutation.
- When a frame cannot be associated with a request ID, a structured parser
  error uses requestId "unknown" and requestSequence 0. Error text is bounded
  and must not contain credentials, complete phone numbers, or device secrets.

## Request fields and operation payloads

Every request carries requestId, strictly increasing requestSequence,
protocolVersion, the currently observed bootId, sessionId and ownerToken
(null only before ownership exists), expectedGeneration, one allowlisted
operation, and its payload. ownerToken is secret material: never log or export
it. bootId and sessionId bind a request to one device boot and one session.

| Operation | Required payload | Effect |
|---|---|---|
| inspect | empty object | Read-only identity, compatibility and status inspection |
| prepare | profileId and manifestDigest | Acquire ownership and prepare a verified runtime; USB work is allowed only after all preparation gates pass |
| renew | empty object | Renew the current owner's device-monotonic lease |
| startRoute | callGeneration | Start an owned route for the specified call generation; it does not change USB configuration |
| stopRoute | callGeneration | Stop only the specified owned route; it does not restore USB or hang up the network call |
| release | empty object | Stop owned media and restore only when ownership, boot, occupancy and last-applied values permit it |
| status | empty object | Read current phase, generation and recovery status |

prepare requires an approved profile and a fresh complete empty CLCC result
before a destructive USB transaction. inspect and status stay read-only.
startRoute requires a unique, fresh active voice call and a ready route.

## Response fields

A response echoes requestId and requestSequence and reports phase, generation,
outcome, errorCode, retryable, recoveryStatus and deviceMonotonicMs. Successful
responses use errorCode NONE. Rejected or failed operations preserve the
current device state and journal; an error does not imply that USB was
restored. recoveryStatus describes the actual recovery result, not the
requested action.

## Error catalog

| Code | Stage and meaning | Retryable | Recovery effect |
|---|---|---:|---|
| NONE | Operation completed or accepted | No | No error |
| MALFORMED_MESSAGE | Frame or JSON structure is invalid | No | No side effect |
| MESSAGE_TOO_LARGE | JSON body exceeds 65,536 bytes | No | No side effect |
| PROTOCOL_VERSION_UNSUPPORTED | Major protocol version is not supported | No | No side effect |
| REQUEST_ID_CONFLICT | A request ID was reused with different content | No | Preserve existing result |
| REQUEST_SEQUENCE_STALE | Sequence is old and its result is no longer retained | No | Return current state; never re-execute |
| OWNER_MISMATCH | Owner token does not own the session | No | No side effect |
| BOOT_MISMATCH | Request belongs to another boot instance | No | Invalidate the old session |
| GENERATION_MISMATCH | Expected generation is stale | No | No side effect |
| OPERATION_NOT_ALLOWED | Operation or payload field is outside the allowlist | No | No side effect |
| OPERATION_CONFLICT | Another coordinated operation owns the device | Yes | Wait or resolve the active operation |
| TRANSPORT_UNAVAILABLE | Selected control transport cannot be reached | Yes | Keep journal and report actual recovery status |
| DEVICE_NOT_FOUND | Selected physical device is absent | Yes | Do not fall back to another device |
| ROOT_UNAVAILABLE | Required device privilege is unavailable | No | Do not upload or load resources |
| UNSUPPORTED_PROFILE | Device build or ABI lacks an approved profile | No | Keep control-only operation available |
| PAYLOAD_INVALID | Resource manifest, digest, or operation data is invalid | No | Do not activate the payload |
| DEVICE_IDENTITY_AMBIGUOUS | Control/audio candidates cannot be uniquely associated | No | Require explicit device selection |
| DEVICE_BUSY_DATA | A fresh complete call list contains non-voice occupancy | Yes | Do not hang up or change USB |
| CALL_STATE_UNKNOWN | Call list is failed, incomplete, stale, or unavailable | Yes | Close uplink; do not treat as idle |
| CALIBRATION_FAILED | Required calibration did not complete | No | Preserve uncertain loaded kernel state; do not report ready |
| ROUTE_NOT_READY | Route ownership or readiness checks failed | Yes | Keep media stopped |
| USB_REENUM_TIMEOUT | Planned USB re-enumeration did not complete by its deadline | Yes | Keep journal and report recovery state |
| ENDPOINT_MISSING | A required audio endpoint is absent or unassociated | Yes | Keep media stopped |
| FORMAT_UNSUPPORTED | A selected endpoint cannot provide a supported shared-mode format | No | Keep media stopped and require a different endpoint or format |
| MIC_PERMISSION_DENIED | Host microphone permission is unavailable | No | Keep uplink closed; AT control remains available |
| MEDIA_STALLED | Required media frames stopped advancing for one second | Yes | Close uplink and clear the current media generation |
| RESTORE_CONFLICT | Current configuration differs from this owner's last-applied value | No | Preserve journal; never overwrite external changes |
| RESTORE_PENDING_CALL | Restore is deferred because occupancy is present or unknown | Yes | Stop media and retain the minimum recovery journal |
| RECOVERY_REQUIRED | Safe automatic recovery cannot be confirmed | No | Retain journal and require inspection or operator action |

## Idempotency and sequencing

The device stores a bounded history of request IDs, content digests, sequence
numbers and results. Repeating the same request ID with the same content
returns the original result or a verifiable current state; it never repeats a
USB write, load, or route start. Reusing an ID with different content returns
REQUEST_ID_CONFLICT. A request older than the retained history returns
REQUEST_SEQUENCE_STALE and is never executed again. Only the current owner
with matching bootId and expectedGeneration may renew or mutate a session.

## State transitions

### Module session

| Current phase | Event and guard | Next phase | Required result |
|---|---|---|---|
| disconnected | inspect | inspecting | Read-only probe |
| inspecting | Probe succeeds with no owned session | disconnected | Return inspection result |
| inspecting | Approved prepare begins and owner lock is acquired | staging | Persist original snapshot and intent before mutation |
| inspecting | Profile/identity/occupancy gate fails | disconnected | Return a structured error without mutation |
| staging | Resources verified and runtime becomes ready | runtime_ready | Report digests and owned processes |
| staging | Failure before device mutation | released | Report failure; no USB restore required |
| staging | Failure after device mutation | restoring or recovery_required | Converge locally or preserve journal |
| runtime_ready | Approved USB transaction starts | usb_reenumerating | Device supervisor owns the bounded transaction |
| usb_reenumerating | Identity and required endpoints are revalidated | standby_ready | USB is stable; no call media is running |
| usb_reenumerating | Deadline or identity check fails | restoring or recovery_required | Report actual rollback/recovery state |
| standby_ready | startRoute / stopRoute for valid call generation | standby_ready | Route state changes; USB fields do not |
| standby_ready | release with verified idle and ownership | restoring | Restore captured values exactly |
| standby_ready | release or lease expiry with occupancy/unknown | restore_pending_call | Stop media; retain minimum journal |
| restore_pending_call | Fresh independent idle proof and valid owner/boot | restoring | Resume bounded restore |
| restoring | Original values restored and read back | released | Return verified restore receipt |
| restoring | External change or restore failure | recovery_required | Preserve journal; do not overwrite or claim success |
| released | New inspect or prepare | inspecting | Start a new session generation |
| recovery_required | status or inspect | recovery_required | Read-only until recovery is verified |

An interrupted disable/write/enable transaction must converge locally while
the supervisor is alive. restore_pending_call is allowed only after the USB
configuration is in a consistent, connectable state; it must not retain an
unfinished disable. A supervisor killed mid-transaction leaves a journal for
the next connection and is not promised unattended recovery.

### Network call state

| Current state | Evidence | Next state | Media consequence |
|---|---|---|---|
| idle | User starts dial | dialing | Uplink remains closed |
| idle | Incoming-call indication | incoming | Uplink remains closed |
| dialing or incoming | Alerting/ringing evidence | alerting | Uplink remains closed |
| dialing, incoming or alerting | Fresh complete CLCC uniquely selects mode=0 active | active | May begin muted startup if runtime and endpoints are ready |
| any state | Fresh complete CLCC reports held or ambiguous multiple voice calls | held or unknown | Close uplink |
| any state | Query fails, is partial, or exceeds three-second freshness | unknown | Close uplink; never infer idle |
| active | Fresh complete CLCC proves target absent | idle | Stop route and clear media generation |
| any state | Boot/module identity changes or continuity cannot be proven | unknown/new generation | Do not resume old media |

AT command acceptance and URCs alone do not establish active or ended state.
Data-mode entries are occupancy, not voice calls.

### Media state

| Current state | Guard/event | Next state | Uplink |
|---|---|---|---|
| stopped | Unique fresh active voice call, ready route, endpoints and permission | starting_muted | Closed |
| starting_muted | Frames advance and call generation is revalidated | running | Open only if user is not muted |
| starting_muted | Call changes, permission fails, endpoint disappears, or timeout | failed or stopped | Closed; clear buffers |
| running | User mute | running | Closed immediately; clear queued capture |
| running | Call becomes held/unknown/stale or media stalls | failed or stopped | Closed; clear generation |
| failed | Bounded retry with fresh call confirmation succeeds | starting_muted | Closed during restart |
| any state | Call ends or generation changes | stopped | Closed; reject late callbacks |

## Examples

The following request is valid. The identifiers and digest are synthetic
examples; production tokens and identifiers must not be copied into logs or
fixtures.

    {
      "messageType": "request",
      "protocolVersion": "1.0",
      "requestId": "example-request-1",
      "requestSequence": 1,
      "bootId": "synthetic-boot-id",
      "sessionId": null,
      "ownerToken": "synthetic-owner-token",
      "expectedGeneration": 0,
      "operation": "prepare",
      "payload": {
        "profileId": "candidate-profile",
        "manifestDigest": "0000000000000000000000000000000000000000000000000000000000000000"
      }
    }

The following are rejected before any operation:

- A request without requestId or requestSequence: MALFORMED_MESSAGE.
- A request with operation set to shell or an extra command field:
  OPERATION_NOT_ALLOWED.
- A prepare request without profileId or a 64-hex manifestDigest:
  PAYLOAD_INVALID.
- A body of 65,537 bytes: MESSAGE_TOO_LARGE, rejected before body allocation.
- A request with protocolVersion 2.0: PROTOCOL_VERSION_UNSUPPORTED, no
  mutation and no attempt to interpret the v2 payload.

