// Which downloads lead for each platform, what the guess says, and how the install tabs move.
import { test } from "node:test";
import assert from "node:assert/strict";

const { DOWNLOADS, chooseDownloads, guessLine, nextTab } = await import(
  new URL("../app.js", import.meta.url).href
);

test("an Android visitor gets the APK first, with the Windows downloads beneath it", () => {
  const view = chooseDownloads("android");
  assert.equal(view.view, "android");
  assert.equal(view.title, "Sightline for Android");
  assert.equal(view.lead, "apk");
  assert.deepEqual(view.order, ["apk", "setup", "portable", "cli"]);
  assert.equal(view.phone, false);
  assert.equal(view.tab, "android");
});

test("a Windows visitor gets the installer, then the portable app and the command line, then the phone", () => {
  const view = chooseDownloads("windows");
  assert.equal(view.view, "windows");
  assert.equal(view.title, "Sightline for Windows");
  assert.equal(view.lead, "setup");
  assert.deepEqual(view.order, ["setup", "portable", "cli", "apk"]);
  assert.equal(view.phone, true, "the APK is offered as Get it on your phone");
  assert.equal(view.tab, "windows");
});

for (const platform of ["ios", "mac", "linux", "chromeos", "unknown", "other"]) {
  test(`a visitor on ${platform} is told where Sightline runs and offered every download`, () => {
    const view = chooseDownloads(platform);
    assert.equal(view.view, "other");
    assert.equal(view.title, "Sightline preview packages for Android and Windows");
    assert.equal(view.lead, "", "no download leads on a device that runs neither");
    assert.deepEqual(view.order, ["apk", "setup", "portable", "cli"]);
    assert.equal(view.phone, false);
    assert.equal(view.tab, "", "the install tabs are left as they are");
  });
}

test("a name that only exists on Object.prototype is not mistaken for a platform", () => {
  assert.equal(chooseDownloads("constructor").view, "other");
  assert.equal(guessLine("constructor"), "Pick the device you are installing on.");
});

test("no view hides a download: each one offers every file exactly once", () => {
  for (const platform of ["android", "windows", "other"]) {
    const { order } = chooseDownloads(platform);
    assert.deepEqual([...order].sort(), Object.keys(DOWNLOADS).sort(), platform);
  }
});

test("every view says what pressing its chip did", () => {
  assert.equal(chooseDownloads("android").chosen, "Showing Sightline for Android.");
  assert.equal(chooseDownloads("windows").chosen, "Showing Sightline for Windows.");
  assert.equal(chooseDownloads("other").chosen, "Showing every download.");
});

test("the guess is said as a guess, naming the device", () => {
  const devices = {
    android: "an Android phone or tablet",
    windows: "a Windows PC",
    ios: "an iPhone or iPad",
    mac: "a Mac",
    linux: "a Linux computer",
    chromeos: "a Chromebook",
  };
  for (const [platform, device] of Object.entries(devices)) {
    assert.equal(guessLine(platform), `This looks like ${device}. Not right? Pick a device.`);
  }
});

test("with no guess, the visitor is simply asked", () => {
  assert.equal(guessLine("unknown"), "Pick the device you are installing on.");
});

test("the arrow keys move between the tabs and wrap at either end", () => {
  assert.equal(nextTab("ArrowRight", 0, 2), 1);
  assert.equal(nextTab("ArrowRight", 1, 2), 0);
  assert.equal(nextTab("ArrowLeft", 1, 2), 0);
  assert.equal(nextTab("ArrowLeft", 0, 2), 1);
});

test("Home and End go to the first and last tab", () => {
  assert.equal(nextTab("Home", 1, 2), 0);
  assert.equal(nextTab("End", 0, 2), 1);
});

test("any other key is left to the page", () => {
  assert.equal(nextTab("Enter", 0, 2), -1);
  assert.equal(nextTab("a", 1, 2), -1);
});
