-- Users mint and manage their own service tokens. Applied live on 2026-08-11.
alter table api_tokens add column if not exists created_by uuid references users(id);
