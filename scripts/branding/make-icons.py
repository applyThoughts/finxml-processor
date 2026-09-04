#!/usr/bin/env python3
"""Renders the FinXml Processor logo to PNG (several sizes) and a multi-size ICO.

Pure standard library so it runs anywhere .NET builds. Geometry mirrors scripts/branding/logo.svg:
a rounded tile with a blue-to-teal gradient, XML angle brackets on the left, and a small spreadsheet
grid with a green header row on the right.

    python scripts/branding/make-icons.py

Outputs into src/FinXmlProcessor.Desktop/Assets/: icon-1024.png, icon-512.png, icon-256.png,
icon-128.png, icon-64.png, icon-32.png, icon-16.png, app.ico.
"""
from __future__ import annotations

import math
import os
import struct
import sys
import zlib

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "src", "FinXmlProcessor.Desktop", "Assets")
SUPERSAMPLE = 3

# Colours (RGB)
TOP = (0x1B, 0x4F, 0x8A)      # deep blue
BOTTOM = (0x11, 0x8A, 0x8F)   # teal
WHITE = (0xFF, 0xFF, 0xFF)
GREEN = (0x2E, 0xB8, 0x6B)    # spreadsheet header green
GRID_BG = (0xF3, 0xF7, 0xFA)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def sd_round_rect(x, y, cx, cy, hw, hh, r):
    qx = abs(x - cx) - hw + r
    qy = abs(y - cy) - hh + r
    return math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - r


def sd_segment(x, y, ax, ay, bx, by):
    px, py = x - ax, y - ay
    dx, dy = bx - ax, by - ay
    h = max(0.0, min(1.0, (px * dx + py * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - dx * h, py - dy * h)


def coverage(d):
    """Signed distance (in unit-square pixels) to 0..1 coverage with ~1px anti-aliasing."""
    return max(0.0, min(1.0, 0.5 - d))


def render(size: int) -> bytes:
    s = size * SUPERSAMPLE
    u = s / 1024.0  # scale from a 1024 design grid
    out = bytearray()
    row_cache = []
    for y in range(s):
        row = bytearray()
        row.append(0)  # PNG filter: none
        for x in range(s):
            # Sample at pixel centre in design units
            X, Y = (x + 0.5) / u, (y + 0.5) / u
            # Tile
            d_tile = sd_round_rect(X, Y, 512, 512, 448, 448, 112) * u
            a_tile = coverage(d_tile)
            if a_tile <= 0:
                row.extend((0, 0, 0, 0))
                continue
            base = lerp(TOP, BOTTOM, min(1.0, max(0.0, (Y - 64) / 896)))
            r, g, b = base

            # Left: angle brackets "< >" as thick strokes
            stroke = 46.0
            d_lt = min(sd_segment(X, Y, 330, 372, 214, 512), sd_segment(X, Y, 214, 512, 330, 652)) - stroke / 2
            d_gt = min(sd_segment(X, Y, 402, 372, 518, 512), sd_segment(X, Y, 518, 512, 402, 652)) - stroke / 2
            a_br = coverage(min(d_lt, d_gt) * u)

            # Right: spreadsheet card
            cx, cy, hw, hh = 730, 512, 150, 190
            d_card = sd_round_rect(X, Y, cx, cy, hw, hh, 22)
            a_card = coverage(d_card * u)
            # Header band (top 25% of the card)
            in_header = (cy - hh) <= Y <= (cy - hh + 0.25 * 2 * hh)
            # Grid lines: 2 vertical, 3 horizontal inside the body
            grid_alpha = 0.0
            if d_card < 0:
                col_step = (2 * hw) / 3
                row_top = cy - hh + 0.25 * 2 * hh
                row_step = (2 * hh - 0.25 * 2 * hh) / 4
                for i in (1, 2):
                    gx = cx - hw + i * col_step
                    grid_alpha = max(grid_alpha, coverage((abs(X - gx) - 5) * u))
                for i in (1, 2, 3):
                    gy = row_top + i * row_step
                    grid_alpha = max(grid_alpha, coverage((abs(Y - gy) - 5) * u))

            # Composite
            def mix(c, a):
                nonlocal r, g, b
                r = int(round(r + (c[0] - r) * a))
                g = int(round(g + (c[1] - g) * a))
                b = int(round(b + (c[2] - b) * a))

            mix(WHITE, a_br)
            if a_card > 0:
                mix(GRID_BG, a_card)
                if in_header:
                    mix(GREEN, a_card)
                # Header text hint: three short white dashes
                if in_header:
                    for i in range(3):
                        dx0 = cx - hw + 26 + i * 100
                        d_dash = sd_round_rect(X, Y, dx0 + 30, cy - hh + 0.125 * 2 * hh, 30, 8, 8)
                        mix(WHITE, coverage(d_dash * u) * a_card)
                else:
                    mix((0xC9, 0xD6, 0xE2), grid_alpha * a_card)
            row.extend((r, g, b, int(round(255 * a_tile))))
        row_cache.append(row)

    # Downsample by box filter
    small = bytearray()
    for y in range(size):
        small.append(0)
        for x in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(SUPERSAMPLE):
                src = row_cache[y * SUPERSAMPLE + sy]
                for sx in range(SUPERSAMPLE):
                    i = 1 + (x * SUPERSAMPLE + sx) * 4
                    a = src[i + 3]
                    acc[0] += src[i] * a
                    acc[1] += src[i + 1] * a
                    acc[2] += src[i + 2] * a
                    acc[3] += a
            n = SUPERSAMPLE * SUPERSAMPLE
            if acc[3] == 0:
                small.extend((0, 0, 0, 0))
            else:
                small.extend((acc[0] // acc[3], acc[1] // acc[3], acc[2] // acc[3], acc[3] // n))
    return bytes(small)


def png(size: int, raw: bytes) -> bytes:
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def ico(images: dict[int, bytes]) -> bytes:
    sizes = sorted(images)
    header = struct.pack("<HHH", 0, 1, len(sizes))
    entries = b""
    data = b""
    offset = 6 + 16 * len(sizes)
    for s in sizes:
        blob = images[s]
        dim = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        data += blob
        offset += len(blob)
    return header + entries + data


def main() -> int:
    os.makedirs(ASSETS, exist_ok=True)
    pngs = {}
    for size in (16, 32, 64, 128, 256, 512, 1024):
        pngs[size] = png(size, render(size))
        with open(os.path.join(ASSETS, f"icon-{size}.png"), "wb") as f:
            f.write(pngs[size])
        print(f"icon-{size}.png {len(pngs[size]):,} bytes")
    with open(os.path.join(ASSETS, "app.ico"), "wb") as f:
        f.write(ico({s: pngs[s] for s in (16, 32, 48 if 48 in pngs else 64, 128, 256)}))
    print("app.ico written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
