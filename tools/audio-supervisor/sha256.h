#ifndef CELLPORT_AUDIO_SHA256_H
#define CELLPORT_AUDIO_SHA256_H

#include <stddef.h>
#include <stdint.h>

void cp_sha256(const uint8_t *data, size_t length, uint8_t digest[32]);
void cp_sha256_hex(const uint8_t *data, size_t length, char hex[65]);

#endif
