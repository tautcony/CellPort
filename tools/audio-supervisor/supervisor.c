#ifndef _WIN32
#define _POSIX_C_SOURCE 200809L
#endif

#include "supervisor.h"
#include "sha256.h"

#include <errno.h>
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <io.h>
#include <windows.h>
#include <direct.h>
#else
#include <fcntl.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#endif

#define CP_LEASE_MS UINT64_C(45000)
#define CP_PREPARE_GRACE_MS UINT64_C(120000)

bool cp_deadline_expired(uint64_t now_ms, uint64_t deadline_ms) {
    return now_ms >= deadline_ms;
}

static bool usb_value_valid(cp_usb_field field, const char *value, bool allow_empty) {
    if (value == NULL || (!allow_empty && value[0] == '\0')) return false;
    if (field == CP_USB_FIELD_AUDIO_ENABLE || field == CP_USB_FIELD_ENABLE)
        return strcmp(value, "0") == 0 || strcmp(value, "1") == 0;
    bool previous_comma = true;
    for (const unsigned char *cursor = (const unsigned char *)value; *cursor != '\0'; ++cursor) {
        if (*cursor == ',') {
            if (previous_comma) return false;
            previous_comma = true;
        } else if (isalnum(*cursor) != 0 || *cursor == '_' || *cursor == '-' || *cursor == '.') {
            previous_comma = false;
        } else {
            return false;
        }
    }
    return allow_empty && value[0] == '\0' ? true : !previous_comma;
}

static bool functions_include_audio(const char *functions) {
    const char *cursor = functions;
    while (*cursor != '\0') {
        const char *end = strchr(cursor, ',');
        const size_t length = end == NULL ? strlen(cursor) : (size_t)(end - cursor);
        if (length == sizeof("audio_enable") - 1u && memcmp(cursor, "audio_enable", length) == 0) return true;
        if (end == NULL) break;
        cursor = end + 1;
    }
    return false;
}

static bool read_usb_values(cp_usb_values *values, cp_usb_read_fn read_value, void *context) {
    for (unsigned field = 0u; field < (unsigned)CP_USB_FIELD_COUNT; ++field) {
        char *target = values->value[field];
        memset(target, 0, CP_USB_VALUE_BYTES);
        if (!read_value(context, (cp_usb_field)field, target, CP_USB_VALUE_BYTES)) return false;
        target[CP_USB_VALUE_BYTES - 1u] = '\0';
        if (!usb_value_valid((cp_usb_field)field, target, field == (unsigned)CP_USB_FIELD_FUNCTIONS)) return false;
    }
    return true;
}

static bool usb_values_json(char *buffer, size_t capacity, const char *boot_id, const cp_usb_values *values) {
    const int length = snprintf(buffer, capacity,
        "{\"bootId\":\"%s\",\"functions\":\"%s\",\"audio_enable\":\"%s\",\"enable\":\"%s\"}",
        boot_id, values->value[CP_USB_FIELD_FUNCTIONS], values->value[CP_USB_FIELD_AUDIO_ENABLE],
        values->value[CP_USB_FIELD_ENABLE]);
    return length > 0 && (size_t)length < capacity;
}

static bool write_usb_value(cp_usb_transaction *transaction, cp_usb_field field, const char *value,
                            cp_usb_write_fn write_value, void *context) {
    if (!usb_value_valid(field, value, field == CP_USB_FIELD_FUNCTIONS) ||
        !write_value(context, field, value)) return false;
    (void)snprintf(transaction->last_applied.value[field], CP_USB_VALUE_BYTES, "%s", value);
    char json[2048];
    if (!usb_values_json(json, sizeof(json), transaction->boot_id, &transaction->last_applied)) return false;
    return cp_journal_write_last_applied(transaction->journal_directory, json) == CP_JOURNAL_OK;
}

static bool usb_profile_valid(const cp_usb_profile *profile) {
    if (profile == NULL || !profile->approved || profile->profile_id == NULL ||
        profile->profile_id[0] == '\0' || profile->functions == NULL ||
        profile->audio_enable == NULL || profile->enable == NULL) return false;
    for (const unsigned char *cursor = (const unsigned char *)profile->profile_id; *cursor != '\0'; ++cursor) {
        if (isalnum(*cursor) == 0 && *cursor != '_' && *cursor != '-' && *cursor != '.') return false;
    }
    return usb_value_valid(CP_USB_FIELD_FUNCTIONS, profile->functions, false) &&
        functions_include_audio(profile->functions) && strcmp(profile->audio_enable, "1") == 0 &&
        strcmp(profile->enable, "1") == 0;
}

