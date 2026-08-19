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

### Cycle 1–2 build log (T0+3 → T0+18, closed 20:11 UTC) — all five work items coded, tested, committed

Commit 1279d84 (pushed). What exists now, with the proof for each brief item:

1. **Ribbon in customer language**: panel "M1" → **Submittals** with Review Submittal / Build
   Family / Batch Build in order of use; tooltips rewritten across Account/Generate/Validate
   (no "AFIS", "OAuth PKCE", "Doc 8", "RFA-generation jobs" in customer-facing text; the one
   deliberate remainder is ".pred.json" in parentheses where the file picker shows that
   extension anyway). Destructive actions confirmable: batch shows a Yes/No dialog naming the
   folder, count, and replace behavior (default **No**); single build confirms replacing an
   existing .rfa; review Save keeps the original as .bak. Operator kit texts updated
   (OPERATOR_CHECKLIST.md step 2, run-batch.ps1 click path).
2. **Batch dialog with per-item progress**: `BatchProgressWindow` (pure code-built WPF — this
   toolchain has no XAML compiler), per-item "Building i of N — file", per-item result lines
   (✓ built / ⚠ built-check-values / ✗ failed+class+message), progress bar, end summary dialog
   with built/failed/check-values counts. Threading per the brief's sanctioned patterns: ALL
   Revit API calls stay on the API thread; the window repaints via a Render-priority
   DispatcherFrame pump between per-item transactions — repaint without input dispatch, so no
   re-entrancy into Revit and no click-during-transaction. Trade-off stated in the pre-run
   dialog ("Revit will be busy"). Progress failures are counted, logged, and never kill the
   batch; scripted (APEX_BATCH_DIR) runs stay headless. DEBT (logged): a truly interactive
   modeless dialog (cancel button, live Revit) needs the ExternalEvent pattern — deferred,
   the modal-but-honest version ships first.
3. **Spec review & override**: `SpecReviewModel` (Revit-free, fully tested here) +
   `SpecReviewWindow` + `ReviewSubmittalCommand`. Shows every extracted value with the
   extraction's confidence; <80% flagged "CHECK" (threshold shared with the report generator
   by a tested constant). Edits limited to values/units — structure (spec_type/group/
   is_instance) stays read-only: a structural miss is an upstream extraction bug, not
   something to hand-patch per drawing. The corrected file goes through the SAME v1
   validator; save REFUSES while invalid; original kept as .bak (first save wins — the .bak
   is always the extraction as delivered). "Save and Build" rebuilds exactly that item after
   the modal closes, on the API thread.
4. **Customer-kept build report**: `BatchRunReport.BuildCustomerReport` writes
   `out/BUILD_REPORT.md` next to the .rfa files: headline counts over ALL drawings, per-item
   equipment name, drawing file, result, size, values set, geometry checks
   (resize/centering), low-confidence values BY NAME, and a what-to-do line per failure class
   (machine problems blamed on the machine, not the drawing). Tested to contain no taxonomy
   jargon and no stack traces.
5. **One log file per run**: `ApexLog.BeginRun` tees every line into
   `run-yyyyMMdd-HHmmss-<name>.log` while the daily log keeps everything; batch, single
   build, and review each open a scope; every summary dialog and the report footer name the
   file ("send me the log" is now one file).

Verification so far (V3-grade, pasted from the suite run at ~20:08 UTC):

```
PASS  review: zero depth is rejected before the override
PASS  review: rejection names geometry.depth.value
PASS  review: depth row borrows the Dimensions parameter's low confidence
PASS  review: save refuses an invalid spec
PASS  review: corrected spec validates
PASS  review: .bak preserves the extraction as delivered
PASS  review: later saves never overwrite the as-delivered .bak
PASS  report: low-confidence threshold shared with the review model (no silent drift)
PASS  report: no taxonomy jargon or stack traces in the customer report
PASS  runlog: nothing lands in the run file after the run ends
ALL TESTS PASSED
```

(43 new assertions total across review/report/runlog sections; both targets compile — net48
"exit: 0" with zero error lines, net8 likewise.)

