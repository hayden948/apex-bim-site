# Release, hashes, and rollback (round 5)

## Release identity

- Tag: `v0.5.0-rc1` (branch `claude/analysis-improvement-fvnun0`; final rc commit recorded in
  LEDGER R5 C5 — the tag exists locally; the authoring session cannot push tag refs (403), so
  pushing it is an operator action).
- Package: `ApexBimStudio-0.5.0-rc1.zip` — sha256
  `16c0cac04b3e8fcc9ecafa789ece4941d80f1eb07cd2d29ce5cd7cb995bce663`
  (zip hashes vary per assembly run — the per-file SHA256SUMS.txt inside is the stable
  integrity source and install.ps1 enforces it before copying anything).
  DLLs: net48 `f62c74684f7d19081d5e0a3cb5445159eb7a2effb0c3bcca81a47defb712f147`,
  net8 `02474c0a95fe5d90474f6680b1136f5f926707fc54497a5ec28e091b4d9e97ce`.
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
