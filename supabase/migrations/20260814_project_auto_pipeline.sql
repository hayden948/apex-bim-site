-- Per-project autonomous pipeline: upload -> extraction -> (confidence-gated)
-- approve -> QA -> generate_rfa job, with no human in the loop. Off by default;
-- extractions below the confidence bar stay 'ready' for human review.
alter table public.projects
  add column if not exists auto_pipeline boolean not null default false,
  add column if not exists auto_min_confidence numeric not null default 0.9;
