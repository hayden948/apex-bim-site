# Release, hashes, and rollback (round 5)

## Release identity

- Tag: `v0.5.0-rc1` (branch `claude/analysis-improvement-fvnun0`; final rc commit recorded in
  LEDGER R5 C5 — the tag exists locally; the authoring session cannot push tag refs (403), so
  pushing it is an operator action).
- Package: `ApexBimStudio-0.5.0-rc1.zip` (zip hashes vary per assembly run — the per-file
  SHA256SUMS.txt inside is the stable integrity source and install.ps1 enforces it before
  copying anything).
  DLLs (version-stamped 0.5.0, built with `/deterministic` — the audit round found the
  earlier recorded hashes were unreproducible because the compiler embedded a per-build
  timestamp/MVID; a double build is now byte-identical, so these hashes are reproducible
  from the tagged source with the same compiler and references): net48
  `4b7c6e1162b6bafd8740789c35a5592f0c37266ec0c4e860839a405321c5929d`, net8
  `f671e8a06f282008bb407559eb67d98147df0d4e19f373b53b0a963e86e4db80`.
  (dotnet SDK builds — the canonical CI path — are deterministic by default; the operator
  records the CI package's hashes here at tag time.)
- Durable artifact source: the CI workflow now ASSEMBLES the package itself
  (`ApexBimStudio-package` artifact on every run, and on tag pushes) and make-package.sh
  refuses to package a build folder missing any runtime dependency. When the operator pushes
  the tag: download that run's package artifact, attach it to a GitHub Release, and record
  its hashes here — that copy, not any container-built zip, goes to CVE.
- Provenance note (honest): this rc zip was assembled in the authoring container from its csc
  builds. **The customer-facing package must be reassembled with
  `deploy/installer/make-package.sh` from the Windows CI artifacts of the tagged commit**
  ("Build Revit plugin" workflow run on the tag), then its new hashes recorded here. Same
  sources, canonical toolchain, version-stamped DLLs.
- Cloud half: Supabase edge function `api` **v29** (deployed), `telegram-webhook` v8; both
  redeployable from this repo (`supabase/functions/`), CI keeps `--no-verify-jwt`.

## How CVE reverts to "nothing broken" (customer-side rollback)

The add-in never modifies existing Revit content — it only CREATES .rfa files, reports, and
logs. Rolling back therefore never risks the customer's model:

1. Run `uninstall.ps1 -RevitVersion <year>` from the package folder (or delete
   `%APPDATA%\Autodesk\Revit\Addins\<year>\ApexBimStudio*`). Revit returns to stock.
2. Every family already built keeps working — .rfa files are plain Revit families with no
   runtime dependency on the add-in, the license, or the cloud.
3. To go back one version instead of to nothing: uninstall, then run the PREVIOUS package's
   install.ps1 (keep every versioned zip; each carries its own SHA256SUMS.txt).
4. The license file survives uninstall by default and is picked up by any reinstall.

## Operator-side rollback

- Plugin: `git checkout <previous tag>` → rebuild via CI → repackage; or reuse the archived
  previous zip directly.
- API: edge functions are versioned by deploy; redeploy the previous git state of
  `supabase/functions/api/index.ts` (CI deploys on merge; manual
  `supabase functions deploy api --no-verify-jwt` matches it). Schema note: migrations in
  `supabase/migrations/` are additive so far; no destructive migration exists to unwind.
- Never rewrite the tag or the pushed branch history; a bad release gets a NEW tag.
