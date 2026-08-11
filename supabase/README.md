# Apex API v1 (Supabase)

Deployed to the `apex-bim-studio` Supabase project (`kdqisuzydkgzkzxlctpv`) as the
edge function **`api`**. This is the server the Revit plugin's `ApexApiClient`
talks to, and it hosts the upload → extraction → approve pipeline (Doc 3).

- Base URL: `https://kdqisuzydkgzkzxlctpv.supabase.co/functions/v1/api`
- Auth (custom, `verify_jwt` disabled): `Authorization: Bearer <token>` where the
  token is either
  - an **`apx_...` service token** for the Revit add-in (api-design.md §service
    tokens) — stored SHA-256-hashed in `public.api_tokens`, revocable per token
    (`revoked_at`), `last_used_at` tracked; or
  - the project's publishable/anon key.
- Plugin config:
  - `APEX_API_URL=https://kdqisuzydkgzkzxlctpv.supabase.co/functions/v1/api`
  - `APEX_API_TOKEN=<apx_... service token>`
- Extraction config: `supabase secrets set ANTHROPIC_API_KEY=sk-ant-...` — until
  it is set, `POST /v1/extractions` returns `503 EXTRACTION_NOT_CONFIGURED`
  (everything else works without it).

### Issuing a service token

```sql
-- generate, then store only the hash; hand the plaintext to the plugin once
insert into api_tokens (name, token_hash)
values ('workstation-01', encode(digest('apx_<random>', 'sha256'), 'hex'));
```

## Endpoints (contract: docs/architecture/api-design.md; QA: Doc 8)

| Route | Behavior |
|---|---|
| `GET /v1/families` | List library families |
| `GET /v1/families/{id}` | AFIS 1.0 document (metric) |
| `POST /v1/families/{id}/validate` | QA Engine: staged S→P→G→Z→L rules, Doc 1 §5.8 finding shape, persisted to `family_validations` |
| `POST /v1/families/{id}/exports` | Field points CSV (Doc 7) |
| `POST /v1/families/{id}/generate-rfa` | QA-gated (no certificate, no export); enqueues a `jobs` row for the Revit worker |
| `POST /v1/families/{id}/rfa` | Worker uploads the built `.rfa` (`{content_base64, revit_version?}` → `rfa` bucket, pointer on the family) |
| `GET /v1/families/{id}/rfa` | Download the built `.rfa` (binary; 404 `NO_RFA` until the worker delivers) |
| `POST /v1/uploads` | `{filename, content_base64}` → `uploads` storage bucket + row (30 MB cap, SHA-256 recorded) |
| `POST /v1/extractions` | `{upload_id}` → Claude (Opus 5, structured output over the PDF) → `extractions` row |
| `GET /v1/extractions/{id}` | Extraction status + result |
| `POST /v1/extractions/{id}/approve` | Prediction → AFIS 1.0 (metric; NEC zone auto-added for electrical) → new `families` row |
| `GET /v1/jobs?kind=&status=` | Worker polling (default `status=queued`) |
| `POST /v1/jobs/{id}/claim` | Atomic queued→running (409 if already claimed) |
| `POST /v1/jobs/{id}/complete` | `{status: "succeeded"\|"failed", error?}` running→finished |

Errors use the structured envelope `{ "error": { "code", "message", "details?" } }`.

## Verified

All endpoints were exercised end-to-end after deployment (via in-database
`extensions.http`):

- Auth: valid `apx_` token 200, unknown token 401 `INVALID_TOKEN`, missing 401
  `UNAUTHENTICATED`.
- Families: list 200, AFIS 200, validate 200, exports 200 (CSV), generate-rfa 202.
- Pipeline: upload 201 → extraction 503 `EXTRACTION_NOT_CONFIGURED` (no API key
  set; row marked failed) → with a simulated extraction result: get 200, approve
  201 (created a family whose generated AFIS then passed validate at score 1.0).
- Jobs: list 200 → claim 200 (queued→running) → complete 200 (succeeded) →
  re-claim 409 `NOT_CLAIMABLE`.
- RFA round-trip: GET 404 `NO_RFA` before → POST 201 (stored in the `rfa`
  bucket) → GET 200 returning the exact uploaded bytes.

Seed data: demo project/upload/extraction chain and one library family
`Panelboard 208V 42ckt` (`a11ce000-0000-4000-8000-000000000001`) with a full
AFIS document (NEC 110.26 front zone, electrical connector, 3 layout points).

## Worker

The `generate_rfa` consumer is the Revit plugin's **Generate → Process Queue**
button (RFA files can only be produced inside a running Revit): it claims each
queued job, builds the family from its AFIS document, saves the `.rfa` under
`%LOCALAPPDATA%\Apex\rfa\`, and completes the job. Claim/complete are atomic
server-side, so several machines can drain the queue concurrently.

## Not yet implemented (next in line)

- Real end-to-end extraction run (needs `ANTHROPIC_API_KEY` secret set by the
  project owner).
- Per-user auth (Supabase JWT) with RLS-scoped projects; today the API runs
  against the demo project.
