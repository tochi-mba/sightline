#!/usr/bin/env python3
"""Compare a benchmark run with the recorded baseline, and fail on a regression.

A performance claim in this repository needs a before and after; this is the after. It reads
BenchmarkDotNet's full JSON reports — every ``*-report-full.json`` under a folder, one per
benchmark class — and compares each benchmark with the same one in the baseline by its full name,
on two things a person pays for: the mean time of one operation, and the bytes it allocates (on a
phone, allocation is garbage collection, dropped frames and battery).

A regression is a benchmark more than ``--slower`` percent slower (default 10), or allocating more
than ``--allocations`` percent more (default 0: any increase). The exit code says whether there was
one, so a script or CI can stop on it. Benchmarks on only one side are listed and skipped: a new
benchmark has nothing to regress from, and a removed one has nothing to measure.

Standard library only.

Usage::

    python tools/scripts/bench_compare.py artifacts/bench/20261004-101500
    python tools/scripts/bench_compare.py RUN --baseline benchmarks/baseline/dotnet --slower 5
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from pathlib import Path

BASELINE = Path(__file__).resolve().parents[2] / "benchmarks" / "baseline" / "dotnet"


@dataclass(frozen=True)
class Result:
    """One benchmark's figures: nanoseconds per operation, and bytes allocated per operation."""

    mean_ns: float
    allocated: float | None


@dataclass(frozen=True)
class Change:
    """One benchmark on both sides, and whether it regressed."""

    name: str
    before: Result
    after: Result
    slower: bool
    allocates_more: bool

    @property
    def regressed(self) -> bool:
        return self.slower or self.allocates_more


def load(folder: Path) -> dict[str, Result]:
    """Every benchmark in every full JSON report under ``folder``, by full name."""
    results: dict[str, Result] = {}
    reports = sorted(folder.rglob("*-report-full.json"))
    if not reports:
        raise FileNotFoundError(f"no BenchmarkDotNet full JSON reports under {folder}")
    for report in reports:
        for benchmark in json.loads(report.read_text(encoding="utf-8")).get("Benchmarks", []):
            statistics = benchmark.get("Statistics") or {}
            if "Mean" not in statistics:
                # A benchmark that failed has no statistics; it cannot be compared, so it is left out.
                continue
            memory = benchmark.get("Memory") or {}
            results[benchmark["FullName"]] = Result(
                float(statistics["Mean"]),
                float(memory["BytesAllocatedPerOperation"]) if "BytesAllocatedPerOperation" in memory else None,
            )
    return results


def compare(
    before: dict[str, Result], after: dict[str, Result], slower: float, allocations: float
) -> tuple[list[Change], list[str], list[str]]:
    """The changes for benchmarks on both sides, then the names only in the run, then only in the baseline."""
    changes = []
    for name in sorted(before.keys() & after.keys()):
        old, new = before[name], after[name]
        allocates_more = (
            old.allocated is not None
            and new.allocated is not None
            and new.allocated > old.allocated * (1 + allocations / 100)
        )
        changes.append(Change(name, old, new, new.mean_ns > old.mean_ns * (1 + slower / 100), allocates_more))
    return changes, sorted(after.keys() - before.keys()), sorted(before.keys() - after.keys())


def delta(old: float, new: float) -> str:
    """A change as a signed percentage, or the plain fact when there was nothing to change from."""
    if old == 0:
        return "same" if new == 0 else "new"
    return f"{(new - old) / old * 100:+.1f}%"


def time(ns: float) -> str:
    """Nanoseconds as a person reads them."""
    for unit, size in (("s", 1e9), ("ms", 1e6), ("us", 1e3)):
        if ns >= size:
            return f"{ns / size:.2f} {unit}"
    return f"{ns:.1f} ns"


def size(allocated: float | None) -> str:
    """Bytes as a person reads them, or a dash when the report did not measure memory."""
    if allocated is None:
        return "-"
    for unit, scale in (("MB", 1 << 20), ("KB", 1 << 10)):
        if allocated >= scale:
            return f"{allocated / scale:.1f} {unit}"
    return f"{allocated:.0f} B"


def report(changes: list[Change], added: list[str], removed: list[str]) -> str:
    """The comparison as a table, with the regressions marked."""
    lines = [f"{'benchmark':<70} {'time':>22} {'allocated':>24}"]
    for change in changes:
        mark = "  <- regression" if change.regressed else ""
        lines.append(
            f"{change.name:<70} "
            f"{time(change.after.mean_ns):>11} {delta(change.before.mean_ns, change.after.mean_ns):>10} "
            f"{size(change.after.allocated):>11} "
            f"{delta(change.before.allocated or 0, change.after.allocated or 0) if change.after.allocated is not None else '':>12}"
            f"{mark}"
        )
    lines.extend(f"new, nothing to compare with: {name}" for name in added)
    lines.extend(f"in the baseline but not this run: {name}" for name in removed)
    regressions = sum(change.regressed for change in changes)
    lines.append(f"{len(changes)} compared, {regressions} regression(s).")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("run", type=Path, help="a folder holding a run's BenchmarkDotNet results")
    parser.add_argument("--baseline", type=Path, default=BASELINE, help="the baseline's results folder")
    parser.add_argument("--slower", type=float, default=10.0, help="percent slower that counts as a regression")
    parser.add_argument("--allocations", type=float, default=0.0, help="percent more allocation that counts as one")
    args = parser.parse_args(sys.argv[1:] if argv is None else argv)
    try:
        before, after = load(args.baseline), load(args.run)
    except FileNotFoundError as missing:
        print(missing)
        return 2
    changes, added, removed = compare(before, after, args.slower, args.allocations)
    print(report(changes, added, removed))
    return 1 if any(change.regressed for change in changes) else 0


if __name__ == "__main__":
    sys.exit(main())
