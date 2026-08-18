# Rounds 3–4 run matrix — what was actually executed, on what, with what result

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

## Executed here — every attempt listed (deployed api version noted per row: rows 3 ran under v27, rows 4/6 under v28, rows 8–10 under v29; rows 1/2/7/11 are version-independent local/suite runs except row 7 which ran live under v28)

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

## Round 4 executed here (all Revit-free; suite runs are fresh-compile — see LEDGER R4 C3 for the toolchain fix that guarantees it)

| # | stage under test | input | result | evidence (LEDGER R4) |
|---|---|---|---|---|
| 12 | review/override core (SpecReviewModel): load → flag low confidence → reject invalid → correct → save with .bak | crafted zero-depth low-confidence spec; nq430 golden; broken JSON; kind-preservation spec | 25/25 asserts pass incl. save-refuses-invalid and .bak-never-overwritten | R4 C2 suite paste |
| 13 | customer report generator (BuildCustomerReport) | 3-row mixed batch + all-failed batch + jsonl row | 13/13 asserts pass incl. no-jargon/no-stack-trace guard and threshold anti-drift | R4 C2 suite paste |
| 14 | per-run log lifecycle (ApexLog.BeginRun) | live writes on this container's filesystem | 5/5 asserts pass: file created, line teed, closed line, post-close isolation | R4 C2 suite paste |
| 15 | negative control: threshold drift (report 0.7 vs review 0.8) | deliberate constant change, fresh compile | suite FAILS with 2 named failures; revert → ALL TESTS PASSED | R4 C3 (both outputs pasted) |
| 16 | sample BUILD_REPORT.md from the real generator | 5 rows mirroring the walkthrough scenario (2 built, 3 failure classes) | report generated, committed as docs/ship/SAMPLE_BUILD_REPORT.md | R4 C3 |

## HUMAN-VERIFY-REQUIRED (code shipped + kit shipped; execution needs the Revit machine)

- Per-drawing build results, wall times, and build-stage failure counts (`BatchBuildCommand`
  writes them; nothing here can run it).
- Revit-side determinism (double run per OPERATOR_CHECKLIST step 7; .rfa files are expected to
  differ at byte level — Revit embeds GUIDs/timestamps — outcome columns must not).
- The plugin's named-field failure dialog rendering, quarantine files on a real disk, and the
  journal-replay path (needs the one-time recording, see run-batch.ps1 header).
- Round 4 in-Revit surfaces: ribbon rendering, confirmation dialogs, the progress window's
  Render-priority repaint under Revit's message loop, the review dialog, and the full
  first-time-user path — the verification vehicle is `revit-plugin/deploy/WALKTHROUGH.md`
  (one page, ~20 min, journal + run logs are the returned evidence).

## BLOCKED-ON-OPERATOR (cannot be produced from here at all)

- The Meta EMT 11990 batch, its failure taxonomy on real drawings, and the blind-holdout
  success rate — **the headline number this round's brief asks for does not exist yet and
  cannot be manufactured here.** Required: the drawings (see SHIP_READINESS #1, and the
  data-handling gate #11 BEFORE any real drawing is uploaded to the cloud pipeline).

## Failure classes observed, attributed to the stage where they were actually observed

Honest attribution (the adversarial review caught the first draft conflating stages):

- **API stage, observed live**: BadInput ×2 (corrupted PDF at extraction, row 6; broken
  correction body, row 7); SchemaViolation ×6-equivalent via the malformed fixtures at the
  validate boundary (row 2).
- **Batch harness itself**: its BadInput/SchemaViolation/Environment/RevitApi paths have NOT
  executed anywhere — only the Revit-free slices are proven (classification mapping, the
  validator the harness calls, marker naming, matrix math; rows 2 and 11). The harness's
  end-to-end failure behavior is exactly what the operator's first run verifies
  (OPERATOR_CHECKLIST "what failed safely looks like").
- Unknown: 0 anywhere.
