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

### Cycle 4 (21:50 → UTC) — triple verification + close

Final V3 gate re-run after all edits (pasted): net48 exit 0, net8 exit 0, `ALL TESTS PASSED`,
`EXTRACTION_SCHEMA PARITY OK`, `REGISTER CHECK PASSED: 16 evidenced rows`. Work pushed as
5ded7ac (37 files, +3162/-36).

V1 SELF — claims I cannot back with output produced this round (explicit list):
1. "Deployed api v27 byte-matches the local file": the deploy payload was assembled in-context,
   not binary-diffed against disk. Backed instead by behavior: deno check on the disk file,
   live health familyspec_version, live INVALID_CORRECTION, 6/6 afisid_param_count=1 under
   v27 — the changed code paths are provably live; untouched routes rest on the v26-era live
   tests. Residual risk accepted and stated.
2. "Plugin shows the named-field dialog": PredValidator's messages are proven by the suite;
   the TaskDialog presentation itself cannot execute here (no Revit — register row #2).
3. "Sprint 001 legacy files will validate as v0": inferred from the v0 rules; no actual
   Sprint 001 file exists in reach to test.
4. roundtrip.py as a script was not executed end-to-end here (needs psql + APEX_DB_URL);
   the procedure it encodes WAS executed step-by-step (MCP SQL + live http) and produced the
   content-verified baseline. The script is the documented re-run path for the operator.

V2 ADVERSARIAL — ship-reviewer agent (registered this session) launched with the charge:
find a code path still reading a non-canonical shape, and a validation bypass. Verdict
recorded below on return.

### Cycle 5 / ROUND 2 CLOSE — V2 verdict, fixes, wall-clock note

WALL-CLOCK NOTE (protocol rule 1): `date` at this checkpoint reads Tue Aug 18 18:19:36 UTC —
the session was suspended by the operator mid-round (after the V2 fixes were built and v28
deployed) and resumed ~20h later. T0+120min passed during the suspension, not during active
work. Per LOOP.md stop conditions, this entry is the close/handoff: only bookkeeping and the
already-verified evidence below were added after resume; no new work items were started.

#### V2 ADVERSARIAL verdict: **REFUTED** (7 findings) — triage and disposition

1. Lenient-then-stamp laundering (extraction path ALWAYS lenient; legacy stamp wrote
   schema-violating "1.0" docs; approve-of-own-data could 422): **FIXED in api v28** —
   writers validate a STAMPED copy (strict) before stamping; a legacy row that needs
   accommodations is approved UNSTAMPED with a `legacy_v0_accommodations` audit entry
   naming every accommodation; familySpecProblems now returns visible warnings.
2. Malformed/typo'd correction silently discarded → uncorrected result approved:
   **FIXED in v28** — non-empty body must parse and carry `result`; broken JSON → 400
   BAD_JSON with the parser position; `{"Result": ...}` → 400 MISSING_RESULT naming the keys.
3. shop2revit universe alive, same "1.0" identifier, jobs-table collision risk: partially
   mitigated — both validators now name the wrong contract when its telltale keys appear
   (`product_type` etc.); the service itself is in the read-only repo → OPERATOR ITEM
   (archive or point away from the product Supabase project; register row #3 note).
4. AFIS readers never reject unknown versions: **FIXED** — `AfisRevitMapper.Apply` (the
   single choke point for both plugin build paths) rejects non-1.x/missing afis_version
   with a named message; AfisModels no longer masks a missing version with a default.
5. Parity checker not in CI: **FIXED** — `deploy-api.yml` runs it as a pre-deploy gate;
   DECISION.md wording corrected.
6. Contract-test blind spots (types/bounds/consts; case-insensitive primitive):
   partially fixed — primitive is now case-sensitive in both validators (schema const),
   parity checker compares the primitive const, tests added (BOX rejected, shop2revit
   hint, afis version gate). Types/bounds comparison remains DEBT (register row #3 note).
7. Display readers (console, Telegram) ignore schema_version: accepted as minor —
   server-side approve still gates; noted, not fixed this round.

#### Live retests against deployed v28 (PROVEN — pasted, reviewer's exact breaking inputs)

Legacy row (no schema_version, no units, no is_instance) on the demo project:

```
broken-json    -> 400 BAD_JSON "Correction body is not valid JSON: Expected property name..."
typo-key       -> 400 MISSING_RESULT "Correction body must be {"result": {...}} (got: Result)..."
legacy-approve -> 201 family created
stamp-check    -> has_schema_version = false   (legacy row NOT laundered to v1)
audit-check    -> legacy_v0_accommodations with named warnings
                  ("geometry.width.unit missing — legacy v0 document, inches will be
                   assumed at build time", ...)
re-approve     -> 201 (no 422-on-system-written-data; prior inconsistency gone)
```

Local gate after fixes (pasted pre-suspension): net48 exit 0, net8 exit 0, ALL TESTS PASSED
(incl. new BOX-rejected / shop2revit-hint / afis-version-gate asserts), PARITY OK,
REGISTER CHECK PASSED. Test DB rows fully deleted after the retests (counts pasted).

#### ROUND 2 EXIT status (final)

- Exactly one schema of record: schemas/familyspec/familyspec.v1.schema.json + DECISION.md. MET.
- Contract test fails on divergence: proven (category→categoryy break + revert, pasted). MET.
- Golden fixtures round-trip: 6/6 content-identical in-database vs expected/ (normalization
  documented; the one behavioral difference found — duplicate Apex_AfisId — was investigated
  as a REGRESSION and fixed in code, not rebaselined). MET.
- Invalid input → named-field error, demonstrated with real malformed files/payloads:
  C# fixture suite + live 400s above. MET.
- Triple verification: V1 (4 unbackable claims listed), V2 REFUTED → findings fixed or
  dispositioned above, V3 checkers green with demonstrated failure modes. RUN.
- Deployed: api v28. Committed: 5ded7ac + the close-out commit carrying this entry.

HANDOFF for round 3: first item remains the headless Revit harness (register #2). New
inputs from this round: PredValidator + AfisRevitMapper.SupportsAfisVersion gate the plugin
side; roundtrip.py is the regression procedure; V2 residuals = shop2revit archive (operator),
type/bounds contract-test depth, display-reader version checks.

---

## ROUND 3 — true success rate + survivable failures (RESTRUCTURED per work-item 0 fallback)

T0 = Tue Aug 18 18:30:36 UTC 2026 (pasted from `date`). Hard stop 20:30:36 UTC.
State re-read on start: LEDGER tail + register verified on disk; branch at e573d1c.

### Cycle 1 — new-inputs check + restructure decision

NEW-INPUTS CHECK (PROVEN, pasted): `git fetch` both repos — apex-bim-site main unchanged
(f588c95 + our branch), parser repo HEAD unchanged (269e33f); DB uploads table newest row is
still nq430-submittal.pdf (2026-08-12). **Zero Meta EMT 11990 drawings are reachable. SPRINT.md
/ CLAUDE.md / AGENTS.md still not in VCS.** No Revit exists in this container (round-1 harness
check stands).

RESTRUCTURE (the round brief's own clause: "If a harness truly cannot be built… produce the
exact run scripts and a step-by-step operator checklist… mark every affected EXIT criterion as
HUMAN-VERIFY-REQUIRED… never report those items as done"):

Deliverables this round, split by executability:
A. BUILDABLE HERE (and built): the batch harness CODE — a scriptable plugin batch command
   (folder of .pred.json → isolated per-drawing build, quarantine, per-drawing JSONL log,
   RUN_MATRIX generator, deterministic ordering), compiled for both targets and unit-tested in
   its Revit-free parts; the operator run kit (journal template + PowerShell launcher +
   step-by-step checklist); QA rules covering the failure classes fixed in rounds 2–3, each
   proven to fail; API-side determinism + corrupted-input containment proven LIVE.
B. HUMAN-VERIFY-REQUIRED (marked, not done): any actual .rfa build execution, the per-drawing
   build results, wall times, and build-stage failure counts.
C. BLOCKED-ON-OPERATOR (not fabricated): the CVE drawing batch, the 20–30% holdout (minimum 3
   drawings — we have 0), and therefore THE headline number. Blunt sentence reserved for EXIT.

Honesty note recorded now, before any work: no number produced this round is a success rate on
Meta EMT 11990 drawings, because none exist here. Anything resembling a rate below refers to
the named fixture set only and is labeled as such.

### Cycle 2 (18:31 → 18:43 UTC) — batch harness built

Changed: revit-plugin/src/BatchRunReport.cs (new, Revit-free report engine),
revit-plugin/src/BatchBuildCommand.cs (new: folder batch, per-drawing isolation, quarantine,
JSONL log, RUN_MATRIX writer, deterministic ordinal ordering, APEX_BATCH_DIR scriptable path),
BuildFromPredJsonCommand.cs (build core extracted as BuildToFile; partial-.rfa deleted on any
failure), ApexApplication.cs (ribbon: M1 → Batch Build), TestMain.cs (report-engine tests).

PROVEN (pasted): net48 + net8 compile clean (fresh DLL timestamps 18:43); suite ALL TESTS
PASSED including: classification table, quarantine naming, jsonl fields, matrix rows,
taxonomy counts, all-files denominator ("1/3 succeeded (33%)"), honesty disclaimer, empty
batch. One real intermediate failure caught and fixed (net48 lacks Contains(string,
StringComparison); invariant P0 formatting) — evidence the suite gates the harness.

### Cycle 3 (18:44 → 19:20 UTC) — operator kit + executable evidence

Changed: revit-plugin/deploy/batch/{OPERATOR_CHECKLIST.md, run-batch.ps1, journal-template.txt}.

PROVEN (pasted this cycle):
- Parser CLI determinism: two runs on the real NQ430 PDF; diff = ONLY source.extracted_at
  (provenance timestamp). No geometry/ordering/naming nondeterminism. (Quarantined repo;
  finding recorded, no fix warranted.)
- API-half determinism: two identical FamilySpec docs approved through deployed v28 →
  normalized AFIS jsonb equality TRUE, QA 0.94 = 0.94.
- Corrupted-input safe fail (live): deliberately corrupt PDF uploaded (201 — upload gate is
  size/base64 only), extraction 202 → row `failed` in 1433 ms, error_message names the cause
  ("The PDF specified was not valid" + upstream request id). No other pipeline state touched.
  Debug-without-reproducing: the row carries status, duration, and cause.
- All probe rows deleted afterward (counts pasted).

### Cycle 4 (19:20 → 19:36 UTC) — QA anti-regression rules, deployed v29, proven failing

- runQaPipeline gains P-4 (error): >1 Apex_AfisId parameters — the round-2 duplicate-stamp
  defect can no longer regress silently. Deployed as **api v29**.
- All three fixed-class rules PROVEN TO FAIL live on crafted bad AFIS (validate endpoint,
  findings pasted): P-4 on a double-stamp doc ("2 Apex_AfisId parameters — a duplicate stamp
  would overwrite the family's id at build time"); S-2 on afis_version "2.0.0"; G-1 on a
  0×0×0 bbox. Probes + parents deleted after (25 validations/3 fams/3 exs/3 ups).
- Classes fixed in r2/r3 with NO QA rule, and why: lenient-stamp laundering + correction
  swallowing are pre-AFIS write-path defects — guarded by familySpecProblems and the contract
  tests, invisible to AFIS-level QA by construction; partial-.rfa containment is plugin-side
  (BuildToFile delete + quarantine markers), covered by code + operator check step "what
  failed safely looks like".
- RunQa guardrail respected: engine structure untouched; one rule added to the existing table.

#### RE-EVALUATION (cycles 2/4 checkpoint)

(a) Highest-value path to EXIT? YES — remaining: RUN_MATRIX.md (written 19:40), triple
    verification, close.
(b) Invalidates the starting plan: nothing new — the round was restructured at cycle 1 on the
    round brief's own fallback clause; everything since has followed that split
    (BUILDABLE-HERE / HUMAN-VERIFY-REQUIRED / BLOCKED-ON-OPERATOR).
(c) Avoiding because hard? Journal replay cannot be armed from here (requires one recorded
    run on a real Revit); said so in run-batch.ps1 and the checklist rather than shipping a
    journal line I cannot verify. The alternative one-click path is documented and honest.

### Cycle 5 — triple verification

V1 SELF — claims I cannot back with executed output (explicit list):
1. BatchBuildCommand's in-Revit behavior (dialog-free under APEX_BATCH_DIR, quarantine files
   on a real disk, batch continuing across a real Revit-API failure) — the logic compiles for
   both targets and its Revit-free half is fully tested, but no execution here. Marked
   HUMAN-VERIFY-REQUIRED in RUN_MATRIX.md, checklist step 5–6.
2. BuildToFile's partial-.rfa deletion presumes Revit's SaveAs can leave a partial file worth
   deleting; the delete-on-catch path has never run against a real failing SaveAs.
3. journal-template.txt replay viability — deliberately shipped UNARMED (placeholder + one-time
   recording procedure); cannot be validated from here.
4. run-batch.ps1 — never executed (no Windows/PowerShell in this container); reviewed only.
All four fall inside the HUMAN-VERIFY-REQUIRED block; none is reported as done.

V2 ADVERSARIAL — ship-reviewer launched with the round-brief charge: is any reported number an
artifact of tuning, was the holdout truly untouched (here: truly blocked, not simulated), do
the containment claims match the code. Verdict recorded below on return.

V3 EMPIRICAL — final gate re-run after all edits (pasted): both targets compile with zero
errors, `ALL TESTS PASSED`, `EXTRACTION_SCHEMA PARITY OK`, `REGISTER CHECK PASSED: 16 rows`.
Work pushed as 2b1e672 (11 files, +713/−30) at 19:38 UTC (T0+68min).

#### V2 ADVERSARIAL verdict: **REFUTED** (honesty of rates/holdout SURVIVED; containment claim (e) failed) — disposition

What survived, per the reviewer's own verification: no success rate claimed for unseen inputs
anywhere; zero customer artifacts confirmed independently; holdout genuinely
BLOCKED-ON-OPERATOR, protocol untouched; HUMAN-VERIFY markings intact.

Findings and dispositions (all four code defects FIXED this cycle, rebuilt, suite green):
1. family_name-derived output paths could collide across drawings — a failing drawing could
   DELETE another's finished .rfa (BuildToFile cleanup), or a succeeding one silently
   overwrite it, while the matrix kept reporting SUCCESS. FIXED: batch outputs derive from the
   input filename (unique per folder by construction); matrix carries relative paths only.
2. Bookkeeping writes (jsonl append, quarantine marker) sat OUTSIDE try/catch — a locked file
   or MAX_PATH-length marker name could abort the whole batch, contradicting the checklist's
   flat "NEVER stops" claim. FIXED: all bookkeeping wrapped, failures counted + surfaced in
   the summary; marker names bounded (100 chars + stable hash) with tests; checklist rewritten
   to state the claim as DESIGNED behavior whose first real verification is the operator run.
3. Stale artifacts across re-runs (append-only jsonl, surviving markers, stale out/*.rfa after
   a later validation failure) manufactured exactly the "containment bug" the checklist
   defines. FIXED: fresh-run semantics — jsonl reset with a run header, markers cleared,
   each input's stale output deleted before processing.
4. The mandated two-copy determinism diff could NEVER pass: the matrix embedded the absolute
   batch folder and absolute rfa paths. FIXED: no absolute paths in the matrix (constant run
   label; relative out/ paths; folder recorded in the jsonl header instead).
5. Quarantine marker promised "stack" but wrote message-only. FIXED: full exception detail
   (ToString incl. stack) now in marker + jsonl row.
6. RUN_MATRIX taxonomy conflated API-stage observations with harness observations. FIXED in
   doc: stage-attributed section; explicit statement that the harness's own failure paths have
   executed nowhere and the operator run is their first verification; pre-Revit BadInput slice
   now covered by a suite test.
7. Version labels per matrix row corrected (v27/v28/v29 as measured).
Residual (accepted, stated): commit 2b1e672's message asserts "one bad drawing never aborts
the batch" as fact — history is not rewritten (guardrail); this entry is the correction.

Post-fix V3 gate (pasted): both targets 0 errors, ALL TESTS PASSED (incl. bounded-marker,
stable-hash, pre-Revit BadInput tests) at 19:43:56 UTC.

#### ROUND 3 EXIT status (final)

- Harness: BUILT (code + tests + operator kit), execution HUMAN-VERIFY-REQUIRED — per the
  round brief's fallback clause, not reported as done.
- RUN_MATRIX.md: complete and honest — stage-scoped, every attempted input listed, stage
  attribution corrected after V2.
- Failure taxonomy: implemented + counted where observed; harness-side counts await the
  operator run.
- Determinism: PROVEN for the API half (identical normalized AFIS + QA on double-approve) and
  parser CLI (timestamp-only diff); Revit half is HUMAN-VERIFY with a now-passable procedure.
- Corrupted input fails safely: PROVEN live at the API stage (named cause, 1.4 s, isolated);
  harness stage: pre-Revit slice proven, remainder HUMAN-VERIFY.
- QA coverage per fixed class: P-4 added; P-4/S-2/G-1 each PROVEN TO FAIL live (v29).
- Holdout + headline number: BLOCKED-ON-OPERATOR. THE BLUNT SENTENCE: the true success rate
  of this pipeline on Meta EMT 11990 drawings is UNKNOWN — not one of those drawings has ever
  touched any stage of it, so the 2-month plan is currently resting on zero direct evidence.
- Triple verification: V1 four unbackable claims listed; V2 REFUTED → 4 code fixes + 3 doc
  fixes, honesty core survived; V3 green post-fix.

HANDOFF for round 4: operator owes drawings (through the #11 data-handling gate first),
Sprint 001 fixtures, CVE Revit version, briefing docs, TELEGRAM_BOT_TOKEN. Round-4 accuracy
work is meaningless until real drawings exist; if they arrive, run the batch kit FIRST, then
tune, keeping the holdout sealed.

---

## ROUND 4 — customer-facing add-in: a CVE modeler who has never seen this tool

**T0: Tue Aug 18 19:53:26 UTC 2026** (hard stop 21:53:26 UTC). Brief: ribbon/commands in the
customer's language; batch dialog with per-item progress/result/summary (Revit API single-threaded
— sanctioned patterns only); spec review & override before build; customer-kept build report next
to the .rfa files; one log file per run. Guardrails: NO schema changes, NO new equipment classes;
schema-implying UI needs → ledger debt + route around.

### Cycle 1 (T0 → T0+25) — state check, plan, environment constraints

New-inputs check (ran 19:56 UTC, pasted):

```
$ git fetch origin main && git log origin/main --oneline -1
f588c95 Initial commit: APEX BIM Studio marketing site
$ git rev-parse --abbrev-ref HEAD; git status --short | wc -l
claude/analysis-improvement-fvnun0
0
-- DB (studio project): select count(*), max(created_at) from uploads;
uploads = 7, latest = 2026-08-12 04:14 UTC
```

Same 7 uploads as rounds 1–3; still zero Meta EMT 11990 drawings. Branch clean at d4b0cee.

**Classification of this round's work** (same honesty scheme as rounds 1–3):

- BUILDABLE-HERE: all C# (compiles against Revit API stubs via the scratchpad csc toolchain),
  the Revit-free logic (per-run logging, spec review/override model, build-report generator,
  progress bookkeeping), all tests, the walkthrough checklist document, ribbon text.
- HUMAN-VERIFY-REQUIRED: everything the EXIT names — the clean-Revit-2025 walkthrough (journal
  + run log are the machine evidence; Claude cannot capture GUI screenshots), dialog rendering,
  real mid-batch failure behavior under Revit. A one-page checklist for Hayden is this round's
  deliverable for that.
- The override demo on "a real round-3 parser miss": round 3's parser misses on REAL drawings
  don't exist (no drawings). The nearest REAL artifact is the round-1/2 fixture family whose
  extraction produced a wrong/low-confidence dimension (nq430: `depth` extracted 0.0 with low
  confidence — LEDGER R1 C3). The override demo therefore uses that real extraction artifact
  class: load a pred.json with a zero/absurd dimension + low confidence field → review surfaces
  it → operator corrects → validator accepts → that one item rebuilds. Demonstrated here at the
  model layer with tests; in-Revit demonstrated via the checklist. Logged as a scope statement,
  not silently substituted.

**Threading decision (logged as reasoned design, per the brief's warning):** the brief sanctions
ExternalEvent for modeless→API. A modeless WPF dialog + ExternalEvent pump would be the maximal
version; it is also the highest-risk untestable-here surface (window lifetime vs Revit idling,
re-entrancy). Round-4 choice: **modal, code-built WPF dialogs** (XAML/BAML compilation is
unavailable in this toolchain — pure-code WPF only) with progress rendered between per-item
transactions on the API thread, using Dispatcher frame pumping so the per-item status paints.
That is the brief's "frozen-but-progressing UI with honest per-item status". ExternalEvent
refactor logged as debt below. All Revit API calls stay on the API thread; zero background
threads touch the API.

Build order decided: (1) ApexLog per-run scope → one timestamped log file per run;
(2) SpecReviewModel — Revit-free load→fields-with-confidence→edit→validate→save core (the
override release valve, testable here); (3) customer BUILD_REPORT generator in BatchRunReport;
(4) BatchBuildCommand: confirmation, per-item progress window, per-run log, report; (5) Review
command + full ribbon customer-language pass; (6) TestMain coverage; (7) walkthrough checklist;
(8) verification + register update.

DEBT logged (schema-implying, routed around): the review UI wants per-field provenance (which
page/table of the submittal a value came from) — that is extraction-schema territory (v1.1
field), NOT added this round. Confidence is already in the contract (`confidence` map) and is
used as-is.
