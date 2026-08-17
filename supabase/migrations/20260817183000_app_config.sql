-- Server-side key/value config for edge functions (e.g. Telegram webhook
-- secret / operator chat id / bot token). Values are inserted operationally
-- and never committed to git; env vars of the same name take precedence in
-- code (TELEGRAM_WEBHOOK_SECRET <-> telegram_webhook_secret, etc.).
create table if not exists app_config (
  key text primary key,
  value text not null,
  updated_at timestamptz not null default now()
);
alter table app_config enable row level security;
-- No policies: only the service-role key (edge functions) can read or write.
