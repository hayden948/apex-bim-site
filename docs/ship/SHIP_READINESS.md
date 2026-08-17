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
| 3 | Schema integrity across the toolchain | **CLOSED (Round 2).** Canonical FamilySpec v1 = the pred shape: schema of record `schemas/familyspec/familyspec.v1.schema.json` + DECISION.md; required `schema_version`; validated at every boundary (model output before write, corrections, stored results, plugin input pre-build) with named-field errors; C#↔schema↔EXTRACTION_SCHEMA tied by contract test + parity checker, both proven able to fail; parser-repo FamilySpec quarantined by decision (zero consumers in shipping repo, grep exit 1; its only sink raises by design). Round-trip net found+fixed a real defect (duplicate Apex_AfisId, api v27) on day one | R2 Cycles 1–3 (LEDGER): suite `ALL TESTS PASSED`; contract-test break/revert; live 400 INVALID_CORRECTION naming geometry.width.value; 6/6 `expected_matches_live = true` | Remaining: codegen not viable in-round (contract test covers); physical quarantine move in parser repo is operator follow-up (read-only access); legacy-v0 accommodation documented in DECISION.md | Drift now fails tests loudly instead of corrupting silently | Done this round; operator: move parser-repo files to legacy/, push SPRINT.md to confirm the Sprint 002 policy reading |
| 4 | Parser accuracy on unseen drawings | 1 extractor (transformer_padmount), tested only against its own embedded text fixture. Forced onto an out-of-class real PDF: **exit 0 at confidence 0.50 with bbox 0×0×0 mm** — the gate passes an unbuildable spec. Auto-detect refuses unknown types (good). R2 update: the CANONICAL path now hard-rejects zero/negative dimensions at every boundary (named-field error), so a zero-bbox spec can no longer reach a build; the P-B parser's own gate is unchanged (repo quarantined, read-only) | R1 Cycle 3 § parser-on-real-PDF; R2 Cycles 1–2 boundary proofs | P-B CLI gate itself unfixed; live-path accuracy on real CVE drawings still unmeasured | Zero-dim garbage is now stopped at the door; wrong-but-plausible values still pass | Round 4 (accuracy on real CVE set); P-B gate only if P-B is revived |
| 5 | Family build success rate | Partially improved (Round 2): a 6-fixture golden regression net now exists (incl. the human-approved 18-param NQ430 and a ZGSL transformer), round-tripped through the DEPLOYED pipeline with content-verified expected AFIS + QA scores and an operator-runnable re-run procedure (`schemas/familyspec/tools/roundtrip.py`). The 19 Sprint 001 M1 inputs remain absent from VCS — cannot be reconstructed from this container (no drawings, no outputs in reach); still an operator action. Revit-side build rate still unmeasured (no harness, row #2) | R2 Cycle 3 (LEDGER): 6× 201 approvals, 6/6 jsonb equality, QA scores pasted | The net covers pred→AFIS→QA, not AFIS→.rfa; Sprint 001 inputs still missing | Regression net has teeth (caught a real defect) but stops at the Revit boundary | Operator: supply Sprint 001 inputs; Round 3: harness measures .rfa rate |
| 6 | QA validation coverage | Two QA engines exist (plugin local, API S/P/G/E/Z/L on AFIS), gate generate-rfa, verified live in prior rounds. But QA runs on AFIS only — nothing QAs a raw .pred.json/FamilySpec pre-build, and QA rule outcomes have no automated tests | Cycle 2 § live-path trace, § test coverage | Garbage-in caught only by predProblem() shape check; rule engine untested against synthetic failure cases | QA passes a spec whose geometry silently defaulted; customer sees a 24×24×24in "transformer" | Round 4 |
| 7 | Failure behavior in front of a customer | Plugin: TaskDialog with real error text + `%LOCALAPPDATA%\Apex\logs`; API: structured error envelope; auto-pipeline: Telegram alert + row stays for review — all verified live. But: Telegram outbound alerts currently OFF (no bot token on project), and a failed extraction burns a paid Claude call with a raw error string surfaced to the console | This session's task-23 round (health `telegram_alerts: false` pasted); Cycle 2 § trace | Alert channel dark until operator sets TELEGRAM_BOT_TOKEN; no rehearsed "it failed, here's what we do" script | Failure mid-demo with no notification and an unpolished error is plausible | Round 5 (dry-run rehearsal script); token = 1-line operator action today |
| 8 | Install / licensing | Package = ONE `.addin` manifest with placeholder vendor URL; manual copy; no installer, no signing. **Ed25519 license check: does not exist in any reachable code** (zero grep hits) | Cycle 2 § PACKAGING SMOKE TEST | Round brief assumes a license check that isn't in VCS; if it exists it lives only on the operator's machine — unverifiable and unshippable from here | Customer installs by hand-copying DLLs; nothing prevents redistribution; "licensed product" claim is false | Round 5; scope decision needed (ship without licensing vs build it — changes round scope materially) |
| 9 | What the customer physically receives + how they open it | Receivable today: `.rfa` via console download (works, verified) or direct from plugin build; plugin itself via manual .addin + DLL copy. No install doc for a customer-facing dry run; briefing docs (CLAUDE.md/SPRINT.md/AGENTS.md) absent from VCS | Cycle 2 § PACKAGING, § environment findings | No packaged deliverable, no one-page install/run sheet, no versioned release artifact | CVE asks "how do we open this?" and the answer is a live improvisation | Round 5 |
| 10 | Round-scoping documents in version control | CLAUDE.md / SPRINT.md / AGENTS.md not in either repo | Cycle 1 environment findings | Rounds 2–5 may be scoped against documents this loop cannot read | Loop optimizes for the wrong target | Operator action: push/paste them; log receipt in LEDGER |
| 11 | Customer data handling, confidentiality, consent (added by V2 adversarial review) | Pipeline stores CVE drawings in Supabase and sends full PDFs to Anthropic (`api/index.ts` extraction call). No privacy policy/terms anywhere (site footer links are `href="#"`), no data-deletion endpoint (uploads have `deleted_at` but nothing sets it), no retention statement, no disclosure that drawings go to an LLM vendor | Cycle 4 § V2 findings (reviewer output pasted) | A Meta-project NDA may prohibit third-party disclosure — the demo upload itself could be a breach; "can you delete our drawings?" has answer "no" | Legal exposure + instant trust loss with CVE's BIM manager | NOT in rounds 2–5 as scoped. Needs operator/legal decision this week + a deletion/retention work item; flagging as ship-blocking |
| 12 | .rfa Revit-version compatibility with the customer's project (added by V2) | Built family's version = whatever Revit the worker runs (`ProcessQueueCommand.cs` records `app.VersionNumber`; nothing selects/checks a target). An .rfa opens only in its build version or newer; Meta-scale BIM execution plans pin the year | Cycle 4 § V2 findings | Worker on 2026 + CVE project on 2024 ⇒ family fails to load at handoff, the last step of the demo | Unrecoverable failure at the moment of delivery | Round 3 (harness pins build version; needs CVE's pinned version from operator) |
| 13 | Demo-day worker availability + pipeline latency (added by V2) | Build stage = a Windows machine with Revit left open, polling every 5 min (`AutoProcessCommand.cs`); stale jobs requeue after 15 min. No run-of-show doc names the machine, network, power settings, or expected per-family wall time | Cycle 4 § V2 findings | Approve live, then narrate dead air for 5+ min — or forever if the worker laptop sleeps | Demo stalls with the customer watching | Round 5 (rehearsal + preflight); poll interval tune-down is a small round-3 item |
| 14 | Customer account/credential provisioning (added by V2) | Console needs a Supabase account (signup may require email confirmation); sessions expire ~1 h (console's own 401 hint); plugin needs a minted `apx_` token; OAuth PKCE endpoints in the plugin point at `auth.apex.example` (nonexistent) | Cycle 4 § V2 findings | No pre-created CVE user/project/token; session can 401 mid-meeting | Meeting opens with an email-confirmation loop; console dies at minute 40 | Round 5 preflight (pre-provision + re-auth script); OAuth stub flagged for scope decision |
| 15 | Input-document constraints: size, pages, multi-unit packages (added by V2) | Upload gate is 30 MB but the model call fails above ~24 MB after base64 inflation; PDF page caps apply; extraction prompt assumes ONE unit per document — real submittal packages bundle many units; no splitting stage exists | Cycle 4 § V2 findings | CVE's combined package 413s, errors at extraction, or yields one bbox for a 40-unit package | Product's own gate accepts input the pipeline then chokes on, live | Round 2 (align gate to ~20 MB + document limits); multi-unit splitting is new scope — operator decision |
| 16 | Customer-visible web surface consistency (added by V2, minor) | Marketing site footer links dead (`href="#"`); claims pages CVE may browse during/after the meeting are not audited against what the dry run shows | Cycle 4 § V2 findings | Site promises what the demo can't show; dead links read as vaporware | Credibility discount on everything demonstrated | Round 5 (content pass), low rank |

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
