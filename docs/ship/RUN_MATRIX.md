# Round 3 run matrix — what was actually executed, on what, with what result

Scope honesty, stated first: **zero Meta EMT 11990 drawings are reachable from this
environment** (new-inputs check pasted in LEDGER Round 3 Cycle 1), and **Revit cannot execute
here** (Linux container, no Revit — LEDGER Round 1). Therefore:

- Every row below names its exact input set. Nothing here is a claim about unseen drawings.
- The **build stage** (AFIS/pred → .rfa in Revit) appears only as HUMAN-VERIFY-REQUIRED, with
  the operator kit that produces its real matrix (`revit-plugin/deploy/batch/`). When the
  operator runs it, the per-drawing matrix this file's title promises is generated as
  `RUN_MATRIX.md` in the batch folder by `BatchBuildCommand` — that file, on real drawings,
  supersedes this one for build-stage claims.
- The **holdout** (work item 0b, minimum 3 drawings) is **BLOCKED-ON-OPERATOR**: with zero
  customer drawings there is nothing to hold out. No holdout was simulated. The holdout
  procedure the operator must follow is in `OPERATOR_CHECKLIST.md` steps 4 and 8.

## Executed here (live, deployed pipeline api v29 unless noted) — every attempt listed

| # | stage under test | input | result | evidence (LEDGER R3 / R2) |
|---|---|---|---|---|
| 1 | validate (FamilySpec v1) | 6 golden fixtures | 6/6 accepted | R2 C1 suite output (re-run R3) |
| 2 | validate (rejection + named-field msg) | 6 malformed fixtures | 6/6 rejected, each names its field | R2 C1; asserted per-fixture in suite |
| 3 | approve→AFIS→QA round-trip | 6 golden fixtures | 6/6 families, QA 0.88–1.0, AFIS content-identical to committed goldens | R2 C3 (6/6 jsonb equality) |
| 4 | determinism (API half): same input approved twice | Determinism Probe DP-1 (×2 identical) | normalized AFIS identical (`true`), QA 0.94 = 0.94 | R3 C3 |
| 5 | determinism (parser CLI, quarantined repo) | nq430-submittal.pdf ×2 | content-identical; ONLY diff = `extracted_at` timestamp | R3 C3 (diff pasted) |
| 6 | corrupted input fails safely | deliberately corrupt PDF (`%PDF` header, garbage body) | upload 201 → extraction `failed` in 1433 ms with cause ("The PDF specified was not valid", upstream request id); no other row affected | R3 C3 |
| 7 | correction failure containment | broken JSON body; typo'd `Result` key | 400 BAD_JSON / 400 MISSING_RESULT — never silently approves | R2 C5 (v28 retests) |
| 8 | QA anti-regression: P-4 duplicate stamp | crafted AFIS, 2× Apex_AfisId | P-4 FAILS naming the defect | R3 C4 |
| 9 | QA anti-regression: S-2 unknown version | crafted AFIS, afis_version 2.0.0 | S-2 FAILS | R3 C4 |
| 10 | QA anti-regression: G-1 degenerate bbox | crafted AFIS, 0×0×0 bbox | G-1 FAILS | R3 C4 |
| 11 | batch report engine (matrix, taxonomy, jsonl, quarantine naming, no-cherry-pick denominator, empty batch) | synthetic rows | all asserts pass (suite) | R3 C2 |

Success counts above are exhaustive over their input sets; no attempted input was omitted.

## HUMAN-VERIFY-REQUIRED (code shipped + kit shipped; execution needs the Revit machine)

- Per-drawing build results, wall times, and build-stage failure counts (`BatchBuildCommand`
  writes them; nothing here can run it).
- Revit-side determinism (double run per OPERATOR_CHECKLIST step 7; .rfa files are expected to
  differ at byte level — Revit embeds GUIDs/timestamps — outcome columns must not).
- The plugin's named-field failure dialog rendering, quarantine files on a real disk, and the
  journal-replay path (needs the one-time recording, see run-batch.ps1 header).

## BLOCKED-ON-OPERATOR (cannot be produced from here at all)

- The Meta EMT 11990 batch, its failure taxonomy on real drawings, and the blind-holdout
  success rate — **the headline number this round's brief asks for does not exist yet and
  cannot be manufactured here.** Required: the drawings (see SHIP_READINESS #1, and the
  data-handling gate #11 BEFORE any real drawing is uploaded to the cloud pipeline).

## Failure taxonomy (as implemented in the harness, counts from executable runs here)

BadInput (broken JSON/PDF): 2 observed (rows 6, 7) · SchemaViolation: 6 observed (row 2) ·
Environment / RevitApi: 0 observable here (Revit absent — the classes exist in the harness
and the operator kit exercises them) · Unknown: 0.
