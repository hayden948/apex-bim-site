// apex-purge — the data-deletion half of the Apex data-handling contract.
// Contract + hop map: revit-mcp-server/docs/ship/DATA_FLOW.md.
//
// Deployed as a SIBLING of the `api` function (whose deployed v29 source was not in
// the repo when this was built — see DATA_FLOW.md F0). Merge into `api` once its
// source of truth lives in-repo; until then this function owns purge exclusively.
//
// Routes (base = https://<ref>.supabase.co/functions/v1/apex-purge):
//   GET    /health                 liveness (no auth)
//   DELETE /uploads/:id            purge one upload + every derived artifact
//   DELETE /projects/:id/data      purge ALL drawing data for a project
//
// What a purge deletes, in order (storage first, so a mid-run failure leaves
// retryable DB rows rather than orphaned, unfindable storage objects):
//   1. storage `uploads` objects (the drawing PDFs)
//   2. storage `rfa` objects (binaries derived from the drawings)
//   3. family_validations, jobs, families, extractions, uploads rows
//   4. audit_log rows for the purged entities (their new_value embeds filenames
//      and family names — drawing-derived content)
// One counts-only audit row records that the purge happened.
//
// Auth (verify_jwt disabled — custom auth, mirroring `api`):
//   - apx_... service token (sha256 in public.api_tokens, not revoked; must be
//     unscoped or scoped to the target project), or
//   - a Supabase Auth user JWT whose user is an ADMIN of the target project.
//   The publishable/anon key is NEVER accepted: deletion is not a demo operation.

import { createClient } from "npm:@supabase/supabase-js@2";

const supabase = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const CORS: Record<string, string> = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, content-type",
  "Access-Control-Allow-Methods": "GET, DELETE, OPTIONS",
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body, null, 2), {
    status, headers: { "Content-Type": "application/json", ...CORS },
  });
}

function fail(status: number, code: string, message: string): Response {
  return json({ error: { code, message } }, status);
}

async function sha256hex(s: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return Array.from(new Uint8Array(digest)).map((b) => b.toString(16).padStart(2, "0")).join("");
}

type Actor =
  | { kind: "service"; tokenId: string; projectId: string | null }
  | { kind: "user"; userId: string };