**The override demo on a real parser-miss class (EXIT item 2), status**: demonstrated at the
model layer end-to-end IN THIS ENVIRONMENT — a spec with `geometry.depth.value = 0` and
confidence 0.31 (the round-1 nq430 depth-miss class) is rejected by the validator naming the
field, surfaced as CHECK in the field list, corrected via TrySet, revalidated, and saved with
the original preserved. The in-Revit click-path version of the same demo is on the human
walkthrough checklist (HUMAN-VERIFY-REQUIRED — no Revit here).

### Cycle 2 re-evaluation (protocol checkpoint, 20:11 UTC)

Q: is the plan still the highest-value path? Assessment against the brief's goal ("a CVE
modeler who has never seen this tool… without calling Hayden"):

- Biggest remaining risk is NOT more code — it is that nobody has walked the path in a real
  Revit. The one-page walkthrough checklist for Hayden is therefore next, before any polish.
- The forced-mid-batch-failure EXIT item: the Revit-free slices (corrupt file → BadInput →
  quarantine name → report text → jsonl line) are all proven by the suite; what remains is
  genuinely Revit-bound and goes on the checklist as its own step with expected artifacts
  listed.
- DEFERRED as debt (would not survive a "is this the best use of the remaining hour?" test):
  ExternalEvent modeless progress; icons; a review grid that edits units inline for
  dimensions (unit edits work via the model; the window exposes value edits — acceptable
  because unit errors are named by the validator and correctable in the portal). Course
  unchanged otherwise.

### Cycle 3 (T0+18 → T0+45) — toolchain integrity incident, negative control, sample report, V1

**Toolchain incident (found by the negative control, fixed, evidence below).** The V3 negative
control (deliberately desync the report threshold 0.8→0.7, expect the suite to fail) FAILED to
revert cleanly: the suite kept failing after the constant was restored. Root cause: the tests
build referenced BOTH WindowsBase.dll from the core net8 ref pack (16 KB compat stub, no
types) AND the real one from the WindowsDesktop pack (106 KB) — csc's identity resolution for
the duplicate went flaky (CS7069) once the new WPF windows joined the compile, and the build
script's `| head` swallowed csc's exit status, so failed compiles silently re-ran the STALE
test dll. Consequence for earlier claims: the plugin dlls were genuinely written by csc at
20:08 (a dll only appears on successful compile — verified by mtime), but intermediate suite
runs in cycle 2 may have executed a stale binary. Fix (scratchpad build scripts, not repo):
skip the stub WindowsBase, write csc output to a log, abort BEFORE running tests if the
compile fails, and delete the previous test dll first so a stale run is impossible. Then
everything was re-proven from fresh compiles:

```
net48 csc exit: 0 (dll present: yes)
net8 csc exit: 0 (dll present: yes)
$ build-tests | grep -c ^PASS         -> 165
$ build-tests | grep FAIL|ALL TESTS   -> ALL TESTS PASSED
```

**Negative control, re-run with guaranteed-fresh compiles (pasted):**

```
[threshold 0.7]  FAIL  report: low-confidence threshold shared with the review model (no silent drift)
                 FAIL  report: low-confidence values surfaced by name with the threshold
                 2 FAILURES
[reverted 0.8]   ALL TESTS PASSED
```

The round-4 tests are proven able to fail. Working tree confirmed clean after revert
(`git diff --stat` empty).

**Sample customer report generated from the real generator** (Revit-free demo harness feeding
`BuildCustomerReport` rows that mirror the walkthrough scenario): committed as
`docs/ship/SAMPLE_BUILD_REPORT.md` (commit b9f4510). One defect found by reading the output as
a customer — headline grammar "1 built but list values" — fixed and re-verified in the same
commit. RUN_MATRIX gains rows 12–16 (round-4 executed section).

**Walkthrough checklist shipped**: `revit-plugin/deploy/WALKTHROUGH.md` — one page, ~20 min,
setup → batch (incl. two planted failures) → override demo (zero-depth class) → honest-failure
spot checks (mid-batch failure message quality; template-missing machine blame) → the exact
evidence bundle to send back (newest Revit journal, run-*.log files, BUILD_REPORT.md,
RUN_MATRIX.md, quarantine markers). Every checkbox is HUMAN-VERIFY-REQUIRED.

