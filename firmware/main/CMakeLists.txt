idf_component_register(SRCS "main.c" "link.c"
                       INCLUDE_DIRS "."
                       PRIV_REQUIRES esp_wifi esp_timer nvs_flash esp_driver_uart esp_ringbuf)
