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
| 2 | Headless/scriptable Revit harness for verification | **Code + kit BUILT (Round 3); execution HUMAN-VERIFY-REQUIRED.** `BatchBuildCommand` (folder batch, per-drawing isolation, quarantine, jsonl, matrix, APEX_BATCH_DIR scriptable path) compiled both targets, Revit-free half fully tested; operator kit in `revit-plugin/deploy/batch/` (checklist with holdout protocol, PowerShell launcher, journal template with honest one-time arming). Hardened after a REFUTED adversarial pass (collision-proof output names, exception-safe bookkeeping, fresh-run semantics, passable determinism diff). This container still cannot execute Revit — no build has ever run | R3 Cycles 2–5 (LEDGER); RUN_MATRIX.md | The harness's own in-Revit behavior is unexecuted; journal replay unarmed until the operator's one-time recording | .rfa success rate remains unmeasured until the operator runs the kit | Operator: run OPERATOR_CHECKLIST on the Revit machine; then rounds 4–5 consume its real matrices |
| 3 | Schema integrity across the toolchain | **CLOSED (Round 2).** Canonical FamilySpec v1 = the pred shape: schema of record `schemas/familyspec/familyspec.v1.schema.json` + DECISION.md; required `schema_version`; validated at every boundary (model output before write, corrections, stored results, plugin input pre-build) with named-field errors; C#↔schema↔EXTRACTION_SCHEMA tied by contract test + parity checker, both proven able to fail; parser-repo FamilySpec quarantined by decision (zero consumers in shipping repo, grep exit 1; its only sink raises by design). Round-trip net found+fixed a real defect (duplicate Apex_AfisId, api v27) on day one | R2 Cycles 1–3 (LEDGER): suite `ALL TESTS PASSED`; contract-test break/revert; live 400 INVALID_CORRECTION naming geometry.width.value; 6/6 `expected_matches_live = true` | Remaining: codegen not viable in-round (contract test covers); physical quarantine move in parser repo is operator follow-up (read-only access); legacy-v0 accommodation documented in DECISION.md | Drift now fails tests loudly instead of corrupting silently | Done this round; operator: move parser-repo files to legacy/, push SPRINT.md to confirm the Sprint 002 policy reading |
| 4 | Parser accuracy on unseen drawings | 1 extractor (transformer_padmount), tested only against its own embedded text fixture. Forced onto an out-of-class real PDF: **exit 0 at confidence 0.50 with bbox 0×0×0 mm** — the gate passes an unbuildable spec. Auto-detect refuses unknown types (good). R2 update: the CANONICAL path now hard-rejects zero/negative dimensions at every boundary (named-field error), so a zero-bbox spec can no longer reach a build; the P-B parser's own gate is unchanged (repo quarantined, read-only) | R1 Cycle 3 § parser-on-real-PDF; R2 Cycles 1–2 boundary proofs | P-B CLI gate itself unfixed; live-path accuracy on real CVE drawings still unmeasured | Zero-dim garbage is now stopped at the door; wrong-but-plausible values still pass | Round 4 (accuracy on real CVE set); P-B gate only if P-B is revived |
| 5 | Family build success rate | Partially improved (Round 2): a 6-fixture golden regression net now exists (incl. the human-approved 18-param NQ430 and a ZGSL transformer), round-tripped through the DEPLOYED pipeline with content-verified expected AFIS + QA scores and an operator-runnable re-run procedure (`schemas/familyspec/tools/roundtrip.py`). The 19 Sprint 001 M1 inputs remain absent from VCS — cannot be reconstructed from this container (no drawings, no outputs in reach); still an operator action. Revit-side build rate still unmeasured (no harness, row #2) | R2 Cycle 3 (LEDGER): 6× 201 approvals, 6/6 jsonb equality, QA scores pasted | The net covers pred→AFIS→QA, not AFIS→.rfa; Sprint 001 inputs still missing | Regression net has teeth (caught a real defect) but stops at the Revit boundary | Operator: supply Sprint 001 inputs; Round 3: harness measures .rfa rate |
| 6 | QA validation coverage | Two QA engines exist (plugin local, API S/P/G/E/Z/L on AFIS), gate generate-rfa, verified live in prior rounds. R2/R3 UPDATE: raw specs ARE now QA'd pre-build at every boundary (FamilySpec v1 validator, named-field errors), and three QA rules (P-4/S-2/G-1) are proven to FAIL on crafted bad AFIS (LEDGER R3 C4). Remaining: the other rules (S-1, P-1..P-3, G-2, E/Z/L) have no synthetic failure-case tests | R1 Cycle 2; R2 C1–C2; R3 C4 | Rule engine only spot-tested against synthetic failures | A regression in an untested rule passes bad AFIS silently | Partially closed (R2/R3); remaining rule tests: round 5+ |
| 7 | Failure behavior in front of a customer | Plugin: TaskDialog with real error text + `%LOCALAPPDATA%\Apex\logs`; API: structured error envelope; auto-pipeline: Telegram alert + row stays for review — all verified live. R4 UPDATE (code, in-Revit rendering HUMAN-VERIFY-REQUIRED): dialogs/ribbon rewritten in customer language with confirmable destructive actions; per-item batch progress with honest ✓/⚠/✗ status; `BUILD_REPORT.md` next to the .rfa files states per failure WHAT to do (machine problems blamed on the machine, not the drawing; tested to contain no taxonomy jargon/stack traces); one log file per run so support = "send that one file"; review/override path (Review Submittal) turns a parser miss into a self-service fix. Still true: Telegram outbound alerts OFF (no bot token), failed extraction burns a paid call | Task-23 round; R4 C1–2 (LEDGER, commit 1279d84, suite paste) | Alert channel dark until operator sets TELEGRAM_BOT_TOKEN; no rehearsed "it failed, here's what we do" script; R4 surfaces unexecuted in a real Revit (WALKTHROUGH.md is the verification vehicle) | Failure mid-demo with no notification is still possible; the on-screen story is now designed, not improvised | Round 5 (rehearsal script); token = 1-line operator action; Hayden runs `revit-plugin/deploy/WALKTHROUGH.md` |
| 8 | Install / licensing | R5 UPDATE: **Ed25519 offline licensing EXISTS and is proven in all four states** (valid/expired/tampered/missing, 17 assertions incl. in-suite sign→verify round trip and cross-key rejection; gate wired into all build commands; key custody in service-role app_config; CVE trial + dev licenses issued and verified against the embedded key). **Installer BUILT** (integrity-gated install.ps1, clean uninstall, package assembler; v0.5.0-rc1 package hashed) — **but NEVER EXECUTED: no Windows/VM exists in the authoring container — NAMED SHIP BLOCKER until walkthrough step 1 runs.** Remaining: DLLs not Authenticode-signed (SmartScreen on managed machines; Unblock-File covers per-user); tag v0.5.0-rc1 local-only (remote 403s tag pushes from the session — operator pushes it) | R5 C1–C3 (LEDGER): suite paste, package hashes, negative controls | Untested installer; no Authenticode | First install on a CVE machine is the first execution ever unless the walkthrough runs first | Operator: run WALKTHROUGH step 1 on clean Revit 2025; push the tag; buy a code-signing cert (post-trial) |
| 9 | What the customer physically receives + how they open it | Receivable today: `.rfa` via console download (works, verified) or direct from plugin build; plugin itself via manual .addin + DLL copy. No install doc for a customer-facing dry run; briefing docs (CLAUDE.md/SPRINT.md/AGENTS.md) absent from VCS. R4 UPDATE: the family handoff now includes `BUILD_REPORT.md` (what was built, from which drawing, with which values, which checks passed) — the artifact that makes the trial defensible inside CVE; `deploy/WALKTHROUGH.md` doubles as a first-run sheet for the add-in itself | Cycle 2 § PACKAGING; R4 C1–2 | No packaged/signed deliverable, no versioned release artifact | CVE asks "how do we open this?" and the answer is a live improvisation | Round 5 |
| 10 | Round-scoping documents in version control | CLAUDE.md / SPRINT.md / AGENTS.md not in either repo | Cycle 1 environment findings | Rounds 2–5 may be scoped against documents this loop cannot read | Loop optimizes for the wrong target | Operator action: push/paste them; log receipt in LEDGER |
| 11 | Customer data handling, confidentiality, consent (added by V2 adversarial review) | Pipeline stores CVE drawings in Supabase and sends full PDFs to Anthropic (`api/index.ts` extraction call). No privacy policy/terms anywhere (site footer links are `href="#"`), no data-deletion endpoint (uploads have `deleted_at` but nothing sets it), no retention statement, no disclosure that drawings go to an LLM vendor | Cycle 4 § V2 findings (reviewer output pasted) | A Meta-project NDA may prohibit third-party disclosure — the demo upload itself could be a breach; "can you delete our drawings?" has answer "no" | Legal exposure + instant trust loss with CVE's BIM manager | NOT in rounds 2–5 as scoped. Needs operator/legal decision this week + a deletion/retention work item; flagging as ship-blocking |
| 12 | .rfa Revit-version compatibility with the customer's project (added by V2) | Built family's version = whatever Revit the worker runs (`ProcessQueueCommand.cs` records `app.VersionNumber`; nothing selects/checks a target). An .rfa opens only in its build version or newer; Meta-scale BIM execution plans pin the year | Cycle 4 § V2 findings | Worker on 2026 + CVE project on 2024 ⇒ family fails to load at handoff, the last step of the demo | Unrecoverable failure at the moment of delivery | Round 3 (harness pins build version; needs CVE's pinned version from operator) |
| 13 | Demo-day worker availability + pipeline latency (added by V2) | R5 UPDATE: REHEARSAL.md now scripts the preflight (worker named + sleep disabled + morning smoke build), the 20-min run of show, scripted failure responses, and the offline fallback (Batch Build needs no cloud). Cloud-half timings MEASURED live: health <1 s, approve→AFIS→QA→queue 766 ms. Scripted ≠ rehearsed: no human has run the script | R5 C3 (LEDGER, pasted outputs) | The script has not been executed once end-to-end | Demo stalls remain possible until one full rehearsal happens | Operator: run REHEARSAL.md once (part of the GO gate) |
| 14 | Customer account/credential provisioning (added by V2) | Console needs a Supabase account (signup may require email confirmation); sessions expire ~1 h (console's own 401 hint); plugin needs a minted `apx_` token; OAuth PKCE endpoints in the plugin point at `auth.apex.example` (nonexistent) | Cycle 4 § V2 findings | No pre-created CVE user/project/token; session can 401 mid-meeting | Meeting opens with an email-confirmation loop; console dies at minute 40 | Round 5 preflight (pre-provision + re-auth script); OAuth stub flagged for scope decision |
| 15 | Input-document constraints: size, pages, multi-unit packages (added by V2) | Upload gate is 30 MB but the model call fails above ~24 MB after base64 inflation; PDF page caps apply; extraction prompt assumes ONE unit per document — real submittal packages bundle many units; no splitting stage exists | Cycle 4 § V2 findings | CVE's combined package 413s, errors at extraction, or yields one bbox for a 40-unit package | Product's own gate accepts input the pipeline then chokes on, live | NOT closed by any round (the 30 MB gate is still live in api/index.ts; documented to the customer in KNOWN_LIMITATIONS.md); gate alignment is an open engineering item; multi-unit splitting is new scope — operator decision |
| 16 | Customer-visible web surface consistency (added by V2, minor) | Marketing site footer links dead (`href="#"`); claims pages CVE may browse during/after the meeting are not audited against what the dry run shows | Cycle 4 § V2 findings | Site promises what the demo can't show; dead links read as vaporware | Credibility discount on everything demonstrated | NOT closed by any round (site untouched); low rank, post-trial |

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

## Ship-blocking five (round-1 verdict, re-ranked after V2 adversarial review)

1. **Customer data handling / NDA exposure (#11)** — the demo uploads Meta-project drawings to a
   third-party LLM with no consent artifact, no deletion path, no retention statement. Every
   other gap risks embarrassment; this one risks the demo itself being a contract breach.
2. **Zero customer inputs in reach (#1)** — everything else is rehearsal until real drawings exist;
   drawing classes (EMT/conduit?) may not even match the one extractor + box-primitive geometry.
3. **No scriptable Revit harness (#2)** — without it every success-rate claim stays untested and
   rounds 3–5 verification degrades to code-reading.
4. **Confidence gate passes garbage (#4)** — exit 0 at 0.50 with a 0×0×0 bbox is the exact
   wrong-but-confident failure a customer remembers.
5. **Demo-day delivery chain (#12+#13+#14)** — Revit-version mismatch at handoff, a worker
   machine nobody named, and credential landmines: three independent ways the last step fails
   live even if everything upstream worked.

Close runners-up, still required before ship: schema fork (#3), packaging/licensing (#8+#9),
input-size/multi-unit limits (#15).

**Do rounds 2–5 as scoped close them?** Not fully — say it plainly:

- #2 (inputs), #3 (harness), #4 (gate): YES, rounds 2–4 as scoped close these, PROVIDED the
  operator supplies the CVE drawings, the Sprint 001 fixtures, and CVE's pinned Revit version.
  No round can conjure any of those from this container.
- #5 (delivery chain): round 5's rehearsal closes it only if its scope is widened to include the
  version-pin check, a named+configured worker machine, and pre-provisioned CVE credentials —
  as briefed ("packaging + rehearsal") it plausibly skips all three. Widen round 5's brief.
- #1 (data/NDA): NO round as scoped touches it. It needs (a) an operator/legal read of CVE's
  Meta obligations before any real drawing is uploaded, and (b) engineering work nobody has
  scheduled (deletion endpoint, retention statement, possibly a no-cloud demo mode using
  pre-approved local .pred.json files — which BuildFromPredJsonCommand already supports offline).
  Until then, rehearse exclusively with non-customer drawings.
- Also: the Ed25519 licensing the round briefs assume does not exist in reachable code. If
  round 5 is expected to "verify" it, round 5 will fail; it must either build licensing or
  ship without it — an operator decision, not a loop decision.
