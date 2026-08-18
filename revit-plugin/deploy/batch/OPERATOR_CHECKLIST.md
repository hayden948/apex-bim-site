# Batch build — operator checklist (Revit machine)

Everything in this file runs on the Windows machine with Revit installed. The container that
authors these rounds cannot execute Revit; every result you produce here is the evidence the
ship ledger marks HUMAN-VERIFY-REQUIRED. Send back the four artifacts in step 6.

## One-time setup

1. Build or copy the plugin (`ApexBimStudio.dll` for your Revit's target: net48 = 2022–2024,
   net8 = 2025+) and the `.addin` manifest into
   `%APPDATA%\Autodesk\Revit\Addins\<version>\` (see `revit-plugin/deploy/`).
2. Start Revit once; confirm the **Apex BIM Studio** tab shows **M1 → Batch Build**.
3. Confirm Revit's Family Template File location is set (Options → File Locations) and the
   Electrical Equipment template exists there — the batch classifies a missing template as an
   `Environment` failure for every file, which tells you about the machine, not the drawings.

## HOLDOUT PROTOCOL — do this FIRST, before anyone fixes anything

4. From the full set of Meta EMT 11990 `.pred.json` files, move 20–30% (minimum 3, spanning
   equipment classes) into a separate folder named `holdout\` on THIS machine. Do not share
   them, do not run them, do not open them, until the final blind run. The tuned-set rate is
   not the headline number; the holdout rate is.

## Per-batch run

5. Put the remaining `.pred.json` files in one folder, e.g. `C:\apex\batch1\`. Then either:
   - **One-click**: open Revit (any document, or none — the start screen is fine), click
     **Batch Build**, pick any file in the folder; or
   - **Scripted**: `powershell -File run-batch.ps1 -BatchDir C:\apex\batch1` (see the
     journal note inside that script — journal replay needs a one-time recording).
   The batch is DESIGNED never to stop on a bad drawing: failures are quarantined, the rest
   continue, and even log/marker write failures are counted and logged rather than fatal.
   That containment design has not run under a real Revit yet — your run is its first real
   verification, so if the batch ever halts early, capture the Apex log and report it as a
   harness bug, not a drawing problem.
6. Collect and send back, unmodified:
   - `RUN_MATRIX.md` (per-drawing results + failure taxonomy + all-files success rate)
   - `batch-run.jsonl` (per-drawing debug records)
   - `quarantine\` (every `*.FAILED.txt` — these are the diagnosable failures)
   - `out\` listing (`dir out`) — the built `.rfa` files stay with you.

## Determinism check (round-3 EXIT item)

7. Run the SAME folder twice, into two copies (`batch1a`, `batch1b`). Compare:
   - `fc a\RUN_MATRIX.md b\RUN_MATRIX.md` — must differ ONLY in the started-at line and
     wall-ms column; any difference in validate/build/params/flex columns is a ship blocker.
   - `.rfa` files are NOT expected to be byte-identical (Revit embeds document GUIDs and
     save timestamps); the determinism claim is about build OUTCOMES. If you want a deeper
     check, open both `.rfa` for one family and compare the type parameters table.

## Blind holdout run (LAST, after all fixing has stopped)

8. Only when the team declares fixing done: run `holdout\` through step 5 once. Do not fix
   anything and re-run. Send back its `RUN_MATRIX.md` — its success rate is THE number that
   goes in front of Cache Valley Electric planning, not the tuned set's.

## What "failed safely" looks like

A failed drawing produces: a `quarantine\<file>.FAILED.txt` naming the failure class, the
exact field or API error, and the full exception detail (stack included), a `batch-run.jsonl`
line, no `.rfa` in `out\` for that drawing, and the batch continues to the next file. Each run
starts fresh: the jsonl is reset, stale quarantine markers are cleared, and each input's
stale output is removed before it is processed — so every artifact you collect belongs to THIS
run. If you ever find a `.rfa` for a drawing whose matrix row says FAIL, that is a containment
bug — report it with both files.
