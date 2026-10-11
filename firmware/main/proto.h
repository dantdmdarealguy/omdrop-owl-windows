/*
 * The wire protocol between owl-radio and the host (tools/spike/spike.py now,
 * windows/owl's io_esp.c later). Keep them in step.
 *
 * Every message is  type:u8 body... crc16:u16le , COBS-encoded and ended with
 * a 0x00. CRC-16/CCITT-FALSE over type and body. Integers are little-endian.
 * The ROM bootloader prints text on the same UART at boot; the delimiter and
 * CRC are how the host skips it.
 */
#pragma once

#define PROTO_VERSION 2
#define PROTO_MAX_FRAME 1600 /* largest 802.11 frame either way */

/* Host to radio. Each is answered by MSG_ACK unless noted. */
enum {
	MSG_HELLO = 0x01,       /* -> MSG_HELLO_RESP */
	MSG_SET_CHANNEL = 0x02, /* ch:u8 */
	MSG_SET_MAC = 0x03,     /* mac:6 */
	MSG_SET_FILTER = 0x04,  /* mode:u8 (FILTER_*) */
	MSG_TX = 0x05,          /* rate:u8 (RATE_*) count:u8 gap_ms:u16 frame... (no FCS) */
	MSG_TIME = 0x06,        /* -> MSG_TIME_RESP */
	MSG_STATS = 0x07,       /* -> MSG_STATS_RESP */
};

/* Radio to host. */
enum {
	MSG_ACK = 0x80,        /* req:u8 err:i32 (esp_err_t) */
	MSG_HELLO_RESP = 0x81, /* proto:u8 mac:6 target:char[16] idf:char[32] */
	MSG_TX_DONE = 0x86,    /* acked:u8 dst:6, per frame sent, when the IDF reports it */
	MSG_TIME_RESP = 0x87,  /* us:u64 (esp_timer) */
	MSG_STATS_RESP = 0x88, /* rx rx_drop tx_ok tx_err tx_acked tx_noack bad_msg: u32 each */
	MSG_RX = 0x90,         /* ts:u32 rssi:i8 ch:u8 rate:u8 sig_mode:u8 mcs:u8 cwb:u8 frame... (no FCS) */
	MSG_LOG = 0x91,        /* text */
};

/* MSG_TX's rate, the same on every chip (wifi_phy_rate_t isn't: its MCS
 * short-GI values move on chips with 802.11ax). Legacy rates are in radiotap's
 * 500 kbit/s units (2 = 1 Mbit/s, 12 = 6, 108 = 54); HT is RATE_MCS. */
#define RATE_MCS 0x80
#define RATE_MCS_SGI 0x10 /* with RATE_MCS; the low nibble is the MCS index */

enum {
	FILTER_AWDL = 0, /* AWDL action frames, and data frames on AWDL's BSSID or to us */
	FILTER_MGMT_DATA = 1,
	FILTER_ALL = 2, /* control frames too */
};
