#include "protocol.h"

#include <ctype.h>
#include <errno.h>
#include <stdlib.h>
#include <string.h>

typedef enum json_type {
    JSON_OBJECT,
    JSON_ARRAY,
    JSON_STRING,
    JSON_NUMBER,
    JSON_TRUE,
    JSON_FALSE,
    JSON_NULL
} json_type;

typedef struct json_token {
    json_type type;
    size_t start;
    size_t end;
    size_t subtree_end;
} json_token;

typedef struct json_parser {
    const uint8_t *text;
    size_t length;
    size_t position;
    json_token *tokens;
    size_t token_count;
    size_t token_capacity;
    unsigned depth;
} json_parser;

static void skip_space(json_parser *parser) {
    while (parser->position < parser->length) {
        const uint8_t c = parser->text[parser->position];
        if (c != ' ' && c != '\t' && c != '\r' && c != '\n') {
            break;
        }
        ++parser->position;
    }
}

static bool add_token(json_parser *parser, json_type type, size_t start, size_t end, size_t *index) {
    if (parser->token_count >= parser->token_capacity) {
        return false;
    }
    const size_t token_index = parser->token_count++;
    parser->tokens[token_index] = (json_token){type, start, end, token_index + 1u};
    *index = token_index;
    return true;
}

static bool parse_string(json_parser *parser, size_t *index) {
    const size_t start = parser->position;
    if (parser->position >= parser->length || parser->text[parser->position] != '"') {
        return false;
    }
    ++parser->position;
    while (parser->position < parser->length) {
        const uint8_t c = parser->text[parser->position++];
        if (c == '"') {
            if (!add_token(parser, JSON_STRING, start, parser->position, index)) {
                return false;
            }
            return true;
        }
        if (c < 0x20u) {
            return false;
        }
        if (c != '\\') {
            continue;
        }
        if (parser->position >= parser->length) {
            return false;
        }
        const uint8_t escape = parser->text[parser->position++];
        if (escape == 'u') {
            for (unsigned i = 0; i < 4u; ++i) {
                if (parser->position >= parser->length ||
                    !isxdigit((unsigned char)parser->text[parser->position++])) {
                    return false;
                }
            }
        } else if (escape != '"' && escape != '\\' && escape != '/' && escape != 'b' &&
                   escape != 'f' && escape != 'n' && escape != 'r' && escape != 't') {
            return false;
        }
    }
    return false;
}

static bool parse_value(json_parser *parser, size_t *index);

static bool parse_object(json_parser *parser, size_t *index) {
    const size_t object_start = parser->position++;
    size_t object_index;
    if (!add_token(parser, JSON_OBJECT, object_start, object_start + 1u, &object_index)) {
        return false;
    }
    if (++parser->depth > 64u) {
        return false;
    }
    skip_space(parser);
    if (parser->position < parser->length && parser->text[parser->position] == '}') {
        ++parser->position;
    } else {
        for (;;) {
            skip_space(parser);
            size_t key_index;
            if (!parse_string(parser, &key_index)) {
                return false;
            }
            skip_space(parser);
            if (parser->position >= parser->length || parser->text[parser->position++] != ':') {
                return false;
            }
            skip_space(parser);
            size_t value_index;
            if (!parse_value(parser, &value_index)) {
                return false;
            }
            skip_space(parser);
            if (parser->position >= parser->length) {
                return false;
            }
            const uint8_t delimiter = parser->text[parser->position++];
            if (delimiter == '}') {
                break;
            }
            if (delimiter != ',') {
                return false;
            }
        }
    }
    --parser->depth;
    parser->tokens[object_index].end = parser->position;
    parser->tokens[object_index].subtree_end = parser->token_count;
    *index = object_index;
    return true;
}

