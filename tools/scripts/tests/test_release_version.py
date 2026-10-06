"""Tests for release_version.py: tagged and rolling versions, and the Android version code."""

from __future__ import annotations

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import release_version  # noqa: E402

SHA = "559503e1c2d4f0a9b8e7d6c5b4a3f2e1d0c9b8a7"


class ResolveTests(unittest.TestCase):
    def test_a_tag_naming_the_version_builds_exactly_it(self) -> None:
        self.assertEqual(release_version.resolve("0.2.0", "refs/tags/v0.2.0", SHA, 9), ("0.2.0", True))

    def test_a_tag_naming_any_other_version_fails(self) -> None:
        with self.assertRaisesRegex(ValueError, "does not match VERSION 0.2.0"):
            release_version.resolve("0.2.0", "refs/tags/v0.3.0", SHA, 9)

    def test_every_other_build_is_rolling_and_ordered_by_its_run(self) -> None:
        self.assertEqual(release_version.resolve("0.2.0", "refs/heads/main", SHA, 42), ("0.2.0-rolling.42.g559503e", False))
        self.assertEqual(release_version.resolve("0.2.0", "refs/pull/7/merge", SHA, 3)[1], False)

    def test_nonsense_is_refused(self) -> None:
        for base, ref, sha, run in (
            ("0.2", "refs/heads/main", SHA, 1),
            ("0.2.0", "refs/heads/main", SHA, 0),
            ("0.2.0", "refs/heads/main", "not-a-commit", 1),
        ):
            with self.subTest(base=base, sha=sha, run=run), self.assertRaises(ValueError):
                release_version.resolve(base, ref, sha, run)

    def test_the_android_code_only_grows(self) -> None:
        self.assertEqual(release_version.version_code(312), 1312)
        with self.assertRaises(ValueError):
            release_version.version_code(0)


class MainTests(unittest.TestCase):
    def run_main(self, version: str, *arguments: str) -> tuple[int, str, str]:
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / "VERSION"
            file.write_text(version + "\n", encoding="utf-8")
            out, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = release_version.main([*arguments, "--version-file", str(file)])
            return code, out.getvalue(), err.getvalue()

    def test_it_prints_step_outputs(self) -> None:
        code, out, _ = self.run_main("0.2.0", "--ref", "refs/heads/main", "--sha", SHA, "--run", "42", "--commits", "312")
        self.assertEqual(code, 0)
        self.assertEqual(out.splitlines(), ["version=0.2.0-rolling.42.g559503e", "tagged=false", "code=1312"])

    def test_a_problem_is_said_and_fails_the_step(self) -> None:
        code, out, err = self.run_main("0.2.0", "--ref", "refs/tags/v9.9.9", "--sha", SHA, "--run", "1", "--commits", "1")
        self.assertEqual(code, 1)
        self.assertEqual(out, "")
        self.assertIn("does not match VERSION", err)

    def test_the_repositorys_version_resolves(self) -> None:
        version = (release_version.ROOT / "VERSION").read_text(encoding="utf-8").strip()
        self.assertEqual(release_version.resolve(version, f"refs/tags/v{version}", SHA, 1), (version, True))


if __name__ == "__main__":
    unittest.main()
