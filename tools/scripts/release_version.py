"""The version a release build is stamped with, from VERSION and what triggered the build.

VERSION is the one version. A push of the tag v<VERSION> builds exactly that version; a tag that names any
other version fails, so a download can never be named one thing and stamped another. Every other build is a
rolling one, <VERSION>-rolling.<run>.g<commit>: the run number is GitHub's, which only ever counts up, so a
newer rolling build always sorts after an older one and reaches installed copies; the commit says exactly
which bytes it is. The Android build's version code is 1000 plus the commit count, which also only counts up
on the main branch.

    python tools/scripts/release_version.py --ref refs/heads/main --sha <sha> --run 42 --commits 312

prints the result as GitHub Actions step outputs: version=..., tagged=..., code=...
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SEMVER = re.compile(r"^\d+\.\d+\.\d+$")


def resolve(base: str, ref: str, sha: str, run: int) -> tuple[str, bool]:
    """The version to build and whether it is a tagged release."""
    if not SEMVER.match(base):
        raise ValueError(f"VERSION must be three numbers, such as 0.1.0, not {base!r}.")
    if ref.startswith("refs/tags/"):
        tag = ref.removeprefix("refs/tags/")
        if tag != f"v{base}":
            raise ValueError(f"The tag {tag} does not match VERSION {base}; tag v{base}, or change VERSION first.")
        return base, True
    if run <= 0:
        raise ValueError("A rolling build needs the run number, which orders it among the others.")
    if not re.fullmatch(r"[0-9a-f]{7,40}", sha):
        raise ValueError(f"{sha!r} is not a commit.")
    return f"{base}-rolling.{run}.g{sha[:7]}", False


def version_code(commits: int) -> int:
    """The Android version code: it must only ever grow, as the commit count on main does."""
    if commits <= 0:
        raise ValueError("The commit count must be positive.")
    return 1000 + commits


def main(arguments: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--ref", required=True)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--run", type=int, required=True)
    parser.add_argument("--commits", type=int, required=True)
    parser.add_argument("--version-file", type=Path, default=ROOT / "VERSION")
    options = parser.parse_args(arguments)
    try:
        version, tagged = resolve(options.version_file.read_text(encoding="utf-8").strip(), options.ref, options.sha, options.run)
        code = version_code(options.commits)
    except ValueError as problem:
        print(problem, file=sys.stderr)
        return 1
    print(f"version={version}")
    print(f"tagged={str(tagged).lower()}")
    print(f"code={code}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
