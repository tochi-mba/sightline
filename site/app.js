/*
  Sightline - REX Technologies. The page works without this file: every download opens the latest
  release page and both sets of install steps are on show. With it, the download panel leads with
  the build for the device reading the page and exact assets returned by GitHub become direct links.

  The guess at the device uses only what the browser says about itself, and a row of chips lets the
  visitor overrule it, because reading about a phone app on a laptop is the ordinary case. The
  size and date come from GitHub's latest-release API. Direct asset URLs are used only when GitHub
  actually returns the exact named file. Until then every control leads to the latest release page;
  the page never invents an asset URL.

  Every decision is a pure function exported for node's test runner, which holds this file to every
  line, branch and function. start() is the only part that touches a page; the tests hand it a
  stand-in built from the real index.html.

  Nothing identifies the visitor. The only request this file makes is one public release lookup,
  made without credentials.
*/

/** The one repository configuration used by both release lookup and download validation. */
export const REPOSITORY = "tochi-mba/sightline";

export const RELEASE_API = `https://api.github.com/repos/${REPOSITORY}/releases/latest`;
export const RELEASE_PAGE = `https://github.com/${REPOSITORY}/releases/latest`;
const DOWNLOAD_PREFIX = `https://github.com/${REPOSITORY}/releases/download/`;

/**
 * The files the panel offers, each by its exact canonical release-asset name. A publisher may add
 * other files, but only these can become the four download buttons.
 */
export const DOWNLOADS = Object.freeze({
  apk: Object.freeze({ asset: "Sightline.apk" }),
  setup: Object.freeze({ asset: "Sightline-Setup.exe" }),
  portable: Object.freeze({ asset: "Sightline-Portable.exe" }),
  cli: Object.freeze({ asset: "sightline-cli.exe" }),
});

/**
 * Which platform a browser is on, from what it says about itself: "android", "windows", "ios",
 * "mac", "linux", "chromeos" or "unknown".
 *
 * The order of the checks is the substance. An iPad has called itself a Mac since iPadOS 13 and is
 * told apart only by its touch screen. A Chromebook says Linux and runs Android apps. Silk, the
 * browser on Fire tablets, drops the word Android in desktop mode, and so does Chrome on a large
 * Android tablet, where only the client hint still says Android. So each answer is given only once
 * everything that could be mistaken for it has been ruled out. Windows on ARM needs no case of its
 * own: its browsers report Windows, and the Windows build is what it runs.
 *
 * @param {string} userAgent navigator.userAgent
 * @param {{platform: string} | undefined} uaData navigator.userAgentData, where the browser has it
 * @param {number} maxTouchPoints navigator.maxTouchPoints
 * @returns {string}
 */