static bool parse_array(json_parser *parser, size_t *index) {
    const size_t array_start = parser->position++;
    size_t array_index;
    if (!add_token(parser, JSON_ARRAY, array_start, array_start + 1u, &array_index)) {
        return false;
    }
    if (++parser->depth > 64u) {
        return false;
    }
    skip_space(parser);
    if (parser->position < parser->length && parser->text[parser->position] == ']') {
        ++parser->position;
    } else {
        for (;;) {
            size_t value_index;
            if (!parse_value(parser, &value_index)) {
                return false;
            }
            skip_space(parser);
            if (parser->position >= parser->length) {
                return false;
            }
            const uint8_t delimiter = parser->text[parser->position++];
            if (delimiter == ']') {
                break;
            }
            if (delimiter != ',') {
                return false;
            }
            skip_space(parser);
        }
    }
    --parser->depth;
    parser->tokens[array_index].end = parser->position;
    parser->tokens[array_index].subtree_end = parser->token_count;
    *index = array_index;
    return true;
}

static bool parse_number(json_parser *parser, size_t *index) {
    const size_t start = parser->position;
    if (parser->text[parser->position] == '-') {
        ++parser->position;
    }
    if (parser->position >= parser->length) {
        return false;
    }
    if (parser->text[parser->position] == '0') {
        ++parser->position;
        if (parser->position < parser->length && isdigit((unsigned char)parser->text[parser->position])) {
            return false;
        }
    } else {
        if (parser->text[parser->position] < '1' || parser->text[parser->position] > '9') {
            return false;
        }
        while (parser->position < parser->length && isdigit((unsigned char)parser->text[parser->position])) {
            ++parser->position;
        }
    }
    if (parser->position < parser->length && parser->text[parser->position] == '.') {
        ++parser->position;
        const size_t fraction_start = parser->position;
        while (parser->position < parser->length && isdigit((unsigned char)parser->text[parser->position])) {
            ++parser->position;
        }
        if (parser->position == fraction_start) {
            return false;
        }
    }
    if (parser->position < parser->length &&
        (parser->text[parser->position] == 'e' || parser->text[parser->position] == 'E')) {
        ++parser->position;
        if (parser->position < parser->length &&
            (parser->text[parser->position] == '+' || parser->text[parser->position] == '-')) {
            ++parser->position;
        }
        const size_t exponent_start = parser->position;
        while (parser->position < parser->length && isdigit((unsigned char)parser->text[parser->position])) {
            ++parser->position;
        }
        if (parser->position == exponent_start) {
            return false;
        }
    }
    return add_token(parser, JSON_NUMBER, start, parser->position, index);
}

static bool parse_literal(json_parser *parser, const char *literal, json_type type, size_t *index) {
    const size_t literal_length = strlen(literal);
    if (literal_length > parser->length - parser->position ||
        memcmp(parser->text + parser->position, literal, literal_length) != 0) {
        return false;
    }
    const size_t start = parser->position;
    parser->position += literal_length;
    return add_token(parser, type, start, parser->position, index);
}

static bool parse_value(json_parser *parser, size_t *index) {
    skip_space(parser);
    if (parser->position >= parser->length) {
        return false;
    }
    switch (parser->text[parser->position]) {
        case '{': return parse_object(parser, index);
        case '[': return parse_array(parser, index);
        case '"': return parse_string(parser, index);
        case 't': return parse_literal(parser, "true", JSON_TRUE, index);
        case 'f': return parse_literal(parser, "false", JSON_FALSE, index);
        case 'n': return parse_literal(parser, "null", JSON_NULL, index);
        default:
            if (parser->text[parser->position] == '-' || isdigit((unsigned char)parser->text[parser->position])) {
                return parse_number(parser, index);
            }
            return false;
    }
}

