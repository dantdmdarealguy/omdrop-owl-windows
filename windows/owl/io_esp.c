/*
 * OWL's io.h on Windows. The WLAN side is an ESP32 running owl-radio over a
 * serial port (firmware/main/proto.h): it does what a monitor interface with
 * injection does on Linux. The host side is awdl0, a Wintun adapter.
 *
 * WLAN: a reader thread decodes the radio's messages and queues received
 *   frames, with a radiotap header like the kernel's, for pcap_dispatch
 *   (core.c's wlan_device_ready). wlan_send strips OWL's radiotap header and
 *   queues the frame, with its rate, for a writer thread, so the event loop
 *   never waits on the serial line.
 * Host: OWL speaks Ethernet (on Linux awdl0 is a tap), Wintun carries bare IP
 *   packets. host_send drops the Ethernet header; host_recv adds one, with the
 *   destination MAC from the neighbour table OWL fills (neighbor_add), what
 *   peers' own frames taught us, or the address's RFC 4291 interface ID.
 *   Nothing answers neighbour solicitations on an L3 adapter, so for our own
 *   address this does.
 *
 * Readiness, as ev_win.c needs it: a pseudo-fd is readable while a handle is
 * signaled. rx_ready is set while frames are queued; host_ready while Wintun
 * may still have packets or a reply of ours is waiting, and Wintun's own read
 * event wakes us when it gets new ones.
 */
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <netioapi.h>

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <ev.h>
#include <radiotap.h>
#include <radiotap_iter.h>

#include "esp.h"
#include "io.h"
#include "log.h"
#include "netutils.h"
#include "proto.h"
#include "state.h" /* clock_time_us */
#include "wintun.h"

#define WLAN_FD 7701
#define HOST_FD 7702
#define DEFAULT_BAUD 3000000

#define RT_LEN 23 /* the radiotap header we put on received frames */
#define RXQ_LEN 256
#define TXQ_LEN 128
#define RAW_MAX (1 + 16 + PROTO_MAX_FRAME + 2)
#define COBS_MAX (RAW_MAX + RAW_MAX / 254 + 2)
#define AWDL_MTU 1450
#define NEIGHBORS 64
#define REPLIES 8

static const uint8_t AWDL_BSSID_BYTES[6] = {0x00, 0x25, 0x00, 0xff, 0x94, 0x73};
static const GUID AWDL_GUID = {0x6f776c30, 0x6177, 0x646c, {0x8a, 0x5e, 0x30, 0x77, 0x69, 0x6e, 0x30, 0x31}};

/* ------------------------------------------------------------------ framing */

static uint16_t crc16(const uint8_t *p, size_t n)
{
	uint16_t crc = 0xffff;
	while (n--) {
		crc ^= (uint16_t) *p++ << 8;
		for (int i = 0; i < 8; i++)
			crc = crc & 0x8000 ? (crc << 1) ^ 0x1021 : crc << 1;
	}
	return crc;
}

static size_t cobs_encode(const uint8_t *in, size_t n, uint8_t *out)
{
	size_t code_at = 0, o = 1;
	uint8_t code = 1;
	for (size_t i = 0; i < n; i++) {
		if (in[i]) {
			out[o++] = in[i];
			code++;
		}
		if (!in[i] || code == 0xff) {
			out[code_at] = code;
			code_at = o++;
			code = 1;
		}
	}
	out[code_at] = code;
	out[o++] = 0;
	return o;
}

static int cobs_decode(uint8_t *buf, size_t n)
{
	size_t i = 0, o = 0;
	while (i < n) {
		uint8_t code = buf[i++];
		if (!code || i + code - 1 > n)
			return -1;
		for (uint8_t k = 1; k < code; k++)
			buf[o++] = buf[i++];
		if (code != 0xff && i < n)
			buf[o++] = 0;
	}
	return (int) o;
}

/* -------------------------------------------------------------------- radio */

struct rx_frame {
	uint32_t len;
	uint64_t t_us;
	uint8_t data[RT_LEN + PROTO_MAX_FRAME];
};

struct tx_msg {
	uint32_t len;
	uint8_t data[COBS_MAX];
};

static struct {
	HANDLE com;
	HANDLE reader, writer;
	volatile LONG stop;

	CRITICAL_SECTION rx_lock;
	struct rx_frame *rxq;
	int rx_head, rx_count;
	HANDLE rx_ready;

	CRITICAL_SECTION tx_lock;
	struct tx_msg *txq;
	int tx_head, tx_count;
	HANDLE tx_wake;

	/* One synchronous request at a time, only while starting up. */
	HANDLE reply_event;
	volatile int want_type, want_req;
	uint8_t reply[64];
	int reply_len;

	struct {
		uint8_t proto;
		uint8_t mac[6];
		char target[17];
		char idf[33];
	} hello;

	volatile LONG tx_errors, tx_acked, tx_unacked, rx_frames, rx_dropped, bad;

	/* The radio's receive clock against clock_time_us() (reader thread only). */
	uint32_t ts_last;
	uint64_t ts_high;
	int64_t offset; /* host - radio, us: the least-delayed frame's */
	int have_offset;
	uint64_t delay_sum, delay_n, delay_max, delay_report;
} radio;

/*
 * When the radio heard a frame, in clock_time_us()'s clock, for OWL's sync
 * (owl -T, patches/owl-07). A frame reaches us a variable while after the
 * radio heard it: the serial line, USB polling, thread wakeups. The fastest
 * frames bound the clock offset from above, so the offset follows the lower
 * envelope of host-minus-radio, allowed to creep up 1 us per frame for the two
 * crystals' drift. Each frame's delay is reported, as a measure of what -T
 * saves sync from.
 */
