#ifndef CELLPORT_AUDIO_SUPERVISOR_H
#define CELLPORT_AUDIO_SUPERVISOR_H

#include "protocol.h"

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define CP_REQUEST_HISTORY_CAPACITY 16u
#define CP_OWNER_TOKEN_BYTES 256u
#define CP_SESSION_BYTES 128u

typedef enum cp_occupancy {
    CP_OCCUPANCY_EMPTY,
    CP_OCCUPANCY_VOICE,
    CP_OCCUPANCY_DATA,
    CP_OCCUPANCY_UNKNOWN
} cp_occupancy;

typedef enum cp_journal_result {
    CP_JOURNAL_OK,
    CP_JOURNAL_ALREADY_EXISTS,
    CP_JOURNAL_IO_ERROR
} cp_journal_result;

typedef enum cp_dedup_result {
    CP_DEDUP_NEW,
    CP_DEDUP_REPLAY,
    CP_DEDUP_ID_CONFLICT,
    CP_DEDUP_SEQUENCE_STALE
} cp_dedup_result;

typedef struct cp_owner_state {
    bool locked;
    bool prepare_grace_used;
    bool restore_pending_call;
    char boot_id[CP_MAX_IDENTIFIER_BYTES + 1u];
    char session_id[CP_SESSION_BYTES + 1u];
    char owner_token[CP_OWNER_TOKEN_BYTES + 1u];
    uint64_t generation;
    uint64_t lease_deadline_ms;
    uint64_t prepare_grace_deadline_ms;
    const char *phase;
} cp_owner_state;

typedef struct cp_request_history_entry {
    bool occupied;
    char request_id[CP_MAX_IDENTIFIER_BYTES + 1u];
    char digest[65];
    uint64_t sequence;
    cp_error result;
} cp_request_history_entry;

typedef struct cp_request_history {
    cp_request_history_entry entries[CP_REQUEST_HISTORY_CAPACITY];
    size_t next_slot;
    uint64_t highest_sequence;
} cp_request_history;

#define CP_USB_VALUE_BYTES 256u
#define CP_USB_BOOT_BYTES (CP_MAX_IDENTIFIER_BYTES + 1u)

typedef enum cp_usb_field {
    CP_USB_FIELD_FUNCTIONS,
    CP_USB_FIELD_AUDIO_ENABLE,
    CP_USB_FIELD_ENABLE,
    CP_USB_FIELD_COUNT
} cp_usb_field;

typedef struct cp_usb_values {
    char value[CP_USB_FIELD_COUNT][CP_USB_VALUE_BYTES];
} cp_usb_values;

typedef struct cp_usb_profile {
    const char *profile_id;
    const char *functions;
    const char *audio_enable;
    const char *enable;
    bool approved;
} cp_usb_profile;

typedef struct cp_usb_transaction {
    bool active;
    bool restored;
    char boot_id[CP_USB_BOOT_BYTES];
    char journal_directory[512];
    cp_usb_values original;
    cp_usb_values last_applied;
} cp_usb_transaction;

typedef bool (*cp_usb_read_fn)(void *context, cp_usb_field field, char *value, size_t capacity);
typedef bool (*cp_usb_write_fn)(void *context, cp_usb_field field, const char *value);

typedef enum cp_reconnect_phase {
    CP_RECONNECT_IDLE,
    CP_RECONNECT_WAIT_AT,
    CP_RECONNECT_WAIT_UAC,
    CP_RECONNECT_LOCAL_CONVERGENCE,
    CP_RECONNECT_COMPLETE,
    CP_RECONNECT_RESTORE_PENDING,
    CP_RECONNECT_RECOVERY_REQUIRED
} cp_reconnect_phase;

typedef struct cp_reconnect_tracker {
    cp_reconnect_phase phase;
    uint64_t deadline_ms;
    uint64_t at_timeout_ms;
    uint64_t uac_timeout_ms;
    unsigned retries;
    unsigned max_retries;
    bool call_arrived_during_commit;
} cp_reconnect_tracker;