static bool utf8_valid(const uint8_t *text, size_t length) {
    size_t i = 0;
    while (i < length) {
        const uint8_t c = text[i++];
        if (c <= 0x7fu) continue;
        unsigned continuation_count;
        uint32_t value;
        if (c >= 0xc2u && c <= 0xdfu) { continuation_count = 1u; value = c & 0x1fu; }
        else if (c >= 0xe0u && c <= 0xefu) { continuation_count = 2u; value = c & 0x0fu; }
        else if (c >= 0xf0u && c <= 0xf4u) { continuation_count = 3u; value = c & 0x07u; }
        else return false;
        if (continuation_count > length - i) return false;
        for (unsigned j = 0; j < continuation_count; ++j) {
            const uint8_t next = text[i++];
            if ((next & 0xc0u) != 0x80u) return false;
            value = (value << 6u) | (next & 0x3fu);
        }
        if ((continuation_count == 1u && value < 0x80u) ||
            (continuation_count == 2u && value < 0x800u) ||
            (continuation_count == 3u && value < 0x10000u) ||
            (value >= 0xd800u && value <= 0xdfffu) || value > 0x10ffffu) return false;
    }
    return true;
}

static int hex_value(uint8_t c) {
    if (c >= '0' && c <= '9') return (int)(c - '0');
    if (c >= 'a' && c <= 'f') return (int)(c - 'a') + 10;
    if (c >= 'A' && c <= 'F') return (int)(c - 'A') + 10;
    return -1;
}

static bool append_utf8(char *out, size_t capacity, size_t *used, uint32_t value) {
    if (value < 0x20u || value == 0x7fu) return false;
    uint8_t bytes[4];
    size_t count;
    if (value <= 0x7fu) { bytes[0] = (uint8_t)value; count = 1u; }
    else if (value <= 0x7ffu) {
        bytes[0] = (uint8_t)(0xc0u | (value >> 6u)); bytes[1] = (uint8_t)(0x80u | (value & 0x3fu)); count = 2u;
    } else if (value <= 0xffffu && !(value >= 0xd800u && value <= 0xdfffu)) {
        bytes[0] = (uint8_t)(0xe0u | (value >> 12u)); bytes[1] = (uint8_t)(0x80u | ((value >> 6u) & 0x3fu));
        bytes[2] = (uint8_t)(0x80u | (value & 0x3fu)); count = 3u;
    } else if (value <= 0x10ffffu) {
        bytes[0] = (uint8_t)(0xf0u | (value >> 18u)); bytes[1] = (uint8_t)(0x80u | ((value >> 12u) & 0x3fu));
        bytes[2] = (uint8_t)(0x80u | ((value >> 6u) & 0x3fu)); bytes[3] = (uint8_t)(0x80u | (value & 0x3fu)); count = 4u;
    } else return false;
    if (*used + count >= capacity) return false;
    memcpy(out + *used, bytes, count);
    *used += count;
    return true;
}

static bool decode_string(const uint8_t *text, const json_token *token, char *out, size_t capacity) {
    if (token->type != JSON_STRING || token->end - token->start < 2u || capacity == 0u) return false;
    size_t used = 0;
    for (size_t i = token->start + 1u; i + 1u < token->end; ++i) {
        uint32_t value = text[i];
        if (text[i] == '\\') {
            ++i;
            switch (text[i]) {
                case '"': value = '"'; break;
                case '\\': value = '\\'; break;
                case '/': value = '/'; break;
                case 'b': value = '\b'; break;
                case 'f': value = '\f'; break;
                case 'n': value = '\n'; break;
                case 'r': value = '\r'; break;
                case 't': value = '\t'; break;
                case 'u': {
                    value = 0u;
                    for (unsigned h = 0; h < 4u; ++h) {
                        const int digit = hex_value(text[++i]);
                        if (digit < 0) return false;
                        value = (value << 4u) | (uint32_t)digit;
                    }
                    if (value >= 0xd800u && value <= 0xdbffu) {
                        if (i + 6u >= token->end || text[i + 1u] != '\\' || text[i + 2u] != 'u') return false;
                        i += 2u;
                        uint32_t low = 0u;
                        for (unsigned h = 0; h < 4u; ++h) {
                            const int digit = hex_value(text[++i]);
                            if (digit < 0) return false;
                            low = (low << 4u) | (uint32_t)digit;
                        }
                        if (low < 0xdc00u || low > 0xdfffu) return false;
                        value = 0x10000u + (((value - 0xd800u) << 10u) | (low - 0xdc00u));
                    } else if (value >= 0xdc00u && value <= 0xdfffu) return false;
                    break;
                }
                default: return false;
            }
        } else if (text[i] >= 0x80u) {
            size_t byte_count = text[i] <= 0xdfu ? 2u : text[i] <= 0xefu ? 3u : 4u;
            if (used + byte_count >= capacity || i + byte_count >= token->end) return false;
            memcpy(out + used, text + i, byte_count);
            used += byte_count;
            i += byte_count - 1u;
            continue;
        }
        if (!append_utf8(out, capacity, &used, value)) return false;
    }
    out[used] = '\0';
    return true;
}

