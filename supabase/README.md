# Apex API v1 (Supabase)

Deployed to the `apex-bim-studio` Supabase project (`kdqisuzydkgzkzxlctpv`) as the
edge function **`api`**. This is the server the Revit plugin's `ApexApiClient`
talks to, and it hosts the upload → extraction → approve pipeline (Doc 3).

- Base URL: `https://kdqisuzydkgzkzxlctpv.supabase.co/functions/v1/api`
- Auth (custom, `verify_jwt` disabled): `Authorization: Bearer <token>` where the
  token is one of
  - an **`apx_...` service token** for the Revit add-in (api-design.md §service
    tokens) — stored SHA-256-hashed in `public.api_tokens`, revocable per token
    (`revoked_at`), `last_used_at` tracked, optionally pinned to one project
    (`api_tokens.project_id`; null = all projects);
  - a **Supabase Auth user JWT** (signed-in user) — scoped to the caller's
    `project_members` memberships: lists are filtered per project, out-of-scope
    reads 404, writes land in the caller's project with their identity, and job
    claim/complete (worker verbs) are refused with 403; or
  - the project's **publishable key** (`sb_publishable_...`) — demo sandbox,
    scoped to the demo project only (it ships in the console's page source, so
    it is not an all-projects credential). (The legacy `eyJ...` anon JWT is no
    longer accepted; this project migrated to the new API-key format.)
- Defense in depth: the schema's RLS policies (`private.is_project_member`)
  enforce the same membership scoping for direct PostgREST access, verified
  live — a user JWT sees only member-project rows, the bare anon key sees none.
- Every mutating endpoint writes a best-effort `audit_log` row
  (entity, event, actor, `api:<caller-kind>`).
- Plugin config:
  - `APEX_API_URL=https://kdqisuzydkgzkzxlctpv.supabase.co/functions/v1/api`
  - `APEX_API_TOKEN=<apx_... service token>`
- Extraction config: the `ANTHROPIC_API_KEY` secret is set on the project and
  real extraction is live (verified end-to-end against a Square D panelboard
  submittal — 18/18 printed ratings captured). If the key is ever removed,
  `POST /v1/extractions` returns `503 EXTRACTION_NOT_CONFIGURED` and everything
  else keeps working.

### Issuing a service token

Sign in on `../app.html` and click **Mint plugin token** (or
`POST /v1/tokens {"name", "project_id"?}` with a user session). The plaintext
`apx_...` value is returned exactly once; only its SHA-256 hash is stored, and
the token is pinned to the chosen project. Manage with `GET /v1/tokens` and
`POST /v1/tokens/{id}/revoke`.

## Endpoints (contract: docs/architecture/api-design.md; QA: Doc 8)

| Route | Behavior |
|---|---|
| `GET /v1/health` | Unauthenticated liveness + config probe: `{ok, extraction_enabled, telegram_alerts}` (used by the CI health check) |
| `GET /v1/families?limit=&cursor=` | List library families (cursor-paginated; `next_cursor` in the response) |
| `GET /v1/families/{id}` | AFIS 1.0 document (metric) |
| `POST /v1/families/{id}/validate` | QA Engine: staged S→P→G→E→Z→L rules, Doc 1 §5.8 finding shape, persisted to `family_validations` |
| `POST /v1/families/{id}/exports` | Field points CSV (Doc 7) |
| `POST /v1/families/{id}/generate-rfa` | QA-gated (no certificate, no export); enqueues a `jobs` row for the Revit worker |
| `POST /v1/families/{id}/rfa` | Worker uploads the built `.rfa` (`{content_base64, revit_version?}` → `rfa` bucket, pointer on the family) |
| `GET /v1/families/{id}/rfa` | Download the built `.rfa` (binary; 404 `NO_RFA` until the worker delivers) |
| `POST /v1/uploads` | `{filename, content_base64, project_id?}` → `uploads` storage bucket + row (30 MB cap, SHA-256 recorded; identical bytes in the same project dedupe to the existing record) |
| `POST /v1/extractions` | `{upload_id}` → **202** immediately; Claude (Opus 5, structured output over the PDF) runs in the background — poll `GET /v1/extractions/{id}` until `ready`/`failed`. Rate-limited to 20/hour per caller; real model cost recorded in `cost_usd` |
| `GET /v1/extractions?status=` | List extractions (default `status=ready` — pending reviews, project-scoped) |
| `GET /v1/extractions/{id}` | Extraction status + result (+ `cost_usd`, `duration_ms`) |
| `POST /v1/extractions/{id}/approve` | Prediction → AFIS 1.0 → new `families` row. Optional body `{result}` carries the reviewer's corrections (validated, persisted, audited). With `?chain=1` the call continues through QA and (if it passes) queues the `generate_rfa` job in one round-trip — used by the Telegram `/approve` command. The AFIS is fully parametric: placed reference planes, Width/Depth dimensions labeled to family parameters, centering constraints, Height driving the extrusion, NEC zone auto-added for electrical |
| `POST /v1/extractions/{id}/reject` | Discard a pending (`ready`) extraction — status → `rejected`, audited; the upload can be re-processed any time |
| `GET /v1/projects` | Projects visible to the caller (members see theirs; unscoped machine tokens see all; the publishable key sees the demo project) |
| `PATCH /v1/projects/{id}` | `{auto_pipeline?, auto_min_confidence?}` — autonomous-pipeline settings (project admins / in-scope service tokens). With `auto_pipeline` on, an upload auto-starts extraction, and a result whose every parameter confidence ≥ the bar is auto-approved, QA'd, and queued for RFA (`api:auto` audit trail); low-confidence results wait for human review |
| `POST /v1/projects` | `{name, client_name?}` → new project with the caller as admin member (signed-in users only) |
| `GET /v1/projects/{id}/members` | Members with roles (project members only) |
| `POST /v1/projects/{id}/members` | `{email, role?}` add a member by email (project admins only; the user must have signed in once) |
| `DELETE /v1/projects/{id}/members/{uid}` | Remove a member (admins only; the last admin is protected) |
| `GET /v1/tokens` | Service tokens the caller minted (hashes never returned) |
| `POST /v1/tokens` | `{name, project_id?}` → project-scoped `apx_` token, plaintext shown once (signed-in users only) |
| `POST /v1/tokens/{id}/revoke` | Revoke a token the caller minted |
| `GET /v1/jobs?kind=&status=` | Worker polling (default `status=queued`); machine callers also requeue jobs stuck `running` > 15 min |
| `POST /v1/jobs/{id}/claim` | Atomic queued→running via `claim_job()` with attempt tracking (409 if already claimed; machine tokens only) |
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
- Per-user auth (real signed-in Supabase Auth user): with no memberships —
  empty list, upload 403 `NO_PROJECT`, claim 403 `FORBIDDEN`, out-of-scope
  family 404; as a demo-project member — sees exactly the member families,
  reads AFIS, uploads land in their project under their identity, another
  project's family stays 404; cursor pagination pages 1-at-a-time with no
  overlap; duplicate upload dedupes to the existing record.
