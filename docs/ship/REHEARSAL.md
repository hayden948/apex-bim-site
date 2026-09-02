# CVE dry run — run-of-show and rehearsal record (round 5)

What was actually rehearsed, what is scripted-but-unrehearsed, and the minute-by-minute plan.
Honesty first: **the Revit half of this flow has never been executed by anyone** (see
WALKTHROUGH.md — that 25-minute checklist is the rehearsal for it), and **no Meta EMT 11990
drawing has ever entered the pipeline**, so the timings below come from fixture drawings.

## Measured (live, deployed api v29, 2026-08-19 — LEDGER R5 C3)

| stage | operator action | measured |
|---|---|---|
| health preflight | `GET /v1/health` | 200 `ok:true` in <1 s |
| approve → AFIS → QA → queue | one Approve click (`?chain=1`) | **766 ms**, QA 0.94, job queued |
| extraction (PDF → reviewable values) | upload in console | NOT re-timed this round (needs a real submittal + a paid model call). Asynchronous — the operator is not blocked; alert arrives via console (Telegram once the operator sets TELEGRAM_BOT_TOKEN). Corrupt-input failure measured at 1.4 s in round 3. |
| Revit build (queued job → .rfa) | automatic (worker) or Batch Build | NEVER MEASURED — no Revit here; walkthrough produces the first real numbers |

## Preflight (day before + hour before)

1. Demo machine: pinned Revit version confirmed against CVE's project (**register #12 — CVE's
   pinned version is still unknown; get it in writing**), family template path set, plugin
   v0.5.0 installed via install.ps1, license valid (About shows licensee + expiry).
2. `GET /v1/health` returns `ok:true`; console signs in; session refreshed within the hour
   (sessions expire ~1 h — re-auth immediately before the meeting, register #14).
3. Worker machine named, plugged in, sleep disabled, Auto Process ON, poll interval checked;
   one throwaway family built end-to-end that morning on a NON-customer fixture.
4. Offline fallback staged: a folder of pre-approved fixture specs — **Batch Build needs no
   cloud at all**; if the network or the API dies mid-demo, the Revit half still demos.
5. TELEGRAM_BOT_TOKEN set if phone alerts are part of the show (health reports
   `telegram_alerts: false` as of this rehearsal).

## Run of show (target 20 min)

1. (2 min) Open with the BUILD_REPORT.md from the morning's test batch — the artifact CVE
   keeps — not with a live upload.
2. (5 min) Upload ONE pre-vetted drawing (run through the pipeline privately beforehand;
   never debut a drawing live). Narrate the extraction confidence view while it processes.
3. (3 min) Review Submittal: show a low-confidence field, correct it, Save and Build.
4. (5 min) Batch Build the folder; progress window; read the report together.
5. (5 min) Questions + the known-limitations page (set expectations explicitly).

## When it fails live (scripted responses, not improvisation)

- Extraction fails or comes back low-confidence → "this is exactly what review is for" →
  Review Submittal, correct, build. The failure IS the override demo.
- Extraction hangs / network dies → offline fallback folder → Batch Build, continue.
- A drawing fails in the batch → read its BUILD_REPORT entry aloud — the message says what to
  do; do it.
- Revit crashes → restart; per-run logs and the report survive; families already built are on
  disk. Resume at the report.

## Not rehearsed, stated plainly

The brief asked for the rehearsal "on the packaged build"; without Revit, only the CLOUD half
of the packaged system (the deployed api — the same build CVE would hit) could be timed. Still
outstanding: upload→extraction on a REAL CVE drawing (none exist in the pipeline; also blocked
by the data-handling/NDA gate, register #11); every Revit-side step (walkthrough pending); the
installer's WINDOWS reality (its full logic has been executed under PowerShell 7.4 on Linux —
LEDGER R5 C5 — but Windows PowerShell 5.1, Unblock-File, ACLs, and Revit loading the DLLs have
not run anywhere); Telegram alerts (token unset). The first true end-to-end rehearsal =
walkthrough + one real drawing through the gate — schedule it BEFORE any customer-facing date.