**V1 self-review — claims I cannot back from this container, stated before V2:**

1. The Render-priority dispatcher pump repaints without input re-entrancy UNDER REVIT'S
   message loop — reasoned from WPF dispatcher semantics, never executed in Revit. Walkthrough
   step 4 is the check; if the window never paints, the batch still completes (paint failures
   are caught, logged, counted).
2. Whether a first-time modeler actually understands the dialogs/report is an empirical UX
   question — walkthrough step 11 asks it explicitly instead of assuming it.
3. TaskDialog rendering with long MainContent strings (confirmation + summary dialogs) is
   unverified visually.
4. Window ownership via MainWindowHandle across Revit versions — try/catch'd; degraded mode is
   center-screen, not a crash.
5. "6 of 8 build" in the walkthrough is an EXPECTATION for the golden set on a configured
   machine, not a claim; per-drawing truth comes from the operator's RUN_MATRIX.
6. The suite exercises the review MODEL, not the WPF window's event handlers; the window's
   Apply/Save wiring is code-reviewed only (thin: ApplyEdits→TrySet, SaveNow→TrySave).

V2 launched: ship-reviewer charged as a hostile first-time CVE modeler walking load → select →
review → build → read report, given ONLY the brief's criteria + the artifacts (code, walkthrough,
sample report, tests) — no author conclusions.

### Cycle 4 (T0+45 → T0+55) — V2 verdict: REFUTED; all findings fixed or dispositioned

V2 (ship-reviewer, hostile first-time CVE modeler, given only criteria + artifacts) returned
**REFUTED** with 10 findings. This is the third round in a row where V1+V3 passed and V2 found
real defects — the design works. Verbatim core of the verdict: "the round's own walkthrough
input set produces 'geometry did not verify — treat it as suspect' on five of the six golden
drawings…, the sample report fabricates 'all passed' for rows that cannot pass, and the first
click of the walkthrough is on a ribbon button that is disabled at Revit's zero-document start
screen."

Disposition, finding by finding (fixes in commits 54e064b + follow-up label commit):

1. **Flex checks fail on 5/6 goldens; sample fabricated "all passed" — CONFIRMED, FIXED.**
   Root cause: labeled dimensions need family parameters named Apex_Width/Depth/Height that
   most specs don't carry. `EnsureDimensionParameters` now creates+values them from the
   geometry inside the build transaction (builder behavior — NOT a schema change), so the
   parametric-box promise no longer depends on the extraction's parameter list. Built-but-
   suspect rows now carry a What-to-do line. The committed sample was regenerated from rows
   mirroring the 8-file walkthrough under a loud "ILLUSTRATIVE — synthetic results, no Revit
   ran" banner. Whether the auto-created parameters actually flex is in-Revit truth:
   walkthrough step 6 now records any FAILED checks instead of promising none.
2. **Dead first click at the start screen — CONFIRMED (standard Revit behavior), FIXED.**
   `CommandAlwaysAvailable` (IExternalCommandAvailability) registered on the three Submittals
   buttons; walkthrough step 1 now explicitly verifies clickability at zero documents and
   calls a grey button ship-blocking. Operator checklist claim corrected to "v0.4.0+".
3. **Containment deleted pre-existing families; review build unconfirmed — CONFIRMED, FIXED.**
   `BuildToFile` deletes the output ONLY after an attempted save (`attemptedSave` guard);
   review Save-and-Build got a Yes/No overwrite confirmation (default No); the failure dialog
   now says "No NEW family file was written", which is what is true.
4. **Override output landed outside out\ and the kept report went stale — CONFIRMED, FIXED.**
   Review rebuilds now write to out\ with the input-stem name (same collision-proof rule as
   the batch) and append a dated UPDATE line to out\BUILD_REPORT.md superseding the stale row.
5. **Suspect families read as plain success — CONFIRMED, FIXED.** One shared predicate
   (`BatchRunReport.NeedsReview`, tested) now drives the report headline, the progress ⚠, and
   the summary count; failed geometry checks can no longer hide inside "✓ built".
