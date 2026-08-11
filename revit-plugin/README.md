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

## Configuration (environment variables)

| Variable | Purpose |
|---|---|
| `APEX_API_URL` | Apex API base URL. Must be `https://` (plain `http` allowed for localhost only). Default `http://localhost:4000`. |
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

## Release checklist (recommended)

- Authenticode-sign `ApexBimStudio.dll` (unsigned add-ins trigger a Revit warning).
- Don't ship the `.pdb` to end users (it leaks internal paths); archive it for
  symbolication instead.
- Keep `System.Text.Json` pinned to the version Revit ships in-process to avoid
  add-in version conflicts (8.0.x for Revit 2025).
