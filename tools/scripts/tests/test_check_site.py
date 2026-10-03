"""The Pages site is checked before it is published, and the shipped site passes.

Run with: python -m unittest discover -s tools/scripts/tests
"""

from __future__ import annotations

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import check_site  # noqa: E402

GOOD = """<!doctype html><html lang="en"><head><title>Sightline — REX Technologies</title>
<link rel="canonical" href="https://tochi-mba.github.io/sightline/">
<link rel="stylesheet" href="styles.css"></head>
<body><a href="#top">top</a><main id="top"><img src="mark.svg" alt="">
<p>No account. Android 10 or newer. Windows 10 and 11, 64-bit.
If it says Windows protected your PC, choose Run anyway.</p>
<span id="hook"></span>
<a href="https://github.com/tochi-mba/sightline">source</a></main>
<script src="app.js"></script></body></html>
"""
SCRIPT = 'document.getElementById("hook");\n'


class CheckSiteTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.root = Path(self._temp.name)

    def tearDown(self) -> None:
        self._temp.cleanup()

    def site(self, html: str = GOOD, *, nojekyll: bool = True, script: str = SCRIPT) -> Path:
        site = self.root / "site"
        site.mkdir()
        (site / "index.html").write_text(html, encoding="utf-8")
        for name in ("styles.css", "mark.svg"):
            (site / name).write_text("", encoding="utf-8")
        (site / "app.js").write_text(script, encoding="utf-8")
        if nojekyll:
            (site / ".nojekyll").write_text("", encoding="utf-8")
        return site

    def test_the_shipped_site_passes(self) -> None:
        self.assertEqual(check_site.check(), [])

    def test_a_sound_site_has_no_problems(self) -> None:
        self.assertEqual(check_site.check(self.site()), [])

    def test_a_missing_index_is_the_only_problem_worth_naming(self) -> None:
        [problem] = check_site.check(self.root)
        self.assertTrue(problem.endswith("is missing; there is no site to publish."))

    def test_each_kind_of_problem_is_named(self) -> None:
        cases = [
            (("<title>Sightline", "<title>Other"), "the title does not name Sightline."),
            (
                ('href="styles.css"', 'href="missing.css"'),
                "asset 'missing.css' is referenced but missing.",
            ),
            (('href="#top"', 'href="#nowhere"'), "anchor '#nowhere' points at an id that does not exist."),
            (('<img src="mark.svg" alt="">', '<img src="mark.svg">'), "image 'mark.svg' has no alt text."),
            (("source</a>", "source</a> coming soon"), "draft text left in the page: 'coming soon'."),
            (
                ("https://github.com/tochi-mba/sightline", "http://example.com/x"),
                "links that are not https: ['http://example.com/x'].",
            ),
            (
                ("github.com/tochi-mba/sightline", "github.com/tochi-mba/flint"),
                "the page links to repositories other than tochi-mba/sightline: ['tochi-mba/flint'].",
            ),
            (("No account.", ""), "the page no longer says 'No account'."),
            (
                ("https://tochi-mba.github.io/sightline/", "https://example.com/"),
                "the canonical address is 'https://example.com/', not 'https://tochi-mba.github.io/sightline/'.",
            ),
        ]
        for (old, new), said in cases:
            with self.subTest(said=said):
                self.assertIn(old, GOOD)
                problems = check_site.check(self.site(GOOD.replace(old, new)))
                self.assertEqual(problems, [f"index.html: {said}"])
                self._temp.cleanup()
                self._temp = tempfile.TemporaryDirectory()
                self.root = Path(self._temp.name)

    def test_the_page_must_still_name_rex_technologies(self) -> None:
        problems = check_site.check(self.site(GOOD.replace("REX Technologies", "Somebody")))
        self.assertIn("index.html: the page does not name REX Technologies.", problems)

    def test_a_script_hook_the_page_does_not_have_is_named(self) -> None:
        site = self.site(script=SCRIPT + 'document.getElementById("gone");\n')
        self.assertEqual(check_site.check(site), ["index.html: app.js looks for ids the page does not have: ['gone']."])

    def test_a_site_without_a_script_skips_the_hook_check(self) -> None:
        site = self.site()
        (site / "app.js").unlink()
        html = GOOD.replace('<script src="app.js"></script>', "")
        (site / "index.html").write_text(html, encoding="utf-8")
        self.assertEqual(check_site.check(site), [])

    def test_a_site_without_nojekyll_is_named(self) -> None:
        [problem] = check_site.check(self.site(nojekyll=False))
        self.assertTrue(problem.startswith("site/.nojekyll is missing"))

    def test_the_404_page_may_name_assets_from_the_sites_root(self) -> None:
        site = self.site()
        not_found = GOOD.replace('href="styles.css"', 'href="/sightline/styles.css"')
        (site / "404.html").write_text(not_found, encoding="utf-8")
        self.assertEqual(check_site.check(site), [])

    def test_every_page_is_checked_not_only_the_index(self) -> None:
        site = self.site()
        (site / "404.html").write_text(GOOD.replace("<title>Sightline", "<title>Lost"), encoding="utf-8")
        self.assertEqual(check_site.check(site), ["404.html: the title does not name Sightline."])

    def test_remote_assets_and_empty_sources_are_not_checked_for_existence(self) -> None:
        html = GOOD.replace(
            '<link rel="stylesheet" href="styles.css">',
            '<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter">'
            '<link rel="preconnect" href="//fonts.gstatic.com"><img src="" alt="">',
        )
        self.assertEqual(check_site.check(self.site(html)), [])

    def test_main_reports_and_exits_as_a_gate(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(check_site.main([str(self.site())]), 0)
        self.assertIn("site checks passed", out.getvalue())
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(check_site.main([str(self.root / "nowhere")]), 1)
        self.assertIn("1 problem(s) with the site:", out.getvalue())
        self.assertIn("there is no site to publish", out.getvalue())

    def test_main_defaults_to_the_shipped_site(self) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(check_site.main([]), 0)


if __name__ == "__main__":
    unittest.main()
