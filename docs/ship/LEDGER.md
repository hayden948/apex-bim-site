# Ship-readiness ledger (append-only)

Source of truth for the ship-readiness loop. Append; never rewrite history.
Protocol: `.claude/LOOP.md`.

---

## ROUND 1 — inventory + harness install

T0 = Mon Aug 17 20:44:20 UTC 2026 (pasted from `date`). Hard stop 22:44:20 UTC.

GOAL (verbatim scope): evidence-backed picture of what works today vs the Cache Valley
Electric "Meta EMT 11990" dry run requirements; install the loop harness. No production code.

### Cycle 1 (T0 → ~21:10) — environment ground truth + harness

Environment findings (PROVEN by command output this cycle):

- `C:\Apex\Product & Engineering` does not exist in this environment
  (`ls: cannot access 'C:\Apex\Product & Engineering': No such file or directory`).
  This loop runs in the Linux container documented in `.claude/LOOP.md` § Environment note.
- CLAUDE.md, SPRINT.md, AGENTS.md: **absent from both reachable repos**
  (Glob `**/{CLAUDE,SPRINT,AGENTS,LOOP}.md` → "No files found" in apex-bim-site;
  parser repo root listing: Dockerfile README.md app parser poller.py requirements.txt schema.sql).
  ASSUMED: they live only on the operator's machine. GAP: round scoping documents not in VCS.
- `revit-plugin/src/BuildFromPredJsonCommand.cs` EXISTS in apex-bim-site (grep hit) —
  the product code the round references is in this repo.
- `hayden948/apex-parser-service` (private) attached and cloned at
  `/workspace/apex-parser-service`, HEAD `269e33f97915d044f17f83533098bbb561455ac5`,
  last push 2026-06-02 (list_repos output) — 2.5 months older than the plugin work.
- Only one `.pred.json` in either repo: `revit-plugin/examples/panelboard.pred.json`.

Changed this cycle: `.claude/LOOP.md` (new), `docs/ship/LEDGER.md` (this file, new).

Next action: create `.claude/agents/ship-reviewer.md` (V2 harness), then STEP 1 inventory —
builds, tests, live-path trace, schema census, fixture check, harness check, packaging smoke.

### Cycle 2 (20:45 → 20:48 UTC — running well ahead of the 25-min cadence) — STEP 1 INVENTORY

Changed this cycle: `.claude/agents/ship-reviewer.md` (new, V2 harness). Everything else read-only.

#### Builds (PROVEN — pasted)

Toolchain: csc assembled from NuGet in the session scratchpad (no MSBuild/csproj build here;
`revit-plugin/ApexBimStudio.csproj` itself was NOT exercised — recorded as debt).

```
=== net48 ===
net48 exit: 0
=== net8 ===
net8 exit: 0
```

Zero compiler diagnostics printed for either target. Warnings-as-debt: none surfaced by csc with
these switches; /warnaserror was not used; the .csproj (with its own analyzers/settings) is unbuilt
in this environment.

#### Test suite (PROVEN — pasted, truncated to representative lines)

```
=== tests ===
PASS  24 in = 2 ft: got 2, want 2
PASS  family_name binds
PASS  qa score binds: got 0.86, want 0.86
PASS  ref plane offset binds: got -0.25, want -0.25
... (53 PASS lines total)
ALL TESTS PASSED
```

Coverage character: unit conversions, JSON DTO binding (pred/AFIS/API responses), config
round-trip, URL scheme guard, token display. NO coverage: any Revit API behavior (geometry
building, constraint pass, template resolution), the QA rule engine outcomes, the Supabase
functions (tested live instead, see prior rounds), the parser repo from this suite.

Parser repo's own test (PROVEN — pasted):

```
$ python3 -m parser.tests.test_transformer
OK: all checks passed.
  Model:      ZGSL-H-1500-34.5-0.48
  Rating:     1500.0 kVA
  ...
  Confidence: 1.00
test exit: 0
```

