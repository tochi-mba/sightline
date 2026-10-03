// detectPlatform against what real browsers send. Each row is a user agent as the browser reports
// it, the client hints where the browser has them (Chromium-based ones do; Safari and Firefox do
// not), and the touch points it reports.
import { test } from "node:test";
import assert from "node:assert/strict";

const { detectPlatform } = await import(new URL("../app.js", import.meta.url).href);

const CHROME_ANDROID = { platform: "Android", mobile: false };
const MAC_SAFARI =
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
  "Version/17.6 Safari/605.1.15";
const CHROME_WINDOWS =
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
  "Chrome/129.0.0.0 Safari/537.36";

const BROWSERS = [
  {
    name: "Samsung Internet on an Android phone",
    userAgent:
      "Mozilla/5.0 (Linux; Android 14; SM-S928B) AppleWebKit/537.36 (KHTML, like Gecko) " +
      "SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36",
    uaData: { platform: "Android", mobile: true },
    maxTouchPoints: 5,
    expected: "android",
  },
  {
    name: "Chrome on an Android tablet",
    userAgent:
      "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) " +
      "Chrome/129.0.0.0 Safari/537.36",
    uaData: CHROME_ANDROID,
    maxTouchPoints: 5,
    expected: "android",
  },
  {
    // Chrome asks for desktop pages on a large tablet and then says Linux; only the hint is left.
    name: "Chrome on a large Android tablet asking for desktop pages",
    userAgent:
      "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) " +
      "Chrome/129.0.0.0 Safari/537.36",
    uaData: CHROME_ANDROID,
    maxTouchPoints: 5,
    expected: "android",
  },
  {
    name: "Silk on a Fire tablet",
    userAgent:
      "Mozilla/5.0 (Linux; Android 11; KFTRWI) AppleWebKit/537.36 (KHTML, like Gecko) " +
      "Silk/128.4.1 like Chrome/128.0.6613.187 Safari/537.36",
    uaData: undefined,
    maxTouchPoints: 5,
    expected: "android",
  },
  {
    // Silk's desktop mode drops the word Android altogether; Silk itself is the tell.
    name: "Silk on a Fire tablet in desktop mode",
    userAgent:
      "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) " +
      "Silk/128.4.1 like Chrome/128.0.6613.187 Safari/537.36",
    uaData: undefined,
    maxTouchPoints: 5,
    expected: "android",
  },
  {
    name: "Edge on Windows 11",
    userAgent: `${CHROME_WINDOWS} Edg/129.0.0.0`,
    uaData: { platform: "Windows", mobile: false },
    maxTouchPoints: 0,
    expected: "windows",
  },
  {
    // Chrome freezes the architecture in its user agent, so an ARM laptop says Win64; x64.
    name: "Chrome on Windows on ARM",
    userAgent: CHROME_WINDOWS,
    uaData: { platform: "Windows", mobile: false },
    maxTouchPoints: 10,
    expected: "windows",
  },
  {
    name: "Firefox on Windows",
    userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0",
    uaData: undefined,
    maxTouchPoints: 0,
    expected: "windows",
  },
  {
    name: "Safari on an iPhone",
    userAgent:
      "Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 " +
      "(KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1",
    uaData: undefined,
    maxTouchPoints: 5,
    expected: "ios",
  },
  {
    // Since iPadOS 13 an iPad sends a Mac's user agent; its touch screen is what gives it away.
    name: "Safari on an iPad, reporting itself as a Mac",
    userAgent: MAC_SAFARI,
    uaData: undefined,
    maxTouchPoints: 5,
    expected: "ios",
  },
  {
    name: "Safari on a Mac",
    userAgent: MAC_SAFARI,
    uaData: undefined,
    maxTouchPoints: 0,
    expected: "mac",
  },
  {
    name: "Chrome on a Mac",
    userAgent:
      "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) " +
      "Chrome/129.0.0.0 Safari/537.36",
    uaData: { platform: "macOS", mobile: false },
    maxTouchPoints: 0,
    expected: "mac",
  },
  {
    name: "Firefox on Linux",
    userAgent: "Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0",
    uaData: undefined,
    maxTouchPoints: 0,
    expected: "linux",
  },
  {
    name: "Chrome on a Chromebook",
    userAgent:
      "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) " +
      "Chrome/129.0.0.0 Safari/537.36",
    uaData: { platform: "Chrome OS", mobile: false },
    maxTouchPoints: 10,
    expected: "chromeos",
  },
  {
    name: "a browser that says nothing about itself",
    userAgent: "",
    uaData: undefined,
    maxTouchPoints: 0,
    expected: "unknown",
  },
];

for (const { name, userAgent, uaData, maxTouchPoints, expected } of BROWSERS) {
  test(`${name} is ${expected}`, () => {
    assert.equal(detectPlatform(userAgent, uaData, maxTouchPoints), expected);
  });
}
