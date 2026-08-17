// Telegram webhook: captures every inbound message into telegram_inbox, and
// answers pipeline commands (/pending, /approve, /reject, /status) from the
// operator's chat. Replies use Telegram's webhook-response shorthand
// ({method: "sendMessage", ...}) so no bot token is needed here; proactive
// alerts (the other direction) live in the api function behind
// TELEGRAM_BOT_TOKEN.
import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import { createClient } from "jsr:@supabase/supabase-js@2";

const supabase = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

// Secrets come from env when set, else from the service-role-only app_config
// table (kept out of git — this repo is public). Missing config fails closed.
const configCache = new Map<string, string | null>();
async function config(envName: string, key: string): Promise<string | null> {
  const fromEnv = Deno.env.get(envName);
  if (fromEnv) return fromEnv;
  if (!configCache.has(key)) {
    const { data } = await supabase.from("app_config").select("value").eq("key", key).maybeSingle();
    configCache.set(key, data?.value ?? null);
  }
  return configCache.get(key) ?? null;
}

/** Call the sibling api function; the service-role key authorizes as an internal unscoped service. */
// deno-lint-ignore no-explicit-any
async function api(path: string, method: string, body?: unknown): Promise<{ status: number; body: any }> {
  const res = await fetch(`${Deno.env.get("SUPABASE_URL")}/functions/v1/api/v1${path}`, {
    method,
    headers: {
      authorization: `Bearer ${Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")}`,
      "content-type": "application/json",
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  return { status: res.status, body: await res.json().catch(() => ({})) };
}

// deno-lint-ignore no-explicit-any
async function pendingRows(): Promise<any[]> {
  const { data } = await supabase.from("extractions")
    .select("id, claude_result, created_at")
    .eq("status", "ready")
    .order("created_at", { ascending: false })
    .limit(50);
  return data ?? [];
}

// deno-lint-ignore no-explicit-any
function lowestConf(pred: any): number {
  const params = Array.isArray(pred?.parameters) ? pred.parameters : [];
  // deno-lint-ignore no-explicit-any
  return params.reduce((m: number, p: any) =>
    Math.min(m, typeof p?.confidence === "number" ? p.confidence : 0), 1);
}

const HELP = "Apex BIM pipeline commands:\n" +
  "/pending — extractions awaiting review\n" +
  "/approve <id> — approve, QA, and queue the RFA build\n" +
  "/reject <id> — discard a pending extraction\n" +
  "/status — pipeline counts";

async function handleCommand(text: string): Promise<string> {
  const [cmd, arg] = text.trim().split(/\s+/);
  const verb = cmd.toLowerCase().replace(/@.*$/, "");

  if (verb === "/pending") {
    const rows = await pendingRows();
    if (!rows.length) return "Nothing is waiting for review. ✨";
    return "Waiting for review:\n" + rows.slice(0, 8).map((r) => {
      const pred = r.claude_result ?? {};
      return `• ${r.id.slice(0, 8)} — ${pred.family_name ?? "?"} (${pred.category ?? "?"}), ` +
        `min conf ${lowestConf(pred).toFixed(2)}`;
    }).join("\n") +
      (rows.length > 8 ? `\n…and ${rows.length - 8} more.` : "") +
      "\n\n/approve <id> · /reject <id>";
  }

  if (verb === "/approve" || verb === "/reject") {
    if (!arg) return `Usage: ${verb} <id prefix from /pending>`;
    const rows = await pendingRows();
    const hits = rows.filter((r) => r.id.startsWith(arg.toLowerCase()));
    if (hits.length === 0) return `No pending extraction matches "${arg}" — try /pending.`;
    if (hits.length > 1) return `"${arg}" matches ${hits.length} extractions — use more characters.`;
    const ex = hits[0];
    const name = ex.claude_result?.family_name ?? ex.id.slice(0, 8);

    if (verb === "/reject") {
      const r = await api(`/extractions/${ex.id}/reject`, "POST", {});
      return r.status < 300
        ? `🗑 Rejected "${name}". The upload can be re-processed any time.`
        : `Reject failed (${r.status}): ${r.body?.error?.message ?? "unknown error"}`;
    }

    const r = await api(`/extractions/${ex.id}/approve?chain=1`, "POST", {});
    if (r.status >= 300) {
      return `Approve failed (${r.status}): ${r.body?.error?.message ?? "unknown error"}`;
    }
    const finalName = r.body?.family?.family_name ?? name;
    const qa = r.body?.qa;
    if (r.body?.job_id) {
      return `✅ Approved "${finalName}" — QA ${qa?.score} — RFA job ${r.body.job_id.slice(0, 8)} queued.`;
    }
    return `✅ Approved "${finalName}" — but QA blocked the build (score ${qa?.score}). Fix it in the console.`;
  }

  if (verb === "/status") {
    const [pend, jobs, fams] = await Promise.all([
      supabase.from("extractions").select("id", { count: "exact", head: true }).eq("status", "ready"),
      supabase.from("jobs").select("id", { count: "exact", head: true }).in("status", ["queued", "running"]),
      supabase.from("families").select("id", { count: "exact", head: true }).is("deleted_at", null),
    ]);
    return `Apex status — ${pend.count ?? 0} awaiting review · ${jobs.count ?? 0} jobs in flight · ` +
      `${fams.count ?? 0} families in the library.`;
  }

  return HELP;
}

Deno.serve(async (req: Request) => {
  if (req.method !== "POST") {
    return new Response("ok", { status: 200 });
  }
  // Telegram sends this header when the webhook was registered with secret_token.
  // No configured secret = fail closed.
  const secret = await config("TELEGRAM_WEBHOOK_SECRET", "telegram_webhook_secret");
  if (!secret || req.headers.get("x-telegram-bot-api-secret-token") !== secret) {
    return new Response("forbidden", { status: 403 });
  }

  // deno-lint-ignore no-explicit-any
  let update: any;
  try {
    update = await req.json();
  } catch {
    return new Response("bad request", { status: 400 });
  }

  const msg = update.message ?? update.edited_message ?? update.channel_post;
  // Always 200 so Telegram doesn't retry forever on updates we ignore
  if (!msg || !msg.chat) {
    return new Response("ignored", { status: 200 });
  }

  const text: string | null =
    msg.text ?? msg.caption ??
    (msg.voice ? "[voice message]" : msg.photo ? "[photo]" : msg.document ? "[document]" : null);

  const { error } = await supabase.from("telegram_inbox").upsert(
    {
      tg_message_id: msg.message_id,
      chat_id: msg.chat.id,
      from_name: [msg.from?.first_name, msg.from?.last_name].filter(Boolean).join(" ") ||
        msg.from?.username || null,
      text,
      sent_at: new Date(msg.date * 1000).toISOString(),
    },
    { onConflict: "chat_id,tg_message_id" },
  );
  if (error) console.error("inbox insert failed", error);

  // Commands are only honored from the operator chat; everyone else is inbox-only.
  const allowedChat = await config("TELEGRAM_CHAT_ID", "telegram_chat_id");
  if (text?.startsWith("/") && allowedChat && String(msg.chat.id) === allowedChat) {
    let reply: string;
    try {
      reply = await handleCommand(text);
    } catch (e) {
      console.error("command failed", e);
      reply = `Command failed: ${e instanceof Error ? e.message : String(e)}`;
    }
    return new Response(
      JSON.stringify({ method: "sendMessage", chat_id: msg.chat.id, text: reply }),
      { status: 200, headers: { "content-type": "application/json" } },
    );
  }

  return new Response("ok", { status: 200 });
});
