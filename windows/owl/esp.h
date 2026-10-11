/*
 * What io_esp.c offers netutils_win.c: the radio is an ESP32 running
 * owl-radio (firmware/), not a kernel interface, and awdl0 is a Wintun adapter
 * whose neighbour table lives here rather than in the kernel.
 */
#pragma once

#include <stdbool.h>
#include <netinet/in.h>
#include <net/ethernet.h>

/* Ask the radio to move to `chan`; doesn't wait for it. */
int esp_set_channel(int chan);
/* Whether the radio can use `chan` (an ESP32-S3 has no 5 GHz). */
bool esp_has_channel(int chan);

void host_neighbor_set(const struct in6_addr *in6, const struct ether_addr *eth);
void host_neighbor_clear(const struct in6_addr *in6);