static bool token_equals(const uint8_t *text, const json_token *token, const char *expected) {
    char decoded[256];
    return decode_string(text, token, decoded, sizeof(decoded)) && strcmp(decoded, expected) == 0;
}

static size_t object_find(const uint8_t *text, const json_token *tokens, size_t object_index, const char *key) {
    const json_token *object = &tokens[object_index];
    if (object->type != JSON_OBJECT) return SIZE_MAX;
    size_t key_index = object_index + 1u;
    while (key_index < object->subtree_end) {
        const size_t value_index = key_index + 1u;
        if (token_equals(text, &tokens[key_index], key)) return value_index;
        key_index = tokens[value_index].subtree_end;
    }
    return SIZE_MAX;
}

static bool objects_have_unique_keys(const uint8_t *text, const json_token *tokens, size_t token_count) {
    for (size_t object_index = 0; object_index < token_count; ++object_index) {
        if (tokens[object_index].type != JSON_OBJECT) continue;
        size_t key_index = object_index + 1u;
        while (key_index < tokens[object_index].subtree_end) {
            char current[256];
            if (!decode_string(text, &tokens[key_index], current, sizeof(current))) return false;
            size_t previous = object_index + 1u;
            while (previous < key_index) {
                char earlier[256];
                if (!decode_string(text, &tokens[previous], earlier, sizeof(earlier)) || strcmp(current, earlier) == 0) {
                    return false;
                }
                previous = tokens[previous + 1u].subtree_end;
            }
            key_index = tokens[key_index + 1u].subtree_end;
        }
    }
    return true;
}

static bool copy_string(const uint8_t *text, const json_token *token, char *out, size_t capacity) {
    return token != NULL && token->type == JSON_STRING && decode_string(text, token, out, capacity);
}

static bool copy_optional_string(const uint8_t *text, const json_token *tokens, size_t token_index,
                                 char *out, size_t capacity, bool *present) {
    if (token_index == SIZE_MAX) return false;
    if (tokens[token_index].type == JSON_NULL) {
        *present = false;
        return true;
    }
    *present = true;
    return copy_string(text, &tokens[token_index], out, capacity);
}

static bool parse_uint(const uint8_t *text, const json_token *token, uint64_t *out) {
    if (token->type != JSON_NUMBER || token->end == token->start || token->end - token->start >= 32u) return false;
    char buffer[32];
    const size_t length = token->end - token->start;
    memcpy(buffer, text + token->start, length);
    buffer[length] = '\0';
    for (size_t i = 0; i < length; ++i) if (!isdigit((unsigned char)buffer[i])) return false;
    errno = 0;
    const unsigned long long parsed = strtoull(buffer, NULL, 10);
    if (errno != 0) return false;
    *out = (uint64_t)parsed;
    return true;
}

static bool valid_identifier(const char *value, size_t maximum) {
    const size_t length = strlen(value);
    if (length == 0u || length > maximum) return false;
    for (size_t i = 0; i < length; ++i) {
        const unsigned char c = (unsigned char)value[i];
        if (!(isalnum(c) || c == '.' || c == '_' || c == ':' || c == '-')) return false;
    }
    return true;
}