cp_error cp_usb_transaction_prepare(cp_usb_transaction *transaction, const cp_usb_profile *profile,
                                    const char *boot_id, const char *journal_directory,
                                    cp_usb_read_fn read_value, cp_usb_write_fn write_value, void *context) {
    if (transaction == NULL || boot_id == NULL || boot_id[0] == '\0' || journal_directory == NULL ||
        journal_directory[0] == '\0' || read_value == NULL || write_value == NULL)
        return CP_ERROR_PAYLOAD_INVALID;
    if (!usb_profile_valid(profile)) return profile != NULL && !profile->approved
        ? CP_ERROR_UNSUPPORTED_PROFILE : CP_ERROR_PAYLOAD_INVALID;
    if (strlen(boot_id) >= sizeof(transaction->boot_id) ||
        strlen(journal_directory) >= sizeof(transaction->journal_directory)) return CP_ERROR_PAYLOAD_INVALID;
    if (transaction->active) return CP_ERROR_OPERATION_CONFLICT;

    memset(transaction, 0, sizeof(*transaction));
    memcpy(transaction->boot_id, boot_id, strlen(boot_id) + 1u);
    memcpy(transaction->journal_directory, journal_directory, strlen(journal_directory) + 1u);
    if (!read_usb_values(&transaction->original, read_value, context)) return CP_ERROR_RECOVERY_REQUIRED;
    transaction->last_applied = transaction->original;

    char original_json[2048];
    char intent_json[1024];
    char last_applied_json[2048];
    if (!usb_values_json(original_json, sizeof(original_json), boot_id, &transaction->original))
        return CP_ERROR_PAYLOAD_INVALID;
    const int intent_length = snprintf(intent_json, sizeof(intent_json),
        "{\"profileId\":\"%s\",\"stage\":\"prepare\",\"targetFunctions\":\"%s\","
        "\"targetAudioEnable\":\"1\",\"targetEnable\":\"1\"}", profile->profile_id, profile->functions);
    if (intent_length <= 0 || (size_t)intent_length >= sizeof(intent_json) ||
        !usb_values_json(last_applied_json, sizeof(last_applied_json), boot_id, &transaction->last_applied))
        return CP_ERROR_PAYLOAD_INVALID;
    if (cp_journal_begin(journal_directory, original_json, intent_json) != CP_JOURNAL_OK ||
        cp_journal_write_last_applied(journal_directory, last_applied_json) != CP_JOURNAL_OK)
        return CP_ERROR_RECOVERY_REQUIRED;
    transaction->active = true;

    if (!write_usb_value(transaction, CP_USB_FIELD_ENABLE, "0", write_value, context) ||
        !write_usb_value(transaction, CP_USB_FIELD_FUNCTIONS, profile->functions, write_value, context) ||
        !write_usb_value(transaction, CP_USB_FIELD_AUDIO_ENABLE, profile->audio_enable, write_value, context) ||
        !write_usb_value(transaction, CP_USB_FIELD_ENABLE, profile->enable, write_value, context))
        return CP_ERROR_RECOVERY_REQUIRED;
    return CP_ERROR_NONE;
}

cp_error cp_usb_transaction_release(cp_usb_transaction *transaction, const char *boot_id,
                                    cp_usb_read_fn read_value, cp_usb_write_fn write_value, void *context) {
    if (transaction == NULL || boot_id == NULL || read_value == NULL || write_value == NULL)
        return CP_ERROR_PAYLOAD_INVALID;
    if (strcmp(transaction->boot_id, boot_id) != 0) return CP_ERROR_BOOT_MISMATCH;
    if (transaction->restored) {
        cp_usb_values current;
        if (!read_usb_values(&current, read_value, context)) return CP_ERROR_RECOVERY_REQUIRED;
        return memcmp(&current, &transaction->original, sizeof(current)) == 0
            ? CP_ERROR_NONE : CP_ERROR_RESTORE_CONFLICT;
    }
    if (!transaction->active) return CP_ERROR_OWNER_MISMATCH;

    cp_usb_values current;
    if (!read_usb_values(&current, read_value, context)) return CP_ERROR_RECOVERY_REQUIRED;
    if (memcmp(&current, &transaction->last_applied, sizeof(current)) != 0) return CP_ERROR_RESTORE_CONFLICT;

    if (!write_usb_value(transaction, CP_USB_FIELD_ENABLE, "0", write_value, context) ||
        !write_usb_value(transaction, CP_USB_FIELD_FUNCTIONS,
                         transaction->original.value[CP_USB_FIELD_FUNCTIONS], write_value, context) ||
        !write_usb_value(transaction, CP_USB_FIELD_AUDIO_ENABLE,
                         transaction->original.value[CP_USB_FIELD_AUDIO_ENABLE], write_value, context) ||
        !write_usb_value(transaction, CP_USB_FIELD_ENABLE,
                         transaction->original.value[CP_USB_FIELD_ENABLE], write_value, context))
        return CP_ERROR_RECOVERY_REQUIRED;

    if (!read_usb_values(&current, read_value, context) ||
        memcmp(&current, &transaction->original, sizeof(current)) != 0) return CP_ERROR_RECOVERY_REQUIRED;
    transaction->active = false;
    transaction->restored = true;
    return CP_ERROR_NONE;
}

