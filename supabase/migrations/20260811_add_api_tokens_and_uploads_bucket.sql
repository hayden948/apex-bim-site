-- Service tokens for the Revit add-in (api-design.md §service tokens) and the
-- storage bucket backing POST /v1/uploads. Applied live on 2026-08-11.

create extension if not exists pgcrypto;

-- Tokens are handed out once as plaintext `apx_...` strings; only the SHA-256
-- hex hash is stored. Revocation is a timestamp so history is kept.
create table if not exists api_tokens (
  id uuid primary key default gen_random_uuid(),
  name text not null,
  token_hash text not null unique,
  scopes jsonb not null default '["revit"]'::jsonb,
  created_at timestamptz not null default now(),
  last_used_at timestamptz,
  revoked_at timestamptz
);

-- Private bucket for submittal PDFs; rows in public.uploads point into it via
-- storage_key. The edge function accesses it with the service-role key.
insert into storage.buckets (id, name, public)
values ('uploads', 'uploads', false)
on conflict (id) do nothing;
