#ifndef _WIN32
#define _POSIX_C_SOURCE 200809L
#endif

#include "runtime.h"

#include <string.h>

#ifdef _WIN32

void cp_lifecycle_init(cp_lifecycle_model *model, bool journal_pending) {
    *model = (cp_lifecycle_model){true, true, false, journal_pending, journal_pending};
}

void cp_lifecycle_mark_ready(cp_lifecycle_model *model) { model->ready = true; }
void cp_lifecycle_control_disconnected(cp_lifecycle_model *model) { model->control_connected = false; }
void cp_lifecycle_process_killed(cp_lifecycle_model *model) {
    model->process_running = false;
    model->ready = false;
    model->journal_pending = true;
    model->recovery_required = true;
}
void cp_lifecycle_reconnect(cp_lifecycle_model *model) {
    if (!model->process_running) {
        model->process_running = true;
        model->ready = false;
    }
    model->control_connected = true;
    model->recovery_required = model->recovery_required || model->journal_pending;
}
uint32_t cp_deadline_remaining_ms(uint64_t now_ms, uint64_t deadline_ms) {
    if (now_ms >= deadline_ms) return 0u;
    const uint64_t remaining = deadline_ms - now_ms;
    return remaining > UINT32_MAX ? UINT32_MAX : (uint32_t)remaining;
}
bool cp_signal_ready(int readiness_fd) { (void)readiness_fd; return false; }
cp_runtime_result cp_daemonize_and_wait_ready(cp_daemon_entry entry, void *context, uint32_t timeout_ms) {
    (void)entry; (void)context; (void)timeout_ms; return CP_RUNTIME_UNAVAILABLE;
}
cp_runtime_result cp_run_allowlisted(const char *executable, char *const argv[],
                                    const char *const *allowed_executables, size_t allowed_count,
                                    uint64_t deadline_monotonic_ms) {
    (void)executable; (void)argv; (void)allowed_executables; (void)allowed_count;
    (void)deadline_monotonic_ms; return CP_RUNTIME_UNAVAILABLE;
}
cp_runtime_result cp_fifo_write_allowlisted(const char *path, const char *const *allowed_paths,
                                           size_t allowed_count, const uint8_t *bytes, size_t length,
                                           uint64_t deadline_monotonic_ms) {
    (void)path; (void)allowed_paths; (void)allowed_count; (void)bytes; (void)length;
    (void)deadline_monotonic_ms; return CP_RUNTIME_UNAVAILABLE;
}

#else

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

void cp_lifecycle_init(cp_lifecycle_model *model, bool journal_pending) {
    *model = (cp_lifecycle_model){true, true, false, journal_pending, journal_pending};
}

void cp_lifecycle_mark_ready(cp_lifecycle_model *model) { model->ready = true; }
void cp_lifecycle_control_disconnected(cp_lifecycle_model *model) { model->control_connected = false; }
void cp_lifecycle_process_killed(cp_lifecycle_model *model) {
    model->process_running = false;
    model->ready = false;
    model->journal_pending = true;
    model->recovery_required = true;
}
void cp_lifecycle_reconnect(cp_lifecycle_model *model) {
    if (!model->process_running) {
        model->process_running = true;
        model->ready = false;
    }
    model->control_connected = true;
    model->recovery_required = model->recovery_required || model->journal_pending;
}

static uint64_t monotonic_ms(void) {
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) return 0u;
    return (uint64_t)now.tv_sec * UINT64_C(1000) + (uint64_t)now.tv_nsec / UINT64_C(1000000);
}

uint32_t cp_deadline_remaining_ms(uint64_t now_ms, uint64_t deadline_ms) {
    if (now_ms >= deadline_ms) return 0u;
    const uint64_t remaining = deadline_ms - now_ms;
    return remaining > UINT32_MAX ? UINT32_MAX : (uint32_t)remaining;
}

bool cp_signal_ready(int readiness_fd) {
    const uint8_t byte = 'R';
    return write(readiness_fd, &byte, sizeof(byte)) == (ssize_t)sizeof(byte);
}

