# owl-radio: an ESP32 as the AWDL radio for OWL on Windows, which has no Wi-Fi
# driver that can inject frames. Promiscuous receive and raw transmit over a
# serial link (main/proto.h).
#   idf.py set-target esp32s3 && idf.py build flash
cmake_minimum_required(VERSION 3.16)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(owl-radio)
