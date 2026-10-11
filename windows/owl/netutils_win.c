/*
 * OWL's netutils.h on Windows. Most of it was nl80211 and rtnetlink work that
 * has no counterpart here: the radio is an ESP32 (esp.h), always in
 * "monitor mode", and awdl0's neighbours live in io_esp.c.
 */
#include <windows.h>
#include <errno.h>
#include <string.h>

#include "netutils.h"
#include "esp.h"
#include "log.h"

int netutils_init()
{
	return 0;
}

void netutils_cleanup()
{
}

int set_monitor_mode(int ifindex)
{
	(void) ifindex;
	return 0;
}

int is_channel_available(int ifindex, int channel, bool *is_available)
{
	(void) ifindex;
	*is_available = esp_has_channel(channel);
	return 0;
}

int set_channel(int ifindex, int channel)
{
	(void) ifindex;
	return esp_set_channel(channel);
}

/* The radio's replies arrive on io_esp.c's reader thread; nothing to drain. */
int set_channel_fd(void)
{
	return -1;
}

void set_channel_drain(void)
{
}

int link_up(int ifindex)
{
	(void) ifindex;
	return 0;
}

int link_down(int ifindex)
{
	(void) ifindex;
	return 0;
}

int link_ether_addr_get(const char *ifname, struct ether_addr *addr)
{
	(void) ifname;
	(void) addr;
	return -ENOSYS; /* io_esp.c takes the radio's MAC from its HELLO */
}

int get_hostname(char *name, size_t len)
{
	DWORD n = (DWORD) len;
	if (!GetComputerNameExA(ComputerNameDnsHostname, name, &n))
		return -EINVAL;
	return 0;
}

int neighbor_add(int ifindex, const struct ether_addr *eth, const struct in6_addr *in6)
{
	(void) ifindex;
	host_neighbor_set(in6, eth);
	return 0;
}

int neighbor_remove(int ifindex, const struct in6_addr *in6)
{
	(void) ifindex;
	host_neighbor_clear(in6);
	return 0;
}

/* From upstream netutils.c. */

int neighbor_add_rfc4291(int ifindex, const struct ether_addr *addr)
{
	struct in6_addr in6;
	rfc4291_addr(addr, &in6);
	return neighbor_add(ifindex, addr, &in6);
}

int neighbor_remove_rfc4291(int ifindex, const struct ether_addr *addr)
{
	struct in6_addr in6;
	rfc4291_addr(addr, &in6);
	return neighbor_remove(ifindex, &in6);
}

void rfc4291_addr(const struct ether_addr *eth, struct in6_addr *in6)
{
	memset(in6, 0, sizeof(struct in6_addr));
	in6->s6_addr[0] = 0xfe;
	in6->s6_addr[1] = 0x80;
	in6->s6_addr[8] = eth->ether_addr_octet[0] ^ 0x02;
	in6->s6_addr[9] = eth->ether_addr_octet[1];
	in6->s6_addr[10] = eth->ether_addr_octet[2];
	in6->s6_addr[11] = 0xff;
	in6->s6_addr[12] = 0xfe;
	in6->s6_addr[13] = eth->ether_addr_octet[3];
	in6->s6_addr[14] = eth->ether_addr_octet[4];
	in6->s6_addr[15] = eth->ether_addr_octet[5];
}
