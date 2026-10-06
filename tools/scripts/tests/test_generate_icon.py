"""Tests for generate_icon.py: the icon's shape, its file format, and the committed copy."""

from __future__ import annotations

import struct
import sys
import tempfile
import unittest
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import generate_icon  # noqa: E402


def pixels(png: bytes) -> tuple[int, bytes]:
    """The side and the raw filtered rows of one of our PNGs."""
    side = struct.unpack(">I", png[16:20])[0]
    length = struct.unpack(">I", png[33:37])[0]
    return side, zlib.decompress(png[41 : 41 + length])


def at(rows: bytes, side: int, x: int, y: int) -> tuple[int, int, int, int]:
    start = y * (side * 4 + 1) + 1 + x * 4
    return tuple(rows[start : start + 4])


class GenerateIconTests(unittest.TestCase):
    def test_the_reticle_is_signal_on_ink_with_clear_corners(self) -> None:
        side, rows = pixels(generate_icon.png(64))
        self.assertEqual(side, 64)
        self.assertEqual(at(rows, side, 0, 0)[3], 0, "the rounded corner is clear")
        self.assertEqual(at(rows, side, 32, 32), (*generate_icon.SIGNAL, 255), "the centre dot is signal")
        self.assertEqual(at(rows, side, 12, 12), (*generate_icon.INK, 255), "off the ring and the arms is ink")
        self.assertEqual(at(rows, side, 32, 9), (*generate_icon.SIGNAL, 255), "the top arm is signal")

    def test_every_size_is_packed_into_one_ico(self) -> None:
        data = generate_icon.ico((16, 256))
        reserved, kind, count = struct.unpack("<HHH", data[:6])
        self.assertEqual((reserved, kind, count), (0, 1, 2))
        first = struct.unpack("<BBBBHHII", data[6:22])
        second = struct.unpack("<BBBBHHII", data[22:38])
        self.assertEqual(first[:2], (16, 16))
        self.assertEqual(second[:2], (0, 0), "256 is written as 0")
        self.assertEqual(data[first[7] : first[7] + 8], b"\x89PNG\r\n\x1a\n")
        self.assertEqual(second[7], first[7] + first[6])

    def test_check_passes_on_a_fresh_icon_and_fails_on_a_stale_one(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / "assets" / "sightline.ico"
            self.assertEqual(generate_icon.main(["--check", "--output", str(target)]), 1)
            self.assertEqual(generate_icon.main(["--output", str(target)]), 0)
            self.assertEqual(generate_icon.main(["--check", "--output", str(target)]), 0)
            target.write_bytes(b"stale")
            self.assertEqual(generate_icon.main(["--check", "--output", str(target)]), 1)

    def test_the_committed_icon_is_what_the_generator_draws(self) -> None:
        self.assertEqual(generate_icon.main(["--check"]), 0)


if __name__ == "__main__":
    unittest.main()