static bool valid_digest(const char *value) {
    if (strlen(value) != 64u) return false;
    for (size_t i = 0; i < 64u; ++i) if (!isxdigit((unsigned char)value[i])) return false;
    return true;
}

static bool valid_member_names(const uint8_t *text, const json_token *tokens, size_t object_index,
                               const char *const *allowed, size_t allowed_count) {
    const json_token *object = &tokens[object_index];
    size_t key_index = object_index + 1u;
    while (key_index < object->subtree_end) {
        bool known = false;
        for (size_t i = 0; i < allowed_count; ++i) {
            if (token_equals(text, &tokens[key_index], allowed[i])) { known = true; break; }
        }
        if (!known) return false;
        key_index = tokens[key_index + 1u].subtree_end;
    }
    return true;
}

static bool parse_operation(const char *name, cp_operation *operation) {
    static const struct { const char *name; cp_operation operation; } values[] = {
        {"inspect", CP_OPERATION_INSPECT}, {"prepare", CP_OPERATION_PREPARE},
        {"renew", CP_OPERATION_RENEW}, {"startRoute", CP_OPERATION_START_ROUTE},
        {"stopRoute", CP_OPERATION_STOP_ROUTE}, {"release", CP_OPERATION_RELEASE},
        {"status", CP_OPERATION_STATUS}
    };
    for (size_t i = 0; i < sizeof(values) / sizeof(values[0]); ++i) {
        if (strcmp(name, values[i].name) == 0) { *operation = values[i].operation; return true; }
    }
    return false;
}

