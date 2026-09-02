-- Fixed-window rate limiting for paid/expensive API endpoints (extractions).
-- One row per key; the window restarts when it has aged out.
create table if not exists public.rate_limits (
  key text primary key,
  window_start timestamptz not null,
  count integer not null default 0
);
alter table public.rate_limits enable row level security; -- service-role only; no policies

create or replace function public.check_rate_limit(rl_key text, rl_limit integer, rl_window_seconds integer)
returns boolean
language plpgsql
security definer
set search_path = public
as $$
declare
  v_count integer;
begin
  insert into rate_limits as r (key, window_start, count)
  values (rl_key, now(), 1)
  on conflict (key) do update set
    count = case when r.window_start < now() - make_interval(secs => rl_window_seconds)
                 then 1 else r.count + 1 end,
    window_start = case when r.window_start < now() - make_interval(secs => rl_window_seconds)
                        then now() else r.window_start end
  returning r.count into v_count;
  return v_count <= rl_limit;
end $$;

revoke execute on function public.check_rate_limit(text, integer, integer) from public;
grant execute on function public.check_rate_limit(text, integer, integer) to service_role;
