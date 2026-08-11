-- Poison-job guard: after 5 claims a stale running job fails permanently
-- instead of requeueing forever. Applied live on 2026-08-11.
create or replace function public.requeue_stale_jobs(max_age interval default interval '15 minutes')
returns integer
language sql volatile security definer set search_path = public as $$
  with failed as (
    update jobs
    set status = 'failed', finished_at = now(),
        last_error = 'gave up: worker died ' || attempt || ' times'
    where status = 'running' and started_at < now() - max_age and attempt >= 5
    returning 1
  ),
  requeued as (
    update jobs
    set status = 'queued', started_at = null,
        last_error = 'requeued: worker exceeded ' || max_age::text
    where status = 'running' and started_at < now() - max_age and attempt < 5
    returning 1
  )
  select coalesce((select count(*) from requeued), 0)::int;
$$;

grant execute on function public.requeue_stale_jobs(interval) to service_role;