cp_error cp_decode_request(const uint8_t *body, size_t length, cp_request *out) {
    if (body == NULL || out == NULL || length == 0u) return CP_ERROR_MALFORMED_MESSAGE;
    if (length > CP_MAX_MESSAGE_BYTES) return CP_ERROR_MESSAGE_TOO_LARGE;
    if (!utf8_valid(body, length)) return CP_ERROR_MALFORMED_MESSAGE;

    json_parser parser = {0};
    parser.text = body;
    parser.length = length;
    parser.token_capacity = length + 1u;
    parser.tokens = (json_token *)calloc(parser.token_capacity, sizeof(json_token));
    if (parser.tokens == NULL) return CP_ERROR_MALFORMED_MESSAGE;

    size_t root_index;
    bool parsed = parse_value(&parser, &root_index);
    skip_space(&parser);
    parsed = parsed && parser.position == length && root_index == 0u && parser.tokens[0].type == JSON_OBJECT &&
             objects_have_unique_keys(body, parser.tokens, parser.token_count);
    if (parsed) {
        char *decoded = (char *)malloc(length + 1u);
        parsed = decoded != NULL;
        for (size_t i = 0; parsed && i < parser.token_count; ++i) {
            if (parser.tokens[i].type == JSON_STRING) {
                parsed = decode_string(body, &parser.tokens[i], decoded, length + 1u);
            }
        }
        free(decoded);
    }
    if (!parsed) {
        free(parser.tokens);
        return CP_ERROR_MALFORMED_MESSAGE;
    }

    static const char *const top_fields[] = {
        "messageType", "protocolVersion", "requestId", "requestSequence", "bootId", "sessionId",
        "ownerToken", "expectedGeneration", "operation", "payload"
    };
    static const char *const payload_fields[] = {"profileId", "manifestDigest", "callGeneration"};
    const size_t top_field_count = sizeof(top_fields) / sizeof(top_fields[0]);
    if (!valid_member_names(body, parser.tokens, root_index, top_fields, top_field_count)) {
        free(parser.tokens);
        return CP_ERROR_OPERATION_NOT_ALLOWED;
    }

    cp_request request = {0};
#define GET_FIELD(name) object_find(body, parser.tokens, root_index, (name))
    const size_t type_index = GET_FIELD("messageType");
    const size_t version_index = GET_FIELD("protocolVersion");
    const size_t id_index = GET_FIELD("requestId");
    const size_t sequence_index = GET_FIELD("requestSequence");
    const size_t boot_index = GET_FIELD("bootId");
    const size_t session_index = GET_FIELD("sessionId");
    const size_t owner_index = GET_FIELD("ownerToken");
    const size_t generation_index = GET_FIELD("expectedGeneration");
    const size_t operation_index = GET_FIELD("operation");
    const size_t payload_index = GET_FIELD("payload");
#undef GET_FIELD
    const bool fields_present = type_index != SIZE_MAX && version_index != SIZE_MAX && id_index != SIZE_MAX &&
        sequence_index != SIZE_MAX && boot_index != SIZE_MAX && session_index != SIZE_MAX && owner_index != SIZE_MAX &&
        generation_index != SIZE_MAX && operation_index != SIZE_MAX && payload_index != SIZE_MAX;
    if (!fields_present || !token_equals(body, &parser.tokens[type_index], "request") ||
        !copy_string(body, &parser.tokens[id_index], request.request_id, sizeof(request.request_id)) ||
        !valid_identifier(request.request_id, CP_MAX_IDENTIFIER_BYTES) ||
        !parse_uint(body, &parser.tokens[sequence_index], &request.request_sequence) || request.request_sequence == 0u ||
        !copy_string(body, &parser.tokens[boot_index], request.boot_id, sizeof(request.boot_id)) ||
        !valid_identifier(request.boot_id, CP_MAX_IDENTIFIER_BYTES) ||
        !parse_uint(body, &parser.tokens[generation_index], &request.expected_generation) ||
        parser.tokens[payload_index].type != JSON_OBJECT) {
        free(parser.tokens);
        return CP_ERROR_MALFORMED_MESSAGE;
    }
    if (!token_equals(body, &parser.tokens[version_index], "1.0")) {
        free(parser.tokens);
        return CP_ERROR_PROTOCOL_VERSION_UNSUPPORTED;
    }
    if (!copy_optional_string(body, parser.tokens, session_index, request.session_id,
                              sizeof(request.session_id), &request.has_session_id) ||
        !copy_optional_string(body, parser.tokens, owner_index, request.owner_token,
                              sizeof(request.owner_token), &request.has_owner_token) ||
        (request.has_session_id && !valid_identifier(request.session_id, CP_MAX_IDENTIFIER_BYTES)) ||
        (request.has_owner_token && (request.owner_token[0] == '\0' ||
                                     strlen(request.owner_token) > CP_OWNER_TOKEN_MAX_BYTES))) {
        free(parser.tokens);
        return CP_ERROR_MALFORMED_MESSAGE;
    }
    char operation_name[32];
    if (!copy_string(body, &parser.tokens[operation_index], operation_name, sizeof(operation_name)) ||
        !parse_operation(operation_name, &request.operation)) {
        free(parser.tokens);
        return CP_ERROR_OPERATION_NOT_ALLOWED;
    }
    if (!valid_member_names(body, parser.tokens, payload_index, payload_fields,
                            sizeof(payload_fields) / sizeof(payload_fields[0]))) {
        free(parser.tokens);
        return CP_ERROR_OPERATION_NOT_ALLOWED;
    }

    const size_t profile_index = object_find(body, parser.tokens, payload_index, "profileId");
    const size_t digest_index = object_find(body, parser.tokens, payload_index, "manifestDigest");
    const size_t call_index = object_find(body, parser.tokens, payload_index, "callGeneration");
    bool payload_ok = true;
    if (request.operation == CP_OPERATION_PREPARE) {
        payload_ok = profile_index != SIZE_MAX && digest_index != SIZE_MAX && call_index == SIZE_MAX &&
            copy_string(body, &parser.tokens[profile_index], request.profile_id, sizeof(request.profile_id)) &&
            valid_identifier(request.profile_id, CP_MAX_PROFILE_BYTES) &&
            copy_string(body, &parser.tokens[digest_index], request.manifest_digest, sizeof(request.manifest_digest)) &&
            valid_digest(request.manifest_digest);
    } else if (request.operation == CP_OPERATION_START_ROUTE || request.operation == CP_OPERATION_STOP_ROUTE) {
        payload_ok = call_index != SIZE_MAX && profile_index == SIZE_MAX && digest_index == SIZE_MAX &&
            copy_string(body, &parser.tokens[call_index], request.call_generation, sizeof(request.call_generation)) &&
            valid_identifier(request.call_generation, CP_MAX_IDENTIFIER_BYTES);
    } else {
        payload_ok = profile_index == SIZE_MAX && digest_index == SIZE_MAX && call_index == SIZE_MAX;
    }
    free(parser.tokens);
    if (!payload_ok) return CP_ERROR_PAYLOAD_INVALID;
    *out = request;
    return CP_ERROR_NONE;
}

