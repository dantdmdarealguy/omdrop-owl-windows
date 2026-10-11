/*
 * owl-radio: the ESP32 as OWL's Wi-Fi card. It receives in promiscuous mode on
 * one channel and sends raw frames with esp_wifi_80211_tx. That is all OWL
 * asks of a monitor interface with injection, which no Windows driver offers.
 *
 * The station interface is started but never connected: its MAC is the AWDL
 * address (MSG_SET_MAC), so the hardware ACKs a peer's unicast to it, and
 * esp_wifi_80211_tx sends through it. Whether the hardware ACKs while
 * unassociated is what `spike.py ack-test` checks.
 */
#include <stdbool.h>
#include <string.h>

#include "esp_idf_version.h"
#include "esp_mac.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "esp_event.h"
#include "freertos/FreeRTOS.h"
#include "freertos/ringbuf.h"
#include "freertos/task.h"
#include "nvs_flash.h"

#include "link.h"
#include "proto.h"

static const uint8_t AWDL_BSSID[6] = {0x00, 0x25, 0x00, 0xff, 0x94, 0x73};
static const uint8_t APPLE_OUI[3] = {0x00, 0x17, 0xf2};

static RingbufHandle_t rx_ring;
static volatile uint8_t filter_mode = FILTER_AWDL;
static uint8_t channel = 6;
static uint8_t our_mac[6];

static struct {
	uint32_t rx, rx_drop, tx_ok, tx_err, tx_acked, tx_noack, bad_msg;
} stats;

/* What precedes each frame in MSG_RX. */
struct __attribute__((packed)) rx_hdr {
	uint32_t ts;
	int8_t rssi;
	uint8_t ch, rate, sig_mode, mcs, cwb;
};

static bool wanted(wifi_promiscuous_pkt_type_t type, const uint8_t *f, size_t len)
{
	switch (filter_mode) {
	case FILTER_ALL:
		return true;
	case FILTER_MGMT_DATA:
		return type != WIFI_PKT_CTRL;
	}
	if (len < 24)
		return false;
	/* Action frame, vendor-specific category 127, Apple's OUI. */
	if (type == WIFI_PKT_MGMT)
		return f[0] == 0xd0 && len > 28 && f[24] == 127 && !memcmp(f + 25, APPLE_OUI, 3);
	/* Data on AWDL's BSSID (addr3), or anything addressed to us (addr1). */
	if (type == WIFI_PKT_DATA)
		return !memcmp(f + 16, AWDL_BSSID, 6) || !memcmp(f + 4, our_mac, 6);
	return false;
}

/* Runs in the Wi-Fi task: copy out and return, never block. */
static void promisc_cb(void *buf, wifi_promiscuous_pkt_type_t type)
{
	const wifi_promiscuous_pkt_t *pkt = buf;
	const wifi_pkt_rx_ctrl_t *rc = &pkt->rx_ctrl;
	if (type == WIFI_PKT_MISC || rc->sig_len < 14)
		return;
	size_t len = rc->sig_len - 4; /* sig_len counts the FCS */
	if (len > PROTO_MAX_FRAME || !wanted(type, pkt->payload, len))
		return;

	struct rx_hdr h = {
		.ts = rc->timestamp,
		.rssi = rc->rssi,
		.ch = rc->channel,
#if !CONFIG_SOC_WIFI_HE_SUPPORT
		.rate = rc->rate,
		.sig_mode = rc->sig_mode,
		.mcs = rc->mcs,
		.cwb = rc->cwb,
#endif
	};
	void *slot;
	if (xRingbufferSendAcquire(rx_ring, &slot, sizeof h + len, 0) != pdTRUE) {
		stats.rx_drop++;
		return;
	}
	memcpy(slot, &h, sizeof h);
	memcpy((uint8_t *)slot + sizeof h, pkt->payload, len);
	xRingbufferSendComplete(rx_ring, slot);
	stats.rx++;
}

static void tx_done_cb(const esp_80211_tx_info_t *info)
{
	uint8_t acked = info->tx_status == WIFI_SEND_SUCCESS;
	acked ? stats.tx_acked++ : stats.tx_noack++;
	link_send(MSG_TX_DONE, &acked, 1, info->des_addr, 6);
}