static uint64_t rx_host_time(uint32_t ts)
{
	uint64_t now = clock_time_us();
	if (ts < radio.ts_last)
		radio.ts_high += 1ull << 32; /* the radio's 32-bit us counter wraps every 71 minutes */
	radio.ts_last = ts;
	int64_t sample = (int64_t) now - (int64_t) (radio.ts_high | ts);
	if (!radio.have_offset || sample < radio.offset + 1) {
		radio.offset = sample;
		radio.have_offset = 1;
	} else {
		radio.offset += 1;
	}
	uint64_t delay = (uint64_t) (sample - radio.offset);
	radio.delay_sum += delay;
	radio.delay_n++;
	if (delay > radio.delay_max)
		radio.delay_max = delay;
	if (now - radio.delay_report > 10000000 && radio.delay_n) {
		log_info("radio: frames reach OWL %.1f ms after the radio hears them (mean; max %.1f ms)",
		         radio.delay_sum / (double) radio.delay_n / 1000.0, radio.delay_max / 1000.0);
		radio.delay_sum = radio.delay_n = radio.delay_max = 0;
		radio.delay_report = now;
	}
	return (uint64_t) ((int64_t) (radio.ts_high | ts) + radio.offset);
}

static void radio_queue(uint8_t type, const void *a, size_t alen, const void *b, size_t blen)
{
	uint8_t raw[RAW_MAX];
	if (1 + alen + blen + 2 > sizeof raw)
		return;
	size_t n = 0;
	raw[n++] = type;
	if (alen) memcpy(raw + n, a, alen), n += alen;
	if (blen) memcpy(raw + n, b, blen), n += blen;
	uint16_t crc = crc16(raw, n);
	raw[n++] = crc & 0xff;
	raw[n++] = crc >> 8;

	EnterCriticalSection(&radio.tx_lock);
	if (radio.tx_count == TXQ_LEN) {
		LeaveCriticalSection(&radio.tx_lock);
		InterlockedIncrement(&radio.tx_errors);
		return;
	}
	struct tx_msg *m = &radio.txq[(radio.tx_head + radio.tx_count) % TXQ_LEN];
	m->len = (uint32_t) cobs_encode(raw, n, m->data);
	radio.tx_count++;
	LeaveCriticalSection(&radio.tx_lock);
	SetEvent(radio.tx_wake);
}

static DWORD WINAPI writer_main(void *arg)
{
	(void) arg;
	OVERLAPPED ov = {.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL)};
	struct tx_msg m;
	while (!radio.stop) {
		WaitForSingleObject(radio.tx_wake, 100);
		for (;;) {
			EnterCriticalSection(&radio.tx_lock);
			if (!radio.tx_count) {
				LeaveCriticalSection(&radio.tx_lock);
				break;
			}
			m = radio.txq[radio.tx_head];
			radio.tx_head = (radio.tx_head + 1) % TXQ_LEN;
			radio.tx_count--;
			LeaveCriticalSection(&radio.tx_lock);

			DWORD done = 0;
			ResetEvent(ov.hEvent);
			if (!WriteFile(radio.com, m.data, m.len, &done, &ov) && GetLastError() == ERROR_IO_PENDING)
				GetOverlappedResult(radio.com, &ov, &done, TRUE);
			if (done != m.len && !radio.stop)
				log_warn("radio: short write (%lu of %u bytes)", done, m.len);
		}
	}
	CloseHandle(ov.hEvent);
	return 0;
}

/* The kernel's radiotap on Linux had more; core.c reads flags and signal. */
static void rx_push(const uint8_t *body, size_t len)
{
	if (len < 10)
		return;
	uint32_t ts;
	memcpy(&ts, body, 4);
	int8_t rssi = (int8_t) body[4];
	uint8_t ch = body[5];
	const uint8_t *f = body + 10;
	size_t flen = len - 10;
	/* What upstream's pcap filter passed: "wlan addr3 00:25:00:ff:94:73". */
	if (flen < 24 || flen > PROTO_MAX_FRAME || memcmp(f + 16, AWDL_BSSID_BYTES, 6))
		return;

	EnterCriticalSection(&radio.rx_lock);
	if (radio.rx_count == RXQ_LEN) {
		LeaveCriticalSection(&radio.rx_lock);
		InterlockedIncrement(&radio.rx_dropped);
		return;
	}
	struct rx_frame *r = &radio.rxq[(radio.rx_head + radio.rx_count) % RXQ_LEN];
	uint8_t *p = r->data;
	uint16_t freq = ch == 14 ? 2484 : ch < 14 ? 2407 + 5 * ch : 5000 + 5 * ch;
	uint16_t chflags = ch <= 14 ? 0x0080 : 0x0100;
	uint64_t tsft = rx_host_time(ts);
	uint32_t present = 1u << IEEE80211_RADIOTAP_TSFT | 1u << IEEE80211_RADIOTAP_FLAGS |
	                   1u << IEEE80211_RADIOTAP_CHANNEL | 1u << IEEE80211_RADIOTAP_DBM_ANTSIGNAL;
	memset(p, 0, RT_LEN);
	p[2] = RT_LEN; /* it_len, little-endian */
	memcpy(p + 4, &present, 4);
	memcpy(p + 8, &tsft, 8);   /* TSFT, 8-aligned: clock_time_us() at reception (owl -T) */
	p[16] = 0;                 /* flags: no FCS, the radio strips it */
	memcpy(p + 18, &freq, 2);  /* channel, 2-aligned */
	memcpy(p + 20, &chflags, 2);
	p[22] = (uint8_t) rssi;
	memcpy(p + RT_LEN, f, flen);
	r->len = (uint32_t) (RT_LEN + flen);
	{
		LARGE_INTEGER c, q;
		QueryPerformanceCounter(&c);
		QueryPerformanceFrequency(&q);
		r->t_us = (uint64_t) (c.QuadPart / (double) q.QuadPart * 1e6);
	}
	radio.rx_count++;
	SetEvent(radio.rx_ready);
	LeaveCriticalSection(&radio.rx_lock);
	InterlockedIncrement(&radio.rx_frames);
}

