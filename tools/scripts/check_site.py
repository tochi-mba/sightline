#!/usr/bin/env python3
"""Check the GitHub Pages site before it is published.

A static site has no compiler, so nothing otherwise catches a broken anchor, a missing asset, an
image a screen reader would read out by file name, draft text that escaped, or a page that has
quietly stopped making a promise it must keep. This is that gate; the Pages workflow runs it on
every push, and so does the test suite.

Standard library only, on purpose: the workflow should not install anything to verify a page that
has no build step.

Usage::

    python tools/scripts/check_site.py                  # the site in this repository
    python tools/scripts/check_site.py path/to/site     # any site laid out the same way
"""

from __future__ import annotations

import re
import sys
from html.parser import HTMLParser
from pathlib import Path

SITE = Path(__file__).resolve().parents[2] / "site"
PRODUCT = "Sightline"
REPOSITORY = "tochi-mba/sightline"
"""The only repository the site may link to; anything else is a stale copy from a sibling."""

CANONICAL = "https://tochi-mba.github.io/sightline/"
SITE_PATH = "/sightline"
"""Where Pages serves the site: a project site lives under its repository's name."""

REQUIRED_CLAIMS = (
    "REX Technologies",
    "No account",
    "Android 10 or newer",
    "Windows 10 and 11, 64-bit",
    "Windows protected your PC",
)
"""What the home page must keep saying: who makes it, what it never asks for, what it runs on, and
the plain warning that the Windows build is not code-signed. A rewrite that drops one fails here."""

FORBIDDEN_PATTERNS = (
    r"\bTODO\b",
    r"\bFIXME\b",
    r"\bTBD\b",
    r"\bLorem ipsum\b",
    r"\bXXX\b",
    r"\bcoming soon\b",
)
"""Text that means a draft escaped."""

REMOTE = ("http://", "https://", "data:", "//", "mailto:")


class PageParser(HTMLParser):
    """Collects the ids, links and asset references a page depends on."""

    def __init__(self) -> None:
        super().__init__()
        self.ids: set[str] = set()
        self.hrefs: list[str] = []
        self.assets: list[str] = []
        self.title = ""
        self.canonical: str | None = None
        self.images_without_alt: list[str] = []
        self._in_title = False

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = {key: (value or "") for key, value in attrs}
        if "id" in values:
            self.ids.add(values["id"])
        if tag == "title":
            self._in_title = True
        if tag == "a" and "href" in values:
            self.hrefs.append(values["href"])
        if tag == "img":
            self.assets.append(values.get("src", ""))
            # An explicitly empty alt marks an image decorative; a missing one does not.
            if "alt" not in values:
                self.images_without_alt.append(values.get("src") or "(no src)")
        if tag == "link" and "href" in values:
            if values.get("rel") == "canonical":
                self.canonical = values["href"]
            else:
                self.assets.append(values["href"])
        if tag == "script" and values.get("src"):
            self.assets.append(values["src"])

    def handle_endtag(self, tag: str) -> None:
        if tag == "title":
            self._in_title = False

    def handle_data(self, data: str) -> None:
        if self._in_title:
            self.title += data


def check(site: Path = SITE) -> list[str]:
    """Every problem with the site. Empty means it is publishable."""
    index = site / "index.html"
    if not index.exists():
        return [f"{index} is missing; there is no site to publish."]
    problems: list[str] = []
    if not (site / ".nojekyll").exists():
        problems.append(
            "site/.nojekyll is missing: without it Pages runs the site through Jekyll, "
            "which drops files beginning with an underscore."
        )
    for page in sorted(site.glob("*.html")):
        problems.extend(f"{page.name}: {problem}" for problem in _check_page(page))
    problems.extend(f"index.html: {problem}" for problem in _check_home(index, site / "app.js"))
    return problems


def _check_page(page: Path) -> list[str]:
    problems: list[str] = []
    html = page.read_text(encoding="utf-8")
    parser = PageParser()
    parser.feed(html)
    if PRODUCT not in parser.title:
        problems.append(f"the title does not name {PRODUCT}.")
    if "REX Technologies" not in html:
        problems.append("the page does not name REX Technologies.")
    for asset in parser.assets:
        if not asset or asset.startswith(REMOTE):
            continue
        if not _local(page.parent, asset).exists():
            problems.append(f"asset '{asset}' is referenced but missing.")
    problems.extend(
        f"anchor '{href}' points at an id that does not exist."
        for href in parser.hrefs
        if href.startswith("#") and href[1:] and href[1:] not in parser.ids
    )
    problems.extend(f"image '{image}' has no alt text." for image in parser.images_without_alt)
    for pattern in FORBIDDEN_PATTERNS:
        match = re.search(pattern, html, re.IGNORECASE)
        if match:
            problems.append(f"draft text left in the page: '{match.group(0)}'.")
    insecure = sorted(set(re.findall(r"""http://[^"'\s<>]+""", html)))
    if insecure:
        problems.append(f"links that are not https: {insecure}.")
    repositories = set(re.findall(r"https://(?:api\.)?github\.com/(?:repos/)?([^/\"'\s]+/[^/\"'\s#?]+)", html))
    if repositories - {REPOSITORY}:
        problems.append(f"the page links to repositories other than {REPOSITORY}: {sorted(repositories)}.")
    return problems


def _local(site: Path, asset: str) -> Path:
    """Where an asset reference lands in the site folder.

    The 404 page is served at whatever address was missing, however deep, so it must name its
    assets from the site's root (/sightline/styles.css); everything else names them relatively.
    """
    path = asset.split("?")[0].split("#")[0]
    root = f"{SITE_PATH}/"
    return site / (path[len(root):] if path.startswith(root) else path)


def _check_home(index: Path, script: Path) -> list[str]:
    """What only the home page has to get right: its claims, its address, and its script's hooks."""
    html = index.read_text(encoding="utf-8")
    parser = PageParser()
    parser.feed(html)
    problems = [f"the page no longer says '{claim}'." for claim in REQUIRED_CLAIMS if claim not in html]
    if parser.canonical != CANONICAL:
        problems.append(f"the canonical address is {parser.canonical!r}, not {CANONICAL!r}.")
    if script.exists():
        wanted = set(re.findall(r"""getElementById\(\s*["']([^"']+)["']""", script.read_text(encoding="utf-8")))
        missing = sorted(wanted - parser.ids)
        if missing:
            problems.append(f"app.js looks for ids the page does not have: {missing}.")
    return problems


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    site = Path(args[0]) if args else SITE
    problems = check(site)
    if problems:
        print(f"{len(problems)} problem(s) with the site:")
        for problem in problems:
            print(f"  - {problem}")
        return 1
    print("site checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
