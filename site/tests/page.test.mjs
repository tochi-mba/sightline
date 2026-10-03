// start() against the real index.html, parsed into a stand-in page: what a visitor on each device
// sees, what the chips, tabs, menu and links do, and what the release lookup changes and does not.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const { DOWNLOADS, RELEASE_PAGE, chooseDownloads, guessLine, start } = await import(
  new URL("../app.js", import.meta.url).href
);
const { StandInEvent, parsePage, standInWindow } = await import(
  new URL("./support/page.mjs", import.meta.url).href
);
const { fakeFetch, fixture } = await import(new URL("./support/network.mjs", import.meta.url).href);

const HTML = await readFile(new URL("../index.html", import.meta.url), "utf8");

const ANDROID_PHONE = {
  userAgent:
    "Mozilla/5.0 (Linux; Android 14; SM-S928B) AppleWebKit/537.36 (KHTML, like Gecko) " +
    "SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36",
  userAgentData: { platform: "Android", mobile: true },
  maxTouchPoints: 5,
};
const WINDOWS_PC = {
  userAgent:
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
    "Chrome/129.0.0.0 Safari/537.36 Edg/129.0.0.0",
  userAgentData: { platform: "Windows", mobile: false },
  maxTouchPoints: 0,
};
const MAC = {
  userAgent:
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
    "Version/17.6 Safari/605.1.15",
  userAgentData: undefined,
  maxTouchPoints: 0,
};

const KEYS = Object.keys(DOWNLOADS);
const VERSION = "0.1.0-rolling.42.gabc1234";

async function latestRelease() {
  const windows = await fixture("latest-windows");
  const android = await fixture("latest-android");
  windows.assets.push(...android.assets);
  return windows;
}

/** Opens the page as a visitor on `device` would, with the release API answering as told. */
async function visit(device, { hash = "", answers = {} } = {}) {
  const page = parsePage(HTML);
  const network = fakeFetch(answers);
  const win = standInWindow({ ...device, hash, fetch: network.fetch });
  await start(page, win);
  return { page, win, network };
}

const element = (page, id) => {
  const found = page.getElementById(id);
  assert.notEqual(found, null, `the page has #${id}`);
  return found;
};
const offered = (page) => element(page, "download-list").children.map((item) => item.id);
const leading = (page) =>
  KEYS.filter((key) => element(page, `download-${key}`).classList.contains("is-lead"));
const pressed = (page) =>
  ["android", "windows", "other"].filter(
    (key) => element(page, `chip-${key}`).getAttribute("aria-pressed") === "true",
  );
const selectedTab = (page) =>
  ["android", "windows"].filter((key) => element(page, `tab-${key}`).getAttribute("aria-selected") === "true");
const shownPanels = (page) =>
  ["android", "windows"].filter((key) => !element(page, `install-${key}`).hidden);
const press = (target, key) => {
  const event = new StandInEvent("keydown", { key });
  target.dispatchEvent(event);
  return event;
};

test("before any script runs, every download safely falls back to the latest release page", () => {
  const page = parsePage(HTML);
  for (const key of Object.keys(DOWNLOADS)) {
    const link = element(page, `download-${key}`).querySelector("a");
    assert.equal(link.getAttribute("href"), RELEASE_PAGE, key);
  }
});

test("without the script, the panel is the view for any device, and both install guides show", () => {
  const page = parsePage(HTML);
  const anyDevice = chooseDownloads("unknown");
  assert.equal(element(page, "download-title").textContent, anyDevice.title);
  assert.equal(element(page, "download-note").textContent, anyDevice.note);
  assert.deepEqual(offered(page), KEYS.map((key) => `download-${key}`));
  assert.deepEqual(leading(page), []);
  assert.equal(element(page, "download-phone").hidden, true);
  assert.equal(element(page, "download-chips").hidden, true, "nothing could act on the chips");
  assert.equal(element(page, "install-tabs").hidden, true, "nothing could act on the tabs");
  assert.deepEqual(shownPanels(page), ["android", "windows"]);
  for (const key of KEYS) {
    const detail = element(page, `meta-${key}`);
    assert.equal(detail.hidden, true, key);
    assert.equal(detail.textContent, "", key);
  }
});

test("the phone's hint and its QR slot belong to the APK's entry, wherever that entry goes", () => {
  const page = parsePage(HTML);
  assert.equal(element(page, "download-phone").parentNode, element(page, "download-apk"));
});

