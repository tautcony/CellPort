#ifndef CELLPORT_AUDIO_PROTOCOL_H
#define CELLPORT_AUDIO_PROTOCOL_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

#define CP_MAX_MESSAGE_BYTES 65536u
#define CP_MAX_IDENTIFIER_BYTES 128u
#define CP_MAX_PROFILE_BYTES 128u
#define CP_OWNER_TOKEN_MAX_BYTES 256u

typedef enum cp_error {
    CP_ERROR_NONE = 0,
    CP_ERROR_MALFORMED_MESSAGE,
    CP_ERROR_MESSAGE_TOO_LARGE,
    CP_ERROR_PROTOCOL_VERSION_UNSUPPORTED,
    CP_ERROR_OPERATION_NOT_ALLOWED,
    CP_ERROR_PAYLOAD_INVALID,
    CP_ERROR_REQUEST_ID_CONFLICT,
    CP_ERROR_REQUEST_SEQUENCE_STALE,
    CP_ERROR_OWNER_MISMATCH,
    CP_ERROR_BOOT_MISMATCH,
    CP_ERROR_GENERATION_MISMATCH,
    CP_ERROR_OPERATION_CONFLICT,
    CP_ERROR_ROOT_UNAVAILABLE,
    CP_ERROR_UNSUPPORTED_PROFILE,
    CP_ERROR_DEVICE_BUSY_DATA,
    CP_ERROR_CALL_STATE_UNKNOWN,
    CP_ERROR_RESTORE_CONFLICT,
    CP_ERROR_RESTORE_PENDING_CALL,
    CP_ERROR_RECOVERY_REQUIRED
} cp_error;

typedef enum cp_operation {
    CP_OPERATION_INSPECT = 0,
    CP_OPERATION_PREPARE,
    CP_OPERATION_RENEW,
    CP_OPERATION_START_ROUTE,
    CP_OPERATION_STOP_ROUTE,
    CP_OPERATION_RELEASE,
    CP_OPERATION_STATUS
} cp_operation;

typedef struct cp_request {
    char request_id[CP_MAX_IDENTIFIER_BYTES + 1u];
    uint64_t request_sequence;
    uint64_t expected_generation;
    char boot_id[CP_MAX_IDENTIFIER_BYTES + 1u];
    char session_id[CP_MAX_IDENTIFIER_BYTES + 1u];
    bool has_session_id;
    char owner_token[257];
    bool has_owner_token;
    cp_operation operation;
    char profile_id[CP_MAX_PROFILE_BYTES + 1u];
    char manifest_digest[65];
    char call_generation[CP_MAX_IDENTIFIER_BYTES + 1u];
} cp_request;

typedef struct cp_response {
    char request_id[CP_MAX_IDENTIFIER_BYTES + 1u];
    uint64_t request_sequence;
    uint64_t generation;
    uint64_t device_monotonic_ms;
    const char *phase;
    const char *outcome;
    const char *error_code;
    bool retryable;
    const char *recovery_status;
} cp_response;

cp_error cp_decode_request(const uint8_t *body, size_t length, cp_request *out);
cp_error cp_read_frame(FILE *input, uint8_t **body, size_t *length);
bool cp_write_frame(FILE *output, const char *body, size_t length);
const char *cp_error_name(cp_error error);
const char *cp_operation_name(cp_operation operation);

#endif