CAVEAT (PROVEN by reading the test file): the test monkeypatches `_extract_text` with an
embedded SAMPLE_TEXT string (`parser/tests/test_transformer.py:119`). No PDF is opened by any
test in either repo. PyMuPDF text extraction has never been exercised by CI anywhere.

#### Live-path trace (PROVEN by reading the named files)

The round brief assumes: shop2revit parser → .pred.json → BuildFromPredJsonCommand → RunQa.
**That chain does not exist as described.** What exists:

- Parser (`/workspace/apex-parser-service/parser/`): CLI `python -m parser.cli <pdf>` emits
  `<stem>.spec.json` in the **FamilySpec** schema (`parser/schema/family_spec.schema.json`,
  models in `parser/schema/models.py`: source/product/geometry.bounding_box_mm/parameters{dict}/
  bill_of_materials, mm units, confidence+warnings). One extractor: transformer_padmount.
- Plugin (`revit-plugin/src/BuildFromPredJsonCommand.cs`): consumes **.pred.json** — a different,
  flat shape (family_name/category/geometry{width{value,unit}}/parameters[list], inches default).
- Bridge FamilySpec→pred: **none**. `grep -rln "pred" /workspace/apex-parser-service` → no matches
  outside .git. Nothing in either repo converts .spec.json to .pred.json.
