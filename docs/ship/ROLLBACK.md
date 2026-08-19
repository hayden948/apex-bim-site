# Release, hashes, and rollback (round 5)

## Release identity

- Tag: `v0.5.0-rc1` (branch `claude/analysis-improvement-fvnun0`; commit recorded in the tag
  and in LEDGER R5 C3).
- Package: `ApexBimStudio-0.5.0-rc1.zip` — sha256
  `68337587bb9556a363bd89fb450692db995c51f25257026e9efe28783e4e9f88` (per-file hashes inside
  as SHA256SUMS.txt; install.ps1 refuses to install if any file differs).
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
