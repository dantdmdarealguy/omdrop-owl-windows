#pragma once
#include <stdint.h>

#define ETHER_ADDR_LEN 6
#define ETHER_HDR_LEN 14
#define ETHER_MAX_LEN 1518
#define ETHERTYPE_IPV6 0x86dd

struct ether_addr {
	uint8_t ether_addr_octet[ETHER_ADDR_LEN];
} __attribute__((__packed__));

char *ether_ntoa(const struct ether_addr *addr);
struct ether_addr *ether_aton(const char *s);
