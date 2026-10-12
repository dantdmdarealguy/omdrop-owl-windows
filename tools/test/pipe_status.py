#!/usr/bin/env python3
"""Ask the OmdropOwl service for its status over the omdrop-owl pipe.

  python tools/test/pipe_status.py [status|start|stop]
"""
import json
import sys

cmd = sys.argv[1] if len(sys.argv) > 1 else "status"
with open(r"\\.\pipe\omdrop-owl", "r+b", buffering=0) as pipe:
    pipe.write((json.dumps({"cmd": cmd}) + "\n").encode())
    print(pipe.readline().decode().strip())
