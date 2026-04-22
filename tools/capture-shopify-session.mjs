#!/usr/bin/env node
// One-shot helper: opens a Chromium window on YOUR host, lets you log in to
// Shopify admin, captures the full Playwright storage state (cookies + localStorage
// + sessionStorage for every origin), and POSTs it to the AutoCommerce backend's
// /session/upload endpoint.
//
// Usage:
//   cd tools
//   npm install            # first time only — installs playwright locally
//   node capture-shopify-session.mjs [backend-url] [find-products-url]
//
// Defaults:
//   backend-url        = http://localhost:5110
//   find-products-url  = read from backend /config
//
// The tool exits 0 on success. It exits non-zero if login is never detected
// within the timeout or the upload fails.

import { chromium } from "playwright";

const BACKEND = (process.argv[2] || "http://localhost:5110").replace(/\/+$/, "");
const FORCED_URL = process.argv[3];
const LOGIN_TIMEOUT_MS = 10 * 60 * 1000; // 10 minutes

async function fetchJson(url, init) {
  const resp = await fetch(url, init);
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}: ${await resp.text()}`);
  return resp.json();
}

async function main() {
  console.log(`[capture] backend = ${BACKEND}`);

  // 1. Get the target URL from backend config unless forced.
  let findUrl = FORCED_URL;
  if (!findUrl) {
    console.log("[capture] fetching Find Products URL from backend config…");
    const cfg = await fetchJson(`${BACKEND}/api/shopify/automation/config`);
    findUrl = cfg.findProductsUrl;
    if (!findUrl) {
      console.error("[capture] ✗ Backend has no findProductsUrl configured. Set it in the dashboard first, or pass it as the 2nd arg.");
      process.exit(2);
    }
  }
  console.log(`[capture] target = ${findUrl}`);

  // 2. Launch a visible Chromium and wait for the user to authenticate.
  console.log("[capture] launching Chromium — a window will open, log in normally, DON'T close it.");
  const browser = await chromium.launch({ headless: false });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();

  page.on("framenavigated", f => {
    if (f === page.mainFrame()) console.log(`[capture] → ${f.url()}`);
  });

  await page.goto(findUrl, { waitUntil: "domcontentloaded" }).catch(() => {});

  // 3. Poll until the page is clearly on the target app.
  const deadline = Date.now() + LOGIN_TIMEOUT_MS;
  let onTarget = false;
  const targetPrefix = targetPrefixOf(findUrl);

  while (Date.now() < deadline) {
    await page.waitForTimeout(1500);
    const url = page.url();
    if (url.toLowerCase().startsWith(targetPrefix)) {
      const hasSearch = await page.$("input[type='search'], input[placeholder*='Search' i], [role='searchbox']");
      if (hasSearch) { onTarget = true; break; }
    }
  }

  if (!onTarget) {
    console.error("[capture] ✗ Timed out waiting for you to reach the Find Products page with an authenticated session.");
    await browser.close();
    process.exit(3);
  }

  console.log("[capture] ✓ Detected authenticated session on target page. Capturing storage state…");

  // 4. Capture + upload.
  const state = await context.storageState();
  console.log(`[capture] captured ${state.cookies.length} cookies, ${state.origins.length} origin(s)`);

  const summary = summarise(state);
  console.log(`[capture]   - cookies on admin.shopify.com: ${summary.adminCookies}`);
  console.log(`[capture]   - cookies on .shopify.com:      ${summary.shopifyCookies}`);
  console.log(`[capture]   - has _secure_admin_session_*:  ${summary.hasAdminSession ? "yes ✓" : "NO ✗  (this is the critical one Shopify checks)"}`);

  await browser.close();

  console.log(`[capture] uploading to ${BACKEND}/api/shopify/automation/session/upload …`);
  const result = await fetchJson(`${BACKEND}/api/shopify/automation/session/upload`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ storageState: JSON.stringify(state) }),
  });

  console.log("[capture] backend response:");
  console.log(JSON.stringify(result, null, 2));

  if (result.state === "connected") {
    console.log("[capture] ✓ DONE — session saved. You can now click Run Automation in the dashboard.");
    process.exit(0);
  } else {
    console.error(`[capture] ✗ Backend says ${result.state}. Message: ${result.message}`);
    process.exit(4);
  }
}

function targetPrefixOf(url) {
  try {
    const u = new URL(url);
    const m = u.pathname.match(/^(\/store\/[^/]+\/apps\/[^/]+)\//i);
    return (u.origin + (m ? m[1] : "")).toLowerCase();
  } catch { return url.toLowerCase(); }
}

function summarise(state) {
  let adminCookies = 0, shopifyCookies = 0, hasAdminSession = false;
  for (const c of state.cookies) {
    const d = (c.domain || "").toLowerCase();
    if (d.includes("admin.shopify.com")) adminCookies++;
    if (d === "shopify.com" || d === ".shopify.com" || d.endsWith(".shopify.com")) shopifyCookies++;
    if (/_secure_admin_session_id/.test(c.name)) hasAdminSession = true;
  }
  return { adminCookies, shopifyCookies, hasAdminSession };
}

main().catch(err => { console.error("[capture] ✗", err); process.exit(1); });