static void terminate_and_reap(pid_t child) {
    (void)kill(child, SIGTERM);
    const uint64_t deadline = monotonic_ms() + 250u;
    int status = 0;
    for (;;) {
        const pid_t result = waitpid(child, &status, WNOHANG);
        if (result == child || (result < 0 && errno == ECHILD)) return;
        if (result < 0 && errno != EINTR) break;
        if (cp_deadline_remaining_ms(monotonic_ms(), deadline) == 0u) break;
        const struct timespec delay = {0, 5000000L};
        (void)nanosleep(&delay, NULL);
    }
    (void)kill(child, SIGKILL);
    while (waitpid(child, &status, 0) < 0 && errno == EINTR) { }
}

static bool wait_for_readiness(int descriptor, uint32_t timeout_ms) {
    struct pollfd poll_descriptor = {descriptor, POLLIN | POLLHUP, 0};
    const int result = poll(&poll_descriptor, 1u, timeout_ms > INT32_MAX ? INT32_MAX : (int)timeout_ms);
    if (result <= 0 || (poll_descriptor.revents & POLLIN) == 0) return false;
    uint8_t byte = 0u;
    return read(descriptor, &byte, sizeof(byte)) == (ssize_t)sizeof(byte) && byte == 'R';
}

cp_runtime_result cp_daemonize_and_wait_ready(cp_daemon_entry entry, void *context, uint32_t timeout_ms) {
    if (entry == NULL || timeout_ms == 0u) return CP_RUNTIME_IO_ERROR;
    int descriptors[2];
    if (pipe(descriptors) != 0) return CP_RUNTIME_IO_ERROR;
    const pid_t child = fork();
    if (child < 0) { (void)close(descriptors[0]); (void)close(descriptors[1]); return CP_RUNTIME_IO_ERROR; }
    if (child > 0) {
        (void)close(descriptors[1]);
        const bool ready = wait_for_readiness(descriptors[0], timeout_ms);
        (void)close(descriptors[0]);
        if (ready) return CP_RUNTIME_OK;
        terminate_and_reap(child);
        return CP_RUNTIME_TIMEOUT;
    }

    (void)close(descriptors[0]);
    if (setsid() < 0 || chdir("/") != 0) _exit(120);
    (void)umask(077u);
    const int null_descriptor = open("/dev/null", O_RDWR);
    if (null_descriptor < 0) _exit(121);
    for (int descriptor = 0; descriptor <= 2; ++descriptor) {
        if (dup2(null_descriptor, descriptor) < 0) _exit(122);
    }
    if (null_descriptor > 2) (void)close(null_descriptor);
    const int result = entry(descriptors[1], context);
    (void)close(descriptors[1]);
    _exit(result == 0 ? 0 : 123);
}

static bool is_allowed(const char *value, const char *const *allowlist, size_t count) {
    if (value == NULL || value[0] != '/' || (allowlist == NULL && count != 0u)) return false;
    for (size_t i = 0; i < count; ++i) if (strcmp(value, allowlist[i]) == 0) return true;
    return false;
}

static bool safe_argument_vector(const char *executable, char *const argv[]) {
    if (argv == NULL || argv[0] == NULL || strcmp(argv[0], executable) != 0) return false;
    const char *base_name = strrchr(executable, '/');
    base_name = base_name == NULL ? executable : base_name + 1;
    if (strcmp(base_name, "sh") == 0 || strcmp(base_name, "bash") == 0 ||
        strcmp(base_name, "ash") == 0 || strcmp(base_name, "busybox") == 0 || strcmp(base_name, "su") == 0) {
        return false;
    }
    for (size_t i = 0; i < 64u; ++i) {
        if (argv[i] == NULL) return true;
        if (strlen(argv[i]) > 512u || strcmp(argv[i], "-c") == 0 || strcmp(argv[i], "--command") == 0) return false;
        for (const unsigned char *p = (const unsigned char *)argv[i]; *p != '\0'; ++p) {
            if (*p < 0x20u || *p == 0x7fu || *p == ';' || *p == '|' || *p == '&' || *p == '`' ||
                *p == '$' || *p == '<' || *p == '>') return false;
        }
    }
    return false;
}