6. **Confidence-borrow missed Apex_ names; two rows both called "Depth"; half-fix trap —
   CONFIRMED, FIXED (with one accepted residual).** Name matching strips the Apex_ prefix
   (tested); geometry rows are labeled "Overall width/depth/height"; new
   `ConsistencyWarnings()` names a geometry-vs-parameter disagreement after a half-fix
   (tested; surfaced on Check values AND after save; walkthrough step 8 makes the modeler fix
   both rows and verify the warning). Residual (schema guardrail): parameter VALUES are still
   not range-checked by the validator — that is v1.1 schema semantics; the consistency warning
   covers the dimension class that bit round 3.
7. **Jargon reached the screen/report — CONFIRMED, FIXED (with a stated wording decision).**
   Validator messages carry no repo paths ("schemas/familyspec/…") or internal codenames
   ("shop2revit") anymore; the Build Family tooltip no longer says ".pred.json"; the report
   jargon-guard test now also bans repo paths/codenames. Decision, stated: the word "spec"
   (as "equipment spec") and named fields like geometry.depth.value STAY — the first is plain
   English for the thing, the second is the pointer the fix needs; both reviewed as
   comprehensible without training.
8. **Forced failure never reached the Revit stage — CONFIRMED, FIXED in the walkthrough.**
   New step 9: set a built out\*.rfa read-only, re-run the batch — a genuine in-Revit write
   failure mid-batch, expected to quarantine as a machine-class failure while the rest
   continue. Execution HUMAN-VERIFY-REQUIRED like the rest of the walkthrough.
