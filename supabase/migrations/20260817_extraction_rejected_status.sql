-- Reviewers can now discard a pending extraction (console or Telegram /reject).
alter table extractions drop constraint if exists ck_extractions_status_valid;
alter table extractions add constraint ck_extractions_status_valid
  check (status in ('queued', 'processing', 'ready', 'approved', 'rejected', 'failed'));
