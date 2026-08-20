# Release, hashes, and rollback (round 5)

## Release identity

- Tag: `v0.5.0-rc1` = commit `32bcde4efe52efd922e4f08df5e0a660f2c557b0` — **the tag is the
  single source of truth for the release commit** (the audit round found three ledger cycles
  naming three different commits as "final"; the ledger is an append-only history of how the
  rc evolved, the tag is the answer). It exists locally in the authoring session (the remote
  403s tag refs from it — root-caused round 6: branch-scoped push credentials); the operator
  pushes it, which also exercises the never-yet-fired `v*` CI trigger.
- **Designated release artifact (round 6): the `ApexBimStudio-package` artifact of CI run
  32395720825 (artifact ID 9416585424)**, built from the tagged commit via workflow_dispatch.
  Its RELEASE.txt states the commit; its embedded SHA256SUMS.txt is the binding manifest,
  printed in the run log and recorded ONCE in LEDGER R6 C2. Install it as-is; do NOT
  substitute another run's artifact even from the same commit — round 6 measured two runs of
  the SAME commit producing different ApexBimStudio.dll bytes (different runner SDK patches),
  so byte-reproducibility across builds is NOT claimed anywhere; identity is "this artifact,
  this run, this commit".
- Package: `ApexBimStudio-<version>.zip` — the version (and so the artifact name) comes from
  the csproj `<Version>` (currently `0.5.0-rc1`). Zip hashes vary per assembly run; the
  per-file SHA256SUMS.txt inside is the stable integrity source and install.ps1 enforces it
  before copying anything.
- Reference DLL hashes from the audit-round container build (version-stamped, compiled with
  `/deterministic` — double build proven byte-identical; recipe in
  `revit-plugin/tools/dev-build/`): net48
  `9d8f55264e558974517a0350390c309d451526d6c86148f4bf1f14f85f7270b1`, net8
  `a9c0bb2c5362364ab5de05f5b92f9158840f759c046a14d1652c4cf8ed2996b0`.
  These are for the audit trail only — container builds bind nothing.
- Durable artifact source: the CI workflow ASSEMBLES the package itself
  (`ApexBimStudio-package` artifact on every run, and on tag pushes) and make-package.sh
  refuses to package a build folder missing any runtime dependency. Operator: download
  artifact 9416585424, attach it to a GitHub Release on the tag so it outlives the 90-day
  artifact retention — that copy goes to CVE.
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