9. **Threading claim overstated — CONFIRMED (the reviewer is right about pushed frames
   pumping the whole thread's message loop), FIXED.** Revit's main window is now DISABLED for
   the run (user32 EnableWindow — the same owner-disable semantics a modal dialog gets) and
   re-enabled in a finally BEFORE any dialog; the progress window refuses to close mid-run;
   the "(Not Responding)" ghosting possibility is stated in the window header and walkthrough
   step 4; comments now describe the real mechanism. In-Revit confirmation: walkthrough.
10. Smaller: committed demo fixtures replace Notepad surgery (a+f; zz-depth-miss carries a
    low-confidence Depth so the CHECK-flag demo is real, and its own warning states the true
    depth); last "M1" reference fixed (b); units now editable in the review grid (c);
    category label says "(from the submittal)" and the template mapping gap is DEBT below
    (d); APEX_BATCH_DIR hijack risk accepted as the scripted-mode switch, with an explicit
    "unset after scripted runs" operator step (e).

DEBT added this cycle: category-driven template selection (hidden-ahu9 is "Mechanical
Equipment" and silently builds on the electrical template — needs a Revit machine to build a
category→template map safely); validator range semantics for parameter values (v1.1).

Suite after fixes: 182 assertions, ALL TESTS PASSED, both targets compile (fresh-compile
guaranteed). Windows CI green through commit b9f4510; runs for 54e064b+ pending at write time.

**Cycle 4 re-evaluation (protocol checkpoint):** V2's findings were concentrated exactly where
V1 predicted ignorance (in-Revit behavior) plus one class V1 missed entirely (the fixtures'
parameter lists vs the flex checks — an integration seam between two rounds' code). Remaining
time goes to: V2 re-verification of the fixes (fresh hostile pass), final V3 gate, EXIT
entry + handoff. No scope changes.

### Cycle 5 (T0+55 → T0+70) — V2 re-review: REFUTED again; second fix pass

The re-verification pass (fresh hostile reviewer, charged to refute the FIXES) confirmed 7 of
the 10 fixes as present and coherent — including tracing every exception path around the
EnableWindow change and finding "no reachable path that leaves Revit disabled" — and REFUTED
on the rest. Its core sentence: "the forced-failure fix (claim 8) is specified against a
failure classification the shipped classifier cannot produce and manufactures the exact
on-disk state the operator checklist defines as a reportable containment bug."

Disposition (fixes in commit fb5fbe3, suite now 199 assertions, ALL TESTS PASSED):

1–2. **Forced-failure class mismatch + containment-bug paradox — CONFIRMED, FIXED
   deterministically.** Instead of guessing which exception Revit's SaveAs throws at a
   read-only target (unknowable from here), the batch now fails the item BEFORE any Revit
   call: an unremovable stale output (IOException/UnauthorizedAccessException on the
   fresh-run delete) is a hard Environment failure whose message names the surviving old
   file and the fix. Walkthrough step 9 now expects exactly that message; the operator
   checklist carves out this one legitimate FAIL-row-with-a-file case. (Linux cannot
   reproduce read-only-delete semantics — Windows denies, POSIX allows via directory
   permissions — so this path's execution is HUMAN-VERIFY step 9; the classification logic
   itself is plain .NET, reviewed.)
3. **Consistency warning invisible on the default Save-and-Build path — CONFIRMED, FIXED.**
   A non-empty ConsistencyWarnings now pops a Yes/No (default No) with the disagreement on
   screen before the build; No keeps the review open.
4. **Sample contradicted the fixtures it claimed to mirror — CONFIRMED, FIXED at the root.**
   The generator now READS the committed fixtures: names, sizes, parameter counts,
   low-confidence flags, and both failure messages are produced by the real
   parser/validator; only build outcomes are synthetic and the banner says exactly which.
   (The real corrupt-file message differs from the invented one — "0x0A is invalid within a
   JSON string", not the guessed text — proving the reviewer's point.)
5. **EnsureDimensionParameters overwrote spec-supplied values — CONFIRMED, FIXED.** Existing
   parameters are left untouched (logged); geometry-vs-parameter disagreement is now also
   surfaced in BATCH runs (counted into needs-review + logged with field names), so the
   mismatch class is visible outside the review window too.
6. **Enum names on the progress window — CONFIRMED, FIXED** via
   `BatchRunReport.CustomerClass` (tested for all classes); RUN_MATRIX/jsonl keep the
   taxonomy by design (operator artifacts).
7. **Tautological test — CONFIRMED, FIXED** with explicit clean-row negative +
   low-confidence positive cases.
8. **Unit/group-blind consistency check — CONFIRMED, FIXED.** Comparison in feet via
   UnitConv (610 mm vs 24 in agree; tested), Dimensions-group only (Electrical "Width"
   ignored; tested), tolerance 0.005 ft.
9. **"Review saved" dialog routed around fixes 3/4 — CONFIRMED, FIXED**: it now points to
   Save-and-Build / Batch Build (both land in out\ and keep the report current).
10. **journal-template M1 — CONFIRMED, FIXED** (the ledger's earlier "last M1 fixed" claim
   was wrong; this entry corrects it).

Residuals stated by the reviewer and accepted with reasons: EnableWindow disables Revit's
MAIN frame only — undocked view windows are separate top-levels (in-Revit truth; walkthrough
step 4 observes overall behavior); template built-ins (Manufacturer/Model) may make
ParamsValued exceed ParamsAdded in real runs (display nuance, operator matrix will show it);
"whether the auto-created Apex_* parameters actually flex" remains the walkthrough's job.

Third V2 pass launched, scoped to exactly these 10 fixes + regressions they could introduce.

### Cycle 6 (T0+70 → T0+80) — third V2 pass, final fixes, V3 gate

Third V2 pass (scoped to the second-pass fixes): **9 of 10 claims verified as present and
coherent** — including byte-checking the sample's failure text against the committed corrupt
fixture with `od`, and confirming the walkthrough/checklist/message wording all agree on the
forced-failure scenario. **REFUTED on exactly one finding**: the SIBLING "review saved" dialog
(the No-on-replace-confirm path) still said "Run Batch Build (or Build Family) when ready" —
the routing defect claimed fixed, alive in a second dialog the fix commit never touched. Two
minor: the sample banner mislabeled "Values set" counts as real (they are build outcomes), and
`JsonNode.Parse` throws ArgumentException on duplicate keys (JsonDocument tolerates them) —
swallowed in the batch but an unhandled crash in the review command.

All three fixed in commit 987c3bf: sibling dialog routes to Save-and-Build/Batch Build
(grep: 0 remaining "Build Family) when ready", 2 Save-and-Build routings); `SpecReviewModel.Load`
catches ALL parse failures → LoadError (duplicate-key regression test added); banner moves
"Values set" to the synthetic side with the valued-can-exceed-added explanation. The one-line
dialog fix was verified by direct inspection + grep + suite rather than a fourth adversarial
pass — proportionate to a single string change; stated here rather than hidden.

**Final V3 gate (pasted, 21:09 UTC):**

```
net48 csc exit: 0 (dll present: yes)
net8 csc exit: 0 (dll present: yes)
ALL TESTS PASSED        (200 assertions, fresh compile guaranteed)
$ python3 docs/ship/check_register.py
REGISTER CHECK PASSED: 16 evidenced rows, T0 present, no banned words, coverage terms present.
```

Independent toolchain: **Windows CI (real SDK + WPF, runs the test project) green through
run 24 = commit fb5fbe3**; run 25 (987c3bf, one dialog string + one catch clause + banner
text) in progress at write time — result to be confirmed before round close below.

#### ROUND 4 EXIT status

Brief's goal: a CVE modeler who has never seen this tool can load a submittal, build
families, understand what happened, and fix what failed — without calling Hayden.

- **Work item 1 (ribbon/commands, customer language, confirmable destruction): BUILT.**
  Submittals panel (Review Submittal / Build Family / Batch Build) with zero-document
  availability; jargon purged from tooltips, dialogs, validator messages (repo paths and
  codenames removed; tested); batch/single/review overwrites all confirmable (default No).
- **Work item 2 (batch dialog, per-item progress/result/summary): BUILT.** Per-item ✓/⚠/✗
  with one shared needs-review definition; honest threading: API calls never leave the API
  thread, Revit's window disabled for the pumped run (modal semantics), close-guarded window,
  ghosting stated. ExternalEvent modeless UI remains logged DEBT.
- **Work item 3 (review & override): BUILT + PROVEN at the model layer.** Confidence-flagged
  field grid (Apex_-aware), value+unit edits, validator-gated save with as-delivered .bak,
  unit-aware consistency cross-check on Check values / Save / Save-and-Build, rebuild of
  exactly that item into out\ with a report UPDATE line.
- **Work item 4 (customer-kept report): BUILT + sample committed** (fixture-true inputs,
  synthetic outcomes labeled) — per-item what-was-built/from-which-drawing/with-which-values/
  which-checks-passed + what-to-do per failure class, no jargon (tested).
- **Work item 5 (one log per run): BUILT + PROVEN** (lifecycle tested; every dialog and the
  report footer name the file).
- **EXIT 1 (clean-Revit-2025 walkthrough evidenced by journal + run log):
  HUMAN-VERIFY-REQUIRED by design** — `revit-plugin/deploy/WALKTHROUGH.md` (12 checkboxes,
  ~25 min, evidence bundle specified). No walkthrough has executed; nothing here claims it
  has.
- **EXIT 2 (override on a real round-3 parser miss): demonstrated on the real miss CLASS**
  (zero dimension + low confidence — the nq430 depth-miss class; no real CVE drawings exist
  anywhere, per every round's new-inputs check), end-to-end at the model layer in the suite,
  in-Revit via walkthrough steps 7–8 with the committed demo fixture.
- **EXIT 3 (forced mid-batch failure → clear message + usable log): the Revit-free slices are
  PROVEN** (classification, message text, quarantine naming, per-run log capture — suite);
  the deterministic read-only path is BUILT with checklist/walkthrough coherence; in-Revit
  execution is walkthrough step 9.
- **Triple verification: V1 (C3, six unbackable claims), V2 ×3 (REFUTED → 10 findings fixed;
  REFUTED → 10 more fixed; REFUTED → 1 + 2 minor fixed) — the hostile-first-time-user charge
  produced real defects every pass, including one (flex checks failing on 5/6 fixtures) that
  invalidated this round's own first sample artifact. V3: 200 assertions green from fresh
  compiles + independent Windows CI.**

THE BLUNT SENTENCE, unchanged in kind from round 3: everything above is code-and-document
truth; not one pixel of this UX has been seen by a human, and the walkthrough bundle
(journal + run logs + report) is the only thing that converts this round's claims into
customer-facing facts.

HANDOFF for round 5 (and operator):
- Hayden: run `revit-plugin/deploy/WALKTHROUGH.md` on clean Revit 2025 (~25 min) and send the
  bundle; that closes rounds 3–4 HUMAN-VERIFY items in one sitting.
- Operator still owes: CVE drawings via data gate #11, Sprint 001 fixtures, CVE's pinned Revit
  version, briefing docs, TELEGRAM_BOT_TOKEN, shop2revit archive.
- DEBT carried: ExternalEvent modeless progress; category→template mapping (hidden-ahu9 is
  Mechanical Equipment silently built on the electrical template); parameter-value range
  semantics (schema v1.1); undocked-view-window EnableWindow coverage; per-field provenance
  (v1.1).
- Round 5 as briefed (packaging/rehearsal) should consume the walkthrough results FIRST.

**Round close confirmation (21:12 UTC):** Windows CI run 25 (commit 987c3bf) = completed,
success (run 32186260537, started 21:09:46Z, finished 21:10:58Z). Every round-4 commit —
1279d84, b9f4510, 54e064b, 61104a5, fb5fbe3, 987c3bf — is green on the authoritative
windows-latest toolchain (real .NET SDK + WPF, test project executed). ROUND 4 CLOSED at
T0+79 min, inside the 120-min stop.

---

## ROUND 5 — signed, installable, licensed build + rehearsal + go/no-go

**T0: Tue Aug 18 21:26:39 UTC 2026** (hard stop 23:26:39 UTC). Brief: installer verified OUTSIDE
the dev machine (or absence flagged as ship blocker); Ed25519 licensing exercised in all four
states (valid/expired/tampered/missing); release tagged with hashes + rollback procedure; timed
dry-run rehearsal on the packaged build; quickstart + known-limitations (with the REAL round-3
holdout rate); final re-evaluation with an unhedged go/no-go.

### Cycle 1 (T0 → T0+25) — state, scope honesty, licensing build begins

New-inputs check (21:26–21:28 UTC, pasted):

```
$ git log origin/main --oneline -1        -> f588c95 (unchanged)
$ git status --short | wc -l              -> 0 (clean at 4c63eb3)
-- DB: uploads = 7, latest = 2026-08-12 04:14 UTC (unchanged since round 1)
```

Still ZERO Meta EMT 11990 drawings anywhere. That fact will dominate the go/no-go.

**Scope honesty, stated before any work — what this environment can and cannot deliver:**

1. **Installer**: BUILDABLE-HERE (scripts, packaged zip, dependency resolution, version stamp,
   SHA256 manifest, uninstall). NOT verifiable here: no Windows, no Revit, no clean VM, no
   fresh user profile exists in this container. Per the brief's own rule, this is stated now:
   **the installer will be UNTESTED and that is a named ship blocker** until a human runs it
   on a clean machine (verification steps will be appended to the walkthrough kit).
2. **Licensing**: the round-1 register (row 8) proved Ed25519 licensing DOES NOT EXIST in any
   reachable code (zero grep hits) and said round 5 must either build it or ship without it.
   This brief orders the flow exercised — read as the decision to BUILD it. Ed25519 is absent
   from the .NET BCL on both targets, so a vetted library (BouncyCastle, netstandard2.0) will
   be fetched from NuGet. The whole flow is Revit-free → all four states are provable HERE
   with real keys in the test suite. Private signing key goes to the service-role-only
   app_config table (public repo — no secrets in git); a TEST keypair (clearly marked) is
   committed for the suite.
3. **Dry-run rehearsal "on the packaged build"**: the packaged CLOUD half (deployed api v29 —
   that IS the production build) can be rehearsed and timed from here live; the Revit half
   cannot (no Revit). Deliverables: timed cloud-half rehearsal with friction log + the
   run-of-show/rehearsal script for the full flow; Revit-half timing is HUMAN-VERIFY via the
   existing walkthrough.
4. **Known-limitations "real holdout success rate"**: the real value is **NEVER MEASURED** —
   the round-3 holdout is BLOCKED-ON-OPERATOR with zero drawings. The limitations page will
   say exactly that instead of inventing a number (honesty rule).

Build order: (1) Ed25519 licensing (ApexLicense + gate + signer tool + 4-state tests);
(2) installer package (install/uninstall.ps1, deps resolved incl. net48 STJ chain, version
stamp, SHA256SUMS, packaged zip built here); (3) tag + hashes + rollback doc; (4) timed
cloud rehearsal + rehearsal script; (5) quickstart + limitations; (6) final re-evaluation +
go/no-go; (7) V1/V2("find the assumption that only holds on your machine")/V3.

### Cycle 1 close (wall clock 22:10 UTC — includes two operator-interrupt pauses; T0 21:26)

Licensing BUILT and PROVEN in all four states. Evidence pasted:

```
PASS  license: valid file -> Valid                       PASS  license: tampered file -> Invalid
PASS  license: expired file -> Expired                   PASS  license: tampered message names the cause
PASS  license: the time gate is the clock, not the file  PASS  license: garbage -> Invalid, no throw
PASS  license: test-key license rejected by the production key (keys are distinct)
PASS  license: correctly signed wrong-product license rejected with its own message
PASS  license: in-suite sign -> verify round trip
PASS  license: no file on this machine -> Missing
PASS  license: beside-DLL search finds the file; test-key content still rejected
ALL TESTS PASSED        (both targets compile; net48 CS1701 unification warning only)
```

Production key custody: signing key + public key + two issued licenses (CVE trial to
2026-11-30, Apex dev to 2027-12-31) stored in service-role-only app_config
(license_signing_key_ed25519 / license_public_key_ed25519 / license_cve_trial /
license_apex_dev — lengths 44/44/309/301 confirmed by SQL; stored CVE license md5
1dfd4c11de0a3076235f78cc56cdda58 = local file md5, byte-identical round trip). Both issued
licenses verified Valid against the EMBEDDED production key by a compiled checker:

```
cve-trial.apexlic: Valid — Cache Valley Electric — Meta EMT 11990 trial until 2026-11-30
apex-dev.apexlic: Valid — Apex internal — Hayden dev/walkthrough until 2027-12-31
```

PROVEN: all four license states + gate code compiled into both targets. ASSUMED (named):
TaskDialog rendering of the messages, and gate behavior inside real Revit — walkthrough gains
a license step. Next: installer package (cycle 2).

### Cycle 2 close (20:50 UTC) — installer built; UNTESTED-INSTALLER named as ship blocker

Installer kit committed (revit-plugin/deploy/installer/): integrity-gated install.ps1,
uninstall.ps1, make-package.sh, README.txt. Package ApexBimStudio-0.5.0-rc1.zip ASSEMBLED here
with full dependency sets and hashes:

```
net48/: ApexBimStudio, BouncyCastle, System.Text.Json + 8-dll dependency chain (Memory,
        Buffers, ValueTuple, Numerics.Vectors, Tasks.Extensions, CompilerServices.Unsafe,
        Bcl.AsyncInterfaces, Encodings.Web)
net8/:  ApexBimStudio, BouncyCastle, ProtectedData
zip sha256: 68337587bb9556a363bd89fb450692db995c51f25257026e9efe28783e4e9f88
$ sha256sum -c SHA256SUMS.txt  -> all OK
negative control: 1 byte flipped in net8/ApexBimStudio.dll -> "FAILED ... did NOT match";
restored -> all OK
```

**STATED PLAINLY, per the brief's own rule: NO clean Windows environment, VM, or fresh user
profile exists in this container — this installer has NEVER been executed. An untested
installer is a SHIP BLOCKER until walkthrough step 1 runs on a real machine.** Two further
honesty notes: (a) the zip above is assembled from this container's csc builds — the CANONICAL
customer package must be assembled by make-package.sh from the Windows CI artifacts of the
tagged commit (procedure in the rollback doc, next cycle); (b) install.ps1 is PowerShell
running on Windows — nothing here can execute it; its logic was verified only by the
sha256sum equivalence above.

RE-EVALUATION (protocol, cycle 2): (a) still highest-value path — yes: licensing and installer
were the two EXIT items buildable here; remaining EXIT items are docs + tag + rehearsal +
verdict, all planned. (b) learned: BCL has no Ed25519 → BouncyCastle dependency now ships with
the add-in (installer accounts for it); www.nuget.org is proxy-blocked but api.nuget.org
works. (c) avoiding nothing identified; the uncomfortable item (go/no-go against shipping) is
scheduled for cycle 4 and will be answered without hedging.
