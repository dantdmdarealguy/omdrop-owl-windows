# Development tasks. `just` lists them. Firmware recipes need ESP-IDF's
# environment (export.ps1) in the shell.
set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

port := "COM9"
target := "esp32s3"

[private]
default:
    @just --list

# Build owl-radio for {{target}}
build-firmware:
    cd firmware; idf.py set-target {{target}}; idf.py build

# Flash owl-radio over {{port}}
flash: build-firmware
    cd firmware; idf.py -p {{port}} flash

# Build owl.exe: OWL for Windows, with the ESP32 as its radio
build-owl:
    ./tools/build-owl.ps1

# Run OWL on the ESP32 with awdl0 (run from an administrator terminal)
owl *ARGS: build-owl
    ./windows/owl/build/owl.exe -i {{port}} -c 6 -N -F -E -S pin -T -v {{ARGS}}

# Phase 0: run one spike test (hello, capture, ack-test, link)
spike TEST *ARGS:
    python tools/spike/spike.py -p {{port}} {{TEST}} {{ARGS}}
