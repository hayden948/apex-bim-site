# Ship readiness — Cache Valley Electric "Meta EMT 11990" dry run

Round 1 gap register. Every "current state" claim is backed by pasted output in
`docs/ship/LEDGER.md` (Round 1, cycles 1–3). Rank = "would this embarrass us in
front of CVE", 1 = worst. Environment caveats: `.claude/LOOP.md` § Environment note.

Two pipelines exist and they are NOT connected to each other:

- **P-A (live, Aug 2026)**: PDF → Supabase `api` (Claude extraction, pred-shaped JSON) → human/auto
  approve → AFIS 1.0 → `generate_rfa` job → Revit plugin worker builds + uploads `.rfa`. Verified
  end-to-end against the deployed system (health 200, Telegram round-trips, prior-round E2E).
- **P-B (stubbed, Jun 2026)**: shop2revit parser CLI → FamilySpec `.spec.json`; FastAPI service whose
  parser adapter is a hardcoded stub; poller whose Revit hook raises. Nothing reads FamilySpec.

| # | Requirement | Current state | Evidence (LEDGER ref) | Gap | Risk if shipped as-is | Round that fixes it |
|---|---|---|---|---|---|---|
| 1 | Customer inputs on hand for the dry run (Meta EMT 11990 drawings: count, classes, parse status) | **0 drawings in VCS or container.** Only inputs anywhere: 1 example `panelboard.pred.json`, 1 embedded ZGSL-H text fixture, 1 NQ430 submittal PDF in session scratch | Cycle 2 § customer-input inventory | The thing being dry-run has never touched the pipeline; equipment classes unknown (EMT/conduit-related classes have no extractor and no AFIS modeling) | Dry run is the first-ever contact with real inputs; failure mode discovered live in front of CVE | Round 2 (obtain + first-pass parse); cannot be closed from this container alone — operator must supply drawings |
| 2 | Headless/scriptable Revit harness for verification | **None.** No journal runner, no batch runner, no Design Automation; `poller.py run_revit_addin()` raises; this container has no Revit at all | Cycle 2 § HARNESS CHECK | Rounds 3–5 "build and execute" degrades to code-reading for every Revit-API path | We claim .rfa success rates we have never measured; first real batch run happens at the customer | Round 3, FIRST item (journal-playback runner on operator machine; DA later) |
| 3 | Schema integrity across the toolchain | **Forked.** FamilySpec (mm, dict params) vs .pred.json (in, list params) vs AFIS 1.0 — no bridge FamilySpec→anything; jsonschema file exists but nothing validates against it; .pred.json and AFIS have no schema files at all | Cycle 2 § live-path trace, § schema census | Parser output is unconsumable by the plugin; contract holes covered only by C# coercion defaults | Parser work (P-B) silently produces artifacts the shipping path can't use; "we support transformers" is untrue in P-A | Round 2 (decide canonical schema + write bridge or retire P-B); FamilySpec changes need an authorizing round per LOOP.md |
| 4 | Parser accuracy on unseen drawings | 1 extractor (transformer_padmount), tested only against its own embedded text fixture. Forced onto an out-of-class real PDF: **exit 0 at confidence 0.50 with bbox 0×0×0 mm** — the gate passes an unbuildable spec. Auto-detect refuses unknown types (good) | Cycle 3 § parser-on-real-PDF | Confidence score saturates at the pass threshold on garbage; zero-dimension specs count as success | A wrong-but-confident family in front of CVE — the worst embarrassment class | Round 2 (tighten gate: zero-bbox ⇒ fail; per-field confidence), Round 4 (accuracy on real CVE set) |
| 5 | Family build success rate | P-A path: builds succeeded in prior-round manual E2E (1 panelboard, 1 seed family; M1 19-transformer batch exists only as operator anecdote — inputs not archived). No measured rate on any batch | Cycle 2 § FIXTURE CHECK; prior-round E2E in session records | No regression set, no rate, no failure catalog | "It worked on the demo family" is the entire evidence base | Round 2 (recover fixtures) + Round 3 (batch harness measures real rate) |
| 6 | QA validation coverage | Two QA engines exist (plugin local, API S/P/G/E/Z/L on AFIS), gate generate-rfa, verified live in prior rounds. But QA runs on AFIS only — nothing QAs a raw .pred.json/FamilySpec pre-build, and QA rule outcomes have no automated tests | Cycle 2 § live-path trace, § test coverage | Garbage-in caught only by predProblem() shape check; rule engine untested against synthetic failure cases | QA passes a spec whose geometry silently defaulted; customer sees a 24×24×24in "transformer" | Round 4 |
| 7 | Failure behavior in front of a customer | Plugin: TaskDialog with real error text + `%LOCALAPPDATA%\Apex\logs`; API: structured error envelope; auto-pipeline: Telegram alert + row stays for review — all verified live. But: Telegram outbound alerts currently OFF (no bot token on project), and a failed extraction burns a paid Claude call with a raw error string surfaced to the console | This session's task-23 round (health `telegram_alerts: false` pasted); Cycle 2 § trace | Alert channel dark until operator sets TELEGRAM_BOT_TOKEN; no rehearsed "it failed, here's what we do" script | Failure mid-demo with no notification and an unpolished error is plausible | Round 5 (dry-run rehearsal script); token = 1-line operator action today |
| 8 | Install / licensing | Package = ONE `.addin` manifest with placeholder vendor URL; manual copy; no installer, no signing. **Ed25519 license check: does not exist in any reachable code** (zero grep hits) | Cycle 2 § PACKAGING SMOKE TEST | Round brief assumes a license check that isn't in VCS; if it exists it lives only on the operator's machine — unverifiable and unshippable from here | Customer installs by hand-copying DLLs; nothing prevents redistribution; "licensed product" claim is false | Round 5; scope decision needed (ship without licensing vs build it — changes round scope materially) |
| 9 | What the customer physically receives + how they open it | Receivable today: `.rfa` via console download (works, verified) or direct from plugin build; plugin itself via manual .addin + DLL copy. No install doc for a customer-facing dry run; briefing docs (CLAUDE.md/SPRINT.md/AGENTS.md) absent from VCS | Cycle 2 § PACKAGING, § environment findings | No packaged deliverable, no one-page install/run sheet, no versioned release artifact | CVE asks "how do we open this?" and the answer is a live improvisation | Round 5 |
| 10 | Round-scoping documents in version control | CLAUDE.md / SPRINT.md / AGENTS.md not in either repo | Cycle 1 environment findings | Rounds 2–5 may be scoped against documents this loop cannot read | Loop optimizes for the wrong target | Operator action: push/paste them; log receipt in LEDGER |