void cp_reconnect_init(cp_reconnect_tracker *tracker, uint64_t at_timeout_ms,
                       uint64_t uac_timeout_ms, unsigned max_retries) {
    memset(tracker, 0, sizeof(*tracker));
    tracker->phase = CP_RECONNECT_IDLE;
    tracker->at_timeout_ms = at_timeout_ms == 0u ? 1u : at_timeout_ms;
    tracker->uac_timeout_ms = uac_timeout_ms == 0u ? 1u : uac_timeout_ms;
    tracker->max_retries = max_retries > 2u ? 2u : max_retries;
}

bool cp_reconnect_begin(cp_reconnect_tracker *tracker, uint64_t now_ms) {
    if (tracker == NULL || tracker->phase != CP_RECONNECT_IDLE) return false;
    tracker->phase = CP_RECONNECT_WAIT_AT;
    tracker->deadline_ms = now_ms + tracker->at_timeout_ms;
    return true;
}

bool cp_reconnect_observe_at(cp_reconnect_tracker *tracker, uint64_t now_ms) {
    if (tracker == NULL || tracker->phase != CP_RECONNECT_WAIT_AT ||
        cp_deadline_expired(now_ms, tracker->deadline_ms)) return false;
    tracker->phase = CP_RECONNECT_WAIT_UAC;
    tracker->deadline_ms = now_ms + tracker->uac_timeout_ms;
    return true;
}

bool cp_reconnect_observe_uac(cp_reconnect_tracker *tracker, uint64_t now_ms) {
    if (tracker == NULL || tracker->phase != CP_RECONNECT_WAIT_UAC ||
        cp_deadline_expired(now_ms, tracker->deadline_ms)) return false;
    tracker->phase = CP_RECONNECT_COMPLETE;
    tracker->deadline_ms = 0u;
    return true;
}

void cp_reconnect_call_arrived(cp_reconnect_tracker *tracker) {
    if (tracker == NULL || (tracker->phase != CP_RECONNECT_WAIT_AT &&
                            tracker->phase != CP_RECONNECT_WAIT_UAC)) return;
    tracker->call_arrived_during_commit = true;
    tracker->phase = CP_RECONNECT_LOCAL_CONVERGENCE;
    tracker->deadline_ms = 0u;
}

cp_reconnect_phase cp_reconnect_tick(cp_reconnect_tracker *tracker, uint64_t now_ms) {
    if (tracker == NULL || (tracker->phase != CP_RECONNECT_WAIT_AT &&
                            tracker->phase != CP_RECONNECT_WAIT_UAC) ||
        !cp_deadline_expired(now_ms, tracker->deadline_ms))
        return tracker == NULL ? CP_RECONNECT_RECOVERY_REQUIRED : tracker->phase;
    if (tracker->retries < tracker->max_retries) {
        ++tracker->retries;
        tracker->phase = CP_RECONNECT_WAIT_AT;
        tracker->deadline_ms = now_ms + tracker->at_timeout_ms;
        return tracker->phase;
    }
    tracker->phase = CP_RECONNECT_LOCAL_CONVERGENCE;
    tracker->deadline_ms = 0u;
    return tracker->phase;
}

