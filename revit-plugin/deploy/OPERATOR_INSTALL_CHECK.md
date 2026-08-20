# Real-Windows install check — NOT the dev laptop (one page, ~30 min)

Round-6 GO-gate item. Machine: a clean VM, a fresh Windows user profile, or a borrowed
machine with **Revit 2025**. Every box is PASS/FAIL; on any FAIL, capture what its line says
and stop there — a failed step with evidence is worth more than a finished list without it.

**Artifact source (no substitutes):** the DESIGNATED release artifact —
`https://github.com/hayden948/apex-bim-site/actions/runs/32395720825` → artifact
**ApexBimStudio-package** (ID 9416585424). Unzip once locally first and open `RELEASE.txt` —
its `commit:` line MUST read `32bcde4efe52efd922e4f08df5e0a660f2c557b0` (= what `v0.5.0-rc1`
names). If it doesn't, STOP: wrong artifact (tag-is-truth rule; don't install it). Do not
substitute another run's artifact even from the same commit — builds are not byte-reproducible
across runs (LEDGER R6 C2); THIS artifact is the release.

- [ ] 0. **THE anchor check (do this before trusting anything inside the download, including
  install.ps1):** the artifact zip contains `ApexBimStudio-0.5.0-rc1.zip`. In PowerShell:
  `Get-FileHash ApexBimStudio-0.5.0-rc1.zip` — compare against **your out-of-band copy of the
  anchor hash** (sent to your Telegram on 2026-08-20; copy it into your password manager),
  NOT against any hash printed inside the package, the repo, or this file — if the repo or
  package were tampered with, so were the hashes they carry. (A different green run at the
  SAME commit produces a different hash, so this one check pins "this artifact, this run",
  which RELEASE.txt alone cannot.) Then check `RELEASE.txt` commit == 32bcde4… .
  FAIL either → STOP; screenshot the hash + RELEASE.txt + the run page.
- [ ] 0a. **Shell self-test (~30 s, makes no changes) — ON THE TEST MACHINE, not your dev
  laptop** (its shell and permissions are what the install will actually use). The packaged
  installer predates this switch, so copy ONE file from your repo checkout to the test
  machine — `revit-plugin\deploy\installer\install.ps1` — put it BESIDE the extracted
  package folder, then in a plain PowerShell there (the default 5.1 is exactly what this
  validates):
  `powershell -ExecutionPolicy Bypass -File install.ps1 -SelfTest -PackageDir <extracted package folder>`
  **Expect:** "SELF-TEST PASSED". Any FAIL line → STOP and send the exact output; nothing has
  touched Revit paths yet.
- [ ] 0b. Push the tag (this closes the never-fired `v*` trigger too):
  `git tag v0.5.0-rc1 32bcde4efe52efd922e4f08df5e0a660f2c557b0 && git push origin v0.5.0-rc1`
  → within ~2 min a new "Build Revit plugin" run should START. That run only verifies the
  trigger — its artifact will NOT match the step-0 hash and must not be installed; the
  DESIGNATED artifact above is the release. No run appears → record that; the trigger defect
  goes back to engineering.
- [ ] 0c. Attach the DESIGNATED zip to a GitHub Release on the tag (artifacts expire
  2026-11-18): upload the exact file whose hash passed step 0, then re-download it from the
  Release page and run `Get-FileHash` once more — same hash. This is the durable copy CVE's
  install is traced to.

## Install (steps mirror QUICKSTART; expected state after each)

- [ ] 1. Copy the zip to the test machine. Right-click → Properties → note whether "Unblock"
  appears (record yes/no) → extract.
- [ ] 2. `powershell -ExecutionPolicy Bypass -File install.ps1 -RevitVersion 2025`
  **Expect:** "Package integrity: OK", the net8 file list, "NO LICENSE INSTALLED".
  FAIL → copy the full console text (this is Windows PowerShell 5.1 — mojibake or a
  parameter error here is itself a finding).
- [ ] 3. Files landed: `%APPDATA%\Autodesk\Revit\Addins\2025\ApexBimStudio.addin` and
  `...\2025\ApexBimStudio\` containing ApexBimStudio.dll + BouncyCastle.Cryptography.dll +
  System.Security.Cryptography.ProtectedData.dll. FAIL → `dir` listing of both paths.
- [ ] 4. Start Revit 2025, stay on the start screen. **Expect:** "Apex BIM Studio" tab;
  Submittals panel; Review Submittal / Build Family / Batch Build all CLICKABLE with no
  document open. FAIL → screenshot ribbon + newest `journal.*.txt`
  (`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2025\Journals`).

## License states (files come from Hayden's app_config store; keep them out of the repo)

- [ ] 5. MISSING: click **Build Family** with no license installed. **Expect:** dialog naming
  `C:\ProgramData\Apex` as where to put license.apexlic — not a crash, not a silent no-op.
- [ ] 6. VALID: put the dev license (`license_apex_dev`) at `C:\ProgramData\Apex\license.apexlic`,
  restart Revit, open **About**. **Expect:** "Licensed to Apex internal — Hayden
  dev/walkthrough until 2027-12-31". Build Family now opens the file picker.
- [ ] 7. EXPIRED: replace the file with `license_expired_demo`, restart, click Build Family.
  **Expect:** "expired on 2026-01-01 … Contact Apex to renew" — and the renewal wording
  ("a new license.apexlic file replaces this one, no reinstall needed").
- [ ] 8. TAMPERED: make a copy of the VALID license, change ONE character in the long first
  section, install it, restart, click Build Family. **Expect:** "damaged or was altered —
  its signature does not match". Restore the valid license after.
  For 5–8, FAIL → screenshot the dialog + `%LOCALAPPDATA%\Apex\logs` newest file.

## Smoke build (proves the installed binaries, not just the ribbon)

- [ ] 9. Copy `schemas/familyspec/fixtures/golden/nq430-panelboard.pred.json` to the machine.
  **Build Family** → pick it → **Expect:** "family built" with Size: 20 in W × 5.75 in D ×
  32 in H, a run-log path, and an .rfa in `out\` beside the spec. Open the .rfa in Revit;
  change the Apex_Width type parameter; **expect the box geometry to follow it** (first
  in-Revit proof of the flex fix). FAIL → the run-*.log named in the dialog + journal.
- [ ] 10. `uninstall.ps1 -RevitVersion 2025` → **Expect:** both paths from step 3 gone,
  license still present; restart Revit → tab gone, no errors at startup.

## Send back

Every box marked PASS/FAIL · console text from step 2 · answers to the two "record" items
(step 1 Unblock, step 4) · any FAIL captures. This closes (or reopens with evidence) the
GO-gate installer item in docs/ship/LEDGER.md.
