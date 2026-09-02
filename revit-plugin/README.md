# Apex BIM Studio — Revit plugin

Source for the `ApexBimStudio.dll` Revit add-in (ribbon app, AFIS→Revit family
mapper, `.pred.json` parametric box builder, Apex API client).

> **Provenance:** this tree was reconstructed from the compiled
> `ApexBimStudio.dll` + `ApexBimStudio.pdb` (v1.0.0, net8.0-windows, Revit 2025
> API) because the original source repository was not available. The
> reconstruction is behavior-faithful, then the fixes below were applied on top.
> Diff against your original source if you still have a copy anywhere.

## Building

```
cd src
dotnet build -c Release
```

Outputs per target framework:

| Target | Revit versions | Output |
|---|---|---|
| `net48` | 2022, 2023, 2024 | `bin/Release/net48/ApexBimStudio.dll` |
| `net8.0-windows` | 2025+ | `bin/Release/net8.0-windows/ApexBimStudio.dll` |

Revit API references come from the `Nice3point.Revit.Api.*` NuGet packages
(reference-only; nothing from Revit is redistributed).

## Installing

Copy the matching build output to
`%APPDATA%\Autodesk\Revit\Addins\<version>\ApexBimStudio\` and
`deploy/ApexBimStudio.addin` to `%APPDATA%\Autodesk\Revit\Addins\<version>\`.

## Configuration

**Recommended (no env vars):** mint a token in the web console (`app.html` →
sign in → *Mint plugin token*, which copies it to the clipboard), then in Revit
use **Apex BIM Studio → Settings**: *Save token from clipboard* (DPAPI-encrypted)
and *Set API URL from clipboard*. The URL lands in
`%LOCALAPPDATA%\Apex\config.json` and wins over the env vars below; changes
apply immediately, no Revit restart.

Environment-variable fallbacks:

| Variable | Purpose |
|---|---|
| `APEX_API_URL` | Apex API base URL. Must be `https://` (plain `http` allowed for localhost only). Default `http://localhost:4000`. The hosted API lives at `https://kdqisuzydkgzkzxlctpv.supabase.co/functions/v1/api` (see `../supabase/README.md`). |
| `APEX_API_TOKEN` | Bearer token fallback when no stored token exists. Use an `apx_...` service token (mint in the console; stored hashed, project-scoped, revocable), or the Supabase publishable key. |
| `APEX_AUTH_URL` / `APEX_TOKEN_URL` / `APEX_CLIENT_ID` | OAuth PKCE endpoints + public client id for Sign In. |
| `APEX_SHOW_PROTOTYPES` | `1` shows the not-yet-implemented ribbon buttons (hidden by default). |

Logs: `%LOCALAPPDATA%\Apex\logs\ApexBimStudio-<date>.log`.
Access token: `%LOCALAPPDATA%\Apex\token.bin`, encrypted with per-user DPAPI.

## Changes vs. the v1.0.0 binary (v0.2.0)

Priority-ordered fixes applied during reconstruction:

1. **Revit 2022–2024 support restored** — multi-target `net48` + `net8.0-windows`;
   the old single net8 build could not load in Revit 2022–2024 despite the About
   dialog claiming support. About now reports the actual assembly version and the
   running Revit version.
2. **Startup hardened + logging** — duplicate ribbon tab/panel creation no longer
   crashes `OnStartup` (reuses existing); all failures land in a rolling file log
   instead of vanishing.
3. **API client fixed** —
   - 30 s `HttpClient` timeout and a `Task.Run`-based sync bridge with an upper
     wait bound, so a dead API can no longer freeze the Revit UI for 100 s+;
   - refuses non-localhost plain-HTTP endpoints (bearer token would be cleartext);
   - request bodies via `JsonSerializer` (no hand-concatenated JSON), URL-escaped ids;
   - non-success responses raise `ApexApiException` carrying the response body;
   - the hardcoded demo family GUID fallback is gone — commands now require an
     explicitly selected family;
   - Sign In implements a real OAuth 2.0 PKCE loopback flow (RFC 8252) and stores
     the token DPAPI-encrypted instead of showing a fake success dialog.
4. **Units respected** — `.pred.json` parameter/dimension values honor their
   declared `units`/`unit` fields (in/ft/mm/cm/m) instead of assuming inches;
   AFIS values honor `unit` with a meters default. Family templates resolve
   against the running Revit's configured template folder instead of a hardcoded
   `RVT 2025` English-Imperial path.
5. **Honest ribbon** — buttons whose commands are stubs are hidden unless
   `APEX_SHOW_PROTOTYPES=1`; stub dialogs say the feature is not available yet
   instead of pretending success.
6. **The M2 loop is wired end-to-end** — Sync fetches the hosted Apex library
   (`supabase/`) and sets the active family; From Library builds that family
   from its AFIS document — into the open family (Family Editor) or built,
   saved, loaded, and placed directly in a project; Run QA executes local
   Doc 8 checks in the Family Editor (required parameters, geometry, flex
   test in a rolled-back transaction) and the cloud QA Engine otherwise.
7. **Three more stubs became real features** —
   - *Generate Schedule*: creates an "Apex Equipment Schedule" with `Apex_*`
     shared-parameter columns and activates it;
   - *Verify Clearances*: finds `Apex_Clearance` solids in placed family
     instances and clash-checks them against the model
     (`ElementIntersectsSolidFilter`), reporting obstructions;
   - *Place*: activates and places the family stamped with the active Apex id
     (falls back to the most recently loaded type) via Revit's native
     placement flow.
8. **Process Queue worker (v0.3)** — RFA files can only be produced inside a
   running Revit, so the cloud API queues `generate_rfa` jobs and the new
   *Generate → Process Queue* button drains them: claim (atomic queued→running
   on the server, safe with several machines) → fetch the family's AFIS → build
   → save to `%LOCALAPPDATA%\Apex\rfa\` → upload the .rfa back to the library →
   mark the job succeeded/failed.
9. **Settings command (v0.4)** — workstation setup without env vars: paste the
   console-minted `apx_` token (DPAPI-encrypted store) and the API URL from the
   clipboard; the running session picks both up immediately. Readable cloud-QA
   results and Sync paging landed in the same release.
10. **Library-first From Library** — when a Process Queue worker has already
    built and uploaded a family's `.rfa`, From Library downloads that exact
    file instead of rebuilding locally (identical output on every machine);
    it falls back to building from AFIS when no built file exists yet. AFIS
    reference planes now carry `axis`/`offset` so extraction-derived families
    are placed and constrained for real: faces lock to coincident planes,
    labeled Width/Depth dimensions and the Height-driven extrusion flex, and
    equality constraints keep the box centered.

## Release checklist (recommended)

- Authenticode-sign `ApexBimStudio.dll` (unsigned add-ins trigger a Revit warning).
- Don't ship the `.pdb` to end users (it leaks internal paths); archive it for
  symbolication instead.
- Keep `System.Text.Json` pinned to the version Revit ships in-process to avoid
  add-in version conflicts (8.0.x for Revit 2025).
