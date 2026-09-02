-- Performance-advisor cleanup (2026-08-14):
-- users.email had two identical indexes; keep the unique-constraint index.
drop index if exists public.ix_users_email;

-- Covering indexes for FKs on API-hot query paths:
--   GET /v1/tokens filters api_tokens by created_by; token auth and minting
--   touch project_id; approve links families to their extraction.
create index if not exists ix_api_tokens_created_by on public.api_tokens (created_by);
create index if not exists ix_api_tokens_project_id on public.api_tokens (project_id);
create index if not exists ix_families_extraction_id on public.families (extraction_id);