test("an Android visitor is offered the APK first, with the Windows downloads beneath", async () => {
  const { page } = await visit(ANDROID_PHONE);
  assert.equal(element(page, "download-title").textContent, "Sightline for Android");
  assert.deepEqual(offered(page), ["download-apk", "download-setup", "download-portable", "download-cli"]);
  assert.deepEqual(leading(page), ["apk"]);
  assert.equal(element(page, "download-phone").hidden, true);
  assert.deepEqual(pressed(page), ["android"]);
  assert.equal(element(page, "download-guess").textContent, guessLine("android"));
  assert.equal(element(page, "download-chips").hidden, false);
  assert.equal(element(page, "install-tabs").hidden, false);
  assert.deepEqual(selectedTab(page), ["android"]);
  assert.deepEqual(shownPanels(page), ["android"]);
});

test("a Windows visitor is offered the installer, then the single files, then the phone", async () => {
  const { page } = await visit(WINDOWS_PC);
  assert.equal(element(page, "download-title").textContent, "Sightline for Windows");
  assert.deepEqual(offered(page), ["download-setup", "download-portable", "download-cli", "download-apk"]);
  assert.deepEqual(leading(page), ["setup"]);
  assert.equal(element(page, "download-phone").hidden, false, "Get it on your phone");
  assert.deepEqual(pressed(page), ["windows"]);
  assert.deepEqual(selectedTab(page), ["windows"]);
  assert.deepEqual(shownPanels(page), ["windows"]);
  assert.equal(element(page, "tab-windows").getAttribute("tabindex"), "0");
  assert.equal(element(page, "tab-android").getAttribute("tabindex"), "-1");
});

test("a Mac visitor is told where Sightline runs and offered every download, none leading", async () => {
  const { page } = await visit(MAC);
  assert.equal(element(page, "download-title").textContent, "Sightline preview packages for Android and Windows");
  assert.deepEqual(offered(page), KEYS.map((key) => `download-${key}`));
  assert.deepEqual(leading(page), []);
  assert.deepEqual(pressed(page), ["other"]);
  assert.equal(element(page, "download-guess").textContent, "This looks like a Mac. Not right? Pick a device.");
  assert.deepEqual(selectedTab(page), ["android"], "the install tabs keep their first tab");
});

test("the chips overrule the guess, and say so", async () => {
  const { page } = await visit(MAC);
  element(page, "chip-windows").click();
  assert.equal(element(page, "download-title").textContent, "Sightline for Windows");
  assert.equal(element(page, "download-guess").textContent, "Showing Sightline for Windows.");
  assert.deepEqual(pressed(page), ["windows"]);
  assert.deepEqual(selectedTab(page), ["windows"]);
  element(page, "chip-other").click();
  assert.equal(element(page, "download-guess").textContent, "Showing every download.");
  assert.deepEqual(leading(page), []);
  assert.equal(element(page, "download-phone").hidden, true);
  assert.deepEqual(selectedTab(page), ["windows"], "every download leaves the tabs where they were");
  element(page, "chip-android").click();
  assert.deepEqual(offered(page)[0], "download-apk");
  assert.deepEqual(selectedTab(page), ["android"]);
});

test("a link to one platform's install steps opens them, whatever the guess said", async () => {
  const { page, win } = await visit(ANDROID_PHONE, { hash: "#install-windows" });
  assert.deepEqual(selectedTab(page), ["windows"]);
  win.location.hash = "#install-android";
  win.dispatch("hashchange");
  assert.deepEqual(selectedTab(page), ["android"]);
  win.location.hash = "#faq";
  win.dispatch("hashchange");
  assert.deepEqual(selectedTab(page), ["android"], "any other anchor leaves the tabs alone");
});

test("the tabs answer a click and the arrow, Home and End keys", async () => {
  const { page } = await visit(MAC);
  const android = element(page, "tab-android");
  const windows = element(page, "tab-windows");
  windows.click();
  assert.deepEqual(selectedTab(page), ["windows"]);
  android.click();
  const right = press(android, "ArrowRight");
  assert.equal(right.defaultPrevented, true);
  assert.deepEqual(selectedTab(page), ["windows"]);
  assert.equal(page.activeElement, windows, "focus follows the selection");
  press(windows, "ArrowRight");
  assert.deepEqual(selectedTab(page), ["android"], "the arrows wrap");
  press(android, "End");
  assert.deepEqual(selectedTab(page), ["windows"]);
  press(windows, "Home");
  assert.deepEqual(selectedTab(page), ["android"]);
  assert.equal(page.activeElement, android);
});