export function detectPlatform(userAgent, uaData, maxTouchPoints) {
  const touchMac = /\bMacintosh\b/.test(userAgent) && maxTouchPoints > 1;
  if (touchMac || /\b(?:iPhone|iPad|iPod)\b/.test(userAgent)) return "ios";
  if (/\bCrOS\b/.test(userAgent)) return "chromeos";
  const androidHint = uaData !== undefined && uaData.platform === "Android";
  if (androidHint || /\bAndroid\b|\bSilk\//.test(userAgent)) return "android";
  if (/\bWindows\b/.test(userAgent)) return "windows";
  if (/\bMacintosh\b/.test(userAgent)) return "mac";
  if (/\b(?:Linux|X11)\b/.test(userAgent)) return "linux";
  return "unknown";
}

/** Every download, in the order the panel offers them when it has no reason to prefer one. */
const EVERY_DOWNLOAD = Object.freeze(["apk", "setup", "portable", "cli"]);

const VIEWS = new Map([
  [
    "android",
    Object.freeze({
      view: "android",
      title: "Sightline for Android",
      note: "The Android app is first. The Windows downloads are underneath, for a PC.",
      lead: "apk",
      order: EVERY_DOWNLOAD,
      phone: false,
      tab: "android",
      chosen: "Showing Sightline for Android.",
    }),
  ],
  [
    "windows",
    Object.freeze({
      view: "windows",
      title: "Sightline for Windows",
      note: "The installer is first, then the portable app and the command line, then the phone app.",
      lead: "setup",
      order: Object.freeze(["setup", "portable", "cli", "apk"]),
      phone: true,
      tab: "windows",
      chosen: "Showing Sightline for Windows.",
    }),
  ],
  [
    "other",
    Object.freeze({
      view: "other",
      title: "Sightline preview packages for Android and Windows",
      note: "Sightline runs on Android and Windows. Every download is shown here, so you can fetch one for another device.",
      lead: "",
      order: EVERY_DOWNLOAD,
      phone: false,
      tab: "",
      chosen: "Showing every download.",
    }),
  ],
]);

/**
 * What the download panel shows for a platform, or for a chip the visitor pressed ("android",
 * "windows" or "other"). Android leads with the APK and keeps the Windows downloads beneath it.
 * Windows leads with the installer, then the portable app and the command line, and offers the APK
 * as "Get it on your phone". Anything else sees every download with none leading, because Sightline
 * does not run there and the visitor is most likely fetching it for another device.
 *
 * The page's own markup is the "other" view, so it is also what a visitor without scripting sees.
 */
export function chooseDownloads(platform) {
  return VIEWS.get(VIEWS.has(platform) ? platform : "other");
}

const DEVICES = new Map([
  ["android", "an Android phone or tablet"],
  ["windows", "a Windows PC"],
  ["ios", "an iPhone or iPad"],
  ["mac", "a Mac"],
  ["linux", "a Linux computer"],
  ["chromeos", "a Chromebook"],
]);

/** The sentence above the chips: the guess, said as a guess, or a plain request without one. */
export function guessLine(platform) {
  return DEVICES.has(platform)
    ? `This looks like ${DEVICES.get(platform)}. Not right? Pick a device.`
    : "Pick the device you are installing on.";
}

/**
 * Which tab a key moves to from the tab at `index`, as the ARIA tabs pattern has it: the arrows
 * wrap, Home and End go to the ends, and any other key is not the tabs' business (-1).
 */
export function nextTab(key, index, count) {
  switch (key) {
    case "ArrowRight":
      return (index + 1) % count;
    case "ArrowLeft":
      return (index - 1 + count) % count;
    case "Home":
      return 0;
    case "End":
      return count - 1;
    default:
      return -1;
  }
}

/**
 * The versioned twin of each stable file: Sightline-<version>.apk, Sightline-Setup-<version>.exe and
 * Sightline-Portable-<version>.exe. Every file on a release is built from the same commit, so the
 * version read from any twin is the command line's too, which has no twin of its own.
 */
const VERSIONED = /^Sightline(?:-Setup|-Portable)?-(\d[0-9A-Za-z.+-]*)\.(?:apk|exe)$/;

/** The version a release's files carry, or "" when none of them says. */
export function versionOf(assets) {
  for (const asset of assets) {
    if (typeof asset?.name !== "string") continue;
    const match = VERSIONED.exec(asset.name);
    if (match !== null) return match[1];
  }
  return "";
}

/** A file size as people read one: whole megabytes from ten up, one decimal below, KB under one. */
export function formatSize(bytes) {
  if (!(Number.isFinite(bytes) && bytes > 0)) return "";
  const megabytes = bytes / 1048576;
  if (megabytes < 1) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${megabytes < 10 ? megabytes.toFixed(1) : Math.round(megabytes)} MB`;
}

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/**
 * A date as "1 Oct 2026", in UTC. Spelling the month out leaves nobody to wonder which number is
 * the day, and UTC gives every reader the same answer, which also keeps the tests exact.
 */
export function formatDate(iso) {
  const when = new Date(typeof iso === "string" ? iso : Number.NaN);
  if (Number.isNaN(when.getTime())) return "";
  return `${when.getUTCDate()} ${MONTHS[when.getUTCMonth()]} ${when.getUTCFullYear()}`;
}

/**
 * The line under a download once its release has been read: version, size and date, as far as the
 * release says them. "" when there is no release or it does not carry the file, and the plain link
 * should stand alone.
 */
export function describeDownload(release, download) {
  if (release === null) return "";
  const asset = release.assets.find((candidate) => candidate?.name === download.asset);
  if (asset === undefined) return "";
  const version = versionOf(release.assets);
  const parts = [
    version === "" ? "" : `Version ${version}`,
    formatSize(asset.size),
    formatDate(asset.updated_at),
  ];
  return parts.filter((part) => part !== "").join(", ");
}

/**
 * GitHub's direct URL for the exact asset, or "". API data is remote input: an asset with the
 * right name but a URL outside this repository is not made clickable.
 */
export function downloadUrl(release, download) {
  if (release === null || !Array.isArray(release.assets)) return "";
  const asset = release.assets.find((candidate) => candidate?.name === download.asset);
  if (asset === undefined || typeof asset.browser_download_url !== "string") return "";
  return asset.browser_download_url.startsWith(DOWNLOAD_PREFIX) ? asset.browser_download_url : "";
}

/** The valid direct URL and description for every exact asset attached to the latest release. */
export function resolveDownloads(release) {
  const resolved = {};
  for (const [key, download] of Object.entries(DOWNLOADS)) {
    const href = downloadUrl(release, download);
    if (href === "") continue;
    resolved[key] = Object.freeze({ href, detail: describeDownload(release, download) });
  }
  return resolved;
}

/** Credentials stay home: the releases API needs none, and the page has nobody to identify. */
const RELEASE_REQUEST = Object.freeze({
  headers: Object.freeze({ Accept: "application/vnd.github+json" }),
  credentials: "omit",
});

/**
 * One release, as the API documents it, or null. Offline, blocked, rate limited, missing or not
 * JSON all come to the same thing for this page: the plain links stand, so there is nothing to say.
 */
export async function fetchRelease(fetcher) {
  try {
    const response = await fetcher(RELEASE_API, RELEASE_REQUEST);
    return response.ok ? await response.json() : null;
  } catch {
    return null;
  }
}

/**
 * Reads the latest release and resolves only exact, well-shaped assets. Offline and unpublished
 * releases leave the release-page fallbacks untouched; a malformed entry is skipped on its own,
 * without costing the well-formed files beside it their direct links.
 */
export async function loadDownloads(fetcher) {
  return resolveDownloads(await fetchRelease(fetcher));
}

/** The header menu on small screens: a toggle, closed by a link, by Escape, and kept in focus. */
function wireMenu(doc) {
  const button = doc.getElementById("menu-button");
  const nav = doc.getElementById("site-nav");
  const isOpen = () => button.getAttribute("aria-expanded") === "true";
  const setOpen = (open) => {
    nav.classList.toggle("open", open);
    button.setAttribute("aria-expanded", String(open));
  };
  button.addEventListener("click", () => setOpen(!isOpen()));
  for (const link of nav.querySelectorAll("a")) link.addEventListener("click", () => setOpen(false));
  doc.addEventListener("keydown", (event) => {
    if (event.key !== "Escape" || !isOpen()) return;
    setOpen(false);
    // Closing with the keyboard has to put focus somewhere sensible, or it falls back to the page.
    button.focus();
  });
  // While the menu is open it is the whole page as far as the keyboard is concerned.
  doc.addEventListener("focusin", (event) => {
    if (!isOpen() || nav.contains(event.target) || event.target === button) return;
    nav.querySelector("a").focus();
  });
}

/**
 * The install steps as tabs. Without scripting both panels show one after the other and the tab row
 * stays hidden, so turning them into tabs is this file's job and nobody else's.
 */
function wireTabs(doc) {
  const keys = ["android", "windows"];
  const tabs = [doc.getElementById("tab-android"), doc.getElementById("tab-windows")];
  const panels = [doc.getElementById("install-android"), doc.getElementById("install-windows")];
  const select = (chosen, focus) => {
    tabs.forEach((tab, index) => {
      tab.setAttribute("aria-selected", String(index === chosen));
      tab.setAttribute("tabindex", index === chosen ? "0" : "-1");
      panels[index].hidden = index !== chosen;
    });
    if (focus) tabs[chosen].focus();
  };
  tabs.forEach((tab, index) => {
    tab.addEventListener("click", () => select(index, false));
    tab.addEventListener("keydown", (event) => {
      const next = nextTab(event.key, index, tabs.length);
      if (next === -1) return;
      event.preventDefault();
      select(next, true);
    });
  });
  doc.getElementById("install-tabs").hidden = false;
  select(0, false);
  return {
    show: (key) => select(keys.indexOf(key), false),
    // A link to #install-windows means the Windows steps, whatever the device guess said.
    follow(win) {
      const fromHash = () => {
        const chosen = panels.findIndex((panel) => `#${panel.id}` === win.location.hash);
        if (chosen !== -1) select(chosen, false);
      };
      win.addEventListener("hashchange", fromHash);
      fromHash();
    },
  };
}

