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

# Phase 0: run one spike test (hello, capture, ack-test, link)
spike TEST *ARGS:
    python tools/spike/spike.py -p {{port}} {{TEST}} {{ARGS}}