typedef struct cp_file_lock {
#ifdef _WIN32
    void *handle;
#else
    int descriptor;
#endif
    bool held;
} cp_file_lock;

void cp_owner_init(cp_owner_state *owner, uint64_t generation);
cp_error cp_owner_prepare(cp_owner_state *owner, const char *boot_id, const char *session_id,
                          const char *owner_token, bool approved_profile, cp_occupancy occupancy,
                          uint64_t now_ms);
cp_error cp_owner_renew(cp_owner_state *owner, const char *boot_id, const char *session_id,
                        const char *owner_token, uint64_t expected_generation, uint64_t now_ms);
bool cp_owner_extend_prepare_grace(cp_owner_state *owner, const char *owner_token, uint64_t now_ms);
bool cp_owner_mark_runtime_ready(cp_owner_state *owner, const char *owner_token, uint64_t now_ms);
cp_error cp_owner_release(cp_owner_state *owner, const char *boot_id, const char *session_id,
                          const char *owner_token, uint64_t expected_generation,
                          cp_occupancy occupancy, bool last_applied_matches, uint64_t now_ms);
cp_error cp_owner_expire(cp_owner_state *owner, cp_occupancy occupancy, uint64_t now_ms);
bool cp_process_identity_matches(uint64_t expected_pid, uint64_t expected_start_time,
                                 const char *expected_exe, const char *expected_argv,
                                 const char *expected_boot_id, const char *expected_owner_token,
                                 uint64_t actual_pid, uint64_t actual_start_time,
                                 const char *actual_exe, const char *actual_argv,
                                 const char *actual_boot_id, const char *actual_owner_token);
bool cp_proc_stat_start_time(const char *stat_line, uint64_t *start_time);

cp_journal_result cp_journal_begin(const char *directory, const char *original_snapshot,
                                   const char *intent);
cp_journal_result cp_journal_write_intent(const char *directory, const char *intent);
cp_journal_result cp_journal_write_last_applied(const char *directory, const char *last_applied);
bool cp_journal_read(const char *directory, const char *file_name, char *buffer, size_t capacity);

void cp_request_history_init(cp_request_history *history);
cp_dedup_result cp_request_history_check(const cp_request_history *history, const char *request_id,
                                        const uint8_t *content, size_t content_length,
                                        uint64_t sequence, cp_error *cached_result);
void cp_request_history_record(cp_request_history *history, const char *request_id,
                              const uint8_t *content, size_t content_length,
                              uint64_t sequence, cp_error result);

bool cp_global_lock_acquire(cp_file_lock *lock, const char *lock_path);
void cp_global_lock_release(cp_file_lock *lock);
bool cp_deadline_expired(uint64_t now_ms, uint64_t deadline_ms);
cp_error cp_usb_transaction_prepare(cp_usb_transaction *transaction, const cp_usb_profile *profile,
                                    const char *boot_id, const char *journal_directory,
                                    cp_usb_read_fn read_value, cp_usb_write_fn write_value, void *context);
cp_error cp_usb_transaction_release(cp_usb_transaction *transaction, const char *boot_id,
                                    cp_usb_read_fn read_value, cp_usb_write_fn write_value, void *context);
void cp_reconnect_init(cp_reconnect_tracker *tracker, uint64_t at_timeout_ms,
                       uint64_t uac_timeout_ms, unsigned max_retries);
bool cp_reconnect_begin(cp_reconnect_tracker *tracker, uint64_t now_ms);
bool cp_reconnect_observe_at(cp_reconnect_tracker *tracker, uint64_t now_ms);
bool cp_reconnect_observe_uac(cp_reconnect_tracker *tracker, uint64_t now_ms);
void cp_reconnect_call_arrived(cp_reconnect_tracker *tracker);
cp_reconnect_phase cp_reconnect_tick(cp_reconnect_tracker *tracker, uint64_t now_ms);
void cp_reconnect_convergence_complete(cp_reconnect_tracker *tracker, bool usb_consistent,
                                       bool restore_verified);

#endif
