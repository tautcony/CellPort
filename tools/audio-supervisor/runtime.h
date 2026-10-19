#ifndef CELLPORT_AUDIO_RUNTIME_H
#define CELLPORT_AUDIO_RUNTIME_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct cp_lifecycle_model {
    bool process_running;
    bool control_connected;
    bool ready;
    bool journal_pending;
    bool recovery_required;
} cp_lifecycle_model;

typedef enum cp_runtime_result {
    CP_RUNTIME_OK,
    CP_RUNTIME_TIMEOUT,
    CP_RUNTIME_NOT_ALLOWED,
    CP_RUNTIME_IO_ERROR,
    CP_RUNTIME_UNAVAILABLE
} cp_runtime_result;

typedef int (*cp_daemon_entry)(int readiness_fd, void *context);

void cp_lifecycle_init(cp_lifecycle_model *model, bool journal_pending);
void cp_lifecycle_mark_ready(cp_lifecycle_model *model);
void cp_lifecycle_control_disconnected(cp_lifecycle_model *model);
void cp_lifecycle_process_killed(cp_lifecycle_model *model);
void cp_lifecycle_reconnect(cp_lifecycle_model *model);
uint32_t cp_deadline_remaining_ms(uint64_t now_ms, uint64_t deadline_ms);
bool cp_signal_ready(int readiness_fd);
cp_runtime_result cp_daemonize_and_wait_ready(cp_daemon_entry entry, void *context, uint32_t timeout_ms);
cp_runtime_result cp_run_allowlisted(const char *executable, char *const argv[],
                                    const char *const *allowed_executables, size_t allowed_count,
                                    uint64_t deadline_monotonic_ms);
cp_runtime_result cp_fifo_write_allowlisted(const char *path, const char *const *allowed_paths,
                                           size_t allowed_count, const uint8_t *bytes, size_t length,
                                           uint64_t deadline_monotonic_ms);

#endif
