# First-time-user walkthrough — clean Revit 2025 (one page, ~25 minutes)

Round-4 EXIT evidence. Run this on a machine whose Revit has never seen the add-in.
Every checkbox is HUMAN-VERIFY-REQUIRED in the ship ledger: the authoring environment has no
Revit, so the machine evidence is (a) the Revit **journal** this session records automatically
(`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2025\Journals\` — grab the newest `journal.*.txt`
when done) and (b) the **per-run log files** (`%LOCALAPPDATA%\Apex\logs\run-*.log`). Screenshots
optional; journal + logs are what the ledger needs.

## Setup (5 min)

- [ ] 1. Copy `ApexBimStudio.dll` (net8 build) + `.addin` into
  `%APPDATA%\Autodesk\Revit\Addins\2025\`. Start Revit — stay on the start screen, do NOT open
  a project. **Expect:** tab **Apex BIM Studio**, panel **Submittals** with Review Submittal ·
  Build Family · Batch Build, and all three CLICKABLE with no document open. (If they are
  greyed out, that is a ship-blocking bug — report it.)
- [ ] 2. Make `C:\apex\walkthrough\` and copy in the six specs from
  `schemas/familyspec/fixtures/golden/` plus the two prepared failures from
  `schemas/familyspec/fixtures/demo/` (`zz-corrupt.pred.json`, `zz-depth-miss.pred.json`).
  8 files total; nothing needs editing.

## Batch: load → select → build → read report (5 min)

- [ ] 3. Click **Batch Build**, pick any file in the folder. **Expect:** a Yes/No confirmation
  naming the folder and 8 files, defaulting to **No**. Click **Yes**.
- [ ] 4. **Expect:** a progress window counting "Building i of 8", one line per drawing as it
  finishes: ✓ or ⚠ for the six goldens, and the two `zz-*` files ✗ in red — the batch DOES NOT
  stop at them. During a large family the titlebar may briefly read "Not Responding"; the
  window says so and the run continues. Revit's own window ignores clicks until the batch ends.
- [ ] 5. **Expect:** a finish dialog "6 of 8 families built — 2 failed", naming
  `out\BUILD_REPORT.md` and the run log path. Note the log path here: ______________________
- [ ] 6. Open `out\BUILD_REPORT.md`. **Expect, without any translation help:** each built item
  with size, values set, and "Build checks … all passed" (if any says FAILED / "treat it as
  suspect", it also carries a What-to-do line — record which); `zz-corrupt` NOT BUILT →
  "re-download / send the run log"; `zz-depth-miss` NOT BUILT → names the depth field and says
  to open **Review Submittal**. `quarantine\` has two `.FAILED.txt`; `out\` has NO .rfa for
  either failure.

## Override a parser miss: review → correct → rebuild that item (5 min)

- [ ] 7. Click **Review Submittal**, open `zz-depth-miss.pred.json`. **Expect:** every value
  with a confidence column; the **Overall depth** row and the **Depth** parameter row both
  flagged (Depth shows "31% — CHECK", yellow); the header says values are marked CHECK.
  Press **Check values** → the problem names the depth field in plain words.
- [ ] 8. Type the real depth (**24**, per the file's own note) into BOTH flagged rows —
  Overall depth and the Depth parameter. (If you fix only one, **Check values** warns you the
  two disagree — verify that, it's deliberate.) Click **Save and Build**. **Expect:** a
  "family built" dialog with "geometry checks: all passed"; `out\` now has the new `.rfa`;
  the folder has `zz-depth-miss.pred.json.bak` (the untouched original); and
  `out\BUILD_REPORT.md` now ends with an UPDATE line recording the correction.

## Honest-failure spot checks (10 min)

- [ ] 9. Mid-batch failure INSIDE Revit (the round's forced-failure item): in `out\`,
  right-click any built `.rfa` → Properties → set **Read-only**. Run **Batch Build** again on
  the same folder, Yes. **Expect:** that one drawing FAILS (machine/environment class — the
  file could not be written), every other golden rebuilds, the batch does not stop, and the
  report's What-to-do blames the machine, not the drawing. Clear the Read-only flag after.
- [ ] 10. Open the newest `run-*batch*.log`: the failures must be findable WITHOUT stack-trace
  literacy — file name + what was wrong + that the batch continued.
- [ ] 11. Rename Revit's family template folder temporarily (Options → File Locations), run
  **Build Family** on any golden spec. **Expect:** "cannot build on this machine" naming the
  template setting. Restore the setting.
- [ ] 12. The whole session, again, as a first-time user: at any point did you need to know
  what "pred", "spec", "AFIS", or "schema" means to proceed? If yes, write down where:
  ____________________________________________________________________

## Send back

Newest journal file · all `run-*.log` from today · `BUILD_REPORT.md` · `RUN_MATRIX.md` ·
`quarantine\*.FAILED.txt` · answers to 5/6/9/12. That bundle closes the round-4
HUMAN-VERIFY-REQUIRED items in `docs/ship/LEDGER.md`.
