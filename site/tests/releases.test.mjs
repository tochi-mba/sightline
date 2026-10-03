// Resolving the latest GitHub release without ever inventing an asset URL.
import { test } from "node:test";
import assert from "node:assert/strict";

const {
  DOWNLOADS,
  RELEASE_API,
  describeDownload,
  downloadUrl,
  fetchRelease,
  formatDate,
  formatSize,
  loadDownloads,
  resolveDownloads,
  versionOf,
} = await import(new URL("../app.js", import.meta.url).href);
const { fakeFetch, fixture } = await import(new URL("./support/network.mjs", import.meta.url).href);

const VERSION = "0.1.0-rolling.42.gabc1234";

async function latestRelease() {
  const windows = await fixture("latest-windows");
  const android = await fixture("latest-android");
  windows.tag_name = "v0.1.0-preview.42";
  windows.assets.push(...android.assets);
  return windows;
}

test("the version comes from a versioned release asset and is never guessed", async () => {
  assert.equal(versionOf((await latestRelease()).assets), VERSION);
  assert.equal(versionOf([{ name: "Sightline.apk" }]), "");
});

test("sizes and dates are formatted for people", () => {
  assert.equal(formatSize(100), "1 KB");
  assert.equal(formatSize(5767168), "5.5 MB");
  assert.equal(formatSize(10485760), "10 MB");
  assert.equal(formatDate("2026-10-01T18:04:09Z"), "1 Oct 2026");
  assert.equal(formatDate("2027-01-01T00:30:00+01:00"), "31 Dec 2026");
});

test("missing or invalid size and date values say nothing", () => {
  for (const bytes of [undefined, null, 0, -1, Number.NaN, Infinity, "1024"]) {
    assert.equal(formatSize(bytes), "");
  }
  for (const iso of [undefined, null, "", "not a date", 1759341849000]) {
    assert.equal(formatDate(iso), "");
  }
});

test("an exact asset is described from release data", async () => {
  const release = await latestRelease();
  assert.equal(
    describeDownload(release, DOWNLOADS.setup),
    `Version ${VERSION}, 94 MB, 2 Oct 2026`,
  );
  assert.equal(describeDownload(release, { asset: "missing.exe" }), "");
  assert.equal(describeDownload(null, DOWNLOADS.setup), "");
});

test("only the exact canonical asset name can become a direct link", async () => {
  const release = await latestRelease();
  assert.match(downloadUrl(release, DOWNLOADS.apk), /\/Sightline\.apk$/);
  assert.equal(downloadUrl(release, { asset: "Sightline-0.1.0.apk" }), "");
  assert.equal(downloadUrl(null, DOWNLOADS.apk), "");
});

test("a matching name with an off-repository URL is rejected", async () => {
  const release = await latestRelease();
  const asset = release.assets.find(({ name }) => name === DOWNLOADS.apk.asset);
  asset.browser_download_url = "https://example.invalid/Sightline.apk";
  assert.equal(downloadUrl(release, DOWNLOADS.apk), "");
});

test("every exact asset on one latest release is resolved", async () => {
  const resolved = resolveDownloads(await latestRelease());
  assert.deepEqual(Object.keys(resolved), Object.keys(DOWNLOADS));
  for (const [key, value] of Object.entries(resolved)) {
    assert.ok(value.href.endsWith(`/${DOWNLOADS[key].asset}`));
    assert.match(value.detail, /2 Oct 2026|1 Oct 2026/);
  }
});

test("missing and malformed assets are omitted", async () => {
  const partial = await latestRelease();
  partial.assets = partial.assets.filter(({ name }) => name === DOWNLOADS.setup.asset);
  assert.deepEqual(Object.keys(resolveDownloads(partial)), ["setup"]);
  assert.deepEqual(resolveDownloads(null), {});
  assert.deepEqual(resolveDownloads({ assets: "none" }), {});
});

test("the public latest-release API is requested once without credentials", async () => {
  const release = await latestRelease();
  const network = fakeFetch({ latest: release });
  assert.deepEqual(await fetchRelease(network.fetch), release);
  assert.deepEqual(network.requests, [{
    url: RELEASE_API,
    init: { headers: { Accept: "application/vnd.github+json" }, credentials: "omit" },
  }]);
});

test("refused, failed and unreadable lookups are no release", async () => {
  for (const answer of [403, 404, new TypeError("Failed to fetch"), "not JSON"]) {
    assert.equal(await fetchRelease(fakeFetch({ latest: answer }).fetch), null);
  }
});

test("a valid latest release is loaded and malformed data changes no links", async () => {
  const release = await latestRelease();
  assert.deepEqual(await loadDownloads(fakeFetch({ latest: release }).fetch), resolveDownloads(release));
  for (const odd of [{}, { assets: "none" }, { assets: [null] }]) {
    assert.deepEqual(await loadDownloads(fakeFetch({ latest: odd }).fetch), {});
  }
  assert.deepEqual(await loadDownloads(fakeFetch({}).fetch), {});
});