- RLS direct (PostgREST, bypassing the API): user JWT → member rows only;
  bare anon key → zero rows.

Seed data: demo project/upload/extraction chain and one library family
`Panelboard 208V 42ckt` (`a11ce000-0000-4000-8000-000000000001`) with a full
AFIS document (NEC 110.26 front zone, electrical connector, 3 layout points).

## Telegram (human-in-the-loop from your phone)

Second edge function **`telegram-webhook`** (also custom-auth — deployed with
`--no-verify-jwt`; Telegram authenticates with the `secret_token` header set at
webhook registration). It stores every inbound message in `telegram_inbox` and
answers pipeline commands from the operator chat:

- `/pending` — extractions awaiting review (id prefix, family, lowest confidence)
- `/approve <id-prefix>` — approve + QA + queue the RFA build (`?chain=1` in one call)
- `/reject <id-prefix>` — discard a pending extraction
- `/status` — pending / in-flight / library counts

Outbound alerts (`notify()` in the `api` function) fire at every autonomous-
pipeline decision point — review needed (with ready-to-tap `/approve` command),
auto-approved + job queued, QA blocked, extraction failed, RFA built/failed —
for projects with `auto_pipeline` on.

Configuration — each value is read from the env secret when set
(`supabase secrets set ...`), else from the service-role-only **`app_config`**
table (`insert into app_config (key, value) ...`), which keeps secrets out of
this public repo:

| Env secret | `app_config` key | Purpose |
|---|---|---|
| `TELEGRAM_BOT_TOKEN` | `telegram_bot_token` | **Required for outbound alerts**; without it commands still work (webhook replies are tokenless) but pushes are silently skipped. `GET /v1/health` reports the effective state as `telegram_alerts`. |
| `TELEGRAM_CHAT_ID` | `telegram_chat_id` | Operator chat allowed to run commands (set ✓); alerts fall back to the most recent `telegram_inbox` chat. |
| `TELEGRAM_WEBHOOK_SECRET` | `telegram_webhook_secret` | Must match the `secret_token` used at webhook registration (set ✓). Missing = webhook fails closed. |

Caveat: any redeploy of either function must keep `--no-verify-jwt` (the CI
deploy workflow does) — with the platform JWT gate on, Telegram's unauthenticated
webhook POSTs are rejected before the function runs.

## Worker

The `generate_rfa` consumer is the Revit plugin's **Generate → Process Queue**
button (RFA files can only be produced inside a running Revit): it claims each
queued job, builds the family from its AFIS document, saves the `.rfa` under
`%LOCALAPPDATA%\Apex\rfa\`, and completes the job. Claim/complete are atomic
server-side, so several machines can drain the queue concurrently.

## Live end-to-end

The full pipeline has been run for real: PDF upload → Claude Opus 5 structured
extraction (18 parameters from a Square D NQ430L2C submittal) → reviewed +
corrected in the console → approve → parametric AFIS family (passed QA at
0.94) → `generate_rfa` job queued for the plugin worker. Accounts, projects,
memberships, tokens, uploads, QA, jobs, and the RFA round-trip are all live
and self-service.

Operational notes:
- Extracted `Width`/`Depth`/`Height` parameters are dropped at approve time —
  geometry's labeled dimensions carry them; a same-named parameter would
  overwrite the Revit Length param with raw internal feet (fixed in v19).
- Supabase advisor WARN: leaked-password protection (HaveIBeenPwned check) is
  disabled — enable it in Dashboard → Auth → Passwords when convenient.