static void dispatch(uint8_t *raw, int n)
{
	if (n < 3 || crc16(raw, n - 2) != (raw[n - 2] | raw[n - 1] << 8)) {
		InterlockedIncrement(&radio.bad); /* boot ROM text and line noise */
		return;
	}
	uint8_t type = raw[0];
	const uint8_t *body = raw + 1;
	int len = n - 3;

	switch (type) {
	case MSG_RX:
		rx_push(body, len);
		return;
	case MSG_TX_DONE:
		InterlockedIncrement(len >= 1 && body[0] ? &radio.tx_acked : &radio.tx_unacked);
		return;
	case MSG_LOG:
		log_info("radio: %.*s", len, body);
		return;
	case MSG_ACK:
		if (len >= 5 && body[0] == MSG_TX) {
			int32_t err;
			memcpy(&err, body + 1, 4);
			if (err)
				InterlockedIncrement(&radio.tx_errors);
			return;
		}
		if (radio.want_type != MSG_ACK || len < 1 || body[0] != radio.want_req)
			return;
		break;
	default:
		if (type != radio.want_type)
			return;
	}
	radio.reply_len = len < (int) sizeof radio.reply ? len : (int) sizeof radio.reply;
	memcpy(radio.reply, body, radio.reply_len);
	radio.want_type = -1;
	SetEvent(radio.reply_event);
}

static DWORD WINAPI reader_main(void *arg)
{
	(void) arg;
	OVERLAPPED ov = {.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL)};
	static uint8_t chunk[8192], acc[COBS_MAX];
	size_t n = 0;
	int overflow = 0;
	while (!radio.stop) {
		DWORD got = 0;
		ResetEvent(ov.hEvent);
		if (!ReadFile(radio.com, chunk, sizeof chunk, &got, &ov)) {
			if (GetLastError() != ERROR_IO_PENDING || !GetOverlappedResult(radio.com, &ov, &got, TRUE)) {
				if (!radio.stop)
					log_error("radio: serial read failed (%lu); unplugged?", GetLastError());
				break;
			}
		}
		for (DWORD i = 0; i < got; i++) {
			uint8_t c = chunk[i];
			if (c) {
				if (n < sizeof acc) acc[n++] = c;
				else overflow = 1;
				continue;
			}
			if (n && !overflow) {
				int len = cobs_decode(acc, n);
				if (len > 0)
					dispatch(acc, len);
				else
					InterlockedIncrement(&radio.bad);
			}
			n = 0;
			overflow = 0;
		}
	}
	CloseHandle(ov.hEvent);
	return 0;
}

/* Send and wait for `expect` (for MSG_ACK, the ACK of `type`). Returns the
 * reply's length, or -1 on timeout. Startup only: one at a time. */
static int radio_request(uint8_t type, const void *body, size_t len, uint8_t expect, DWORD timeout_ms)
{
	ResetEvent(radio.reply_event);
	radio.want_req = type;
	radio.want_type = expect;
	radio_queue(type, body, len, NULL, 0);
	if (WaitForSingleObject(radio.reply_event, timeout_ms) != WAIT_OBJECT_0) {
		radio.want_type = -1;
		return -1;
	}
	return radio.reply_len;
}

static int radio_command(uint8_t type, const void *body, size_t len, const char *what)
{
	if (radio_request(type, body, len, MSG_ACK, 3000) < 5) {
		log_error("radio: no reply to %s", what);
		return -ETIMEDOUT;
	}
	int32_t err;
	memcpy(&err, radio.reply + 1, 4);
	if (err) {
		log_error("radio: %s failed (esp_err 0x%x)", what, (unsigned) err);
		return -EIO;
	}
	return 0;
}

static int radio_hello(void)
{
	for (int i = 0; i < 10; i++) {
		int n = radio_request(MSG_HELLO, NULL, 0, MSG_HELLO_RESP, 300);
		if (n >= 55) {
			radio.hello.proto = radio.reply[0];
			memcpy(radio.hello.mac, radio.reply + 1, 6);
			memcpy(radio.hello.target, radio.reply + 7, 16);
			memcpy(radio.hello.idf, radio.reply + 23, 32);
			return 0;
		}
	}
	return -1;
}

