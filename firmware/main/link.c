/*
 * COBS framing with a CRC over UART0, which on the ESP32-S3-DevKitC-1 is the
 * port behind the CH343 bridge ("COM"). 3 Mbit/s is about 300 kB/s, plenty for
 * AWDL at the rates a fixed-channel radio sees.
 * TODO(phase 1): USB transport (TinyUSB on the S3's native port,
 * USB-Serial-JTAG on the C5), behind this same interface.
 */
#include "link.h"
#include "proto.h"

#include <string.h>

#include "driver/uart.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "sdkconfig.h"
#include "soc/uart_pins.h"

#define LINK_UART UART_NUM_0
#define LINK_BAUD CONFIG_OWL_RADIO_BAUD
#define RAW_MAX (1 + 16 + PROTO_MAX_FRAME + 2)
#define COBS_MAX (RAW_MAX + RAW_MAX / 254 + 2)

static SemaphoreHandle_t tx_lock;
static uint8_t tx_raw[RAW_MAX], tx_enc[COBS_MAX];

static uint16_t crc16(const uint8_t *p, size_t n)
{
	uint16_t crc = 0xffff;
	while (n--) {
		crc ^= (uint16_t)*p++ << 8;
		for (int i = 0; i < 8; i++)
			crc = crc & 0x8000 ? (crc << 1) ^ 0x1021 : crc << 1;
	}
	return crc;
}

/* Writes the encoding of in[0..n) and the 0x00 delimiter; returns its length. */
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

/* Decodes in place; returns the decoded length, or -1. */
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
	return (int)o;
}

void link_init(void)
{
	const uart_config_t cfg = {
		.baud_rate = LINK_BAUD,
		.data_bits = UART_DATA_8_BITS,
		.parity = UART_PARITY_DISABLE,
		.stop_bits = UART_STOP_BITS_1,
		.flow_ctrl = UART_HW_FLOWCTRL_DISABLE,
		.source_clk = UART_SCLK_DEFAULT,
	};
	ESP_ERROR_CHECK(uart_driver_install(LINK_UART, 8192, 16384, 0, NULL, 0));
	ESP_ERROR_CHECK(uart_param_config(LINK_UART, &cfg));
	/* Only a UART console routes these; with the console on USB they're unset. */
	ESP_ERROR_CHECK(uart_set_pin(LINK_UART, U0TXD_GPIO_NUM, U0RXD_GPIO_NUM,
				     UART_PIN_NO_CHANGE, UART_PIN_NO_CHANGE));
	tx_lock = xSemaphoreCreateMutex();
}

void link_send(uint8_t type, const void *a, size_t alen, const void *b, size_t blen)
{
	if (1 + alen + blen + 2 > RAW_MAX)
		return;
	xSemaphoreTake(tx_lock, portMAX_DELAY);
	size_t n = 0;
	tx_raw[n++] = type;
	if (alen) memcpy(tx_raw + n, a, alen), n += alen;
	if (blen) memcpy(tx_raw + n, b, blen), n += blen;
	uint16_t crc = crc16(tx_raw, n);
	tx_raw[n++] = crc & 0xff;
	tx_raw[n++] = crc >> 8;
	uart_write_bytes(LINK_UART, tx_enc, cobs_encode(tx_raw, n, tx_enc));
	xSemaphoreGive(tx_lock);
}

int link_recv(uint8_t *buf, size_t cap)
{
	/* Read in chunks: a driver call per byte costs ~10 us, 14 ms a frame. */
	static uint8_t in[512];
	static size_t in_len, in_pos;
	size_t n = 0;
	bool overflow = false;
	for (;;) {
		if (in_pos == in_len) {
			int got = uart_read_bytes(LINK_UART, in, 1, portMAX_DELAY);
			if (got != 1)
				continue;
			size_t more = 0;
			uart_get_buffered_data_len(LINK_UART, &more);
			if (more > sizeof in - 1)
				more = sizeof in - 1;
			in_len = 1 + (more ? uart_read_bytes(LINK_UART, in + 1, more, 0) : 0);
			in_pos = 0;
		}
		uint8_t c = in[in_pos++];
		if (c) {
			if (n < cap) buf[n++] = c;
			else overflow = true;
			continue;
		}
		if (!n)
			continue; /* back-to-back delimiters */
		int len = overflow ? -1 : cobs_decode(buf, n);
		if (len < 3 || crc16(buf, len - 2) != (buf[len - 2] | buf[len - 1] << 8))
			return -1;
		return len - 2;
	}
}
