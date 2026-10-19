#include "protocol.h"
#include "supervisor.h"

#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>

static bool write_rejection(const char *request_id, uint64_t sequence, const char *error_code) {
    char response[1024];
    const int length = snprintf(response, sizeof(response),
        "{\"messageType\":\"response\",\"protocolVersion\":\"1.0\",\"requestId\":\"%s\","
        "\"requestSequence\":%" PRIu64 ",\"phase\":\"disconnected\",\"generation\":0,"
        "\"outcome\":\"rejected\",\"errorCode\":\"%s\",\"retryable\":false,"
        "\"recoveryStatus\":\"none\",\"deviceMonotonicMs\":0}", request_id, sequence, error_code);
    return length > 0 && (size_t)length < sizeof(response) && cp_write_frame(stdout, response, (size_t)length);
}

static bool write_result(const cp_request *request, const char *phase, uint64_t generation,
                         const char *error_code, bool retryable, const char *recovery_status) {
    char response[1024];
    const bool success = error_code != NULL && strcmp(error_code, "NONE") == 0;
    const int length = snprintf(response, sizeof(response),
        "{\"messageType\":\"response\",\"protocolVersion\":\"1.0\",\"requestId\":\"%s\","
        "\"requestSequence\":%" PRIu64 ",\"phase\":\"%s\",\"generation\":%" PRIu64 ","
        "\"outcome\":\"%s\",\"errorCode\":\"%s\",\"retryable\":%s,"
        "\"recoveryStatus\":\"%s\",\"deviceMonotonicMs\":0}",
        request->request_id, request->request_sequence, phase, generation,
        success ? "completed" : "rejected", error_code, retryable ? "true" : "false", recovery_status);
    return length > 0 && (size_t)length < sizeof(response) && cp_write_frame(stdout, response, (size_t)length);
}

int main(void) {
    cp_owner_state owner;
    cp_owner_init(&owner, 0u);
    uint64_t clock_ms = 1u;
    for (;;) {
        uint8_t *body = NULL;
        size_t length = 0u;
        const cp_error frame_error = cp_read_frame(stdin, &body, &length);
        if (frame_error != CP_ERROR_NONE) {
            if (feof(stdin)) return 0;
            (void)write_rejection("unknown", 0u, cp_error_name(frame_error));
            return 2;
        }

        cp_request request;
        const cp_error parse_error = cp_decode_request(body, length, &request);
        free(body);
        if (parse_error != CP_ERROR_NONE) {
            (void)write_rejection("unknown", 0u, cp_error_name(parse_error));
            continue;
        }

        const uint64_t generation = owner.generation;
        const char *phase = owner.locked ? "standby_ready" : "disconnected";
        cp_error result = CP_ERROR_NONE;
        switch (request.operation) {
        case CP_OPERATION_INSPECT:
        case CP_OPERATION_STATUS:
            break;
        case CP_OPERATION_PREPARE:
            /* This profile exists only in the host simulator and has no device or USB side effects. */
            if (strcmp(request.profile_id, "simulator-only") != 0) {
                result = CP_ERROR_UNSUPPORTED_PROFILE;
            } else {
                result = cp_owner_prepare(&owner, request.boot_id,
                    request.has_session_id ? request.session_id : "",
                    request.has_owner_token ? request.owner_token : "", true,
                    CP_OCCUPANCY_EMPTY, clock_ms++);
                if (result == CP_ERROR_NONE) {
                    (void)cp_owner_mark_runtime_ready(&owner,
                        request.has_owner_token ? request.owner_token : "", clock_ms++);
                    phase = "standby_ready";
                }
            }
            break;
        case CP_OPERATION_RENEW:
            result = cp_owner_renew(&owner, request.boot_id,
                request.has_session_id ? request.session_id : "",
                request.has_owner_token ? request.owner_token : "",
                request.expected_generation, clock_ms++);
            break;
        case CP_OPERATION_START_ROUTE:
        case CP_OPERATION_STOP_ROUTE:
            if (!owner.locked || strcmp(owner.boot_id, request.boot_id) != 0 ||
                strcmp(owner.session_id, request.session_id) != 0 ||
                strcmp(owner.owner_token, request.owner_token) != 0) result = CP_ERROR_OWNER_MISMATCH;
            else if (owner.generation != request.expected_generation) result = CP_ERROR_GENERATION_MISMATCH;
            break;
        case CP_OPERATION_RELEASE:
            result = cp_owner_release(&owner, request.boot_id,
                request.has_session_id ? request.session_id : "",
                request.has_owner_token ? request.owner_token : "",
                request.expected_generation, CP_OCCUPANCY_EMPTY, true, clock_ms++);
            phase = owner.locked ? "recovery_required" : "released";
            break;
        default:
            result = CP_ERROR_OPERATION_NOT_ALLOWED;
            break;
        }

        const char *error = cp_error_name(result);
        if (request.operation == CP_OPERATION_RELEASE && result == CP_ERROR_RESTORE_PENDING_CALL)
            phase = "restore_pending_call";
        if (!write_result(&request, phase, owner.generation, error,
                          result == CP_ERROR_OPERATION_CONFLICT || result == CP_ERROR_RESTORE_PENDING_CALL,
                          result == CP_ERROR_NONE && request.operation == CP_OPERATION_RELEASE
                              ? "restored" : "none")) return 3;
    }
}
