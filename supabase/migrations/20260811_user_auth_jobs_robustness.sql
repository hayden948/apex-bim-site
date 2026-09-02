-- Per-user auth support + jobs-queue robustness. Applied live on 2026-08-11.

-- Service tokens can be scoped to one project (null = all projects, dev/admin).
alter table api_tokens add column if not exists project_id uuid references projects(id);

-- Members may read job status for families in their projects (writes stay API-only).
drop policy if exists jobs_member_select on jobs;
create policy jobs_member_select on jobs for select to authenticated
  using (exists (
    select 1 from families f
    where f.id = jobs.entity_id and private.is_project_member(f.project_id)));

-- Members may read audit entries for their own actions.
drop policy if exists audit_log_actor_select on audit_log;
create policy audit_log_actor_select on audit_log for select to authenticated
  using (actor_id = (select auth.uid()));

-- Atomic claim: queued -> running with attempt tracking, in one statement.
create or replace function public.claim_job(jid uuid)
returns setof jobs
language sql volatile security definer set search_path = public as $$
  update jobs
  set status = 'running', attempt = attempt + 1, started_at = now()
  where id = jid and status = 'queued'
  returning *;
$$;

-- Requeue jobs whose worker died mid-run (running for longer than max_age).
create or replace function public.requeue_stale_jobs(max_age interval default interval '15 minutes')
returns integer
language sql volatile security definer set search_path = public as $$
  with r as (
    update jobs
    set status = 'queued', started_at = null,
        last_error = 'requeued: worker exceeded ' || max_age::text
    where status = 'running' and started_at < now() - max_age
    returning 1)
  select count(*)::int from r;
$$;

-- Machine-only: the API calls these with the service role.
revoke execute on function public.claim_job(uuid) from public, anon, authenticated;
revoke execute on function public.requeue_stale_jobs(interval) from public, anon, authenticated;

-- The API's service-role client is the intended caller.
grant execute on function public.claim_job(uuid) to service_role;
grant execute on function public.requeue_stale_jobs(interval) to service_role;
