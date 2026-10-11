/* The glibc pieces OWL calls that MinGW lacks. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <net/ethernet.h>

#include "log.h"

/* The CRT fully buffers stdout and stderr when they're a pipe (owl.exe |
 * Tee-Object), so OWL's log arrived in 4 kB lumps, or not at all if the
 * pipeline was stopped. OWL_LOG_FILE also copies the log to a file. */
__attribute__((constructor)) static void owl_win_stdio(void)
{
	setvbuf(stdout, NULL, _IONBF, 0);
	setvbuf(stderr, NULL, _IONBF, 0);
	const char *path = getenv("OWL_LOG_FILE");
	if (path && *path) {
		FILE *f = fopen(path, "a");
		if (f) {
			setvbuf(f, NULL, _IONBF, 0);
			log_set_fp(f);
		}
	}
}

/* glibc's format: unpadded hex octets ("86:8:4:8a:2b:f4"), which the omdrop
 * tooling's peer parsing expects from OWL's log. */
char *ether_ntoa(const struct ether_addr *addr)
{
	static char buf[18];
	const uint8_t *o = addr->ether_addr_octet;
	snprintf(buf, sizeof buf, "%x:%x:%x:%x:%x:%x", o[0], o[1], o[2], o[3], o[4], o[5]);
	return buf;
}

struct ether_addr *ether_aton(const char *s)
{
	static struct ether_addr addr;
	unsigned int o[6];
	char tail;
	if (sscanf(s, "%x:%x:%x:%x:%x:%x%c", &o[0], &o[1], &o[2], &o[3], &o[4], &o[5], &tail) != 6)
		return NULL;
	for (int i = 0; i < 6; i++) {
		if (o[i] > 0xff)
			return NULL;
		addr.ether_addr_octet[i] = (uint8_t) o[i];
	}
	return &addr;
}