void cp_reconnect_convergence_complete(cp_reconnect_tracker *tracker, bool usb_consistent,
                                       bool restore_verified) {
    if (tracker == NULL || tracker->phase != CP_RECONNECT_LOCAL_CONVERGENCE) return;
    tracker->deadline_ms = 0u;
    tracker->phase = !usb_consistent ? CP_RECONNECT_RECOVERY_REQUIRED
        : restore_verified ? CP_RECONNECT_COMPLETE : CP_RECONNECT_RESTORE_PENDING;
}

void cp_owner_init(cp_owner_state *owner, uint64_t generation) {
    memset(owner, 0, sizeof(*owner));
    owner->generation = generation;
    owner->phase = "disconnected";
}

static bool nonempty(const char *value) {
    return value != NULL && value[0] != '\0';
}

static bool owner_matches(const cp_owner_state *owner, const char *boot_id, const char *session_id,
                          const char *owner_token, uint64_t expected_generation) {
    return owner->locked && boot_id != NULL && session_id != NULL && owner_token != NULL &&
           strcmp(owner->boot_id, boot_id) == 0 && strcmp(owner->session_id, session_id) == 0 &&
           strcmp(owner->owner_token, owner_token) == 0 && owner->generation == expected_generation;
}

cp_error cp_owner_prepare(cp_owner_state *owner, const char *boot_id, const char *session_id,
                          const char *owner_token, bool approved_profile, cp_occupancy occupancy,
                          uint64_t now_ms) {
    if (owner == NULL || !nonempty(boot_id) || !nonempty(session_id) || !nonempty(owner_token)) {
        return CP_ERROR_PAYLOAD_INVALID;
    }
    if (!approved_profile) return CP_ERROR_UNSUPPORTED_PROFILE;
    if (occupancy == CP_OCCUPANCY_DATA) return CP_ERROR_DEVICE_BUSY_DATA;
    if (occupancy == CP_OCCUPANCY_UNKNOWN) return CP_ERROR_CALL_STATE_UNKNOWN;
    if (occupancy == CP_OCCUPANCY_VOICE) return CP_ERROR_OPERATION_CONFLICT;
    if (owner->locked) return CP_ERROR_OPERATION_CONFLICT;
    if (strlen(boot_id) > CP_MAX_IDENTIFIER_BYTES || strlen(session_id) > CP_SESSION_BYTES ||
        strlen(owner_token) > CP_OWNER_TOKEN_BYTES) return CP_ERROR_PAYLOAD_INVALID;

    owner->locked = true;
    owner->prepare_grace_used = false;
    owner->restore_pending_call = false;
    memcpy(owner->boot_id, boot_id, strlen(boot_id) + 1u);
    memcpy(owner->session_id, session_id, strlen(session_id) + 1u);
    memcpy(owner->owner_token, owner_token, strlen(owner_token) + 1u);
    ++owner->generation;
    owner->lease_deadline_ms = now_ms + CP_LEASE_MS;
    owner->prepare_grace_deadline_ms = now_ms + CP_PREPARE_GRACE_MS;
    owner->phase = "staging";
    return CP_ERROR_NONE;
}

cp_error cp_owner_renew(cp_owner_state *owner, const char *boot_id, const char *session_id,
                        const char *owner_token, uint64_t expected_generation, uint64_t now_ms) {
    if (owner == NULL || !owner->locked) return CP_ERROR_OWNER_MISMATCH;
    if (strcmp(owner->boot_id, boot_id != NULL ? boot_id : "") != 0) return CP_ERROR_BOOT_MISMATCH;
    if (owner->generation != expected_generation) return CP_ERROR_GENERATION_MISMATCH;
    if (!owner_matches(owner, boot_id, session_id, owner_token, expected_generation)) return CP_ERROR_OWNER_MISMATCH;
    if (cp_deadline_expired(now_ms, owner->lease_deadline_ms)) {
        return owner->restore_pending_call ? CP_ERROR_RESTORE_PENDING_CALL : CP_ERROR_RECOVERY_REQUIRED;
    }
    owner->lease_deadline_ms = now_ms + CP_LEASE_MS;
    if (strcmp(owner->phase, "staging") == 0 && owner->lease_deadline_ms > owner->prepare_grace_deadline_ms) {
        owner->lease_deadline_ms = owner->prepare_grace_deadline_ms;
    }
    return CP_ERROR_NONE;
}

