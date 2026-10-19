#include "protocol.h"
#include "runtime.h"
#include "sha256.h"
#include "supervisor.h"

#include <inttypes.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#include <direct.h>
#include <io.h>
#include <process.h>
#define get_process_id _getpid
#else
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#define get_process_id getpid
#endif

static int failures;
static unsigned assertions;
static unsigned test_directory_counter;

#define CHECK(expression) do { \
    ++assertions; \
    if (!(expression)) { \
        ++failures; \
        fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #expression); \
    } \
} while (0)

static const char *valid_prepare =
    "{\"messageType\":\"request\",\"protocolVersion\":\"1.0\",\"requestId\":\"req-1\","
    "\"requestSequence\":1,\"bootId\":\"boot-1\",\"sessionId\":\"session-1\","
    "\"ownerToken\":\"owner-1\",\"expectedGeneration\":0,\"operation\":\"prepare\","
    "\"payload\":{\"profileId\":\"candidate\",\"manifestDigest\":"
    "\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}}";

static void test_protocol(void) {
    cp_request request;
    CHECK(cp_decode_request((const uint8_t *)valid_prepare, strlen(valid_prepare), &request) == CP_ERROR_NONE);
    CHECK(strcmp(request.request_id, "req-1") == 0);
    CHECK(request.operation == CP_OPERATION_PREPARE);
    CHECK(strcmp(request.profile_id, "candidate") == 0);
    CHECK(strcmp(request.manifest_digest, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") == 0);

    const char *unknown_major =
        "{\"messageType\":\"request\",\"protocolVersion\":\"2.0\",\"requestId\":\"req-1\","
        "\"requestSequence\":1,\"bootId\":\"boot-1\",\"sessionId\":null,\"ownerToken\":null,"
        "\"expectedGeneration\":0,\"operation\":\"inspect\",\"payload\":{}}";
    CHECK(cp_decode_request((const uint8_t *)unknown_major, strlen(unknown_major), &request) ==
          CP_ERROR_PROTOCOL_VERSION_UNSUPPORTED);

    const char *shell_operation =
        "{\"messageType\":\"request\",\"protocolVersion\":\"1.0\",\"requestId\":\"req-1\","
        "\"requestSequence\":1,\"bootId\":\"boot-1\",\"sessionId\":null,\"ownerToken\":null,"
        "\"expectedGeneration\":0,\"operation\":\"shell\",\"payload\":{}}";
    CHECK(cp_decode_request((const uint8_t *)shell_operation, strlen(shell_operation), &request) ==
          CP_ERROR_OPERATION_NOT_ALLOWED);

    const char *arbitrary_payload =
        "{\"messageType\":\"request\",\"protocolVersion\":\"1.0\",\"requestId\":\"req-1\","
        "\"requestSequence\":1,\"bootId\":\"boot-1\",\"sessionId\":null,\"ownerToken\":null,"
        "\"expectedGeneration\":0,\"operation\":\"inspect\",\"command\":\"id\",\"payload\":{}}";
    CHECK(cp_decode_request((const uint8_t *)arbitrary_payload, strlen(arbitrary_payload), &request) ==
          CP_ERROR_OPERATION_NOT_ALLOWED);

    const char *duplicate =
        "{\"messageType\":\"request\",\"protocolVersion\":\"1.0\",\"requestId\":\"req-1\","
        "\"requestId\":\"req-2\",\"requestSequence\":1,\"bootId\":\"boot-1\","
        "\"sessionId\":null,\"ownerToken\":null,\"expectedGeneration\":0,\"operation\":\"inspect\",\"payload\":{}}";
    CHECK(cp_decode_request((const uint8_t *)duplicate, strlen(duplicate), &request) == CP_ERROR_MALFORMED_MESSAGE);

    const char *escaped_duplicate =
        "{\"messageType\":\"request\",\"protocolVersion\":\"1.0\",\"requestId\":\"req-1\","
        "\"request\\u0049d\":\"req-2\",\"requestSequence\":1,\"bootId\":\"boot-1\","
        "\"sessionId\":null,\"ownerToken\":null,\"expectedGeneration\":0,\"operation\":\"inspect\",\"payload\":{}}";
    CHECK(cp_decode_request((const uint8_t *)escaped_duplicate, strlen(escaped_duplicate), &request) ==
          CP_ERROR_MALFORMED_MESSAGE);

    char trailing[1024];
    (void)snprintf(trailing, sizeof(trailing), "%s {}", valid_prepare);
    CHECK(cp_decode_request((const uint8_t *)trailing, strlen(trailing), &request) == CP_ERROR_MALFORMED_MESSAGE);
    const char *bad_surrogate = "{\"x\":\"\\uD800\"}";
    CHECK(cp_decode_request((const uint8_t *)bad_surrogate, strlen(bad_surrogate), &request) ==
          CP_ERROR_MALFORMED_MESSAGE);

    uint8_t *too_large = (uint8_t *)malloc(CP_MAX_MESSAGE_BYTES + 1u);
    CHECK(too_large != NULL);
    if (too_large != NULL) {
        memset(too_large, ' ', CP_MAX_MESSAGE_BYTES + 1u);
        CHECK(cp_decode_request(too_large, CP_MAX_MESSAGE_BYTES + 1u, &request) == CP_ERROR_MESSAGE_TOO_LARGE);
        free(too_large);
    }

    FILE *frame_file = tmpfile();
    CHECK(frame_file != NULL);
    if (frame_file != NULL) {
        const size_t body_length = strlen(valid_prepare);
        const uint8_t header[4] = {(uint8_t)(body_length >> 24u), (uint8_t)(body_length >> 16u),
                                   (uint8_t)(body_length >> 8u), (uint8_t)body_length};
        CHECK(fwrite(header, 1u, sizeof(header), frame_file) == sizeof(header));
        CHECK(fwrite(valid_prepare, 1u, body_length, frame_file) == body_length);
        rewind(frame_file);
        uint8_t *framed_body = NULL;
        size_t framed_length = 0u;
        CHECK(cp_read_frame(frame_file, &framed_body, &framed_length) == CP_ERROR_NONE);
        CHECK(framed_length == body_length && memcmp(framed_body, valid_prepare, body_length) == 0);
        free(framed_body);
        fclose(frame_file);
    }

    frame_file = tmpfile();
    CHECK(frame_file != NULL);
    if (frame_file != NULL) {
        const uint8_t oversized_header[4] = {0u, 1u, 0u, 1u};
        CHECK(fwrite(oversized_header, 1u, sizeof(oversized_header), frame_file) == sizeof(oversized_header));
        rewind(frame_file);
        uint8_t *framed_body = NULL;
        size_t framed_length = 0u;
        CHECK(cp_read_frame(frame_file, &framed_body, &framed_length) == CP_ERROR_MESSAGE_TOO_LARGE);
        CHECK(framed_body == NULL && framed_length == 0u);
        fclose(frame_file);
    }

    frame_file = tmpfile();
    CHECK(frame_file != NULL);
    if (frame_file != NULL) {
        const uint8_t truncated_frame[6] = {0u, 0u, 0u, 4u, '{', '}'};
        CHECK(fwrite(truncated_frame, 1u, sizeof(truncated_frame), frame_file) == sizeof(truncated_frame));
        rewind(frame_file);
        uint8_t *framed_body = NULL;
        size_t framed_length = 0u;
        CHECK(cp_read_frame(frame_file, &framed_body, &framed_length) == CP_ERROR_MALFORMED_MESSAGE);
        CHECK(framed_body == NULL);
        fclose(frame_file);
    }
}

static void test_sha256(void) {
    char digest[65];
    cp_sha256_hex((const uint8_t *)"abc", 3u, digest);
    CHECK(strcmp(digest, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad") == 0);
    cp_sha256_hex((const uint8_t *)"", 0u, digest);
    CHECK(strcmp(digest, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855") == 0);
    char block_boundary[57];
    memset(block_boundary, 'a', 56u);
    block_boundary[56] = '\0';
    cp_sha256_hex((const uint8_t *)block_boundary, 56u, digest);
    CHECK(strcmp(digest, "b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a") == 0);
}

static void test_ownership_and_process_identity(void) {
    cp_owner_state owner;
    cp_owner_init(&owner, 7u);
    CHECK(cp_owner_prepare(&owner, "boot-a", "session-a", "secret-a", false, CP_OCCUPANCY_EMPTY, 100u) ==
          CP_ERROR_UNSUPPORTED_PROFILE);
    CHECK(!owner.locked && owner.generation == 7u);
    CHECK(cp_owner_prepare(&owner, "boot-a", "session-a", "secret-a", true, CP_OCCUPANCY_DATA, 100u) ==
          CP_ERROR_DEVICE_BUSY_DATA);
    CHECK(!owner.locked);
    CHECK(cp_owner_prepare(&owner, "boot-a", "session-a", "secret-a", true, CP_OCCUPANCY_UNKNOWN, 100u) ==
          CP_ERROR_CALL_STATE_UNKNOWN);
    CHECK(!owner.locked);
    CHECK(cp_owner_prepare(&owner, "boot-a", "session-a", "secret-a", true, CP_OCCUPANCY_EMPTY, 100u) ==
          CP_ERROR_NONE);
    CHECK(owner.locked && owner.generation == 8u && owner.lease_deadline_ms == 45100u);
    CHECK(cp_owner_prepare(&owner, "boot-a", "session-b", "secret-b", true, CP_OCCUPANCY_EMPTY, 101u) ==
          CP_ERROR_OPERATION_CONFLICT);
    CHECK(cp_owner_renew(&owner, "boot-old", "session-a", "secret-a", 8u, 200u) == CP_ERROR_BOOT_MISMATCH);
    CHECK(cp_owner_renew(&owner, "boot-a", "session-a", "wrong", 8u, 200u) == CP_ERROR_OWNER_MISMATCH);
    CHECK(cp_owner_extend_prepare_grace(&owner, "secret-a", 200u));
    CHECK(owner.prepare_grace_used && owner.lease_deadline_ms == 120100u);
    CHECK(!cp_owner_extend_prepare_grace(&owner, "secret-a", 201u));
    CHECK(cp_owner_renew(&owner, "boot-a", "session-a", "secret-a", 8u, 100000u) == CP_ERROR_NONE);
    CHECK(owner.lease_deadline_ms == 120100u);
    CHECK(cp_owner_expire(&owner, CP_OCCUPANCY_UNKNOWN, 120100u) == CP_ERROR_RESTORE_PENDING_CALL);
    CHECK(owner.locked && owner.restore_pending_call && strcmp(owner.phase, "restore_pending_call") == 0);
    CHECK(cp_owner_release(&owner, "boot-a", "session-a", "secret-a", 8u,
                           CP_OCCUPANCY_EMPTY, false, 120101u) == CP_ERROR_RESTORE_CONFLICT);
    CHECK(owner.locked && strcmp(owner.phase, "recovery_required") == 0);
    CHECK(cp_owner_release(&owner, "boot-a", "session-a", "secret-a", 8u,
                           CP_OCCUPANCY_EMPTY, true, 120102u) == CP_ERROR_NONE);
    CHECK(!owner.locked && owner.generation == 9u);

    cp_owner_state ready_owner;
    cp_owner_init(&ready_owner, 0u);
    CHECK(cp_owner_prepare(&ready_owner, "boot-b", "session-b", "secret-b", true,
                           CP_OCCUPANCY_EMPTY, 200000u) == CP_ERROR_NONE);
    CHECK(cp_owner_mark_runtime_ready(&ready_owner, "secret-b", 200100u));
    CHECK(strcmp(ready_owner.phase, "runtime_ready") == 0);
    CHECK(cp_owner_renew(&ready_owner, "boot-b", "session-b", "secret-b", 1u, 240000u) == CP_ERROR_NONE);
    CHECK(ready_owner.lease_deadline_ms == 285000u);

    CHECK(cp_process_identity_matches(55u, 1000u, "/system/bin/service", "service --audio",
          "boot-a", "owner-a", 55u, 1000u, "/system/bin/service", "service --audio", "boot-a", "owner-a"));
    CHECK(!cp_process_identity_matches(55u, 1000u, "/system/bin/service", "service --audio",
          "boot-a", "owner-a", 55u, 1001u, "/system/bin/service", "service --audio", "boot-a", "owner-a"));
    CHECK(!cp_process_identity_matches(55u, 1000u, "/system/bin/service", "service --audio",
          "boot-a", "owner-a", 55u, 1000u, "/system/bin/calibration", "calibration", "boot-a", "owner-a"));

    uint64_t start_time = 0u;
    const char *stat_line = "123 (daemon name with (parens)) S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 98765 21 22";
    CHECK(cp_proc_stat_start_time(stat_line, &start_time));
    CHECK(start_time == 98765u);
    CHECK(!cp_proc_stat_start_time("123 bad fields", &start_time));
}

static void make_test_directory(char *directory, size_t capacity) {
    const unsigned long process_id = (unsigned long)get_process_id();
    for (unsigned attempt = 0u; attempt < 100u; ++attempt) {
        ++test_directory_counter;
        (void)snprintf(directory, capacity, "audio-supervisor-test-%lu-%u", process_id, test_directory_counter);
#ifdef _WIN32
        if (_mkdir(directory) == 0) return;
#else
        if (mkdir(directory, 0700) == 0) return;
#endif
        if (errno != EEXIST) break;
    }
    fprintf(stderr, "Could not allocate a unique supervisor test directory.\n");
    exit(EXIT_FAILURE);
}

static void remove_test_directory(const char *directory) {
    char path[512];
    (void)snprintf(path, sizeof(path), "%s/original.snapshot", directory); (void)remove(path);
    (void)snprintf(path, sizeof(path), "%s/intent.json", directory); (void)remove(path);
    (void)snprintf(path, sizeof(path), "%s/last-applied.json", directory); (void)remove(path);
    (void)snprintf(path, sizeof(path), "%s/intent.json.part", directory); (void)remove(path);
    (void)snprintf(path, sizeof(path), "%s/original.snapshot.part", directory); (void)remove(path);
#ifdef _WIN32
    (void)_rmdir(directory);
#else
    (void)rmdir(directory);
#endif
}

static void test_journal(void) {
    char directory[256];
    make_test_directory(directory, sizeof(directory));
    CHECK(cp_journal_begin(directory, "{\"functions\":\"rndis,acm\",\"enable\":\"1\"}",
                           "{\"stage\":\"prepare-intent\"}") == CP_JOURNAL_OK);
    char snapshot[256];
    char intent[256];
    CHECK(cp_journal_read(directory, "original.snapshot", snapshot, sizeof(snapshot)));
    CHECK(strcmp(snapshot, "{\"functions\":\"rndis,acm\",\"enable\":\"1\"}") == 0);
    CHECK(cp_journal_read(directory, "intent.json", intent, sizeof(intent)));
    CHECK(strcmp(intent, "{\"stage\":\"prepare-intent\"}") == 0);

    char partial_path[512];
    (void)snprintf(partial_path, sizeof(partial_path), "%s/intent.json.part", directory);
    FILE *partial = fopen(partial_path, "wb");
    CHECK(partial != NULL);
    if (partial != NULL) {
        CHECK(fwrite("partial crash write", 1u, 20u, partial) == 20u);
        fclose(partial);
    }
    CHECK(cp_journal_read(directory, "intent.json", intent, sizeof(intent)));
    CHECK(strcmp(intent, "{\"stage\":\"prepare-intent\"}") == 0);

    CHECK(cp_journal_write_intent(directory, "{\"stage\":\"usb-write-intent\"}") == CP_JOURNAL_OK);
    CHECK(cp_journal_write_last_applied(directory, "{\"enable\":\"0\"}") == CP_JOURNAL_OK);
    CHECK(cp_journal_read(directory, "original.snapshot", snapshot, sizeof(snapshot)));
    CHECK(strcmp(snapshot, "{\"functions\":\"rndis,acm\",\"enable\":\"1\"}") == 0);
    CHECK(cp_journal_begin(directory, "{\"enable\":\"0\"}", "{}") == CP_JOURNAL_ALREADY_EXISTS);
    CHECK(cp_journal_read(directory, "original.snapshot", snapshot, sizeof(snapshot)));
    CHECK(strcmp(snapshot, "{\"functions\":\"rndis,acm\",\"enable\":\"1\"}") == 0);
    remove_test_directory(directory);
}

typedef struct fake_usb_device {
    cp_usb_values values;
    unsigned writes;
    bool reachable;
} fake_usb_device;

static bool fake_usb_read(void *context, cp_usb_field field, char *value, size_t capacity) {
    fake_usb_device *device = (fake_usb_device *)context;
    if (!device->reachable) return false;
    const char *source = device->values.value[field];
    const int result = snprintf(value, capacity, "%s", source);
    return result >= 0 && (size_t)result < capacity;
}

static bool fake_usb_write(void *context, cp_usb_field field, const char *value) {
    fake_usb_device *device = (fake_usb_device *)context;
    if (field < CP_USB_FIELD_FUNCTIONS || field >= CP_USB_FIELD_COUNT || value == NULL ||
        strlen(value) >= CP_USB_VALUE_BYTES) return false;
    (void)snprintf(device->values.value[field], CP_USB_VALUE_BYTES, "%s", value);
    ++device->writes;
    return true;
}

static void fake_usb_init(fake_usb_device *device, const char *functions, const char *audio_enable,
                          const char *enable) {
    memset(device, 0, sizeof(*device));
    device->reachable = true;
    (void)snprintf(device->values.value[CP_USB_FIELD_FUNCTIONS], CP_USB_VALUE_BYTES, "%s", functions);
    (void)snprintf(device->values.value[CP_USB_FIELD_AUDIO_ENABLE], CP_USB_VALUE_BYTES, "%s", audio_enable);
    (void)snprintf(device->values.value[CP_USB_FIELD_ENABLE], CP_USB_VALUE_BYTES, "%s", enable);
}

static void test_usb_transaction_exact_restore(void) {
    const cp_usb_profile profile = {
        "qdc507-candidate", "diag,serial,rmnet,ffs,audio_enable", "1", "1", true
    };
    char directory[256];
    make_test_directory(directory, sizeof(directory));

    fake_usb_device device;
    fake_usb_init(&device, "diag,serial,rmnet,ffs", "0", "1");
    cp_usb_transaction transaction = {0};
    CHECK(cp_usb_transaction_prepare(&transaction, &profile, "boot-a", directory,
                                    fake_usb_read, fake_usb_write, &device) == CP_ERROR_NONE);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_FUNCTIONS], profile.functions) == 0);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_AUDIO_ENABLE], "1") == 0);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_ENABLE], "1") == 0);
    device.reachable = false;
    CHECK(cp_usb_transaction_release(&transaction, "boot-a", fake_usb_read, fake_usb_write, &device) ==
          CP_ERROR_RECOVERY_REQUIRED);
    CHECK(transaction.active && !transaction.restored);
    device.reachable = true;
    CHECK(cp_usb_transaction_release(&transaction, "boot-a", fake_usb_read, fake_usb_write, &device) ==
          CP_ERROR_NONE);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_FUNCTIONS], "diag,serial,rmnet,ffs") == 0);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_AUDIO_ENABLE], "0") == 0);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_ENABLE], "1") == 0);
    const unsigned writes_after_restore = device.writes;
    CHECK(cp_usb_transaction_release(&transaction, "boot-a", fake_usb_read, fake_usb_write, &device) ==
          CP_ERROR_NONE);
    CHECK(device.writes == writes_after_restore);
    remove_test_directory(directory);

    make_test_directory(directory, sizeof(directory));
    fake_usb_init(&device, "diag,serial,audio_enable", "1", "0");
    memset(&transaction, 0, sizeof(transaction));
    CHECK(cp_usb_transaction_prepare(&transaction, &profile, "boot-b", directory,
                                    fake_usb_read, fake_usb_write, &device) == CP_ERROR_NONE);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_ENABLE], "1") == 0);
    CHECK(cp_usb_transaction_release(&transaction, "boot-b", fake_usb_read, fake_usb_write, &device) ==
          CP_ERROR_NONE);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_FUNCTIONS], "diag,serial,audio_enable") == 0);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_AUDIO_ENABLE], "1") == 0);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_ENABLE], "0") == 0);
    remove_test_directory(directory);
}

