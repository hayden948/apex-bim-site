-- Built .rfa artifacts: private bucket + pointer columns on families.
-- The Revit worker uploads the file after a generate_rfa job succeeds.
-- Applied live on 2026-08-11.

insert into storage.buckets (id, name, public)
values ('rfa', 'rfa', false)
on conflict (id) do nothing;

alter table families
  add column if not exists rfa_storage_key text,
  add column if not exists rfa_size_bytes bigint,
  add column if not exists rfa_revit_version text,
  add column if not exists rfa_uploaded_at timestamptz;
