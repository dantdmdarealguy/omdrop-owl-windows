#pragma once
/* Windows on x86-64 and arm64 is little-endian. */
#include <stdint.h>

#define htobe16(x) __builtin_bswap16(x)
#define htole16(x) ((uint16_t) (x))
#define be16toh(x) __builtin_bswap16(x)
#define le16toh(x) ((uint16_t) (x))
#define htobe32(x) __builtin_bswap32(x)
#define htole32(x) ((uint32_t) (x))
#define be32toh(x) __builtin_bswap32(x)
#define le32toh(x) ((uint32_t) (x))
#define htobe64(x) __builtin_bswap64(x)
#define htole64(x) ((uint64_t) (x))
#define be64toh(x) __builtin_bswap64(x)
#define le64toh(x) ((uint64_t) (x))