static void test_usb_transaction_conflict_boot_and_profile_guards(void) {
    const cp_usb_profile profile = {
        "qdc507-candidate", "diag,serial,rmnet,ffs,audio_enable", "1", "1", true
    };
    char directory[256];
    make_test_directory(directory, sizeof(directory));
    fake_usb_device device;
    fake_usb_init(&device, "diag,serial,rmnet,ffs", "0", "1");
    cp_usb_transaction transaction = {0};
    CHECK(cp_usb_transaction_prepare(&transaction, &profile, "boot-c", directory,
                                    fake_usb_read, fake_usb_write, &device) == CP_ERROR_NONE);

    const unsigned writes_before_boot_mismatch = device.writes;
    CHECK(cp_usb_transaction_release(&transaction, "boot-replaced", fake_usb_read, fake_usb_write, &device) ==
          CP_ERROR_BOOT_MISMATCH);
    CHECK(device.writes == writes_before_boot_mismatch);

    (void)snprintf(device.values.value[CP_USB_FIELD_FUNCTIONS], CP_USB_VALUE_BYTES,
                   "diag,serial,rmnet,ffs,external");
    const unsigned writes_before_conflict = device.writes;
    CHECK(cp_usb_transaction_release(&transaction, "boot-c", fake_usb_read, fake_usb_write, &device) ==
          CP_ERROR_RESTORE_CONFLICT);
    CHECK(device.writes == writes_before_conflict);
    CHECK(strcmp(device.values.value[CP_USB_FIELD_FUNCTIONS], "diag,serial,rmnet,ffs,external") == 0);
    remove_test_directory(directory);

    make_test_directory(directory, sizeof(directory));
    fake_usb_init(&device, "diag,serial,rmnet,ffs", "0", "1");
    cp_usb_transaction unapproved = {0};
    const cp_usb_profile rejected = {
        "qdc507-candidate", "diag,serial,rmnet,ffs,audio_enable", "1", "1", false
    };
    CHECK(cp_usb_transaction_prepare(&unapproved, &rejected, "boot-d", directory,
                                    fake_usb_read, fake_usb_write, &device) == CP_ERROR_UNSUPPORTED_PROFILE);
    CHECK(device.writes == 0u);
    const cp_usb_profile invalid = {
        "qdc507-candidate", "diag,serial,rmnet,ffs,arbitrary", "1", "1", true
    };
    CHECK(cp_usb_transaction_prepare(&unapproved, &invalid, "boot-d", directory,
                                    fake_usb_read, fake_usb_write, &device) == CP_ERROR_PAYLOAD_INVALID);
    CHECK(device.writes == 0u);
    remove_test_directory(directory);
}