/** The download panel: the guess, the chips that overrule it, and the order the files are offered. */
function wirePanel(doc, platform, tabs) {
  const guess = doc.getElementById("download-guess");
  const title = doc.getElementById("download-title");
  const note = doc.getElementById("download-note");
  const list = doc.getElementById("download-list");
  const phone = doc.getElementById("download-phone");
  const chips = new Map([
    ["android", doc.getElementById("chip-android")],
    ["windows", doc.getElementById("chip-windows")],
    ["other", doc.getElementById("chip-other")],
  ]);
  const items = new Map([
    ["apk", doc.getElementById("download-apk")],
    ["setup", doc.getElementById("download-setup")],
    ["portable", doc.getElementById("download-portable")],
    ["cli", doc.getElementById("download-cli")],
  ]);
  const details = new Map([
    ["apk", doc.getElementById("meta-apk")],
    ["setup", doc.getElementById("meta-setup")],
    ["portable", doc.getElementById("meta-portable")],
    ["cli", doc.getElementById("meta-cli")],
  ]);
  const links = new Map(
    [...items].map(([key, item]) => [key, item.querySelector("a")]),
  );
  const files = new Map([...items.keys()].map((key) => [key, doc.getElementById(`file-${key}`)]));
  const show = (platform) => {
    const view = chooseDownloads(platform);
    title.textContent = view.title;
    note.textContent = view.note;
    // Moved rather than reordered with CSS, so a screen reader and the Tab key meet them in the
    // order the eye does.
    list.append(...view.order.map((key) => items.get(key)));
    items.forEach((item, key) => item.classList.toggle("is-lead", key === view.lead));
    phone.hidden = !view.phone;
    chips.forEach((chip, key) => chip.setAttribute("aria-pressed", String(key === view.view)));
    if (view.tab !== "") tabs.show(view.tab);
    return view;
  };
  chips.forEach((chip, key) =>
    chip.addEventListener("click", () => {
      guess.textContent = show(key).chosen;
    }),
  );
  doc.getElementById("download-chips").hidden = false;
  show(platform);
  guess.textContent = guessLine(platform);
  return {
    annotate(downloads) {
      for (const [key, download] of Object.entries(downloads)) {
        const detail = details.get(key);
        links.get(key).setAttribute("href", download.href);
        files.get(key).textContent = DOWNLOADS[key].asset;
        detail.textContent = download.detail;
        detail.hidden = false;
      }
    },
  };
}

/**
 * Wires the page, then adds each download's version, size and date once GitHub answers. With no
 * document, as when node's test runner imports this file, it does nothing; the tests call it with a
 * stand-in page instead. The promise settles when the release lookup has finished, whatever it found.
 */
export async function start(doc = globalThis.document, win = globalThis) {
  if (doc === undefined) return;
  const { userAgent, userAgentData, maxTouchPoints } = win.navigator;
  wireMenu(doc);
  const tabs = wireTabs(doc);
  const panel = wirePanel(doc, detectPlatform(userAgent, userAgentData, maxTouchPoints), tabs);
  tabs.follow(win);
  panel.annotate(await loadDownloads((url, init) => win.fetch(url, init)));
}

start();
