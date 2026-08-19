# Apex BIM Studio — quickstart (one page)

**What it does:** turns the equipment specs Apex extracts from your PDF submittals into
parametric Revit families, in batches, with a report you keep.

## Install (once, ~3 minutes, no admin)

1. Extract `ApexBimStudio-<version>.zip`. In the folder:
   `powershell -ExecutionPolicy Bypass -File install.ps1 -RevitVersion 2025`
   (2022–2024 work too — the installer picks the right build).
2. Put the `license.apexlic` you received from Apex into `C:\ProgramData\Apex\`.
   (Shared workstation note: if a different Windows user installed a license there before,
   have an administrator replace the file — default ProgramData permissions only let the
   original creator overwrite it.)
3. Start Revit → the **Apex BIM Studio** tab appears. No project needs to be open.

## Daily use — the Submittals panel, left to right

1. **Review Submittal** — open a spec that came from a submittal. Every extracted value is
   shown with how sure the extraction was; anything under 80% is flagged **CHECK** in yellow.
   Fix a value, **Save and Build** — your correction is validated, the original is kept as a
   `.bak`, and that one family is rebuilt.
2. **Build Family** — build one spec into one family.
3. **Batch Build** — pick any spec in a folder; every spec in it is built. One bad drawing
   never stops the rest. Results land in `out\`:
   - the `.rfa` families;
   - **BUILD_REPORT.md** — what was built, from which drawing, with which values, which
     checks passed, and — for anything that failed — exactly what to do next. Keep this file
     with the families.

## When something fails

The message on screen and the build report say what to do — most fixes are "correct the named
field in Review Submittal" or a machine setting (the message tells you which). For anything
else: every run writes one log file (`%LOCALAPPDATA%\Apex\logs\run-*.log`; the report footer
names it) — send that one file to Apex. You never need to reconstruct what you clicked.

## Uninstall

`uninstall.ps1` in the package folder. Your families, reports, and drawings are never touched.