static esp_err_t radio_apply(void)
{
	const wifi_promiscuous_filter_t f = {
		.filter_mask = WIFI_PROMIS_FILTER_MASK_MGMT | WIFI_PROMIS_FILTER_MASK_DATA |
			       (filter_mode == FILTER_ALL ? WIFI_PROMIS_FILTER_MASK_CTRL : 0),
	};
	const wifi_promiscuous_filter_t cf = {.filter_mask = WIFI_PROMIS_CTRL_FILTER_MASK_ALL};
	ESP_ERROR_CHECK(esp_wifi_set_promiscuous_filter(&f));
	ESP_ERROR_CHECK(esp_wifi_set_promiscuous_ctrl_filter(&cf));
	ESP_ERROR_CHECK(esp_wifi_set_promiscuous_rx_cb(promisc_cb));
	ESP_ERROR_CHECK(esp_wifi_set_promiscuous(true));
	return esp_wifi_set_channel(channel, WIFI_SECOND_CHAN_NONE);
}

static void radio_init(void)
{
	esp_err_t err = nvs_flash_init();
	if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
		ESP_ERROR_CHECK(nvs_flash_erase());
		ESP_ERROR_CHECK(nvs_flash_init());
	}
	ESP_ERROR_CHECK(esp_event_loop_create_default());
	wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
	ESP_ERROR_CHECK(esp_wifi_init(&cfg));
	ESP_ERROR_CHECK(esp_wifi_set_storage(WIFI_STORAGE_RAM));
	ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
	ESP_ERROR_CHECK(esp_wifi_start());
	ESP_ERROR_CHECK(esp_wifi_set_ps(WIFI_PS_NONE));
	ESP_ERROR_CHECK(esp_wifi_get_mac(WIFI_IF_STA, our_mac));
	ESP_ERROR_CHECK(esp_wifi_register_80211_tx_cb(tx_done_cb));
	ESP_ERROR_CHECK(radio_apply());
}

/* The station MAC can only change while Wi-Fi is stopped. */
static esp_err_t set_mac(const uint8_t *mac)
{
	esp_wifi_set_promiscuous(false);
	esp_wifi_stop();
	esp_err_t err = esp_wifi_set_mac(WIFI_IF_STA, mac);
	ESP_ERROR_CHECK(esp_wifi_start());
	ESP_ERROR_CHECK(esp_wifi_set_ps(WIFI_PS_NONE));
	esp_wifi_get_mac(WIFI_IF_STA, our_mac);
	radio_apply();
	return err;
}

/* proto.h's portable rate to this chip's wifi_phy_rate_t; -1 if unknown. */
static int phy_rate(uint8_t r)
{
	if (r & RATE_MCS) {
		int mcs = r & 0x0f;
		if (mcs > 7)
			return -1;
		return (r & RATE_MCS_SGI ? WIFI_PHY_RATE_MCS0_SGI : WIFI_PHY_RATE_MCS0_LGI) + mcs;
	}
	switch (r) {
	case 2: return WIFI_PHY_RATE_1M_L;
	case 4: return WIFI_PHY_RATE_2M_L;
	case 11: return WIFI_PHY_RATE_5M_L;
	case 22: return WIFI_PHY_RATE_11M_L;
	case 12: return WIFI_PHY_RATE_6M;
	case 18: return WIFI_PHY_RATE_9M;
	case 24: return WIFI_PHY_RATE_12M;
	case 36: return WIFI_PHY_RATE_18M;
	case 48: return WIFI_PHY_RATE_24M;
	case 72: return WIFI_PHY_RATE_36M;
	case 96: return WIFI_PHY_RATE_48M;
	case 108: return WIFI_PHY_RATE_54M;
	}
	return -1;
}