bool cp_owner_extend_prepare_grace(cp_owner_state *owner, const char *owner_token, uint64_t now_ms) {
    if (owner == NULL || !owner->locked || owner->prepare_grace_used ||
        strcmp(owner->phase, "staging") != 0 || owner_token == NULL ||
        strcmp(owner->owner_token, owner_token) != 0 || cp_deadline_expired(now_ms, owner->lease_deadline_ms) ||
        cp_deadline_expired(now_ms, owner->prepare_grace_deadline_ms)) {
        return false;
    }
    owner->prepare_grace_used = true;
    owner->lease_deadline_ms = owner->prepare_grace_deadline_ms;
    return true;
}

bool cp_owner_mark_runtime_ready(cp_owner_state *owner, const char *owner_token, uint64_t now_ms) {
    if (owner == NULL || !owner->locked || owner_token == NULL ||
        strcmp(owner->owner_token, owner_token) != 0 || strcmp(owner->phase, "staging") != 0 ||
        cp_deadline_expired(now_ms, owner->lease_deadline_ms)) return false;
    owner->phase = "runtime_ready";
    return true;
}

cp_error cp_owner_release(cp_owner_state *owner, const char *boot_id, const char *session_id,
                          const char *owner_token, uint64_t expected_generation,
                          cp_occupancy occupancy, bool last_applied_matches, uint64_t now_ms) {
    (void)now_ms;
    if (owner == NULL || !owner->locked) return CP_ERROR_OWNER_MISMATCH;
    if (strcmp(owner->boot_id, boot_id != NULL ? boot_id : "") != 0) return CP_ERROR_BOOT_MISMATCH;
    if (owner->generation != expected_generation) return CP_ERROR_GENERATION_MISMATCH;
    if (!owner_matches(owner, boot_id, session_id, owner_token, expected_generation)) return CP_ERROR_OWNER_MISMATCH;
    if (occupancy == CP_OCCUPANCY_DATA || occupancy == CP_OCCUPANCY_VOICE || occupancy == CP_OCCUPANCY_UNKNOWN) {
        owner->restore_pending_call = true;
        owner->phase = "restore_pending_call";
        return CP_ERROR_RESTORE_PENDING_CALL;
    }
    if (!last_applied_matches) {
        owner->phase = "recovery_required";
        return CP_ERROR_RESTORE_CONFLICT;
    }
    owner->locked = false;
    owner->restore_pending_call = false;
    owner->phase = "released";
    ++owner->generation;
    memset(owner->boot_id, 0, sizeof(owner->boot_id));
    memset(owner->session_id, 0, sizeof(owner->session_id));
    memset(owner->owner_token, 0, sizeof(owner->owner_token));
    owner->lease_deadline_ms = 0u;
    owner->prepare_grace_deadline_ms = 0u;
    return CP_ERROR_NONE;
}

cp_error cp_owner_expire(cp_owner_state *owner, cp_occupancy occupancy, uint64_t now_ms) {
    if (owner == NULL || !owner->locked || !cp_deadline_expired(now_ms, owner->lease_deadline_ms)) {
        return CP_ERROR_NONE;
    }
    owner->restore_pending_call = occupancy != CP_OCCUPANCY_EMPTY;
    owner->phase = owner->restore_pending_call ? "restore_pending_call" : "restoring";
    return owner->restore_pending_call ? CP_ERROR_RESTORE_PENDING_CALL : CP_ERROR_RECOVERY_REQUIRED;
}

bool cp_process_identity_matches(uint64_t expected_pid, uint64_t expected_start_time,
                                 const char *expected_exe, const char *expected_argv,
                                 const char *expected_boot_id, const char *expected_owner_token,
                                 uint64_t actual_pid, uint64_t actual_start_time,
                                 const char *actual_exe, const char *actual_argv,
                                 const char *actual_boot_id, const char *actual_owner_token) {
    return expected_pid != 0u && expected_start_time != 0u && expected_exe != NULL && expected_argv != NULL &&
           expected_boot_id != NULL && expected_owner_token != NULL && actual_exe != NULL && actual_argv != NULL &&
           actual_boot_id != NULL && actual_owner_token != NULL && expected_pid == actual_pid &&
           expected_start_time == actual_start_time && strcmp(expected_exe, actual_exe) == 0 &&
           strcmp(expected_argv, actual_argv) == 0 && strcmp(expected_boot_id, actual_boot_id) == 0 &&
           strcmp(expected_owner_token, actual_owner_token) == 0;
}

