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
