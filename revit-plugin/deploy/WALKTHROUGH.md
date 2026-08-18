# First-time-user walkthrough — clean Revit 2025 (one page, ~20 minutes)

Round-4 EXIT evidence. Run this on a machine whose Revit has never seen the add-in.
Every checkbox is HUMAN-VERIFY-REQUIRED in the ship ledger: the authoring environment has no
Revit, so the machine evidence is (a) the Revit **journal** this session records automatically
(`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2025\Journals\` — grab the newest `journal.*.txt`
when done) and (b) the **per-run log files** (`%LOCALAPPDATA%\Apex\logs\run-*.log`). Screenshots
optional; journal + logs are what the ledger needs.

## Setup (5 min)

- [ ] 1. Copy `ApexBimStudio.dll` (net8 build) + `.addin` into
  `%APPDATA%\Autodesk\Revit\Addins\2025\`. Start Revit. **Expect:** tab **Apex BIM Studio**
  with panel **Submittals**: Review Submittal · Build Family · Batch Build.
- [ ] 2. Make `C:\apex\walkthrough\` and copy the six specs from
  `schemas/familyspec/fixtures/golden/` into it. Then create the two demo failures:
  - copy `nq430-panelboard.pred.json` to `zz-corrupt.pred.json` and DELETE the second half of
    the file in Notepad (make it un-parseable);
  - copy `transformer-t1-corrected.pred.json` to `zz-depth-miss.pred.json` and edit
    `"depth": { "value": 24` → `"value": 0`.

## Batch: load → select → build → read report (5 min)

- [ ] 3. Click **Batch Build**, pick any file in the folder. **Expect:** a Yes/No confirmation
  naming the folder and "8" (files), defaulting to **No**. Click **Yes**.
- [ ] 4. **Expect:** a progress window counting "Building i of 8", one line per drawing as it
  finishes: 6 ✓, and the two `zz-*` files ✗ in red — the batch DOES NOT stop at them.
- [ ] 5. **Expect:** a finish dialog "6 of 8 families built — 2 failed", naming
  `out\BUILD_REPORT.md` and the run log path. Note the log path here: ______________________
- [ ] 6. Open `out\BUILD_REPORT.md`. **Expect, without any translation help:** each built item
  with size + "values set" + geometry checks; `zz-corrupt` NOT BUILT → "re-download / send the
  run log"; `zz-depth-miss` NOT BUILT → names `geometry.depth.value` and says to open **Review
  Submittal**. Quarantine folder has two `.FAILED.txt`; `out\` has NO .rfa for either failure.

## Override a parser miss: review → correct → rebuild that item (5 min)

- [ ] 7. Click **Review Submittal**, open `zz-depth-miss.pred.json`. **Expect:** every value
  listed with a confidence column; Depth row readable; press **Check values** → problem names
  the depth field in plain words.
- [ ] 8. Type the real depth (24) into the Depth/geometry row, click **Save and Build**.
  **Expect:** "family built" dialog; the folder now has `zz-depth-miss.pred.json.bak`
  (the untouched original) and the new `.rfa`.

## Honest-failure spot checks (5 min)

- [ ] 9. Mid-batch failure message quality: open the newest `run-*batch*.log` — the two
  failures must be findable WITHOUT reading any stack trace knowledge: file name + what was
  wrong + that the batch continued.
- [ ] 10. Rename Revit's family template folder temporarily (Options → File Locations), run
  **Build Family** on any golden spec. **Expect:** "cannot build on this machine" naming the
  template setting — blaming the machine, not the drawing. Restore the setting.
- [ ] 11. The whole session, again, as a first-time user: at any point did you need to know
  what "pred", "spec", "AFIS", or "schema" means to proceed? If yes, write down where:
  ____________________________________________________________________

## Send back

Newest journal file · all `run-*.log` from today · `BUILD_REPORT.md` · `RUN_MATRIX.md` ·
`quarantine\*.FAILED.txt` · answers to 5/6/11. That bundle closes the round-4
HUMAN-VERIFY-REQUIRED items in `docs/ship/LEDGER.md`.
