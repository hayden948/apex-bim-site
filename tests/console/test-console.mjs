// End-to-end browser test of app.html against the contract mock (mock-api.mjs
// must be listening on 4600). Locally: CHROMIUM_PATH=/opt/pw-browsers/chromium.
import { chromium } from "playwright-core";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const appPath = "file://" + path.resolve(here, "../../app.html");
const API = "http://127.0.0.1:4600/functions/v1/api";
const APX = "apx_3bad07ea1b0a26a9016d2f653b558218eee0a1b178f308a0";

let failures = 0;
const check = (ok, label) => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${label}`);
  if (!ok) failures++;
};

const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_PATH || undefined,
});
const page = await browser.newPage();
const jsErrors = [];
page.on("pageerror", (e) => jsErrors.push(e.message));
page.on("dialog", (d) => d.accept("Field Ops Project"));

const text = (sel) => page.textContent(sel);
const waitText = (sel, needle) =>
  page.waitForFunction(([s, n]) => document.querySelector(s)?.textContent.includes(n), [sel, needle]);

await page.goto(appPath);
await page.fill("#apiUrl", API);

// --- service-token path ---
await page.fill("#apiToken", "apx_bogus");
await page.click("#btnConnect");
await waitText("#outConnect", "INVALID_TOKEN");
check(true, "bad token rejected");

await page.fill("#apiToken", APX);
await page.click("#btnConnect");
await waitText("#outConnect", "Connected");
check((await text("#outConnect")).includes("1 famil"), "service token connects");

// --- sign-in path ---
await page.fill("#email", "qa@apex.test");
await page.fill("#password", "wrong");
await page.click("#btnSignin");
await waitText("#outConnect", "Sign-in failed");
check(true, "bad password rejected");

await page.fill("#password", "pw123456");
await page.click("#btnSignin");
await waitText("#outConnect", "Connected");
check((await page.inputValue("#apiToken")) === "user-jwt-abc123", "session JWT drives the console");

// --- projects ---
await page.waitForFunction(() => document.querySelectorAll("#projSel option").length >= 2);
await page.click("#btnNewProj");
await waitText("#outUpload", "Field Ops Project");
// The success message shows before loadProjects() finishes repopulating the
// picker — wait on the picker state itself, not the message.
await page.waitForFunction(() => document.querySelector("#projSel")?.value === "2bcc843d-7634-4cbc-8eed-4d317e2f286f");
check(true, "created project auto-selected");

// --- autonomous pipeline toggle ---
await page.waitForFunction(() => !document.querySelector("#autoPipe")?.disabled);
await page.check("#autoPipe");
await waitText("#outUpload", "Auto-build ON");
check(true, "auto-build toggle patches project");
await page.uncheck("#autoPipe");
await waitText("#outUpload", "Auto-build OFF");
check(true, "auto-build toggle off");

// --- resume a pending review from the list ---
await page.waitForSelector('#pendingList button[data-resume]');
await page.click('#pendingList button[data-resume]');
await page.waitForSelector("#extractReview table");
check((await page.inputValue("#rvName")).includes("Panelboard"), "resume loads pending review");

// --- pipeline: upload -> extract -> approve -> validate -> queue -> download ---
const pdf = path.join(here, "fixture.pdf");
fs.writeFileSync(pdf, "%PDF-1.4 fixture");
await page.setInputFiles("#pdfFile", pdf);
await page.click("#btnUpload");
await waitText("#outUpload", "Uploaded");
check(true, "upload");

await page.click("#btnExtract");
// The resume step already rendered a review table, so waiting on the selector
// would race the extract's re-render; wait for the extract response instead
// (it contains "result", the resume echo contains "resumed").
await waitText("#outExtract", '"result"');
check((await page.$$eval("#extractReview tbody tr", (r) => r.length)) === 1, "extraction review table");
check((await text("#outExtract")).includes("cost_usd"), "extraction reports model cost");

// human-in-the-loop: correct the name and a dimension before approving
await page.fill("#rvName", "Corrected Panelboard XL");
await page.fill('[data-dim="width"]', "24");
await page.click("#btnApprove");
await waitText("#outApprove", "Corrected Panelboard XL");
check(true, "approve carries corrections");

await page.click('#famList button[data-act="validate"]');
await waitText("#outFam", "PASSED");
check(true, "validate");

await page.click('#famList button[data-act="queue"]');
await waitText("#outFam", "queued");
check(true, "queue rfa");

const dl = page.waitForEvent("download");
await page.click('#famList button[data-act="download"]');
check(!!(await dl), "download rfa");

// --- token minting ---
await page.click("#btnMint");
await waitText("#outConnect", "apx_minted_secret_42");
check((await text("#outConnect")).includes("APEX_API_TOKEN=apx_minted_secret_42"), "mint shows one-time token");

// --- signup (confirmation path) ---
await page.fill("#email", "new@apex.test");
await page.click("#btnSignup");
await waitText("#outConnect", "confirm your email");
check(true, "signup confirmation message");

check(jsErrors.length === 0, "no JS errors" + (jsErrors.length ? ": " + jsErrors.join("; ") : ""));
await browser.close();
fs.rmSync(pdf, { force: true });
console.log(failures === 0 ? "\nALL CONSOLE TESTS PASSED" : `\n${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
