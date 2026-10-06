"""Draws Sightline's icon, the reticle from site/favicon.svg, into a Windows .ico.

Standard library only, like the other scripts here: each size is rendered from the favicon's geometry with
4x4 supersampling, encoded as PNG with zlib, and packed into one .ico, which Windows has read PNG frames from
since Vista. The output is deterministic, so a test can hold the committed icon to the generator.

    python tools/scripts/generate_icon.py            # writes apps/windows/src/Sightline.App/Assets/sightline.ico
    python tools/scripts/generate_icon.py --check    # fails when the committed icon differs
"""

from __future__ import annotations

import argparse
import functools
import math
import struct
import sys
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "apps" / "windows" / "src" / "Sightline.App" / "Assets" / "sightline.ico"
SIZES = (16, 24, 32, 48, 64, 128, 256)
SAMPLES = 4

INK = (0x08, 0x0A, 0x09)
SIGNAL = (0xD7, 0xFF, 0x3F)

# The favicon's geometry, in its 32 by 32 view box.
CORNER = 7.0
RING = (16.0, 16.0, 7.5)
STROKE = 2.4
ARMS = (((16.0, 3.5), (16.0, 9.5)), ((16.0, 22.5), (16.0, 28.5)), ((3.5, 16.0), (9.5, 16.0)), ((22.5, 16.0), (28.5, 16.0)))
DOT = 2.2


def ground_distance(x: float, y: float) -> float:
    """How far a point is outside the rounded square behind the reticle; negative inside it."""
    qx = abs(x - 16) - (16 - CORNER)
    qy = abs(y - 16) - (16 - CORNER)
    return math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - CORNER


def reticle_distance(x: float, y: float) -> float:
    """How far a point is off the ring, the arms and the centre dot; negative on them."""
    cx, cy, radius = RING
    half = STROKE / 2
    from_centre = math.hypot(x - cx, y - cy)
    distance = min(abs(from_centre - radius) - half, from_centre - DOT)
    for (ax, ay), (bx, by) in ARMS:
        # Every arm is straight up or across, with round ends: the nearest point of its line, clamped to its ends.
        nearest_x = min(max(x, min(ax, bx)), max(ax, bx))
        nearest_y = min(max(y, min(ay, by)), max(ay, by))
        distance = min(distance, math.hypot(x - nearest_x, y - nearest_y) - half)
    return distance


def render(size: int) -> bytes:
    """
    The icon at size by size pixels, as straight RGBA rows.

    A pixel further than half its diagonal from every edge is the same all over, so it is filled at once;
    only pixels an edge crosses are supersampled.
    """
    scale = 32.0 / size
    margin = scale * 0.71
    total = SAMPLES * SAMPLES
    rows = bytearray()
    for py in range(size):
        rows.append(0)  # PNG filter: none
        for px in range(size):
            centre_x = (px + 0.5) * scale
            centre_y = (py + 0.5) * scale
            ground = ground_distance(centre_x, centre_y)
            if ground > margin:
                rows += b"\x00\x00\x00\x00"
                continue
            reticle = reticle_distance(centre_x, centre_y)
            if ground < -margin and abs(reticle) > margin:
                rows += bytes((*(SIGNAL if reticle < 0 else INK), 255))
                continue
            coverage = signal = 0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    x = (px + (sx + 0.5) / SAMPLES) * scale
                    y = (py + (sy + 0.5) / SAMPLES) * scale
                    if ground_distance(x, y) <= 0:
                        coverage += 1
                        signal += reticle_distance(x, y) <= 0
            if coverage == 0:
                rows += b"\x00\x00\x00\x00"
                continue
            mix = signal / coverage
            rows += bytes(round(INK[i] + (SIGNAL[i] - INK[i]) * mix) for i in range(3))
            rows.append(round(255 * coverage / total))
    return bytes(rows)


@functools.cache
def png(size: int) -> bytes:
    """The icon at size by size pixels, as a PNG file."""

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(render(size), 9)) + chunk(b"IEND", b"")


def ico(sizes: tuple[int, ...] = SIZES) -> bytes:
    """Every size, packed into one .ico."""
    images = [png(size) for size in sizes]
    directory = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for size, image in zip(sizes, images):
        side = 0 if size >= 256 else size  # 0 means 256 in an .ico directory
        directory += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(image), offset)
        offset += len(image)
    return directory + b"".join(images)


def main(arguments: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="fail when the committed icon differs from a fresh one")
    parser.add_argument("--output", type=Path, default=OUTPUT)
    options = parser.parse_args(arguments)
    fresh = ico()
    if options.check:
        if not options.output.exists() or options.output.read_bytes() != fresh:
            print(f"{options.output} is not what generate_icon.py draws; run it without --check.", file=sys.stderr)
            return 1
        print(f"{options.output} is up to date.")
        return 0
    options.output.parent.mkdir(parents=True, exist_ok=True)
    options.output.write_bytes(fresh)
    print(f"Wrote {options.output} ({len(fresh)} bytes).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
