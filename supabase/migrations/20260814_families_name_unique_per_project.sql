-- Family names were globally unique across all projects (one tenant's
-- "Panelboard" blocked every other tenant). Scope the uniqueness per project.
drop index if exists public.uq_families_name_active;
create unique index uq_families_name_active
  on public.families (project_id, family_name)
  where (deleted_at is null);
