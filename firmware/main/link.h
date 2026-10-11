/* The serial link to the host: framing for proto.h's messages. */
#pragma once

#include <stddef.h>
#include <stdint.h>

void link_init(void);

/* Send one message: type, then a, then b (either may be NULL). Thread-safe. */
void link_send(uint8_t type, const void *a, size_t alen, const void *b, size_t blen);

/* Block for the next message whose CRC checks out; returns its length (type
 * and body, CRC removed) or -1 for a message that didn't decode. */
int link_recv(uint8_t *buf, size_t cap);
