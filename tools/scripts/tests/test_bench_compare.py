"""The benchmark comparison: what counts as a regression, and how it is reported.

Run with: python -m unittest discover -s tools/scripts/tests
"""

from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import bench_compare  # noqa: E402
from bench_compare import Result  # noqa: E402


def write_report(folder: Path, name: str, benchmarks: list[dict]) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    (folder / f"{name}-report-full.json").write_text(json.dumps({"Benchmarks": benchmarks}), encoding="utf-8")


def benchmark(name: str, mean: float, allocated: float | None = None) -> dict:
    entry = {"FullName": name, "Statistics": {"Mean": mean}}
    if allocated is not None:
        entry["Memory"] = {"BytesAllocatedPerOperation": allocated}
    return entry


class BenchCompareTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.root = Path(self._temp.name)

    def tearDown(self) -> None:
        self._temp.cleanup()

    def test_every_full_report_under_a_folder_is_read_by_full_name(self) -> None:
        write_report(self.root / "a", "Framing", [benchmark("Framing.Encode", 40.0, 64)])
        write_report(self.root / "a" / "nested", "Parse", [benchmark("Parse.Menu", 9_000.0)])

        results = bench_compare.load(self.root / "a")

        self.assertEqual(results, {"Framing.Encode": Result(40.0, 64.0), "Parse.Menu": Result(9_000.0, None)})

    def test_a_benchmark_that_failed_has_no_figures_and_is_left_out(self) -> None:
        write_report(self.root, "Broken", [{"FullName": "Broken.Run", "Statistics": None}, {"FullName": "Odd.Run"}])

        self.assertEqual(bench_compare.load(self.root), {})

    def test_a_folder_with_no_reports_is_an_error_not_an_empty_success(self) -> None:
        with self.assertRaises(FileNotFoundError):
            bench_compare.load(self.root)

    def test_slower_beyond_the_threshold_is_a_regression_and_within_it_is_not(self) -> None:
        before = {"A": Result(100.0, None), "B": Result(100.0, None)}
        after = {"A": Result(111.0, None), "B": Result(109.0, None)}

        changes, _, _ = bench_compare.compare(before, after, slower=10, allocations=0)

        self.assertEqual([(c.name, c.slower) for c in changes], [("A", True), ("B", False)])

    def test_any_extra_allocation_is_a_regression_by_default(self) -> None:
        before = {"A": Result(100.0, 1000.0), "B": Result(100.0, 1000.0), "C": Result(100.0, None)}
        after = {"A": Result(90.0, 1001.0), "B": Result(90.0, 1000.0), "C": Result(90.0, 50.0)}

        changes, _, _ = bench_compare.compare(before, after, slower=10, allocations=0)

        self.assertEqual([(c.name, c.allocates_more) for c in changes], [("A", True), ("B", False), ("C", False)])
        self.assertTrue(changes[0].regressed)

    def test_benchmarks_on_one_side_only_are_named_not_compared(self) -> None:
        changes, added, removed = bench_compare.compare(
            {"Old": Result(1.0, None), "Both": Result(1.0, None)},
            {"New": Result(1.0, None), "Both": Result(1.0, None)},
            slower=10, allocations=0)

        self.assertEqual([c.name for c in changes], ["Both"])
        self.assertEqual((added, removed), (["New"], ["Old"]))

    def test_figures_are_written_as_a_person_reads_them(self) -> None:
        self.assertEqual(
            [bench_compare.time(ns) for ns in (12.0, 3_400.0, 2_500_000.0, 1_200_000_000.0)],
            ["12.0 ns", "3.40 us", "2.50 ms", "1.20 s"])
        self.assertEqual([bench_compare.size(b) for b in (None, 512.0, 2048.0, 3_145_728.0)], ["-", "512 B", "2.0 KB", "3.0 MB"])
        self.assertEqual([bench_compare.delta(o, n) for o, n in ((100, 110), (100, 90), (0, 0), (0, 5))], ["+10.0%", "-10.0%", "same", "new"])

    def test_the_report_marks_regressions_and_counts_them(self) -> None:
        changes, added, removed = bench_compare.compare(
            {"Fast": Result(100.0, 10.0), "Gone": Result(1.0, None), "Mem": Result(100.0, None)},
            {"Fast": Result(150.0, 10.0), "Fresh": Result(1.0, None), "Mem": Result(100.0, None)},
            slower=10, allocations=0)

        text = bench_compare.report(changes, added, removed)

        self.assertIn("Fast", text)
        self.assertIn("+50.0%", text)
        self.assertIn("<- regression", text)
        self.assertIn("new, nothing to compare with: Fresh", text)
        self.assertIn("in the baseline but not this run: Gone", text)
        self.assertTrue(text.endswith("2 compared, 1 regression(s)."))

    def test_main_exits_non_zero_on_a_regression_and_zero_without(self) -> None:
        write_report(self.root / "base", "S", [benchmark("S.Run", 100.0, 10)])
        write_report(self.root / "good", "S", [benchmark("S.Run", 101.0, 10)])
        write_report(self.root / "bad", "S", [benchmark("S.Run", 300.0, 10)])
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(bench_compare.main([str(self.root / "good"), "--baseline", str(self.root / "base")]), 0)
            self.assertEqual(bench_compare.main([str(self.root / "bad"), "--baseline", str(self.root / "base")]), 1)
            self.assertEqual(
                bench_compare.main([str(self.root / "bad"), "--baseline", str(self.root / "base"), "--slower", "500"]), 0)

    def test_main_says_when_a_folder_has_no_results(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(bench_compare.main([str(self.root / "nothing"), "--baseline", str(self.root)]), 2)
        self.assertIn("no BenchmarkDotNet full JSON reports", out.getvalue())


if __name__ == "__main__":
    unittest.main()