static void test_reconnect_order_deadlines_and_call_race(void) {
    cp_reconnect_tracker reconnect;
    cp_reconnect_init(&reconnect, 100u, 500u, 1u);
    CHECK(cp_reconnect_begin(&reconnect, 10u));
    CHECK(reconnect.phase == CP_RECONNECT_WAIT_AT && reconnect.deadline_ms == 110u);
    CHECK(cp_reconnect_observe_at(&reconnect, 40u));
    CHECK(reconnect.phase == CP_RECONNECT_WAIT_UAC && reconnect.deadline_ms == 540u);
    CHECK(cp_reconnect_observe_uac(&reconnect, 80u));
    CHECK(reconnect.phase == CP_RECONNECT_COMPLETE);
    CHECK(!cp_reconnect_begin(&reconnect, 90u));

    cp_reconnect_init(&reconnect, 100u, 500u, 1u);
    CHECK(cp_reconnect_begin(&reconnect, 0u));
    CHECK(cp_reconnect_tick(&reconnect, 100u) == CP_RECONNECT_WAIT_AT);
    CHECK(reconnect.retries == 1u && reconnect.deadline_ms == 200u);
    CHECK(cp_reconnect_tick(&reconnect, 200u) == CP_RECONNECT_LOCAL_CONVERGENCE);
    CHECK(reconnect.deadline_ms == 0u);
    cp_reconnect_convergence_complete(&reconnect, true, false);
    CHECK(reconnect.phase == CP_RECONNECT_RESTORE_PENDING);

    cp_reconnect_init(&reconnect, 100u, 500u, 2u);
    CHECK(cp_reconnect_begin(&reconnect, 1000u));
    CHECK(cp_reconnect_observe_at(&reconnect, 1050u));
    cp_reconnect_call_arrived(&reconnect);
    CHECK(reconnect.phase == CP_RECONNECT_LOCAL_CONVERGENCE);
    CHECK(reconnect.call_arrived_during_commit && reconnect.deadline_ms == 0u);
    cp_reconnect_convergence_complete(&reconnect, true, false);
    CHECK(reconnect.phase == CP_RECONNECT_RESTORE_PENDING);
    CHECK(cp_reconnect_tick(&reconnect, UINT64_MAX) == CP_RECONNECT_RESTORE_PENDING);
}