/* "COM9" or "COM9@115200". */
static int radio_open(const char *spec)
{
	char port[64], path[80];
	unsigned long baud = DEFAULT_BAUD;
	const char *at = strchr(spec, '@');
	size_t plen = at ? (size_t) (at - spec) : strlen(spec);
	if (!plen || plen >= sizeof port || _strnicmp(spec, "COM", 3)) {
		log_error("-i takes the ESP32's serial port, like COM9 or COM9@115200, not '%s'", spec);
		return -EINVAL;
	}
	memcpy(port, spec, plen);
	port[plen] = 0;
	if (at)
		baud = strtoul(at + 1, NULL, 10);
	snprintf(path, sizeof path, "\\\\.\\%s", port);

	radio.com = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
	if (radio.com == INVALID_HANDLE_VALUE) {
		log_error("radio: can't open %s (error %lu; in use by another program?)", port, GetLastError());
		return -ENOENT;
	}
	SetupComm(radio.com, 1 << 16, 1 << 16);
	DCB dcb = {.DCBlength = sizeof dcb};
	GetCommState(radio.com, &dcb);
	dcb.BaudRate = baud;
	dcb.ByteSize = 8;
	dcb.Parity = NOPARITY;
	dcb.StopBits = ONESTOPBIT;
	dcb.fBinary = TRUE;
	dcb.fParity = FALSE;
	dcb.fOutxCtsFlow = dcb.fOutxDsrFlow = FALSE;
	/* DTR and RTS drive the DevKit's IO0 and EN: low, so the chip isn't held
	 * in reset or sent to its bootloader. */
	dcb.fDtrControl = DTR_CONTROL_DISABLE;
	dcb.fRtsControl = RTS_CONTROL_DISABLE;
	dcb.fOutX = dcb.fInX = FALSE;
	if (!SetCommState(radio.com, &dcb)) {
		log_error("radio: can't set %s to %lu baud (error %lu)", port, baud, GetLastError());
		return -EIO;
	}
	/* Return as soon as anything arrives, or after 100 ms of nothing. */
	COMMTIMEOUTS t = {.ReadIntervalTimeout = MAXDWORD, .ReadTotalTimeoutMultiplier = MAXDWORD,
	                  .ReadTotalTimeoutConstant = 100};
	SetCommTimeouts(radio.com, &t);
	PurgeComm(radio.com, PURGE_RXCLEAR | PURGE_TXCLEAR);

	InitializeCriticalSection(&radio.rx_lock);
	InitializeCriticalSection(&radio.tx_lock);
	radio.rxq = calloc(RXQ_LEN, sizeof *radio.rxq);
	radio.txq = calloc(TXQ_LEN, sizeof *radio.txq);
	radio.rx_ready = CreateEventW(NULL, TRUE, FALSE, NULL);
	radio.tx_wake = CreateEventW(NULL, FALSE, FALSE, NULL);
	radio.reply_event = CreateEventW(NULL, TRUE, FALSE, NULL);
	radio.want_type = -1;
	radio.reader = CreateThread(NULL, 0, reader_main, NULL, 0, NULL);
	radio.writer = CreateThread(NULL, 0, writer_main, NULL, 0, NULL);
	SetThreadPriority(radio.reader, THREAD_PRIORITY_TIME_CRITICAL);

	if (radio_hello() < 0) {
		log_error("radio: no owl-radio answering on %s at %lu baud (flashed? the board's UART port?)", port, baud);
		return -ENODEV;
	}
	log_info("radio: %s on %s, owl-radio protocol %d, ESP-IDF %s", radio.hello.target, port,
	         radio.hello.proto, radio.hello.idf);
	if (radio.hello.proto != PROTO_VERSION) {
		log_error("radio: firmware speaks protocol %d, this owl %d; reflash firmware/",
		          radio.hello.proto, PROTO_VERSION);
		return -EPROTO;
	}
	uint8_t mode = FILTER_AWDL;
	return radio_command(MSG_SET_FILTER, &mode, 1, "set filter");
}

int esp_set_channel(int chan)
{
	uint8_t c = (uint8_t) chan;
	if (radio.com == NULL || radio.com == INVALID_HANDLE_VALUE)
		return -ENODEV;
	radio_queue(MSG_SET_CHANNEL, &c, 1, NULL, 0);
	return 0;
}

bool esp_has_channel(int chan)
{
	if (chan <= 14)
		return true;
	/* 5 GHz: only chips that have it. */
	return !strcmp(radio.hello.target, "esp32c5") || !strcmp(radio.hello.target, "esp32c6") ||
	       !strcmp(radio.hello.target, "esp32c61");
}

/* ------------------------------------------------------------- pcap shim */

struct pcap {
	int dead;
};
struct pcap_dumper {
	FILE *f;
};
static struct pcap radio_pcap;

int pcap_dispatch(pcap_t *p, int cnt, pcap_handler callback, u_char *user)
{
	static struct rx_frame cur;
	int n = 0;
	(void) p;
	while (n < cnt) {
		EnterCriticalSection(&radio.rx_lock);
		if (!radio.rx_count) {
			ResetEvent(radio.rx_ready);
			LeaveCriticalSection(&radio.rx_lock);
			break;
		}
		cur = radio.rxq[radio.rx_head];
		radio.rx_head = (radio.rx_head + 1) % RXQ_LEN;
		if (!--radio.rx_count)
			ResetEvent(radio.rx_ready);
		LeaveCriticalSection(&radio.rx_lock);

		struct pcap_pkthdr hdr = {
			.ts = {.tv_sec = (long) (cur.t_us / 1000000), .tv_usec = (long) (cur.t_us % 1000000)},
			.caplen = cur.len,
			.len = cur.len,
		};
		callback(user, &hdr, cur.data);
		n++;
	}
	return n;
}

pcap_t *pcap_open_dead(int linktype, int snaplen)
{
	(void) linktype;
	(void) snaplen;
	pcap_t *p = calloc(1, sizeof *p);
	if (p)
		p->dead = 1;
	return p;
}