static bool wait_child_until(pid_t child, uint64_t deadline_ms, int *status) {
    for (;;) {
        const pid_t result = waitpid(child, status, WNOHANG);
        if (result == child) return true;
        if (result < 0 && errno != EINTR) return false;
        const uint64_t now = monotonic_ms();
        if (cp_deadline_remaining_ms(now, deadline_ms) == 0u) return false;
        const struct timespec delay = {0, 5000000L};
        (void)nanosleep(&delay, NULL);
    }
}

cp_runtime_result cp_run_allowlisted(const char *executable, char *const argv[],
                                    const char *const *allowed_executables, size_t allowed_count,
                                    uint64_t deadline_monotonic_ms) {
    if (!is_allowed(executable, allowed_executables, allowed_count) || !safe_argument_vector(executable, argv)) {
        return CP_RUNTIME_NOT_ALLOWED;
    }
    if (cp_deadline_remaining_ms(monotonic_ms(), deadline_monotonic_ms) == 0u) return CP_RUNTIME_TIMEOUT;
    const pid_t child = fork();
    if (child < 0) return CP_RUNTIME_IO_ERROR;
    if (child == 0) {
        execv(executable, argv);
        _exit(127);
    }
    int status = 0;
    if (wait_child_until(child, deadline_monotonic_ms, &status)) {
        return WIFEXITED(status) && WEXITSTATUS(status) == 0 ? CP_RUNTIME_OK : CP_RUNTIME_IO_ERROR;
    }
    (void)kill(child, SIGTERM);
    const uint64_t terminate_deadline = monotonic_ms() + 250u;
    if (!wait_child_until(child, terminate_deadline, &status)) {
        (void)kill(child, SIGKILL);
        while (waitpid(child, &status, 0) < 0 && errno == EINTR) { }
    }
    return CP_RUNTIME_TIMEOUT;
}

cp_runtime_result cp_fifo_write_allowlisted(const char *path, const char *const *allowed_paths,
                                           size_t allowed_count, const uint8_t *bytes, size_t length,
                                           uint64_t deadline_monotonic_ms) {
    if (!is_allowed(path, allowed_paths, allowed_count) || bytes == NULL || length == 0u) {
        return CP_RUNTIME_NOT_ALLOWED;
    }
    int descriptor = -1;
    while (descriptor < 0) {
        descriptor = open(path, O_WRONLY | O_NONBLOCK | O_CLOEXEC);
        if (descriptor >= 0) break;
        if (errno != ENXIO) return CP_RUNTIME_IO_ERROR;
        const uint32_t remaining = cp_deadline_remaining_ms(monotonic_ms(), deadline_monotonic_ms);
        if (remaining == 0u) return CP_RUNTIME_TIMEOUT;
        const struct timespec delay = {0, remaining < 5u ? (long)remaining * 1000000L : 5000000L};
        (void)nanosleep(&delay, NULL);
    }
    struct stat file_status;
    if (fstat(descriptor, &file_status) != 0 || !S_ISFIFO(file_status.st_mode)) {
        (void)close(descriptor);
        return CP_RUNTIME_NOT_ALLOWED;
    }
    size_t written = 0u;
    while (written < length) {
        const ssize_t result = write(descriptor, bytes + written, length - written);
        if (result > 0) { written += (size_t)result; continue; }
        if (result < 0 && errno != EAGAIN && errno != EINTR) { (void)close(descriptor); return CP_RUNTIME_IO_ERROR; }
        const uint32_t remaining = cp_deadline_remaining_ms(monotonic_ms(), deadline_monotonic_ms);
        if (remaining == 0u) { (void)close(descriptor); return CP_RUNTIME_TIMEOUT; }
        struct pollfd poll_descriptor = {descriptor, POLLOUT, 0};
        const int ready = poll(&poll_descriptor, 1u, remaining > INT32_MAX ? INT32_MAX : (int)remaining);
        if (ready == 0) { (void)close(descriptor); return CP_RUNTIME_TIMEOUT; }
        if (ready < 0 && errno != EINTR) { (void)close(descriptor); return CP_RUNTIME_IO_ERROR; }
    }
    (void)close(descriptor);
    return CP_RUNTIME_OK;
}

#endif
