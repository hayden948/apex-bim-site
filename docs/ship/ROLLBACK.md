# Release, hashes, and rollback (round 5)

## Release identity

- Tag: `v0.5.0-rc1` — **the tag is the single source of truth for the release commit** (the
  audit round found three ledger cycles naming three different commits as "final"; the ledger
  is an append-only history of how the rc evolved, the tag is the answer). It exists locally
  in the authoring session (the remote 403s tag refs from it); the operator pushes it, which
  triggers the CI build+package run for the tagged source.
- Package: `ApexBimStudio-<version>.zip` — the version (and so the artifact name) comes from
  the csproj `<Version>` (currently `0.5.0-rc1`). Zip hashes vary per assembly run; the
  per-file SHA256SUMS.txt inside is the stable integrity source and install.ps1 enforces it
  before copying anything.
- Reference DLL hashes from the audit-round container build (version-stamped, compiled with
  `/deterministic` — double build proven byte-identical; recipe in
  `revit-plugin/tools/dev-build/`): net48
  `9d8f55264e558974517a0350390c309d451526d6c86148f4bf1f14f85f7270b1`, net8
  `a9c0bb2c5362364ab5de05f5b92f9158840f759c046a14d1652c4cf8ed2996b0`.
  These are for the audit trail only. **The binding release hashes are recorded ONCE, by the
  operator, from the CI package artifact of the tagged commit** (dotnet SDK builds are
  deterministic by default) — that copy goes to CVE.
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
