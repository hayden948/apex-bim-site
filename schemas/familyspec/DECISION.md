# FamilySpec v1 — canonical contract decision (Round 2)

## The decision

The canonical interchange contract for "what a drawing extraction says about a family" is the
**pred shape**, formalized here as **FamilySpec v1**:

- Schema of record: `schemas/familyspec/familyspec.v1.schema.json` (this directory).
- Required `schema_version: "1.0"` field. **Every future change is breaking** (Sprint 002
  policy as relayed by the round brief): any shape change increments the major version, gets a
  new schema file (`familyspec.v2.schema.json`), and readers reject versions they don't know.
- File extension stays `.pred.json` (the name the tooling and operators already use; renaming
  artifacts is churn with no integrity payoff).

## Status of the other two shapes (the fork being closed)

1. **`family_spec.schema.json` + `models.py`** (apex-parser-service): NON-CANONICAL,
   quarantined by decision. Round-1 evidence: nothing in either repo reads FamilySpec output
   (LEDGER Round 1 Cycle 2, live-path trace — zero grep hits for "pred" in the parser repo,
   zero readers of `.spec.json` anywhere). The parser service's HTTP adapter is a stub. The
   parser repo is attached read-only in this environment, so the physical move to a
   `legacy/` folder is an operator follow-up; until then this document and the ship register
   are the quarantine. Any future revival of that parser MUST emit FamilySpec v1, not the mm
   dict shape.
2. **AFIS 1.0** (`AfisModels.cs`, `predToAfis()` in `supabase/functions/api/index.ts`): NOT a
   competing interchange contract — it is the DERIVED build artifact, produced from a
   FamilySpec at approve time. It is already versioned (`afis_version: "1.0.0"`), has exactly
   one writer (`predToAfis`) and two readers (plugin mapper, API QA engine). It stays as-is
   this round (RunQa guardrail).

## Why the pred shape wins

- It is the shape the LIVE pipeline already emits and consumes end-to-end: Claude structured
  output (`EXTRACTION_SCHEMA`), the console's review/correction UI, the approve endpoint's
  `predProblem` gate, `predToAfis`, and the plugin's local `BuildFromPredJsonCommand`.
  Round 1 proved this is the only connected path.
- It is the shape of every existing artifact we can use as a fixture (repo example, live DB
  `claude_result` rows, and — per the operator — the Sprint 001 M1 inputs on the Windows
  machine are `.pred.json`).
- The alternative (parser-repo FamilySpec) has richer structure (BOM, appurtenances,
  connectors, mm units) but zero consumers, a stubbed service, and a June-frozen repo.

## What this choice costs (stated per the round brief)

- The parser repo's richer fields (bill_of_materials, appurtenances list, per-spec confidence
  provenance, connector details) are NOT expressible in FamilySpec v1. If switchgear/vault
  work later needs them, that is a v2 design discussion — not a silent extension.
- mm-first geometry is gone; v1 is unit-tagged per dimension (in/mm/cm/m/ft) with inches as
  the documented default convention for hand-authored files.
- The one extractor in the parser repo (transformer_padmount) emits a dead shape until someone
  ports its output to v1 (deliberately NOT done this round — read-only repo, and porting a
  stubbed service is round-3+ scope if the operator wants P-B alive at all).
- Legacy accommodation (documented, deliberate, the only one): files/rows written before v1
  lack `schema_version`. Validators treat a MISSING version as "legacy v0", emit a visible
  warning naming the file, and stamp `"1.0"` on the stored copy where they own storage (API).
  An UNKNOWN version (e.g. "2.0") is a hard reject. This keeps the Sprint 001 fixture net
  usable; it is not a silent default — the warning names the file and the field.

## Enforcement points installed this round

| Boundary | Mechanism |
|---|---|
| Schema ↔ C# DTOs | Contract test in `revit-plugin/tests/TestMain.cs` reflecting `[JsonPropertyName]` attrs against the schema file's properties/required/enums — fails the suite on any divergence (codegen judged not viable in-round: no offline NJsonSchema toolchain in the container; recorded as debt) |
| Schema ↔ API `EXTRACTION_SCHEMA` | `schemas/familyspec/check_extraction_schema.py` extracts the TS literal and deep-compares to the schema file — run in CI/test loops |
| Extraction output (before write) | `api/index.ts` validates the model's result against the v1 rules before storing `claude_result`; invalid → extraction `failed` with a named-field message |
| Correction input (before write) | approve body `{result}` validated the same way; invalid → `400 INVALID_CORRECTION` naming field + value |
| Add-in input (before build) | `PredValidator.cs` runs before any Revit API call; invalid → dialog naming file + field + expected/got, never a stack trace |
| Fixtures | `schemas/familyspec/fixtures/golden/` (valid, round-tripped) + `fixtures/malformed/` (each with its expected named-field error, asserted by the test suite) |