cp_error cp_read_frame(FILE *input, uint8_t **body, size_t *length) {
    if (input == NULL || body == NULL || length == NULL) return CP_ERROR_MALFORMED_MESSAGE;
    *body = NULL;
    *length = 0u;
    uint8_t header[4];
    if (fread(header, 1u, sizeof(header), input) != sizeof(header)) return CP_ERROR_MALFORMED_MESSAGE;
    const size_t size = ((size_t)header[0] << 24u) | ((size_t)header[1] << 16u) |
                       ((size_t)header[2] << 8u) | (size_t)header[3];
    if (size == 0u) return CP_ERROR_MALFORMED_MESSAGE;
    if (size > CP_MAX_MESSAGE_BYTES) return CP_ERROR_MESSAGE_TOO_LARGE;
    uint8_t *allocated = (uint8_t *)malloc(size);
    if (allocated == NULL) return CP_ERROR_MALFORMED_MESSAGE;
    if (fread(allocated, 1u, size, input) != size) {
        free(allocated);
        return CP_ERROR_MALFORMED_MESSAGE;
    }
    *body = allocated;
    *length = size;
    return CP_ERROR_NONE;
}

bool cp_write_frame(FILE *output, const char *body, size_t length) {
    if (output == NULL || body == NULL || length == 0u || length > CP_MAX_MESSAGE_BYTES) return false;
    const uint8_t header[4] = {(uint8_t)(length >> 24u), (uint8_t)(length >> 16u),
                               (uint8_t)(length >> 8u), (uint8_t)length};
    return fwrite(header, 1u, sizeof(header), output) == sizeof(header) &&
           fwrite(body, 1u, length, output) == length && fflush(output) == 0;
}

const char *cp_error_name(cp_error error) {
    static const char *const names[] = {
        "NONE", "MALFORMED_MESSAGE", "MESSAGE_TOO_LARGE", "PROTOCOL_VERSION_UNSUPPORTED",
        "OPERATION_NOT_ALLOWED", "PAYLOAD_INVALID", "REQUEST_ID_CONFLICT", "REQUEST_SEQUENCE_STALE",
        "OWNER_MISMATCH", "BOOT_MISMATCH", "GENERATION_MISMATCH", "OPERATION_CONFLICT",
        "ROOT_UNAVAILABLE", "UNSUPPORTED_PROFILE", "DEVICE_BUSY_DATA", "CALL_STATE_UNKNOWN",
        "RESTORE_CONFLICT", "RESTORE_PENDING_CALL", "RECOVERY_REQUIRED"
    };
    const size_t count = sizeof(names) / sizeof(names[0]);
    return (unsigned)error < count ? names[(unsigned)error] : "MALFORMED_MESSAGE";
}

const char *cp_operation_name(cp_operation operation) {
    static const char *const names[] = {"inspect", "prepare", "renew", "startRoute", "stopRoute", "release", "status"};
    return (unsigned)operation < sizeof(names) / sizeof(names[0]) ? names[(unsigned)operation] : "unknown";
}