test("a key that is not the tabs' business is left to the page", async () => {
  const { page } = await visit(MAC);
  const enter = press(element(page, "tab-android"), "Enter");
  assert.equal(enter.defaultPrevented, false);
  assert.deepEqual(selectedTab(page), ["android"]);
  assert.equal(page.activeElement, null);
});

test("the menu opens and closes from its button, and closes when a link in it is followed", async () => {
  const { page } = await visit(ANDROID_PHONE);
  const button = element(page, "menu-button");
  const nav = element(page, "site-nav");
  button.click();
  assert.equal(nav.classList.contains("open"), true);
  assert.equal(button.getAttribute("aria-expanded"), "true");
  button.click();
  assert.equal(nav.classList.contains("open"), false);
  assert.equal(button.getAttribute("aria-expanded"), "false");
  button.click();
  nav.querySelector("a").click();
  assert.equal(nav.classList.contains("open"), false);
});

test("Escape closes the open menu and puts focus back on its button", async () => {
  const { page } = await visit(ANDROID_PHONE);
  const button = element(page, "menu-button");
  button.click();
  press(element(page, "main"), "a");
  assert.equal(button.getAttribute("aria-expanded"), "true", "other keys leave it open");
  press(element(page, "main"), "Escape");
  assert.equal(button.getAttribute("aria-expanded"), "false");
  assert.equal(page.activeElement, button);
});

test("Escape with the menu closed leaves focus where it is", async () => {
  const { page } = await visit(ANDROID_PHONE);
  const chip = element(page, "chip-windows");
  chip.focus();
  press(chip, "Escape");
  assert.equal(page.activeElement, chip);
  assert.equal(element(page, "menu-button").getAttribute("aria-expanded"), "false");
});

test("while the menu is open, focus that leaves it is brought back to its first link", async () => {
  const { page } = await visit(ANDROID_PHONE);
  const button = element(page, "menu-button");
  const nav = element(page, "site-nav");
  const [first, second] = nav.querySelectorAll("a");
  button.click();
  second.focus();
  assert.equal(page.activeElement, second, "moving within the menu is fine");
  button.focus();
  assert.equal(page.activeElement, button, "so is going back to its button");
  element(page, "chip-android").focus();
  assert.equal(page.activeElement, first);
  button.click();
  element(page, "chip-android").focus();
  assert.equal(page.activeElement, element(page, "chip-android"), "a closed menu holds nothing");
});

test("exact assets become direct links once GitHub's latest release answers", async () => {
  const { page, network } = await visit(WINDOWS_PC, { answers: { latest: await latestRelease() } });
  const lines = {
    apk: `Version ${VERSION}, 31 MB, 1 Oct 2026`,
    setup: `Version ${VERSION}, 94 MB, 2 Oct 2026`,
    portable: `Version ${VERSION}, 68 MB, 2 Oct 2026`,
    cli: `Version ${VERSION}, 5.5 MB, 2 Oct 2026`,
  };
  for (const [key, line] of Object.entries(lines)) {
    assert.equal(element(page, `meta-${key}`).textContent, line, key);
    assert.equal(element(page, `meta-${key}`).hidden, false, key);
    const link = element(page, `download-${key}`).querySelector("a");
    assert.ok(link.getAttribute("href").endsWith(`/${DOWNLOADS[key].asset}`), key);
    assert.equal(link.querySelector(".download-file").textContent, DOWNLOADS[key].asset);
  }
  assert.equal(network.requests.length, 1);
});

test("a latest release that lacks files leaves their release-page fallbacks intact", async () => {
  const { page } = await visit(WINDOWS_PC, { answers: { latest: await fixture("latest-windows-partial") } });
  assert.equal(element(page, "meta-setup").hidden, false);
  for (const key of ["apk", "portable", "cli"]) {
    assert.equal(element(page, `meta-${key}`).hidden, true, key);
    assert.equal(element(page, `download-${key}`).querySelector("a").getAttribute("href"), RELEASE_PAGE);
  }
});

test("when GitHub cannot be reached or refuses, the page looks just as it would have", async () => {
  for (const answers of [{}, { latest: 403 }]) {
    const { page } = await visit(ANDROID_PHONE, { answers });
    for (const key of Object.keys(DOWNLOADS)) {
      assert.equal(element(page, `meta-${key}`).hidden, true, key);
      assert.equal(element(page, `meta-${key}`).textContent, "", key);
      const href = element(page, `download-${key}`).querySelector("a").getAttribute("href");
      assert.equal(href, RELEASE_PAGE);
    }
  }
});

test("with no page to wire, start does nothing", async () => {
  assert.equal(await start(), undefined);
});