bool cp_proc_stat_start_time(const char *stat_line, uint64_t *start_time) {
    if (stat_line == NULL || start_time == NULL) return false;
    const char *right_parenthesis = strrchr(stat_line, ')');
    if (right_parenthesis == NULL) return false;
    const char *cursor = right_parenthesis + 1;
    unsigned field = 3u;
    while (field <= 22u) {
        while (*cursor == ' ' || *cursor == '\t') ++cursor;
        if (*cursor == '\0' || *cursor == '\n' || *cursor == '\r') return false;
        const char *start = cursor;
        while (*cursor != '\0' && *cursor != '\n' && *cursor != '\r' && *cursor != ' ' && *cursor != '\t') ++cursor;
        if (field == 22u) {
            char number[32];
            const size_t length = (size_t)(cursor - start);
            if (length == 0u || length >= sizeof(number)) return false;
            memcpy(number, start, length);
            number[length] = '\0';
            for (size_t i = 0; i < length; ++i) if (number[i] < '0' || number[i] > '9') return false;
            errno = 0;
            const unsigned long long value = strtoull(number, NULL, 10);
            if (errno != 0) return false;
            *start_time = (uint64_t)value;
            return true;
        }
        ++field;
    }
    return false;
}

static bool make_path(char *out, size_t capacity, const char *directory, const char *name) {
    if (directory == NULL || name == NULL) return false;
    const int result = snprintf(out, capacity, "%s/%s", directory, name);
    return result >= 0 && (size_t)result < capacity;
}

static bool ensure_directory(const char *directory) {
#ifdef _WIN32
    if (_mkdir(directory) == 0 || errno == EEXIST) return true;
#else
    if (mkdir(directory, 0700) == 0 || errno == EEXIST) return true;
#endif
    return false;
}

