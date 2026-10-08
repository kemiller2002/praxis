#!/usr/bin/env python3
"""Decodes the A024-TELEMETRY lines a session printed and verifies the SHA-256.

Usage: python3 decode_telemetry.py TEXT_FILE OUTPUT.json   (TEXT_FILE holds the tool output)
Exit 1 when the lines are missing or the digest does not match.
"""
import base64
import gzip
import hashlib
import json
import re
import sys


def main():
    text = open(sys.argv[1], encoding="utf-8").read()
    digest = re.search(r"A024-TELEMETRY-SHA256 ([0-9a-f]{64})", text)
    payload = re.search(r"A024-TELEMETRY-GZB64 ([A-Za-z0-9+/=]+)", text)
    if not (digest and payload):
        sys.exit("telemetry lines not found")
    compact = gzip.decompress(base64.b64decode(payload.group(1))).decode("utf-8")
    if hashlib.sha256(compact.encode()).hexdigest() != digest.group(1):
        sys.exit("telemetry digest mismatch")
    json.dump(json.loads(compact), open(sys.argv[2], "w"), indent=2)
    print(f"verified {digest.group(1)} -> {sys.argv[2]}")


if __name__ == "__main__":
    main()