void pcap_close(pcap_t *p)
{
	if (p && p->dead)
		free(p);
}

pcap_dumper_t *pcap_dump_open_append(pcap_t *p, const char *fname)
{
	(void) p;
	FILE *f = fopen(fname, "ab");
	if (!f)
		return NULL;
	fseek(f, 0, SEEK_END);
	if (ftell(f) == 0) {
		struct { uint32_t magic; uint16_t major, minor; int32_t zone; uint32_t sigfigs, snaplen, linktype; } h =
			{0xa1b2c3d4, 2, 4, 0, 0, 65535, DLT_IEEE802_11_RADIO};
		fwrite(&h, sizeof h, 1, f);
	}
	pcap_dumper_t *d = malloc(sizeof *d);
	if (!d) {
		fclose(f);
		return NULL;
	}
	d->f = f;
	return d;
}

void pcap_dump(u_char *dumper, const struct pcap_pkthdr *hdr, const u_char *bytes)
{
	pcap_dumper_t *d = (pcap_dumper_t *) dumper;
	if (!d)
		return;
	uint32_t rec[4] = {(uint32_t) hdr->ts.tv_sec, (uint32_t) hdr->ts.tv_usec, hdr->caplen, hdr->len};
	fwrite(rec, sizeof rec, 1, d->f);
	fwrite(bytes, 1, hdr->caplen, d->f);
}

void pcap_dump_close(pcap_dumper_t *d)
{
	if (d) {
		fclose(d->f);
		free(d);
	}
}

/* --------------------------------------------------------------- awdl0 */

static struct {
	HMODULE dll;
	WINTUN_CREATE_ADAPTER_FUNC *CreateAdapter;
	WINTUN_CLOSE_ADAPTER_FUNC *CloseAdapter;
	WINTUN_GET_ADAPTER_LUID_FUNC *GetAdapterLUID;
	WINTUN_START_SESSION_FUNC *StartSession;
	WINTUN_END_SESSION_FUNC *EndSession;
	WINTUN_GET_READ_WAIT_EVENT_FUNC *GetReadWaitEvent;
	WINTUN_RECEIVE_PACKET_FUNC *ReceivePacket;
	WINTUN_RELEASE_RECEIVE_PACKET_FUNC *ReleaseReceivePacket;
	WINTUN_ALLOCATE_SEND_PACKET_FUNC *AllocateSendPacket;
	WINTUN_SEND_PACKET_FUNC *SendPacket;
	WINTUN_SET_LOGGER_FUNC *SetLogger;

	WINTUN_ADAPTER_HANDLE adapter;
	WINTUN_SESSION_HANDLE session;
	NET_LUID luid;
	HANDLE ready; /* host_ready */

	struct ether_addr self;
	struct in6_addr self_ll;

	struct {
		int used;
		struct in6_addr ip;
		struct ether_addr mac;
	} nb[NEIGHBORS];
	int nb_next;

	/* Frames of our own (neighbour advertisements) for host_recv to return. */
	struct {
		int len;
		uint8_t data[14 + 40 + 32];
	} replies[REPLIES];
	int nreplies;
} tun;

static void CALLBACK wintun_log(WINTUN_LOGGER_LEVEL level, DWORD64 timestamp, LPCWSTR message)
{
	char msg[512];
	(void) timestamp;
	WideCharToMultiByte(CP_UTF8, 0, message, -1, msg, sizeof msg, NULL, NULL);
	if (level == WINTUN_LOG_ERR)
		log_error("wintun: %s", msg);
	else if (level == WINTUN_LOG_WARN)
		log_warn("wintun: %s", msg);
	else
		log_info("wintun: %s", msg);
}

