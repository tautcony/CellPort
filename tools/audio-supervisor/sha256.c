#include "sha256.h"

#include <string.h>

typedef struct sha256_state {
    uint32_t words[8];
    uint64_t bit_count;
    uint8_t block[64];
    size_t block_length;
} sha256_state;

static const uint32_t constants[64] = {
    0x428a2f98u,0x71374491u,0xb5c0fbcfu,0xe9b5dba5u,0x3956c25bu,0x59f111f1u,0x923f82a4u,0xab1c5ed5u,
    0xd807aa98u,0x12835b01u,0x243185beu,0x550c7dc3u,0x72be5d74u,0x80deb1feu,0x9bdc06a7u,0xc19bf174u,
    0xe49b69c1u,0xefbe4786u,0x0fc19dc6u,0x240ca1ccu,0x2de92c6fu,0x4a7484aau,0x5cb0a9dcu,0x76f988dau,
    0x983e5152u,0xa831c66du,0xb00327c8u,0xbf597fc7u,0xc6e00bf3u,0xd5a79147u,0x06ca6351u,0x14292967u,
    0x27b70a85u,0x2e1b2138u,0x4d2c6dfcu,0x53380d13u,0x650a7354u,0x766a0abbu,0x81c2c92eu,0x92722c85u,
    0xa2bfe8a1u,0xa81a664bu,0xc24b8b70u,0xc76c51a3u,0xd192e819u,0xd6990624u,0xf40e3585u,0x106aa070u,
    0x19a4c116u,0x1e376c08u,0x2748774cu,0x34b0bcb5u,0x391c0cb3u,0x4ed8aa4au,0x5b9cca4fu,0x682e6ff3u,
    0x748f82eeu,0x78a5636fu,0x84c87814u,0x8cc70208u,0x90befffau,0xa4506cebu,0xbef9a3f7u,0xc67178f2u
};

static uint32_t rotate_right(uint32_t value, unsigned count) {
    return (value >> count) | (value << (32u - count));
}

static void transform(sha256_state *state, const uint8_t block[64]) {
    uint32_t schedule[64];
    for (unsigned i = 0; i < 16u; ++i) {
        const size_t offset = (size_t)i * 4u;
        schedule[i] = ((uint32_t)block[offset] << 24u) | ((uint32_t)block[offset + 1u] << 16u) |
                      ((uint32_t)block[offset + 2u] << 8u) | (uint32_t)block[offset + 3u];
    }
    for (unsigned i = 16u; i < 64u; ++i) {
        const uint32_t x = schedule[i - 15u];
        const uint32_t y = schedule[i - 2u];
        const uint32_t sigma0 = rotate_right(x, 7u) ^ rotate_right(x, 18u) ^ (x >> 3u);
        const uint32_t sigma1 = rotate_right(y, 17u) ^ rotate_right(y, 19u) ^ (y >> 10u);
        schedule[i] = schedule[i - 16u] + sigma0 + schedule[i - 7u] + sigma1;
    }

    uint32_t a = state->words[0], b = state->words[1], c = state->words[2], d = state->words[3];
    uint32_t e = state->words[4], f = state->words[5], g = state->words[6], h = state->words[7];
    for (unsigned i = 0; i < 64u; ++i) {
        const uint32_t sum1 = rotate_right(e, 6u) ^ rotate_right(e, 11u) ^ rotate_right(e, 25u);
        const uint32_t choice = (e & f) ^ (~e & g);
        const uint32_t temp1 = h + sum1 + choice + constants[i] + schedule[i];
        const uint32_t sum0 = rotate_right(a, 2u) ^ rotate_right(a, 13u) ^ rotate_right(a, 22u);
        const uint32_t majority = (a & b) ^ (a & c) ^ (b & c);
        const uint32_t temp2 = sum0 + majority;
        h = g; g = f; f = e; e = d + temp1; d = c; c = b; b = a; a = temp1 + temp2;
    }
    state->words[0] += a; state->words[1] += b; state->words[2] += c; state->words[3] += d;
    state->words[4] += e; state->words[5] += f; state->words[6] += g; state->words[7] += h;
}

static void update(sha256_state *state, const uint8_t *data, size_t length) {
    for (size_t i = 0; i < length; ++i) {
        state->block[state->block_length++] = data[i];
        state->bit_count += 8u;
        if (state->block_length == sizeof(state->block)) {
            transform(state, state->block);
            state->block_length = 0u;
        }
    }
}

void cp_sha256(const uint8_t *data, size_t length, uint8_t digest[32]) {
    sha256_state state = {{0x6a09e667u,0xbb67ae85u,0x3c6ef372u,0xa54ff53au,
                           0x510e527fu,0x9b05688cu,0x1f83d9abu,0x5be0cd19u}, 0u, {0}, 0u};
    update(&state, data, length);
    const uint64_t bit_count = state.bit_count;
    const uint8_t marker = 0x80u;
    update(&state, &marker, 1u);
    const uint8_t zero = 0u;
    while (state.block_length != 56u) update(&state, &zero, 1u);
    uint8_t length_bytes[8];
    for (unsigned i = 0; i < 8u; ++i) length_bytes[7u - i] = (uint8_t)(bit_count >> (i * 8u));
    update(&state, length_bytes, sizeof(length_bytes));
    for (unsigned i = 0; i < 8u; ++i) {
        digest[i * 4u] = (uint8_t)(state.words[i] >> 24u);
        digest[i * 4u + 1u] = (uint8_t)(state.words[i] >> 16u);
        digest[i * 4u + 2u] = (uint8_t)(state.words[i] >> 8u);
        digest[i * 4u + 3u] = (uint8_t)state.words[i];
    }
    memset(&state, 0, sizeof(state));
}

void cp_sha256_hex(const uint8_t *data, size_t length, char hex[65]) {
    static const char digits[] = "0123456789abcdef";
    uint8_t digest[32];
    cp_sha256(data, length, digest);
    for (size_t i = 0; i < sizeof(digest); ++i) {
        hex[i * 2u] = digits[digest[i] >> 4u];
        hex[i * 2u + 1u] = digits[digest[i] & 0x0fu];
    }
    hex[64] = '\0';
}