- The pipeline that IS connected end-to-end (built+verified in Aug rounds, this branch):
  PDF upload → Supabase `api` fn → Claude extraction → pred-shaped `claude_result` → human/auto
  approve → AFIS 1.0 → `generate_rfa` job → plugin ProcessQueue/AutoProcess builds from **AFIS**
  (AfisRevitMapper) → .rfa uploaded back. Verified live this session:
  `GET /v1/health` → `{"ok": true, "extraction_enabled": true, ...}` (pasted earlier this session);
  Telegram command round-trips exercised 20:41–20:55 UTC (see session round for task #23).
- Contract validation per hop (what trusts what):
  - parser CLI: dataclasses catch shape errors; JSON-schema file exists but NOTHING runs
    jsonschema validation against emitted specs (grep: no `jsonschema` import anywhere).
  - service `app/parser_adapter.py`: **STUB** — returns a hardcoded ZGSL-H FamilySpec with
    `"_stub": True` (lines 51–73). The real parser package in the SAME repo is not wired in.
  - `poller.py run_revit_addin()`: raises by design; job stays `ready` (README lines 75–79).
  - BuildFromPredJsonCommand: validates JSON deserializes, `geometry.primitive == "box"`,
    positive dimensions, template resolvable. Silently coerces: unknown spec_type→Text,
    unknown group→IdentityData, unknown units→inches, unparseable numeric values→skipped
    (counted in the dialog but non-fatal).
  - RunQaCommand (`Commands.cs:375`): family-editor local QA, else cloud QA on the ACTIVE
    library family (AFIS-based). Nothing runs QA on a raw .pred.json before build.
- Supabase-side validation: `predProblem()` shape-check gates approve; `runQaPipeline`
  (S/P/G/E/Z/L rules) gates generate-rfa. (api/index.ts, deployed v25.)

#### Schema census (PROVEN — paths + `git log -1 --format=%ci` dates)

1. `parser/schema/family_spec.schema.json` + `models.py` (FamilySpec, mm) — writer: parser CLI +
   (stub) service; reader: NOTHING in any repo. Repo last push 2026-06-02.
2. `.pred.json` (no schema file; shape defined by C# DTOs in BuildFromPredJsonCommand.cs and by
   the Claude EXTRACTION_SCHEMA in `supabase/functions/api/index.ts`) — writers: Supabase
   extraction, hand-authoring; readers: BuildFromPredJsonCommand, console review UI.
3. AFIS 1.0 (`AfisModels.cs`, `predToAfis()` in api/index.ts; no .schema.json file) — writer:
   API approve; readers: AfisRevitMapper/GeometryBuilder (plugin), runQaPipeline (API).
4. Old AFIS format: the deployed seed family + `revit-plugin/examples/` reflect current AFIS only;
   no older AFIS variant found in either repo (searched *.schema.json, "afis_version").

#### Customer-input inventory for "Meta EMT 11990" (PROVEN absent)

`grep -ri "11990|Meta|EMT|Cache Valley|CVE"` across both repos: zero hits for 11990/Meta/EMT
as customer artifacts (only marketing-site partner logo text mentions Cache Valley). Drawing
count for the dry run available in-repo: **0**. The only sample inputs anywhere:
`revit-plugin/examples/panelboard.pred.json` (1 file) and the embedded ZGSL-H SAMPLE_TEXT in
the parser test. Real CVE drawings are presumed to exist only on the operator's machine.

#### HARNESS CHECK (blocking) — PROVEN absent

`grep -ri "journal|Design Automation|batch|headless"` across both repos: only my own LOOP.md
note and marketing copy. There is NO journal-playback runner, NO batch runner, NO Design
Automation integration, and `poller.py run_revit_addin()` raises. Additionally this loop runs
on Linux with no Revit install, so even a journal harness would need the operator's machine.
**Ship-blocking gap; first work item of round 3.**

#### FIXTURE CHECK (blocking for round 2) — PROVEN absent here

`find / -name "*.pred.json"` (repo scope): exactly one file, `revit-plugin/examples/panelboard.pred.json`.
The 19 human-verified Sprint 001 M1 transformer .pred.json inputs are NOT in either repo, and no
Sprint 001 artifacts exist in VCS at all (no SPRINT.md). If they exist only on the operator's
machine, round 2 cannot regression-test without them → round 2 first task = obtain or reconstruct.

#### PACKAGING SMOKE TEST (timeboxed) — PROVEN

- Package = `revit-plugin/deploy/ApexBimStudio.addin` (ONE file, pasted in full this round's
  transcript): manual-copy manifest, `VendorDescription ... https://apexbim.example` placeholder
  domain still present. No installer project, no signing, no MSI/EXE.
- Ed25519 license check: `grep -rn -i "ed25519|licens"` across plugin src, deploy, and supabase
  functions → **zero hits**. The license check the round brief references DOES NOT EXIST in
  reachable code. Licensing/entitlement today = Supabase auth + apx_ tokens only.

#### RE-EVALUATION (protocol step 3, end of cycle 2)

(a) Highest-value path to EXIT? YES — inventory is complete with evidence; writing the gap
register now is the direct path.
(b) What invalidates the starting plan? The round brief's premises are partially wrong for this
environment: the pred.json chain isn't the connected path (AFIS pipeline is), the Ed25519 check
doesn't exist, briefing docs aren't in VCS, and customer inputs are absent. The register must
therefore document TWO pipelines (June parser-service one, stubbed at both ends; Aug Supabase
one, live) and the schema fork between them.
(c) Avoiding because hard? Running a real PDF through the parser (no real drawing PDF available
here — the fake.pdf in scratchpad is a minimal stub; I will attempt it anyway and record the
result rather than skip it silently).

Next action: attempt parser CLI on available PDFs, then write docs/ship/SHIP_READINESS.md.

### Cycle 3 (20:48 → 21:00 UTC) — parser-on-real-PDF, register, triple verification

Changed: `docs/ship/SHIP_READINESS.md` (new), `docs/ship/check_register.py` (new),
this ledger section.

#### Parser on a real, out-of-class PDF (PROVEN — pasted)

`pip install pymupdf==1.24.10` then, against the real Square D NQ430 panelboard submittal
(scratchpad `nq430-submittal.pdf`, the same PDF used for the live Claude-extraction E2E):

```
$ python3 -m parser.cli nq430-submittal.pdf
error: Could not auto-detect product type for nq430-submittal.pdf. Re-run with --product=...
cli exit: 1        <-- GOOD: auto-detect fails closed on unknown class

$ python3 -m parser.cli nq430-submittal.pdf --product=transformer_padmount --out=/tmp/nq430.spec.json
wrote /tmp/nq430.spec.json
  5 warning(s): Could not determine kVA rating. / HV voltage. / LV voltage. /
                Found only 0 dimension markers; bounding box may be wrong. /
                Only 1 appurtenances detected...
  confidence: 0.50
cli exit: 0        <-- BAD: exit 0 ("send to Revit") at exactly the 0.5 threshold
bbox_mm: {'length': 0.0, 'width': 0.0, 'height': 0.0}   <-- unbuildable spec passed the gate
```

Also PROVEN this cycle: parser's own suite passes but monkeypatches text extraction
(`test_transformer.py:119`) — real PDF extraction verified for the first time by the run above.

#### Live health re-probe (PROVEN — pasted 20:56 UTC, register row #7 evidence)

```
GET /v1/health -> 200 {"ok": true, "service": "apex-api",
                       "extraction_enabled": true, "telegram_alerts": false}
```

#### TRIPLE VERIFICATION of "the gap register is complete"

V1 SELF — claims in the register I cannot back with output produced THIS round (explicit list):
1. Row #5 "builds succeeded in prior-round manual E2E" — rests on prior rounds in this session's
   history (panelboard E2E, seed family), not re-executed today. Revit-side build cannot be
   re-executed from this container at all (register row #2 is that gap).
2. Row #5 "19 human-verified M1 transformers" — figure originates from the round brief; this
   round proved only that the INPUT fixtures are absent from VCS, not that the batch happened.
3. Row #8 "no signing" — proven for VCS content only; a signing setup could exist on the
   operator machine, unverifiable from here.
All three are labeled as such in the register (anecdote/prior-round/VCS-scoped wording).
Everything else in the "current state" column traces to output pasted in cycles 1–3.

V2 ADVERSARIAL — ship-reviewer instructions applied via a general-purpose read-only subagent
(the .claude/agents registry loads at session start, so the new agent file registers next
session; instruction text was supplied verbatim). Charge: find requirements the register omits
entirely. Verdict recorded below when the agent returns.

V3 EMPIRICAL — `docs/ship/check_register.py` (evidence-reference, banned-word, coverage-term,
and cycle-reference checks). First run FAILED legitimately — the register cited Cycle 3 before
this ledger section existed:

```
REGISTER CHECK FAILED:
  - REGISTER references Cycle 3, absent from LEDGER
exit: 1
```

Negative control (corrupted evidence cell + injected hedge word) also fails, proving the check
can fail:

```
  - REGISTER row 1: evidence cell has no ledger/session reference: 'TODO'
  - REGISTER: banned hedge word 'Should work'
exit: 1
```

Post-fix run (after appending Cycle 3 and scoping the banned-word rule to claims, not quoted
evidence — fenced blocks excluded):

```
$ python3 docs/ship/check_register.py
REGISTER CHECK PASSED: 10 evidenced rows, T0 present, no banned words, coverage terms present.
exit: 0
$ python3 docs/ship/check_register.py /tmp/broken_register.md docs/ship/LEDGER.md
REGISTER CHECK FAILED:  (corrupted evidence cell + injected hedge word)
exit: 1
```

V3 verdict: GREEN — the check passes on the real register and demonstrably fails on broken input.

### Cycle 4 (20:55:44 UTC per `date`) — V2 verdict + register revision

#### V2 ADVERSARIAL verdict: **REFUTED** (register was incomplete)

Reviewer (read-only subagent, given ONLY the claim + acceptance criteria + repo access, no
author conclusions) found four materially omitted categories plus two lesser ones:

1. Customer data handling/confidentiality/consent — pipeline sends full customer PDFs to
   Anthropic (`api/index.ts` extraction call) and stores them in Supabase; no privacy
   policy/terms (site footer links are `href="#"`), no deletion endpoint (only DELETE route in
   the API is project members; `uploads.deleted_at` is never set by any endpoint), no retention
   statement. A Meta-project NDA could make the demo upload itself a breach.
2. .rfa Revit-version compatibility with the customer's pinned project version — worker builds
   on whatever Revit it runs (`ProcessQueueCommand.cs` records `app.VersionNumber`); nothing
   targets/checks CVE's version; .rfa files don't open in older Revit.
3. Demo-day worker availability + latency — the build stage is a Revit session left open
   polling every 5 min (`AutoProcessCommand.cs`), 15-min stale requeue; no run-of-show names
   the machine or the expected per-family wall time.
4. Customer account/credential provisioning — email-confirmation signup loop, ~1 h session
   expiry mid-meeting, `apx_` token minting prerequisite, and plugin OAuth endpoints pointing
   at nonexistent `auth.apex.example`.
5. Input-document constraints — 30 MB upload gate exceeds the ~24 MB practical model-call limit
   (base64 inflation vs 32 MB request cap); one-unit-per-PDF prompt assumption vs multi-unit
   submittal packages; no splitting stage.
6. (minor) Customer-visible web surface not audited (dead footer links, claims pages).

Reviewer's "could not check from here": deployed-function-vs-repo drift, CVE's actual
contract/Revit version, operator-machine-only artifacts, live reproduction of the size/expiry
limits, physical-meeting logistics (venue network egress etc.).

Note on V1-vs-V2 independence: V1 SELF found labeling issues only; V2 found whole missing
categories. The disagreement is evidence the reviewer was not led. Findings accepted in full.

#### Register revision (this cycle)

`SHIP_READINESS.md`: added rows 11–16 (one per V2 finding), re-ranked the ship-blocking five —
data/NDA (#11) now ranks first, demo-day delivery chain (#12+#13+#14) enters at fifth — and
rewrote the rounds-2–5 verdict: #1 data/NDA is closed by NO scoped round (operator/legal action
plus unscheduled engineering: deletion endpoint, retention statement, or a no-cloud demo mode
via local BuildFromPredJson); round 5's brief must widen to cover version-pin, worker machine,
and credential preflight.

#### Register checker re-run after revision (PROVEN — pasted)

```
$ python3 docs/ship/check_register.py
REGISTER CHECK PASSED: 16 evidenced rows, T0 present, no banned words, coverage terms present.
exit: 0
```

Confirmed: the run executed immediately after this edit produced exactly the output above
(exit 0, 16 rows).

#### ROUND 1 EXIT status

- `.claude/LOOP.md`, `docs/ship/LEDGER.md`: EXIST (this file), pushed in db683dd.
- `SHIP_READINESS.md`: EXISTS, every current-state cell evidence-referenced, ranked.
- Triple verification on "register is complete": V1 (3 unbackable-claim labels), V2 REFUTED →
  register amended with all findings, V3 checker green with a demonstrated failure mode.
  Post-amendment claim is "register is complete AS OF the V2 findings"; a fresh V2 pass on the
  amended register is round 2's opening act, per the be-suspicious-of-agreement rule.
- Ship-blocking five + rounds-2–5 verdict: recorded in SHIP_READINESS.md § re-ranked verdict.

HANDOFF NOTE for round 2 (fresh-agent assumptions): read LOOP.md + this ledger fully; the
operator owes: CVE drawings, Sprint 001 fixture .pred.json files, CVE's pinned Revit version,
legal read on Meta NDA, briefing docs (CLAUDE/SPRINT/AGENTS.md) into VCS, TELEGRAM_BOT_TOKEN.
Round 2 opening acts: re-run V2 on amended register; fixture recovery/reconstruction; schema
canonicalization decision (P-A AFIS path is live; P-B FamilySpec feeds nothing).

---

## ROUND 2 — one canonical FamilySpec contract

T0 = Mon Aug 17 21:00:39 UTC 2026 (pasted from `date`). Hard stop 23:00:39 UTC.
Same session as round 1; no compaction; on-disk ledger tail verified against context before start.
Branch: claude/analysis-improvement-fvnun0 at f4b7938 (no history rewrites this round, per guardrail).

GOAL: one canonical, versioned FamilySpec contract validated at every boundary; the
pred.json / family_spec.schema.json / C#-DTO fork permanently closed. Sprint 002 "every change
is breaking" policy honored. NOTE: SPRINT.md is still not in VCS (round-1 gap #10), so the
"locked Sprint 002 decision" is honored as stated in the round brief; if the actual document
contradicts this round's interpretation, reconciliation is a logged operator follow-up.

### Cycle 1 (21:00 →) — canonical decision + schema of record

DECISION (rationale + costs in schemas/familyspec/DECISION.md, written this cycle):
canonical = the pred shape, formalized as **FamilySpec v1** in
`schemas/familyspec/familyspec.v1.schema.json`, with required `schema_version: "1.0"`.
AFIS 1.0 remains a DERIVED internal artifact (already versioned via afis_version; single
writer predToAfis, single reader AfisRevitMapper) — not a competing interchange contract.
The parser repo's family_spec.schema.json + models.py are declared NON-CANONICAL (nothing
reads them — proven round 1) and quarantined by decision; physical relocation blocked by
read-only access to that repo (logged as operator follow-up).

Cycle 1 complete (21:10 UTC). Changed: schemas/familyspec/{DECISION.md, familyspec.v1.schema.json,
fixtures/golden/*6 files, fixtures/malformed/*6+6 files, fixtures/README.md},
revit-plugin/src/PredValidator.cs (new), revit-plugin/src/BuildFromPredJsonCommand.cs (validator
wired before deserialize/build; DTOs gain schema_version/warnings/confidence),
revit-plugin/examples/panelboard.pred.json (stamped v1), revit-plugin/tests/TestMain.cs
(validator + schema-contract + fixture tests).

PROVEN (pasted in transcript, summarized):
- Full suite: `ALL TESTS PASSED` (97 asserts: 53 pre-existing + validator/contract/fixture).
- Contract test FAILS on deliberate schema break (category→categoryy):
  `FAIL contract: root props == PredFamily DTO: only-in-first [categoryy] only-in-second [category]`
  then reverted → `ALL TESTS PASSED`. (EXIT proof #2.)
- 6 golden fixtures validate; 6 malformed fixtures each rejected WITH the expected named-field
  substring (missing-family-name, negative-width, bad-spec-type, future-version, unknown-field,
  sphere-primitive). (EXIT proof #4, C# side.)
- Golden provenance: live-DB extractions incl. the human-approved NQ430 (18 params), ids in
  fixtures/README.md. Sprint 001's 19 inputs remain operator-blocked (register #5) — this set is
  the interim net, NOT a claim that the 19 are reconstructed.

ASSUMED (to be closed later this round): API boundary still unvalidated/unstamped (cycle 2);
EXTRACTION_SCHEMA parity unchecked (cycle 2); round-trip goldens not yet generated (cycle 3).

Next: api/index.ts validate+stamp, named-field correction errors, parity checker, deploy v26.

### Cycle 2 (21:08 → 21:30 UTC) — API boundary + parity checker + live proofs

Changed: supabase/functions/api/index.ts (familySpecProblems derived from EXTRACTION_SCHEMA;
extraction output validated+stamped before write; approve validates stored result and
corrections with named-field messages; legacy stamp accommodation; health reports
familyspec_version), deployed as **api v26**; schemas/familyspec/tools/check_extraction_schema.py.

PROVEN (pasted in transcript this cycle):
- deno check on edited api: only the 4 pre-existing stub TS7006s (same as rounds prior).
- Parity checker: `EXTRACTION_SCHEMA PARITY OK` (allowed divergences A1-A3 documented in the
  script); negative control (enum value renamed in a schema copy) fails with a named diff and
  exit 1, then reverts clean.
- Live v26: GET /v1/health -> `"familyspec_version": "1.0"`.
- Live named-field rejection (EXIT proof #4, API side, real malformed payload):
  `400 INVALID_CORRECTION "extraction bdb4d3cf correction: geometry.width.value must be a
  number > 0 (got -4)"` + second problem naming spec_type "Nummber" in details.problems.
- Live legacy stamp: synthetic un-versioned extraction approved -> stored claude_result now
  `schema_version = "1.0"`, status approved; test rows fully deleted afterward (counts pasted).

### Cycle 3 (21:30 → 21:50 UTC) — round-trip goldens + a real defect found and fixed

- Round-trip harness executed against the DEPLOYED pipeline: 6 golden fixtures inserted as
  synthetic ready extractions on the demo project, approved via
  `POST /v1/extractions/{id}/approve?chain=1` (all six 201).
- **DEFECT FOUND BY THE ROUND-TRIP** (the fixture net doing its job on day one):
  predToAfis filtered width/depth/height names but NOT the server-owned Apex_AfisId — the
  panelboard-example fixture produced an AFIS with TWO Apex_AfisId parameters, and the
  extracted one ("example-panelboard-001") would overwrite the family's real id at build time
  (plugin sets params by name, last write wins). Fixed in predToAfis (drop incoming
  apex_afisid), deployed as **api v27**; all six re-approved under v27:
  `afisid_param_count = 1` for 6/6 (pasted).
- Expected goldens written to schemas/familyspec/fixtures/expected/*.afis.json (normalization:
  family UUID -> <FAMILY_ID>, " (N)" name suffix stripped — NORMALIZATION.md). Transcription
  integrity PROVEN in-database: each committed expected doc compared as jsonb against the live
  family's normalized AFIS — `expected_matches_live = true` for 6/6 (pasted).
- QA scores recorded per fixture: SB-2 0.88, AHU-9 1.0, NQ430 0.94, panelboard 0.94, T-1 0.94,
  ZGSL 0.94.
- DB restored: deleted 73 validations + 6 jobs + 6 families (first pass), then 73/6/6/6/6
  (validations/jobs/families/extractions/uploads) after regeneration — counts pasted; demo
  project back to pre-round state.
- tools/roundtrip.py committed: the operator-runnable regression procedure (psql + live API),
  same steps that produced the baseline.

#### Quarantine of the dead schema (work item 5) — PROVEN, with one precision

- apex-bim-site: `grep -rn "spec\.json|family_spec|FamilySpec"` over plugin src, supabase,
  console, CI (excluding the new v1 artifacts' own names) -> **no matches, grep exit 1**
  (pasted). Nothing in the shipping repo reads the parser FamilySpec shape.
- apex-parser-service: the P-B service DOES move FamilySpec internally (app/main.py, store.py;
  poller writes `<job>.familyspec.json` at poller.py:76) but its only consumer hook,
  `run_revit_addin()`, raises by design — the path terminates at a stub. Quarantine therefore =
  DECISION.md declaration + this evidence; physically moving files in that repo is blocked by
  read-only access (operator follow-up, noted in DECISION.md). No deletion performed in
  apex-bim-site because nothing there is a dead schema definition (AfisModels.cs is live:
  ProcessQueue worker + QA read AFIS).
- Environment note: round brief says "grep with Select-String" (PowerShell, operator machine);
  this container uses grep — same evidence class.

#### RE-EVALUATION (protocol step 3 — cycles 2/4 checkpoint, taken here)

(a) Still highest-value path to EXIT? YES — all five EXIT bullets have evidence; remaining:
    ledger/register bookkeeping + triple verification.
(b) Learned that invalidates the starting plan: (1) the "reconstruct the 19 Sprint 001
    fixtures" first-task is impossible from this container (no source drawings, no outputs in
    VCS — round-1 evidence stands); built the 6-fixture interim net from live-DB artifacts
    instead and said so — register row #5 stays open on the operator. (2) The round-trip
    baseline is DB-content-verified, sidestepping the transcription risk this environment's
    MCP-only DB access creates. (3) A genuine defect (duplicate Apex_AfisId) surfaced —
    evidence the net has teeth.
(c) Avoiding because hard? Codegen for the C# DTOs (no offline NJsonSchema toolchain);
    declared not viable in-round in DECISION.md, covered by the contract test instead —
    exactly the fallback the round brief authorizes.