static void test_request_history(void) {
    cp_request_history history;
    cp_request_history_init(&history);
    cp_error cached = CP_ERROR_NONE;
    CHECK(cp_request_history_check(&history, "req-1", (const uint8_t *)"prepare:a", 9u, 1u, &cached) == CP_DEDUP_NEW);
    cp_request_history_record(&history, "req-1", (const uint8_t *)"prepare:a", 9u, 1u, CP_ERROR_NONE);
    CHECK(cp_request_history_check(&history, "req-1", (const uint8_t *)"prepare:a", 9u, 1u, &cached) == CP_DEDUP_REPLAY);
    CHECK(cached == CP_ERROR_NONE);
    CHECK(cp_request_history_check(&history, "req-1", (const uint8_t *)"prepare:b", 9u, 1u, &cached) ==
          CP_DEDUP_ID_CONFLICT);
    CHECK(cp_request_history_check(&history, "req-1", (const uint8_t *)"prepare:a", 9u, 2u, &cached) ==
          CP_DEDUP_ID_CONFLICT);
    for (uint64_t sequence = 2u; sequence <= CP_REQUEST_HISTORY_CAPACITY + 1u; ++sequence) {
        char id[32];
        char body[32];
        (void)snprintf(id, sizeof(id), "req-%" PRIu64, sequence);
        (void)snprintf(body, sizeof(body), "status:%" PRIu64, sequence);
        cp_request_history_record(&history, id, (const uint8_t *)body, strlen(body), sequence, CP_ERROR_NONE);
    }
    CHECK(cp_request_history_check(&history, "req-1", (const uint8_t *)"prepare:a", 9u, 1u, &cached) ==
          CP_DEDUP_SEQUENCE_STALE);
    CHECK(cp_request_history_check(&history, "req-99", (const uint8_t *)"status", 6u, 99u, &cached) == CP_DEDUP_NEW);
}