## Pre-mortem (STEP 3) — "the dry run failed badly." 8 most likely causes

1. **The drawings were classes we've never parsed** (EMT/conduit/switchboard, not transformers).
   Cheapest check: get the drawing list from CVE TODAY and diff against the one extractor +
   AFIS box-primitive limits. (Register #1.)
2. **Parser confidently emitted garbage** (0.50-on-garbage behavior, zero bbox passes the gate).
   Cheapest check: run every CVE PDF through the CLI the day received; reject anything with
   zero-dims or warnings > N before it gets near Revit. (Register #4.)
3. **First real batch build hit Revit-API failures we'd never seen** because no batch has ever run.
   Cheapest check: build the journal harness (round 3) and run the existing example + recovered
   fixtures through it before touching CVE inputs. (Register #2, #5.)
4. **Schema mismatch at demo time** — someone runs the P-B parser and the plugin can't read its
   output. Cheapest check: decide the canonical path NOW (P-A) and mark P-B stub outputs
   unmistakably (the `_stub` flag exists; extend to CLI output or retire the service). (Register #3.)
5. **Environment drift on the demo machine** — wrong Revit version (net48 vs net8 targets),
   missing family template path (`ResolveTemplate` returns null → hard fail dialog).
   Cheapest check: 10-minute preflight script on the actual demo machine: Revit version,
   FamilyTemplatePath set, template file present, plugin loads. (Register #9.)
6. **The live cloud dependency failed mid-demo** (Supabase egress, Anthropic outage, rate limit
   20/h exhausted by rehearsal runs that morning). Cheapest check: rehearse on a separate
   project/token; verify `/v1/health` in the preflight; know the offline story (local
   BuildFromPredJson from a pre-approved .pred.json needs NO cloud). (Register #7.)
7. **Failure had no graceful path** — error dialog in front of the customer, no alert, no script.
   Cheapest check: set TELEGRAM_BOT_TOKEN (1 line), write the 5-line "when it fails" script,
   pre-stage a known-good fallback family. (Register #7.)
8. **Install friction burned the meeting** — hand-copied DLLs, SmartScreen/unblock prompts on
   unsigned binaries, wrong .addin path. Cheapest check: do one cold install on a machine that
   has never seen the plugin, timed, following only the written sheet. (Register #8, #9.)

All eight map to existing register rows; no new rows required — but #5's preflight script and
#6's offline fallback are now explicit round-5 deliverables.

## Ship-blocking five (round-1 verdict)

1. Zero customer inputs in reach (#1) — everything else is rehearsal until real drawings exist.
2. No scriptable Revit harness (#2) — without it every success-rate claim is untested.
3. Schema fork with no bridge (#3) — the parser the brief centers on feeds nothing.
4. Confidence gate passes garbage (#4) — the exact embarrassment scenario.
5. No packaged deliverable / install story, licensing nonexistent (#8+#9).

**Do rounds 2–5 as scoped close them?** Partially. Rounds 2 (fixtures+schema), 3 (harness+batch),
4 (accuracy+QA), 5 (packaging+rehearsal) map cleanly onto #2–#5 IF the operator supplies the CVE
drawings and the Sprint 001 fixtures — neither exists in any repo this loop can reach, and no
round can conjure them. #1 is therefore NOT closable by the loop as scoped: it needs an operator
action this week. Flag also that the Ed25519 licensing the brief assumes does not exist in VCS;
if round 5 is expected to "verify" it, round 5 as scoped will fail — it must either build it or
ship without it, and that decision belongs to the operator, not the loop.