static int wintun_load(void)
{
	tun.dll = LoadLibraryExW(L"wintun.dll", NULL, LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
	if (!tun.dll) {
		log_error("awdl0: wintun.dll not found beside owl.exe (https://www.wintun.net)");
		return -ENOENT;
	}
#define LOAD(name) ((tun.name = (void *) GetProcAddress(tun.dll, "Wintun" #name)) != NULL)
	if (!LOAD(CreateAdapter) || !LOAD(CloseAdapter) || !LOAD(GetAdapterLUID) || !LOAD(StartSession) ||
	    !LOAD(EndSession) || !LOAD(GetReadWaitEvent) || !LOAD(ReceivePacket) || !LOAD(ReleaseReceivePacket) ||
	    !LOAD(AllocateSendPacket) || !LOAD(SendPacket) || !LOAD(SetLogger)) {
		log_error("awdl0: wintun.dll is missing functions; wrong version?");
		return -ENOENT;
	}
#undef LOAD
	tun.SetLogger(wintun_log);
	return 0;
}

/* What the kernel did for a tap on Linux: our link-local address, the MTU, and
 * no duplicate address detection or router discovery. Windows also gives a
 * new adapter a random link-local address of its own, which peers can't map
 * to our MAC, so that goes. */
static void adapter_configure(void)
{
	MIB_IPINTERFACE_ROW row;
	InitializeIpInterfaceEntry(&row);
	row.Family = AF_INET6;
	row.InterfaceLuid = tun.luid;
	if (GetIpInterfaceEntry(&row) == NO_ERROR) {
		row.NlMtu = AWDL_MTU;
		row.DadTransmits = 0;
		row.RouterDiscoveryBehavior = RouterDiscoveryDisabled;
		row.SitePrefixLength = 0;
		DWORD err = SetIpInterfaceEntry(&row);
		if (err != NO_ERROR)
			log_warn("awdl0: could not set MTU and IPv6 options (%lu)", err);
	}

	PMIB_UNICASTIPADDRESS_TABLE table;
	if (GetUnicastIpAddressTable(AF_INET6, &table) == NO_ERROR) {
		for (ULONG i = 0; i < table->NumEntries; i++) {
			MIB_UNICASTIPADDRESS_ROW *a = &table->Table[i];
			if (a->InterfaceLuid.Value == tun.luid.Value &&
			    memcmp(&a->Address.Ipv6.sin6_addr, &tun.self_ll, 16))
				DeleteUnicastIpAddressEntry(a);
		}
		FreeMibTable(table);
	}

	MIB_UNICASTIPADDRESS_ROW a;
	InitializeUnicastIpAddressEntry(&a);
	a.InterfaceLuid = tun.luid;
	a.Address.Ipv6.sin6_family = AF_INET6;
	memcpy(&a.Address.Ipv6.sin6_addr, &tun.self_ll, 16);
	a.OnLinkPrefixLength = 64;
	a.DadState = IpDadStatePreferred;
	DWORD err = CreateUnicastIpAddressEntry(&a);
	if (err != NO_ERROR && err != ERROR_OBJECT_ALREADY_EXISTS)
		log_warn("awdl0: could not set its link-local address (%lu)", err);
}

static int host_open(struct io_state *state, const char *name)
{
	int err;
	WCHAR wname[IFNAMSIZ];
	if ((err = wintun_load()))
		return err;
	MultiByteToWideChar(CP_UTF8, 0, name, -1, wname, IFNAMSIZ);

	tun.self = state->if_ether_addr;
	rfc4291_addr(&tun.self, &tun.self_ll);

	tun.adapter = tun.CreateAdapter(wname, L"OWL", &AWDL_GUID);
	if (!tun.adapter) {
		DWORD e = GetLastError();
		log_error("awdl0: could not create the Wintun adapter (error %lu)%s", e,
		          e == ERROR_ACCESS_DENIED ? "; run owl as administrator" : "");
		return -EACCES;
	}
	tun.GetAdapterLUID(tun.adapter, &tun.luid);
	NET_IFINDEX index = 0;
	ConvertInterfaceLuidToIndex(&tun.luid, &index);
	state->host_ifindex = (int) index;
	{
		WCHAR walias[NDIS_IF_MAX_STRING_SIZE + 1] = L"";
		char alias[3 * NDIS_IF_MAX_STRING_SIZE + 1] = "";
		ConvertInterfaceLuidToAlias(&tun.luid, walias, NDIS_IF_MAX_STRING_SIZE + 1);
		WideCharToMultiByte(CP_UTF8, 0, walias, -1, alias, sizeof alias, NULL, NULL);
		log_info("awdl0: Wintun adapter \"%s\", interface index %lu", alias, (unsigned long) index);
		if (strcmp(alias, name))
			log_warn("awdl0: Windows named the adapter \"%s\", not \"%s\"; use index %lu as its scope",
			         alias, name, (unsigned long) index);
	}
	adapter_configure();

	tun.session = tun.StartSession(tun.adapter, 0x400000);
	if (!tun.session) {
		log_error("awdl0: could not start a Wintun session (error %lu)", GetLastError());
		return -EIO;
	}
	tun.ready = CreateEventW(NULL, TRUE, TRUE, NULL);
	ev_win_watch(HOST_FD, tun.ready);
	ev_win_watch(HOST_FD, tun.GetReadWaitEvent(tun.session));
	return 0;
}

static void host_close(void)
{
	if (tun.session)
		tun.EndSession(tun.session);
	if (tun.adapter)
		tun.CloseAdapter(tun.adapter); /* removes the adapter we created */
	tun.session = NULL;
	tun.adapter = NULL;
}

void host_neighbor_set(const struct in6_addr *in6, const struct ether_addr *eth)
{
	int free_slot = -1;
	for (int i = 0; i < NEIGHBORS; i++) {
		if (tun.nb[i].used && !memcmp(&tun.nb[i].ip, in6, 16)) {
			tun.nb[i].mac = *eth;
			return;
		}
		if (!tun.nb[i].used && free_slot < 0)
			free_slot = i;
	}
	if (free_slot < 0) /* full: overwrite round-robin */
		free_slot = tun.nb_next++ % NEIGHBORS;
	tun.nb[free_slot].used = 1;
	memcpy(&tun.nb[free_slot].ip, in6, 16);
	tun.nb[free_slot].mac = *eth;
}

void host_neighbor_clear(const struct in6_addr *in6)
{
	for (int i = 0; i < NEIGHBORS; i++)
		if (tun.nb[i].used && !memcmp(&tun.nb[i].ip, in6, 16))
			tun.nb[i].used = 0;
}

static int neighbor_lookup(const uint8_t *ip, struct ether_addr *mac)
{
	for (int i = 0; i < NEIGHBORS; i++) {
		if (tun.nb[i].used && !memcmp(&tun.nb[i].ip, ip, 16)) {
			*mac = tun.nb[i].mac;
			return 0;
		}
	}
	/* fe80::xxxx:xxff:fexx:xxxx carries its MAC (RFC 4291 appendix A). */
	if (ip[0] == 0xfe && (ip[1] & 0xc0) == 0x80 && ip[11] == 0xff && ip[12] == 0xfe) {
		uint8_t m[6] = {ip[8] ^ 0x02, ip[9], ip[10], ip[13], ip[14], ip[15]};
		memcpy(mac->ether_addr_octet, m, 6);
		return 0;
	}
	return -1;
}

static uint16_t icmp6_checksum(const uint8_t *ip6, const uint8_t *icmp, uint32_t len)
{
	uint32_t sum = 0;
	for (int i = 8; i < 40; i += 2) /* source and destination */
		sum += ip6[i] << 8 | ip6[i + 1];
	sum += len >> 16;
	sum += len & 0xffff;
	sum += 58;
	for (uint32_t i = 0; i + 1 < len; i += 2)
		sum += icmp[i] << 8 | icmp[i + 1];
	if (len & 1)
		sum += icmp[len - 1] << 8;
	while (sum >> 16)
		sum = (sum & 0xffff) + (sum >> 16);
	return (uint16_t) ~sum;
}

/* A neighbour solicitation for our address, from a peer: answer it, as the
 * kernel would have. `eth` is the whole Ethernet frame. */
static void answer_solicitation(const uint8_t *eth)
{
	const uint8_t *ns_ip = eth + 14;
	if (tun.nreplies == REPLIES)
		return;
	uint8_t *f = tun.replies[tun.nreplies].data;
	uint8_t *ip = f + 14, *icmp = ip + 40;
	int from_unspecified = 1;
	for (int i = 8; i < 24; i++)
		if (ns_ip[i])
			from_unspecified = 0;

	memset(f, 0, sizeof tun.replies[0].data);
	/* Ethernet */
	if (from_unspecified)
		memcpy(f, (const uint8_t[]) {0x33, 0x33, 0, 0, 0, 1}, 6);
	else
		memcpy(f, eth + 6, 6);
	memcpy(f + 6, tun.self.ether_addr_octet, 6);
	f[12] = 0x86;
	f[13] = 0xdd;
	/* IPv6: to the solicitor, or all nodes if it has no address yet */
	ip[0] = 0x60;
	ip[5] = 32; /* payload length */
	ip[6] = 58; /* ICMPv6 */
	ip[7] = 255;
	memcpy(ip + 8, &tun.self_ll, 16);
	if (from_unspecified)
		memcpy(ip + 24, (const uint8_t[]) {0xff, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1}, 16);
	else
		memcpy(ip + 24, ns_ip + 8, 16);
	/* Neighbour advertisement: solicited (unless to all nodes), override */
	icmp[0] = 136;
	icmp[4] = (from_unspecified ? 0 : 0x40) | 0x20;
	memcpy(icmp + 8, &tun.self_ll, 16);
	icmp[24] = 2; /* target link-layer address */
	icmp[25] = 1;
	memcpy(icmp + 26, tun.self.ether_addr_octet, 6);
	uint16_t sum = icmp6_checksum(ip, icmp, 32);
	icmp[2] = sum >> 8;
	icmp[3] = sum & 0xff;

	tun.replies[tun.nreplies++].len = 14 + 40 + 32;
	SetEvent(tun.ready);
}

/* From a peer, through OWL, to Windows. */
int host_send(const struct io_state *state, const uint8_t *buf, int len)
{
	(void) state;
	if (!tun.session)
		return -EINVAL;
	if (len < 14 + 40 || buf[12] != 0x86 || buf[13] != 0xdd)
		return 0; /* AWDL carries IPv6 only */
	const uint8_t *ip = buf + 14;
	int iplen = len - 14;

	/* Learn the sender: its address and MAC, as the kernel's neighbour cache would. */
	if (ip[8] != 0xff) {
		int unspecified = 1;
		for (int i = 8; i < 24; i++)
			if (ip[i])
				unspecified = 0;
		if (!unspecified)
			host_neighbor_set((const struct in6_addr *) (ip + 8), (const struct ether_addr *) (buf + 6));
	}

	/* ICMPv6 neighbour solicitation (no extension headers) for our address. */
	if (ip[6] == 58 && iplen >= 40 + 24 && ip[40] == 135 && !memcmp(ip + 48, &tun.self_ll, 16)) {
		answer_solicitation(buf);
		return 0;
	}

	BYTE *pkt = tun.AllocateSendPacket(tun.session, (DWORD) iplen);
	if (!pkt)
		return -ENOBUFS; /* ring full: Windows isn't reading */
	memcpy(pkt, ip, iplen);
	tun.SendPacket(tun.session, pkt);
	return 0;
}

/* Wrap one IPv6 packet from Windows for OWL; 0 drops it. */
static int ip6_to_ether(const BYTE *pkt, DWORD size, uint8_t *out, int cap)
{
	struct ether_addr dst;
	if (size < 40 || (pkt[0] >> 4) != 6 || (int) size + 14 > cap)
		return 0; /* IPv4, or too big */
	const uint8_t *daddr = pkt + 24;
	if (daddr[0] == 0xff) {
		uint8_t m[6] = {0x33, 0x33, daddr[12], daddr[13], daddr[14], daddr[15]};
		memcpy(dst.ether_addr_octet, m, 6);
	} else if (neighbor_lookup(daddr, &dst) < 0) {
		char s[INET6_ADDRSTRLEN];
		log_debug("awdl0: no MAC for %s, dropped", inet_ntop(AF_INET6, daddr, s, sizeof s));
		return 0;
	}
	memcpy(out, dst.ether_addr_octet, 6);
	memcpy(out + 6, tun.self.ether_addr_octet, 6);
	out[12] = 0x86;
	out[13] = 0xdd;
	memcpy(out + 14, pkt, size);
	return (int) size + 14;
}

/* From Windows, to OWL. -EAGAIN when there's nothing more, like a
 * non-blocking read. */
int host_recv(const struct io_state *state, uint8_t *buf, int *len)
{
	(void) state;
	if (!tun.session)
		return -EINVAL;
	if (tun.nreplies) {
		int n = tun.replies[0].len;
		if (n > *len)
			n = *len;
		memcpy(buf, tun.replies[0].data, n);
		memmove(&tun.replies[0], &tun.replies[1], (--tun.nreplies) * sizeof tun.replies[0]);
		*len = n;
		return 0;
	}
	for (;;) {
		DWORD size;
		BYTE *pkt = tun.ReceivePacket(tun.session, &size);
		if (!pkt) {
			if (GetLastError() != ERROR_NO_MORE_ITEMS) {
				log_error("awdl0: Wintun read failed (%lu)", GetLastError());
				return -EIO;
			}
			/* Reset, then look once more, so a packet arriving in between
			 * isn't left waiting for the next one. */
			ResetEvent(tun.ready);
			pkt = tun.ReceivePacket(tun.session, &size);
			if (!pkt) {
				errno = EAGAIN;
				return -EAGAIN;
			}
			SetEvent(tun.ready);
		}
		int n = ip6_to_ether(pkt, size, buf, *len);
		tun.ReleaseReceivePacket(tun.session, pkt);
		if (n > 0) {
			*len = n;
			return 0;
		}
	}
}

/* ------------------------------------------------------------------ io.h */

/* OWL's radiotap rate (RATE, or MCS from owl-01) to proto.h's. */
static uint8_t tx_rate(const uint8_t *rt, int rtlen)
{
	struct ieee80211_radiotap_iterator it;
	uint8_t rate = 12; /* 6 Mbit/s */
	if (ieee80211_radiotap_iterator_init(&it, (struct ieee80211_radiotap_header *) rt, rtlen, NULL))
		return rate;
	while (!ieee80211_radiotap_iterator_next(&it)) {
		if (it.this_arg_index == IEEE80211_RADIOTAP_RATE) {
			rate = *it.this_arg;
		} else if (it.this_arg_index == IEEE80211_RADIOTAP_MCS) {
			uint8_t flags = it.this_arg[1], mcs = it.this_arg[2];
			rate = RATE_MCS | (flags & IEEE80211_RADIOTAP_MCS_SGI ? RATE_MCS_SGI : 0) | (mcs & 0x0f);
		}
		/* TX_FLAGS' NOACK (owl-05) has no counterpart: the ESP32 sends unicast
		 * the ordinary way, and an iPhone ACKs it. */
	}
	return rate;
}

int wlan_send(const struct io_state *state, const uint8_t *buf, int len)
{
	if (!state || !state->wlan_handle || len < 4)
		return -EINVAL;
	int rtlen = buf[2] | buf[3] << 8;
	if (rtlen > len || len - rtlen > PROTO_MAX_FRAME)
		return -EINVAL;
	uint8_t hdr[4] = {tx_rate(buf, rtlen), 1, 0, 0}; /* rate, count, gap */
	radio_queue(MSG_TX, hdr, sizeof hdr, buf + rtlen, len - rtlen);
	return 0;
}

int io_state_init(struct io_state *state, const char *wlan, const char *host, const struct ether_addr *bssid_filter)
{
	int err;
	(void) bssid_filter; /* filtered in rx_push, and by the radio */

	strncpy(state->wlan_ifname, wlan, sizeof state->wlan_ifname - 1);
	state->wlan_is_file = 0;
	state->wlan_ifindex = 1; /* nonzero: core.c and owl.c take it as "a real radio" */
	state->dumpfile = NULL;
	if ((err = radio_open(wlan)))
		return err;

	if (state->addr_override) {
		/* The radio must carry our AWDL address, or it won't ACK peers' unicast. */
		if ((err = radio_command(MSG_SET_MAC, state->override_addr.ether_addr_octet, 6, "set MAC")))
			return err;
		if (radio_hello() < 0)
			return -ENODEV;
		log_info("AWDL address %s (overrides the radio's own)", ether_ntoa(&state->override_addr));
	}
	memcpy(state->if_ether_addr.ether_addr_octet, radio.hello.mac, 6);
	state->wlan_handle = &radio_pcap;
	state->wlan_fd = WLAN_FD;
	ev_win_watch(WLAN_FD, radio.rx_ready);

	state->host_ifindex = 0;
	if (host && *host) {
		strncpy(state->host_ifname, host, sizeof state->host_ifname - 1);
		if ((err = host_open(state, host)))
			return err;
		state->host_fd = HOST_FD;
	} else {
		state->host_fd = 0;
		log_debug("No host device given, start without host device");
	}
	return 0;
}

void io_state_free(struct io_state *state)
{
	(void) state;
	log_info("shutting down: removing awdl0, closing the radio");
	host_close();
	InterlockedExchange(&radio.stop, 1);
	if (radio.com && radio.com != INVALID_HANDLE_VALUE) {
		CancelIoEx(radio.com, NULL);
		SetEvent(radio.tx_wake);
		HANDLE threads[2] = {radio.reader, radio.writer};
		WaitForMultipleObjects(2, threads, TRUE, 1000);
		CloseHandle(radio.com);
	}
	log_info("radio: rx %ld frames (%ld dropped), tx ACKed %ld, not ACKed %ld, errors %ld, bad messages %ld",
	         radio.rx_frames, radio.rx_dropped, radio.tx_acked, radio.tx_unacked, radio.tx_errors, radio.bad);
}