static void test_global_lock(void) {
    char path[256];
    (void)snprintf(path, sizeof(path), "audio-supervisor-lock-%lu", (unsigned long)get_process_id());
    (void)remove(path);
    cp_file_lock first = {0};
    cp_file_lock second = {0};
    CHECK(cp_global_lock_acquire(&first, path));
    CHECK(!cp_global_lock_acquire(&second, path));
    cp_global_lock_release(&first);
    CHECK(cp_global_lock_acquire(&second, path));
    cp_global_lock_release(&second);
    (void)remove(path);
}

static void test_detached_lifecycle_and_deadlines(void) {
    cp_lifecycle_model lifecycle;
    cp_lifecycle_init(&lifecycle, true);
    CHECK(lifecycle.process_running && lifecycle.control_connected && lifecycle.journal_pending &&
          lifecycle.recovery_required);
    cp_lifecycle_mark_ready(&lifecycle);
    CHECK(lifecycle.ready);
    cp_lifecycle_control_disconnected(&lifecycle);
    CHECK(lifecycle.process_running && lifecycle.ready && !lifecycle.control_connected);
    cp_lifecycle_reconnect(&lifecycle);
    CHECK(lifecycle.process_running && lifecycle.control_connected && lifecycle.recovery_required);
    cp_lifecycle_process_killed(&lifecycle);
    CHECK(!lifecycle.process_running && lifecycle.journal_pending && lifecycle.recovery_required);
    cp_lifecycle_reconnect(&lifecycle);
    CHECK(lifecycle.process_running && !lifecycle.ready && lifecycle.control_connected &&
          lifecycle.recovery_required && lifecycle.journal_pending);
    CHECK(cp_deadline_remaining_ms(100u, 200u) == 100u);
    CHECK(cp_deadline_remaining_ms(200u, 200u) == 0u);
    CHECK(cp_deadline_remaining_ms(300u, 200u) == 0u);
}

int main(void) {
    test_protocol();
    test_sha256();
    test_ownership_and_process_identity();
    test_journal();
    test_usb_transaction_exact_restore();
    test_usb_transaction_conflict_boot_and_profile_guards();
    test_reconnect_order_deadlines_and_call_race();
    test_request_history();
    test_global_lock();
    test_detached_lifecycle_and_deadlines();
    if (failures != 0) {
        fprintf(stderr, "%d of %u assertions failed\n", failures, assertions);
        return 1;
    }
    printf("all %u assertions passed\n", assertions);
    return 0;
}
