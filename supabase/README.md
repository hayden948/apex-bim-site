# Apex API v1 (Supabase)

Deployed to the `apex-bim-studio` Supabase project (`kdqisuzydkgzkzxlctpv`) as the
edge function **`api`**. This is the server the Revit plugin's `ApexApiClient`
talks to.

- Base URL: `https://kdqisuzydkgzkzxlctpv.supabase.co/functions/v1/api`
- Auth: `Authorization: Bearer <publishable/anon key>` (Supabase JWT verification).
  The plugin reads `APEX_API_TOKEN` as its bearer-token fallback when no OAuth
  sign-in token is stored.
- Plugin config:
  - `APEX_API_URL=https://kdqisuzydkgzkzxlctpv.supabase.co/functions/v1/api`
  - `APEX_API_TOKEN=<publishable key>`

## Endpoints (contract: docs/architecture/api-design.md; QA: Doc 8)

| Route | Behavior |
|---|---|
| `GET /v1/families` | List library families |
| `GET /v1/families/{id}` | AFIS 1.0 document (metric) |
| `POST /v1/families/{id}/validate` | QA Engine: staged S→P→G→Z→L rules, Doc 1 §5.8 finding shape, persisted to `family_validations` |
| `POST /v1/families/{id}/exports` | Field points CSV (Doc 7) |
| `POST /v1/families/{id}/generate-rfa` | QA-gated (no certificate, no export); enqueues a `jobs` row for the Revit worker |

Errors use the structured envelope `{ "error": { "code", "message", "details?" } }`.

## Verified

All endpoints were exercised end-to-end after deployment (via in-database
`extensions.http`): list 200, AFIS 200, validate 200 (12 findings, score 1.0),
exports 200 (CSV), generate-rfa 202 (job queued), unauthenticated 401.

Seed data: demo project/upload/extraction chain and one library family
`Panelboard 208V 42ckt` (`a11ce000-0000-4000-8000-000000000001`) with a full
AFIS document (NEC 110.26 front zone, electrical connector, 3 layout points).

## Not yet implemented (next in line)

- Service tokens for the plugin (`/revit/*` routes per api-design.md) instead of
  the publishable key.
- The `generate_rfa` job worker (Windows/Revit or APS Design Automation, Doc 3
  Stage 11) that consumes the `jobs` table.
- Upload → extraction pipeline endpoints (`/uploads`, `/extractions`, Doc 3).
