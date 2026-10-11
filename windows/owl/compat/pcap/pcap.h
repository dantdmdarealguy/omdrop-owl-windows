/*
 * The slice of libpcap that OWL's core.c uses, for a radio that isn't a pcap
 * device: frames come from the ESP32 (io_esp.c), and pcap_dispatch hands
 * them to core.c's callback exactly as libpcap would. The dump functions write
 * a real pcap file, for owl -d.
 */
#pragma once

#include <stdint.h>
#include <stdio.h>
#include <winsock2.h> /* struct timeval */

#define DLT_IEEE802_11_RADIO 127
#define PCAP_ERRBUF_SIZE 256

typedef unsigned char u_char;
typedef struct pcap pcap_t;
typedef struct pcap_dumper pcap_dumper_t;

struct pcap_pkthdr {
	struct timeval ts;
	uint32_t caplen;
	uint32_t len;
};

typedef void (*pcap_handler)(u_char *user, const struct pcap_pkthdr *hdr, const u_char *bytes);

/* Up to cnt received frames to callback; returns how many (0: none waiting). */
int pcap_dispatch(pcap_t *p, int cnt, pcap_handler callback, u_char *user);

pcap_t *pcap_open_dead(int linktype, int snaplen);
void pcap_close(pcap_t *p);
pcap_dumper_t *pcap_dump_open_append(pcap_t *p, const char *fname);
void pcap_dump(u_char *dumper, const struct pcap_pkthdr *hdr, const u_char *bytes);
void pcap_dump_close(pcap_dumper_t *dumper);