static bool atomic_write(const char *path, const char *contents) {
    if (path == NULL || contents == NULL) return false;
    char temporary[1024];
    const int path_length = snprintf(temporary, sizeof(temporary), "%s.part", path);
    if (path_length < 0 || (size_t)path_length >= sizeof(temporary)) return false;
    FILE *file = fopen(temporary, "wb");
    if (file == NULL) return false;
    const size_t length = strlen(contents);
    bool success = fwrite(contents, 1u, length, file) == length && fflush(file) == 0;
#ifdef _WIN32
    if (success) success = _commit(_fileno(file)) == 0;
#else
    if (success) success = fsync(fileno(file)) == 0;
#endif
    if (fclose(file) != 0) success = false;
    if (!success) {
        remove(temporary);
        return false;
    }
#ifdef _WIN32
    bool moved = false;
    for (unsigned attempt = 0u; attempt < 5u; ++attempt) {
        if (MoveFileExA(temporary, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
            moved = true;
            break;
        }
        const DWORD error = GetLastError();
        if (error != ERROR_ACCESS_DENIED && error != ERROR_SHARING_VIOLATION && error != ERROR_LOCK_VIOLATION)
            break;
        Sleep(10u * (attempt + 1u));
    }
    if (!moved) { remove(temporary); return false; }
#else
    if (rename(temporary, path) != 0) {
        remove(temporary);
        return false;
    }
    char directory[1024];
    const char *slash = strrchr(path, '/');
    if (slash != NULL) {
        const size_t length = (size_t)(slash - path);
        if (length < sizeof(directory)) {
            memcpy(directory, path, length);
            directory[length] = '\0';
            const int descriptor = open(length == 0u ? "/" : directory, O_RDONLY);
            if (descriptor >= 0) { (void)fsync(descriptor); (void)close(descriptor); }
        }
    }
#endif
    return true;
}

static bool file_exists(const char *path) {
    FILE *file = fopen(path, "rb");
    if (file == NULL) return false;
    (void)fclose(file);
    return true;
}

cp_journal_result cp_journal_begin(const char *directory, const char *original_snapshot,
                                   const char *intent) {
    if (!ensure_directory(directory)) return CP_JOURNAL_IO_ERROR;
    char snapshot_path[1024];
    char intent_path[1024];
    if (!make_path(snapshot_path, sizeof(snapshot_path), directory, "original.snapshot") ||
        !make_path(intent_path, sizeof(intent_path), directory, "intent.json")) return CP_JOURNAL_IO_ERROR;
    if (file_exists(snapshot_path)) return CP_JOURNAL_ALREADY_EXISTS;
    if (!atomic_write(snapshot_path, original_snapshot)) return CP_JOURNAL_IO_ERROR;
    if (!atomic_write(intent_path, intent)) return CP_JOURNAL_IO_ERROR;
    return CP_JOURNAL_OK;
}

cp_journal_result cp_journal_write_intent(const char *directory, const char *intent) {
    char path[1024];
    if (!make_path(path, sizeof(path), directory, "intent.json") || !atomic_write(path, intent)) {
        return CP_JOURNAL_IO_ERROR;
    }
    return CP_JOURNAL_OK;
}

cp_journal_result cp_journal_write_last_applied(const char *directory, const char *last_applied) {
    char path[1024];
    if (!make_path(path, sizeof(path), directory, "last-applied.json") || !atomic_write(path, last_applied)) {
        return CP_JOURNAL_IO_ERROR;
    }
    return CP_JOURNAL_OK;
}

bool cp_journal_read(const char *directory, const char *file_name, char *buffer, size_t capacity) {
    char path[1024];
    if (buffer == NULL || capacity == 0u || !make_path(path, sizeof(path), directory, file_name)) return false;
    FILE *file = fopen(path, "rb");
    if (file == NULL) return false;
    const size_t read_count = fread(buffer, 1u, capacity - 1u, file);
    const bool read_error = ferror(file) != 0;
    const int extra = read_error ? EOF : fgetc(file);
    const bool too_large = extra != EOF;
    (void)fclose(file);
    if (too_large || read_error) return false;
    buffer[read_count] = '\0';
    return true;
}

void cp_request_history_init(cp_request_history *history) {
    memset(history, 0, sizeof(*history));
}

static void calculate_digest(const uint8_t *content, size_t content_length, char digest[65]) {
    cp_sha256_hex(content, content_length, digest);
}

cp_dedup_result cp_request_history_check(const cp_request_history *history, const char *request_id,
                                        const uint8_t *content, size_t content_length,
                                        uint64_t sequence, cp_error *cached_result) {
    char digest[65];
    calculate_digest(content, content_length, digest);
    for (size_t i = 0; i < CP_REQUEST_HISTORY_CAPACITY; ++i) {
        const cp_request_history_entry *entry = &history->entries[i];
        if (!entry->occupied || strcmp(entry->request_id, request_id) != 0) continue;
        if (strcmp(entry->digest, digest) != 0 || entry->sequence != sequence) return CP_DEDUP_ID_CONFLICT;
        if (cached_result != NULL) *cached_result = entry->result;
        return CP_DEDUP_REPLAY;
    }
    if (sequence <= history->highest_sequence) return CP_DEDUP_SEQUENCE_STALE;
    return CP_DEDUP_NEW;
}

void cp_request_history_record(cp_request_history *history, const char *request_id,
                              const uint8_t *content, size_t content_length,
                              uint64_t sequence, cp_error result) {
    cp_request_history_entry *entry = &history->entries[history->next_slot];
    memset(entry, 0, sizeof(*entry));
    entry->occupied = true;
    (void)snprintf(entry->request_id, sizeof(entry->request_id), "%s", request_id);
    calculate_digest(content, content_length, entry->digest);
    entry->sequence = sequence;
    entry->result = result;
    history->next_slot = (history->next_slot + 1u) % CP_REQUEST_HISTORY_CAPACITY;
    if (sequence > history->highest_sequence) history->highest_sequence = sequence;
}

bool cp_global_lock_acquire(cp_file_lock *lock, const char *lock_path) {
    if (lock == NULL || lock_path == NULL || lock->held) return false;
#ifdef _WIN32
    HANDLE handle = CreateFileA(lock_path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_ALWAYS,
                                FILE_ATTRIBUTE_HIDDEN, NULL);
    if (handle == INVALID_HANDLE_VALUE) return false;
    lock->handle = (void *)handle;
#else
    const int descriptor = open(lock_path, O_CREAT | O_RDWR, 0600);
    if (descriptor < 0) return false;
    if (flock(descriptor, LOCK_EX | LOCK_NB) != 0) { (void)close(descriptor); return false; }
    lock->descriptor = descriptor;
#endif
    lock->held = true;
    return true;
}

void cp_global_lock_release(cp_file_lock *lock) {
    if (lock == NULL || !lock->held) return;
#ifdef _WIN32
    (void)CloseHandle((HANDLE)lock->handle);
    lock->handle = NULL;
#else
    (void)flock(lock->descriptor, LOCK_UN);
    (void)close(lock->descriptor);
    lock->descriptor = -1;
#endif
    lock->held = false;
}