static esp_err_t tx(const uint8_t *body, size_t len)
{
	if (len < 4 + 24)
		return ESP_ERR_INVALID_SIZE;
	int rate = phy_rate(body[0]);
	uint8_t count = body[1] ? body[1] : 1;
	uint16_t gap_ms = body[2] | body[3] << 8;
	const uint8_t *frame = body + 4;
	static int cur_rate = -1; /* reconfiguring costs milliseconds; only on change */
	esp_err_t err = ESP_OK;
	if (rate < 0)
		return ESP_ERR_INVALID_ARG;
	if (rate != cur_rate) {
		err = esp_wifi_config_80211_tx_rate(WIFI_IF_STA, (wifi_phy_rate_t)rate);
		cur_rate = err == ESP_OK ? rate : -1;
	}
	for (int i = 0; i < count && err == ESP_OK; i++) {
		err = esp_wifi_80211_tx(WIFI_IF_STA, frame, len - 4, false);
		err == ESP_OK ? stats.tx_ok++ : stats.tx_err++;
		if (gap_ms && i + 1 < count)
			vTaskDelay(pdMS_TO_TICKS(gap_ms));
	}
	return err;
}

static void ack(uint8_t req, esp_err_t err)
{
	int32_t e = err;
	link_send(MSG_ACK, &req, 1, &e, 4);
}

static void hello(void)
{
	struct __attribute__((packed)) {
		uint8_t proto;
		uint8_t mac[6];
		char target[16];
		char idf[32];
	} r = {.proto = PROTO_VERSION};
	memcpy(r.mac, our_mac, 6);
	strlcpy(r.target, CONFIG_IDF_TARGET, sizeof r.target);
	strlcpy(r.idf, esp_get_idf_version(), sizeof r.idf);
	link_send(MSG_HELLO_RESP, &r, sizeof r, NULL, 0);
}

static void uplink_task(void *arg)
{
	for (;;) {
		size_t n;
		void *item = xRingbufferReceive(rx_ring, &n, portMAX_DELAY);
		if (!item)
			continue;
		link_send(MSG_RX, item, n, NULL, 0);
		vRingbufferReturnItem(rx_ring, item);
	}
}

static void command_task(void *arg)
{
	static uint8_t msg[1 + 4 + PROTO_MAX_FRAME + 16];
	for (;;) {
		int n = link_recv(msg, sizeof msg);
		if (n < 1) {
			stats.bad_msg++;
			continue;
		}
		const uint8_t *body = msg + 1;
		size_t blen = n - 1;
		switch (msg[0]) {
		case MSG_HELLO:
			hello();
			break;
		case MSG_SET_CHANNEL:
			if (blen < 1) { ack(msg[0], ESP_ERR_INVALID_SIZE); break; }
			channel = body[0];
			ack(msg[0], esp_wifi_set_channel(channel, WIFI_SECOND_CHAN_NONE));
			break;
		case MSG_SET_MAC:
			ack(msg[0], blen < 6 ? ESP_ERR_INVALID_SIZE : set_mac(body));
			break;
		case MSG_SET_FILTER:
			if (blen < 1) { ack(msg[0], ESP_ERR_INVALID_SIZE); break; }
			filter_mode = body[0];
			ack(msg[0], radio_apply());
			break;
		case MSG_TX:
			ack(msg[0], tx(body, blen));
			break;
		case MSG_TIME: {
			uint64_t now = esp_timer_get_time();
			link_send(MSG_TIME_RESP, &now, 8, NULL, 0);
			break;
		}
		case MSG_STATS:
			link_send(MSG_STATS_RESP, &stats, sizeof stats, NULL, 0);
			break;
		default:
			ack(msg[0], ESP_ERR_NOT_SUPPORTED);
		}
	}
}

void app_main(void)
{
	link_init();
	rx_ring = xRingbufferCreate(48 * 1024, RINGBUF_TYPE_NOSPLIT);
	radio_init();
	xTaskCreatePinnedToCore(uplink_task, "uplink", 4096, NULL, 10, NULL, tskNO_AFFINITY);
	xTaskCreatePinnedToCore(command_task, "command", 6144, NULL, 11, NULL, tskNO_AFFINITY);
	static const char up[] = "owl-radio up";
	link_send(MSG_LOG, up, sizeof up - 1, NULL, 0);
}