/** May this caller purge this project's data? (See auth notes in the header.) */
async function authorizePurge(req: Request, projectId: string): Promise<{ actor: Actor } | { deny: Response }> {
  const header = req.headers.get("Authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice(7).trim() : "";
  if (!token) return { deny: fail(401, "UNAUTHENTICATED", "Missing Authorization: Bearer token") };

  if (token.startsWith("apx_")) {
    const hash = await sha256hex(token);
    const { data } = await supabase.from("api_tokens")
      .select("id, project_id").eq("token_hash", hash).is("revoked_at", null).maybeSingle();
    if (!data) return { deny: fail(401, "INVALID_TOKEN", "Unknown or revoked service token") };
    if (data.project_id && data.project_id !== projectId)
      return { deny: fail(403, "FORBIDDEN", "Service token is scoped to a different project") };
    return { actor: { kind: "service", tokenId: data.id, projectId: data.project_id ?? null } };
  }

  const { data: userData, error } = await supabase.auth.getUser(token);
  if (error || !userData?.user)
    return { deny: fail(401, "INVALID_TOKEN", "Token is not a service token (apx_...) or a valid user session") };
  const { data: mem } = await supabase.from("project_members")
    .select("role").eq("project_id", projectId).eq("user_id", userData.user.id).maybeSingle();
  if (mem?.role !== "admin")
    return { deny: fail(403, "FORBIDDEN", "Purging requires a project admin or an in-scope service token") };
  return { actor: { kind: "user", userId: userData.user.id } };
}

interface UploadRow { id: string; storage_key: string | null }

async function purgeUploads(uploadRows: UploadRow[], projectId: string, actor: Actor) {
  const uploadIds = uploadRows.map((u) => u.id);
  const counts = {
    project_id: projectId, uploads: 0, extractions: 0, families: 0,
    storage_objects: 0, audit_rows: 0,
  };
  if (uploadIds.length === 0) return counts;

  const { data: exRows } = await supabase.from("extractions").select("id").in("upload_id", uploadIds);
  const exIds = (exRows ?? []).map((e) => e.id);
  const famRows = exIds.length
    ? (await supabase.from("families").select("id, rfa_storage_key").in("extraction_id", exIds)).data ?? []
    : [];
  const famIds = famRows.map((f) => f.id);

  // 1-2. Storage objects first (see header for why).
  const uploadKeys = uploadRows.map((u) => u.storage_key).filter((k): k is string => !!k);
  if (uploadKeys.length) {
    const { error } = await supabase.storage.from("uploads").remove(uploadKeys);
    if (error) throw new Error(`storage purge (uploads): ${error.message}`);
  }
  const rfaKeys = famRows.map((f) => f.rfa_storage_key).filter((k): k is string => !!k);
  if (rfaKeys.length) {
    const { error } = await supabase.storage.from("rfa").remove(rfaKeys);
    if (error) throw new Error(`storage purge (rfa): ${error.message}`);
  }

  // 3. DB rows, leaf-first.
  if (famIds.length) {
    await supabase.from("family_validations").delete().in("family_id", famIds);
    await supabase.from("jobs").delete().in("entity_id", famIds);
    const { error } = await supabase.from("families").delete().in("id", famIds);
    if (error) throw new Error(`db purge (families): ${error.message}`);
  }
  if (exIds.length) {
    const { error } = await supabase.from("extractions").delete().in("id", exIds);
    if (error) throw new Error(`db purge (extractions): ${error.message}`);
  }
  {
    const { error } = await supabase.from("uploads").delete().in("id", uploadIds);
    if (error) throw new Error(`db purge (uploads): ${error.message}`);
  }

  // 4. Content-bearing audit rows for the purged entities.
  const purgedIds = [...uploadIds, ...exIds, ...famIds];
  let auditRows = 0;
  for (let i = 0; i < purgedIds.length; i += 100) {
    const chunk = purgedIds.slice(i, i + 100);
    const { data } = await supabase.from("audit_log").delete().in("entity_id", chunk).select("id");
    auditRows += (data ?? []).length;
  }

  counts.uploads = uploadIds.length;
  counts.extractions = exIds.length;
  counts.families = famIds.length;
  counts.storage_objects = uploadKeys.length + rfaKeys.length;
  counts.audit_rows = auditRows;

  // Counts-only record that the purge happened (no filenames, no family names).
  await supabase.from("audit_log").insert({
    entity_type: "project", entity_id: projectId, field: "data_purged",
    new_value: counts,
    actor_id: actor.kind === "user" ? actor.userId : "00000000-0000-4000-8000-000000000001",
    reason: `apex-purge:${actor.kind}`,
  });
  return counts;
}

Deno.serve(async (req: Request) => {
  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: CORS });

  const parts = new URL(req.url).pathname.split("/").filter(Boolean);
  // Strip the function mount ("apex-purge", possibly preceded by "functions/v1").
  const i = parts.indexOf("apex-purge");
  const route = i >= 0 ? parts.slice(i + 1) : parts;

  if (route[0] === "health" && req.method === "GET") {
    return json({ ok: true, service: "apex-purge", version: "1.0.0" });
  }

  // DELETE /uploads/:id
  if (route[0] === "uploads" && route.length === 2 && req.method === "DELETE") {
    const uploadId = route[1];
    if (!UUID_RE.test(uploadId)) return fail(400, "INVALID_UPLOAD_ID", "Upload id must be a UUID");
    const { data: upload } = await supabase.from("uploads")
      .select("id, storage_key, project_id").eq("id", uploadId).maybeSingle();
    if (!upload) return fail(404, "UPLOAD_NOT_FOUND", `No upload ${uploadId}`);
    const auth = await authorizePurge(req, upload.project_id);
    if ("deny" in auth) return auth.deny;
    try {
      const counts = await purgeUploads([upload], upload.project_id, auth.actor);
      return json({ purged: true, ...counts });
    } catch (e) {
      return fail(500, "PURGE_FAILED", e instanceof Error ? e.message : String(e));
    }
  }

  // DELETE /projects/:id/data
  if (route[0] === "projects" && route.length === 3 && route[2] === "data" && req.method === "DELETE") {
    const projId = route[1];
    if (!UUID_RE.test(projId)) return fail(400, "INVALID_PROJECT_ID", "Project id must be a UUID");
    const auth = await authorizePurge(req, projId);
    if ("deny" in auth) return auth.deny;
    const { data: uploadRows } = await supabase.from("uploads")
      .select("id, storage_key").eq("project_id", projId);
    try {
      const counts = await purgeUploads(uploadRows ?? [], projId, auth.actor);
      return json({ purged: true, ...counts });
    } catch (e) {
      return fail(500, "PURGE_FAILED", e instanceof Error ? e.message : String(e));
    }
  }

  return fail(404, "NOT_FOUND", "Routes: GET /health, DELETE /uploads/:id, DELETE /projects/:id/data");
});
