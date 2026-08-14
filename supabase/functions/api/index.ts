// Apex BIM Studio API v1 — consumed by the Revit plugin's ApexApiClient and the
// upload→extraction pipeline (Doc 3). Contract: docs/architecture/api-design.md;
// QA rules: Doc 8 + Doc 1 §5. Error envelope: { error: { code, message, details? } }.
//
// Auth (custom — verify_jwt disabled): Authorization: Bearer <token> where token is
//   - an apx_... service token (hashed in public.api_tokens; optionally project-scoped),
//   - a Supabase Auth user JWT (scoped to the caller's project memberships), or
//   - the project's publishable key (SUPABASE_ANON_KEY env, sb_publishable_...;
//     demo, unrestricted).
//
// Routes (base = https://<ref>.supabase.co/functions/v1/api; /api/v1/* also accepted):
//   GET  /v1/families?limit=&cursor=        list library families (cursor-paginated)
//   GET  /v1/families/:id                   AFIS document
//   POST /v1/families/:id/validate          QA Engine: staged rule checks (Doc 8)
//   POST /v1/families/:id/exports           field points export (Doc 7), {"format":"csv"}
//   POST /v1/families/:id/generate-rfa      QA-gated; enqueues a jobs row (Doc 3 Stage 11)
//   POST /v1/families/:id/rfa               worker uploads the built .rfa {"content_base64","revit_version"?}
//   GET  /v1/families/:id/rfa               download the built .rfa (binary)
//   POST /v1/uploads                        {"filename","content_base64"} → storage + uploads row
//   POST /v1/extractions                    {"upload_id"} → Claude extraction → extractions row
//   GET  /v1/extractions?status=            list extractions (default status=ready: pending reviews)
//   GET  /v1/extractions/:id                extraction status + result
//   POST /v1/extractions/:id/approve        extraction → AFIS → families row (library)
//   GET  /v1/projects                       projects visible to the caller
//   POST /v1/projects                       {"name","client_name"?} → project (+ caller as admin member)
//   GET  /v1/projects/:id/members           members of a project (members only)
//   POST /v1/projects/:id/members           {"email","role"?} add member (admins only)
//   DELETE /v1/projects/:id/members/:uid    remove member (admins only; last admin protected)
//   GET  /v1/tokens                         service tokens the caller minted
//   POST /v1/tokens                         {"name","project_id"?} → apx_ token (plaintext shown once)
//   POST /v1/tokens/:id/revoke              revoke a token the caller minted
//   GET  /v1/jobs?kind=&status=             list jobs (worker polling)
//   POST /v1/jobs/:id/claim                 queued → running
//   POST /v1/jobs/:id/complete              {"status":"succeeded"|"failed","error"?}
import { createClient } from "jsr:@supabase/supabase-js@2";
import Anthropic from "npm:@anthropic-ai/sdk";

const supabase = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const NEC_MIN_CLEARANCE_M = 0.9144; // NEC 110.26 working space, 36 in
const DEMO_PROJECT = "00000000-0000-4000-8000-000000000002";
const DEMO_USER = "00000000-0000-4000-8000-000000000001";

// Browser clients (the app.html pipeline console) need CORS; tokens are sent via
// the Authorization header, never cookies, so a wildcard origin is safe here.
const CORS: Record<string, string> = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, content-type",
  "Access-Control-Allow-Methods": "GET, POST, DELETE, OPTIONS",
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: { "Content-Type": "application/json", ...CORS },
  });
}

function fail(status: number, code: string, message: string, details?: unknown): Response {
  return json({ error: { code, message, details } }, status);
}

function csvEscape(s: string): string {
  return /[",\n]/.test(s) ? '"' + s.replaceAll('"', '""') + '"' : s;
}

async function sha256hex(s: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return Array.from(new Uint8Array(digest)).map((b) => b.toString(16).padStart(2, "0")).join("");
}

/**
 * Auth context. Three caller classes (api-design.md):
 *  - service: apx_ machine token (Revit worker); optionally pinned to one project.
 *  - user:    Supabase Auth JWT; scoped to the caller's project memberships.
 *  - anon:    the project publishable key; demo/back-compat, unrestricted like before.
 */
type AuthCtx =
  | { kind: "service"; projectId: string | null }
  | { kind: "user"; userId: string; projectIds: string[] }
  | { kind: "anon" };

/** Custom auth: apx_ service tokens (hashed in DB), Supabase user JWTs, or the anon key. */
async function authorize(req: Request): Promise<{ ctx: AuthCtx } | { deny: Response }> {
  const header = req.headers.get("Authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice(7).trim() : "";
  if (!token) return { deny: fail(401, "UNAUTHENTICATED", "Missing Authorization: Bearer token") };

  if (token.startsWith("apx_")) {
    const hash = await sha256hex(token);
    const { data } = await supabase
      .from("api_tokens")
      .select("id, project_id")
      .eq("token_hash", hash)
      .is("revoked_at", null)
      .maybeSingle();
    if (!data) return { deny: fail(401, "INVALID_TOKEN", "Unknown or revoked service token") };
    await supabase.from("api_tokens").update({ last_used_at: new Date().toISOString() }).eq("id", data.id);
    return { ctx: { kind: "service", projectId: data.project_id ?? null } };
  }

  if (token === Deno.env.get("SUPABASE_ANON_KEY")) return { ctx: { kind: "anon" } };

  // Supabase Auth JWT (signed-in user).
  const { data: userData, error: userErr } = await supabase.auth.getUser(token);
  if (userErr || !userData?.user) {
    return { deny: fail(401, "INVALID_TOKEN", "Token is not a service token (apx_...), the project key, or a valid user session") };
  }
  const u = userData.user;
  // App tables FK created_by/actor_id to public.users — keep a mirror row.
  await supabase.from("users").upsert({
    id: u.id,
    email: u.email ?? `${u.id}@users.invalid`,
    full_name: (u.user_metadata?.full_name as string | undefined) ?? u.email ?? "User",
  }, { onConflict: "id" });
  const { data: memberships } = await supabase
    .from("project_members").select("project_id").eq("user_id", u.id);
  return { ctx: { kind: "user", userId: u.id, projectIds: (memberships ?? []).map((m) => m.project_id) } };
}

/** Projects this caller may touch; null = unrestricted (anon/demo, unscoped service token). */
function projectScope(ctx: AuthCtx): string[] | null {
  if (ctx.kind === "user") return ctx.projectIds;
  if (ctx.kind === "service") return ctx.projectId ? [ctx.projectId] : null;
  return null;
}

function actorId(ctx: AuthCtx): string {
  return ctx.kind === "user" ? ctx.userId : DEMO_USER;
}

/** Project new records land in when the request doesn't name one. */
function defaultProject(ctx: AuthCtx): string | null {
  if (ctx.kind === "user") return ctx.projectIds[0] ?? null;
  if (ctx.kind === "service") return ctx.projectId ?? DEMO_PROJECT;
  return DEMO_PROJECT;
}

function inScope(ctx: AuthCtx, projectId: string | null): boolean {
  const scope = projectScope(ctx);
  return scope === null || (projectId !== null && scope.includes(projectId));
}

/** Best-effort audit trail (Doc 1); an audit failure never fails the request. */
async function audit(ctx: AuthCtx, entityType: string, entityId: string, event: string, value?: unknown) {
  try {
    await supabase.from("audit_log").insert({
      entity_type: entityType,
      entity_id: entityId,
      field: event,
      new_value: value ?? null,
      actor_id: actorId(ctx),
      reason: `api:${ctx.kind}`,
    });
  } catch (e) {
    console.error("audit insert failed", e);
  }
}

// ---------- QA Engine (Doc 8: staged S→P→G→E→Z→L; Doc 1 §5.8 finding shape) ----------

interface Finding {
  rule: string;
  severity: "info" | "warning" | "error";
  category: string;
  path: string;
  passed: boolean;
  message: string;
  fix_hint?: string;
  auto_fixable?: boolean;
}

// deno-lint-ignore no-explicit-any
function runQaPipeline(afis: any): Finding[] {
  const f: Finding[] = [];
  const add = (rule: string, severity: Finding["severity"], category: string, path: string,
    passed: boolean, message: string, fix_hint?: string, auto_fixable?: boolean) =>
    f.push({ rule, severity, category, path, passed, message, fix_hint, auto_fixable });

  add("S-1", "error", "schema", "$", !!afis,
    afis ? "AFIS document present" : "No AFIS document stored for this family",
    "Re-run extraction/generation to produce an AFIS document");
  if (!afis) return f;
  const versionOk = typeof afis.afis_version === "string" && afis.afis_version.startsWith("1.");
  add("S-2", "error", "schema", "$.afis_version", versionOk,
    versionOk ? `AFIS version ${afis.afis_version}` : "afis_version missing or unsupported");
  if (!versionOk) return f;

  add("P-1", "error", "profile", "$.identity.name", !!afis.identity?.name,
    afis.identity?.name ? "identity.name present" : "identity.name is required");
  add("P-2", "error", "profile", "$.identity.category", !!afis.identity?.category,
    afis.identity?.category ? "identity.category present" : "identity.category is required");
  const params: any[] = afis.parameters ?? [];
  add("P-3", "warning", "profile", "$.parameters",
    params.some((p) => p?.name === "Apex_AfisId"),
    "Families should carry an Apex_AfisId stamp parameter",
    "Add an Apex_AfisId Text parameter holding the family id", true);

  const min = afis.geometry?.bbox?.min, max = afis.geometry?.bbox?.max;
  const bboxOk = Array.isArray(min) && Array.isArray(max) && min.length === 3 && max.length === 3 &&
    max.every((v: number, i: number) => typeof v === "number" && v > (min[i] as number));
  add("G-1", "error", "geometry", "$.geometry.bbox", bboxOk,
    bboxOk ? "Bounding box has positive extents" : "bbox min/max invalid or non-positive extents");
  for (const c of afis.connectors ?? []) {
    const ok = Array.isArray(c.location) && c.location.length === 3 &&
      Array.isArray(c.direction) && c.direction.length === 3 &&
      ["duct", "pipe", "electrical"].includes(c.system);
    add("G-2", "error", "geometry", `$.connectors[id=${c.id ?? "?"}]`, ok,
      ok ? `Connector '${c.id}' valid (${c.system})` : `Connector '${c.id}' missing system/location/direction`);
  }

  const zones: any[] = afis.zones ?? [];
  const isElectrical = (afis.identity?.category ?? "").toLowerCase().includes("electrical") ||
    (afis.connectors ?? []).some((c: any) => c.system === "electrical");
  if (isElectrical) {
    // E — electrical profile (Doc 8 stage E). Warnings, not gates: extraction-
    // derived families legitimately start without connectors modeled.
    add("E-1", "warning", "electrical", "$.parameters",
      params.some((p) => /volt/i.test(p?.name ?? "")),
      "Voltage parameter present",
      "Electrical equipment should carry a voltage parameter (e.g. Apex_Voltage)",
      true);
    add("E-2", "warning", "electrical", "$.connectors",
      (afis.connectors ?? []).some((c: any) => c.system === "electrical"),
      "Electrical connector present",
      "Add an electrical connector so the family can join power circuits", true);
  }
  if (isElectrical) {
    const necZone = zones.find((z) => ["service_access", "clearance_code", "electrical_nec"].includes(z.type));
    add("Z-1", "error", "spatial", "$.zones", !!necZone,
      necZone ? `NEC working-space zone present ('${necZone.id}')`
        : "Electrically powered object missing NEC 110.26 clearance zone",
      "Add electrical_nec zone, depth ≥ 0.91m", true);
    if (necZone) {
      const deep = typeof necZone.depth === "number" && necZone.depth >= NEC_MIN_CLEARANCE_M;
      add("Z-2", "error", "spatial", `$.zones[id=${necZone.id}].depth`, deep,
        deep ? `Clearance depth ${necZone.depth} m meets NEC 110.26 minimum`
          : `Service/clearance zone depth below required minimum (${NEC_MIN_CLEARANCE_M} m)`,
        "Increase zone depth to the code/manufacturer minimum", true);
    }
  }
  for (const z of zones) {
    const ok = ["front", "back", "left", "right"].includes(z.face) && typeof z.depth === "number" && z.depth > 0;
    add("Z-3", "error", "spatial", `$.zones[id=${z.id ?? "?"}]`, ok,
      ok ? `Zone '${z.id}' well-formed (${z.face}, ${z.depth} m)`
        : `Zone '${z.id}' needs face front/back/left/right and positive depth`);
  }

  const points: any[] = afis.points ?? [];
  add("L-1", "warning", "layout", "$.points",
    points.some((p) => ["layout", "control"].includes(p.type)),
    "A control/layout point should be present for field stakeout",
    "Add a layout point at the placement centerline", true);
  add("L-2", "info", "layout", "$.points",
    points.every((p) => !!p.point_code),
    points.length ? "Point codes present on all points" : "No points defined");

  return f;
}

// ---------- Extraction (Doc 3: submittal PDF → structured prediction → AFIS) ----------

const EXTRACTION_SCHEMA = {
  type: "object",
  properties: {
    family_name: { type: "string" },
    category: { type: "string" },
    family_template: { type: "string" },
    geometry: {
      type: "object",
      properties: {
        primitive: { type: "string", enum: ["box"] },
        width: { type: "object", properties: { value: { type: "number" }, unit: { type: "string", enum: ["in", "mm", "cm", "m", "ft"] } }, required: ["value", "unit"], additionalProperties: false },
        depth: { type: "object", properties: { value: { type: "number" }, unit: { type: "string", enum: ["in", "mm", "cm", "m", "ft"] } }, required: ["value", "unit"], additionalProperties: false },
        height: { type: "object", properties: { value: { type: "number" }, unit: { type: "string", enum: ["in", "mm", "cm", "m", "ft"] } }, required: ["value", "unit"], additionalProperties: false },
      },
      required: ["primitive", "width", "depth", "height"],
      additionalProperties: false,
    },
    parameters: {
      type: "array",
      items: {
        type: "object",
        properties: {
          name: { type: "string" },
          spec_type: { type: "string", enum: ["Text", "Length", "Integer", "Number"] },
          group: { type: "string", enum: ["Dimensions", "Electrical", "Electrical - Loads", "Constraints", "Identity Data"] },
          is_instance: { type: "boolean" },
          value: { type: "string" },
          units: { type: "string" },
          confidence: { type: "number" },
        },
        required: ["name", "spec_type", "group", "is_instance", "value"],
        additionalProperties: false,
      },
    },
    warnings: { type: "array", items: { type: "string" } },
  },
  required: ["family_name", "category", "geometry", "parameters"],
  additionalProperties: false,
} as const;

const EXTRACTION_PROMPT =
  `You are the extraction stage of the Apex BIM Studio pipeline. From this equipment
submittal PDF, extract the data needed to generate a parametric Revit family.

- family_name: manufacturer model or a concise descriptive name.
- category: the Revit category (e.g. "Electrical Equipment", "Mechanical Equipment").
- geometry: the overall bounding box of the unit as a box primitive, with the
  dimensions and units exactly as printed on the drawing.
- parameters: one entry for EVERY engineering value printed on the sheet — do not
  summarize or keep only the most important. A typical submittal yields 5-10:
  manufacturer, catalog/model number, system voltage, main rating (A), branch
  circuit count, short-circuit rating (kA), frequency (Hz), phases/wires, weight,
  enclosure type, mounting. Ratings with units go in as spec_type Number/Integer
  with the bare numeral in value and the unit in units (e.g. value "100",
  units "A"); compound ratings like "208Y/120 VAC" stay Text. Include a
  confidence 0-1 per parameter.
- warnings: anything ambiguous, missing, or assumed.

Extract only what the document supports; do not invent values. Completeness of
parameters matters: every rating on the sheet that an electrical engineer would
put on a Revit schedule should be captured.`;

async function runExtraction(pdfBase64: string): Promise<{ ok: true; result: unknown } | { ok: false; resp: Response }> {
  const apiKey = Deno.env.get("ANTHROPIC_API_KEY");
  if (!apiKey) {
    return {
      ok: false,
      resp: fail(503, "EXTRACTION_NOT_CONFIGURED",
        "ANTHROPIC_API_KEY is not set on this Supabase project. Set it with: supabase secrets set ANTHROPIC_API_KEY=sk-ant-..."),
    };
  }
  const anthropic = new Anthropic({ apiKey });
  const response = await anthropic.messages.create({
    model: "claude-opus-5",
    max_tokens: 16000,
    output_config: { format: { type: "json_schema", schema: EXTRACTION_SCHEMA } },
    messages: [{
      role: "user",
      content: [
        { type: "document", source: { type: "base64", media_type: "application/pdf", data: pdfBase64 } },
        { type: "text", text: EXTRACTION_PROMPT },
      ],
    }],
  });

  if (response.stop_reason === "refusal") {
    return { ok: false, resp: fail(422, "EXTRACTION_REFUSED", "The model declined to process this document") };
  }
  const textBlock = response.content.find((b: { type: string }) => b.type === "text") as { text: string } | undefined;
  if (!textBlock) return { ok: false, resp: fail(502, "EXTRACTION_EMPTY", "Model returned no text content") };
  return { ok: true, result: JSON.parse(textBlock.text) };
}

const M_PER: Record<string, number> = { in: 0.0254, mm: 0.001, cm: 0.01, m: 1, ft: 0.3048 };
const toMeters = (v: number, unit: string) => v * (M_PER[unit] ?? 0.0254);

/** Shape check for a prediction about to become a family (raw or user-corrected). */
// deno-lint-ignore no-explicit-any
function predProblem(p: any): string | null {
  if (typeof p?.family_name !== "string" || !p.family_name.trim()) return "family_name is required";
  if (typeof p?.category !== "string" || !p.category.trim()) return "category is required";
  for (const k of ["width", "depth", "height"]) {
    const g = p?.geometry?.[k];
    if (typeof g?.value !== "number" || !(g.value > 0) || !(g?.unit in M_PER))
      return `geometry.${k} must be {value > 0, unit one of ${Object.keys(M_PER).join("/")}}`;
  }
  if (p.parameters != null && !Array.isArray(p.parameters)) return "parameters must be an array";
  return null;
}

/** Convert an approved extraction (pred shape) into an AFIS 1.0 document. */
// deno-lint-ignore no-explicit-any
function predToAfis(familyId: string, pred: any): unknown {
  const w = toMeters(pred.geometry.width.value, pred.geometry.width.unit);
  const d = toMeters(pred.geometry.depth.value, pred.geometry.depth.unit);
  const h = toMeters(pred.geometry.height.value, pred.geometry.height.unit);
  const isElectrical = (pred.category ?? "").toLowerCase().includes("electrical");

  const groupMap: Record<string, string> = {
    "Dimensions": "PG_GEOMETRY", "Electrical": "PG_ELECTRICAL", "Electrical - Loads": "PG_ELECTRICAL",
    "Constraints": "PG_IDENTITY_DATA", "Identity Data": "PG_IDENTITY_DATA",
  };
  const dataTypeMap: Record<string, string> = { Text: "Text", Length: "Length", Integer: "Integer", Number: "Number" };

  return {
    afis_version: "1.0.0",
    id: familyId,
    tier: "type",
    identity: {
      name: pred.family_name,
      category: pred.category,
      family_template: pred.family_template ?? "Electrical Equipment.rft",
    },
    geometry: {
      origin: [0, 0, 0],
      bbox: { min: [-w / 2, -d / 2, 0], max: [w / 2, d / 2, h] },
      // Placed planes + labeled dimensions make the box genuinely parametric in
      // Revit: faces lock to the planes, Width/Depth drive the plane pairs, and
      // the equality constraints keep the box centered while it flexes.
      reference_planes: [
        { id: "rp-center-x", name: "Center X", axis: "x", offset: 0, is_origin: true },
        { id: "rp-center-y", name: "Center Y", axis: "y", offset: 0, is_origin: true },
        { id: "rp-left", name: "Left", axis: "x", offset: -w / 2 },
        { id: "rp-right", name: "Right", axis: "x", offset: w / 2 },
        { id: "rp-front", name: "Front", axis: "y", offset: -d / 2 },
        { id: "rp-back", name: "Back", axis: "y", offset: d / 2 },
      ],
      solids: [{ id: "s1", method: "extrusion", depth_param: "Height" }],
      dimensions: [
        { id: "dim-width", references: ["rp-left", "rp-right"], label_param: "Width", value: w },
        { id: "dim-depth", references: ["rp-front", "rp-back"], label_param: "Depth", value: d },
      ],
      constraints: [
        { type: "equality", refs: ["rp-left", "rp-center-x", "rp-right"] },
        { type: "equality", refs: ["rp-front", "rp-center-y", "rp-back"] },
      ],
    },
    parameters: [
      { name: "Apex_AfisId", data_type: "Text", binding: "type", group: "PG_IDENTITY_DATA", value: familyId },
      // Width/Depth/Height are carried by geometry (labeled dimensions drive
      // Length parameters in Revit); a duplicate extracted "Width: 20" would
      // overwrite the Length param with 20 internal feet. Drop them here.
      // Entries without a usable name (possible in a hand-corrected result)
      // are dropped too — the plugin could do nothing with them.
      // deno-lint-ignore no-explicit-any
      ...(pred.parameters ?? []).filter((p: any) => {
        const name = typeof p?.name === "string" ? p.name.trim().toLowerCase() : "";
        return name !== "" && !["width", "depth", "height"].includes(name);
      // deno-lint-ignore no-explicit-any
      }).map((p: any) => ({
        name: p.name,
        data_type: dataTypeMap[p.spec_type] ?? "Text",
        binding: p.is_instance ? "instance" : "type",
        group: groupMap[p.group] ?? "PG_IDENTITY_DATA",
        value: p.value,
        unit: p.units,
      })),
    ],
    connectors: [],
    zones: isElectrical
      ? [{ id: "nec-workspace", type: "service_access", face: "front", depth: NEC_MIN_CLEARANCE_M, is_blocking: true }]
      : [],
    points: [{ id: "p-center", type: "layout", local: [0, 0, 0], description: "Placement centerline", point_code: "CL" }],
  };
}

// ---------- Router ----------

Deno.serve(async (req: Request) => {
  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: CORS });

  const auth = await authorize(req);
  if ("deny" in auth) return auth.deny;
  const ctx = auth.ctx;

  const url = new URL(req.url);
  const parts = url.pathname.split("/").filter(Boolean);
  while (parts.length && parts[0] === "api") parts.shift();
  if (parts[0] !== "v1") return fail(404, "NOT_FOUND", "Unknown route; try /v1/families");
  const resource = parts[1];

  // ----- uploads -----
  if (resource === "uploads" && req.method === "POST") {
    // deno-lint-ignore no-explicit-any
    let body: any;
    try {
      body = await req.json();
    } catch {
      return fail(400, "BAD_JSON", "Body must be JSON: {filename, content_base64}");
    }
    if (typeof body?.filename !== "string" || typeof body?.content_base64 !== "string")
      return fail(400, "MISSING_FIELDS", "filename and content_base64 are required");

    let bytes: Uint8Array;
    try {
      bytes = Uint8Array.from(atob(body.content_base64), (c) => c.charCodeAt(0));
    } catch {
      return fail(400, "BAD_BASE64", "content_base64 is not valid base64");
    }
    if (bytes.length === 0) return fail(400, "EMPTY_FILE", "File is empty");
    if (bytes.length > 30 * 1024 * 1024) return fail(413, "FILE_TOO_LARGE", "Max 30 MB");

    const projectId = typeof body?.project_id === "string" ? body.project_id : defaultProject(ctx);
    if (!projectId) return fail(403, "NO_PROJECT", "You are not a member of any project; ask an admin to add you");
    if (!inScope(ctx, projectId)) return fail(403, "FORBIDDEN", "You are not a member of that project");

    const hashHex = Array.from(
      new Uint8Array(await crypto.subtle.digest("SHA-256", bytes.buffer as ArrayBuffer)),
    ).map((b) => b.toString(16).padStart(2, "0")).join("");
    const storageKey = `submittals/${crypto.randomUUID()}/${body.filename.replace(/[^\w.\-]/g, "_")}`;
    const { error: upErr } = await supabase.storage.from("uploads").upload(storageKey, bytes, {
      contentType: "application/pdf",
    });
    if (upErr) return fail(500, "STORAGE_ERROR", upErr.message);

    const { data: row, error: dbErr } = await supabase.from("uploads").insert({
      project_id: projectId,
      filename: body.filename,
      storage_key: storageKey,
      size_bytes: bytes.length,
      hash_sha256: hashHex,
      mime_type: "application/pdf",
      status: "ready",
      created_by: actorId(ctx),
    }).select("id, filename, size_bytes, status").single();
    if (dbErr) {
      // Same bytes already in this project (uq_uploads_hash_project_active):
      // idempotent upload — drop the duplicate object, return the existing record.
      if (dbErr.code === "23505") {
        await supabase.storage.from("uploads").remove([storageKey]);
        const { data: existing } = await supabase.from("uploads")
          .select("id, filename, size_bytes, status")
          .eq("project_id", projectId).eq("hash_sha256", hashHex).is("deleted_at", null)
          .maybeSingle();
        if (existing) return json({ ...existing, deduplicated: true });
      }
      return fail(500, "DB_ERROR", dbErr.message);
    }
    await audit(ctx, "upload", row.id, "upload_created", { filename: row.filename, size_bytes: row.size_bytes });
    return json(row, 201);
  }

  // ----- extractions -----
  if (resource === "extractions") {
    // GET /v1/extractions?status= — pending reviews by default, so a reload
    // of the console can pick a review back up instead of orphaning it.
    if (parts.length === 2 && req.method === "GET") {
      const status = url.searchParams.get("status") ?? "ready";
      let q = supabase.from("extractions")
        .select("id, status, category, created_at, claude_result, uploads!inner(project_id, filename)")
        .eq("status", status)
        .order("created_at", { ascending: false })
        .limit(50);
      const scope = projectScope(ctx);
      if (scope !== null) {
        if (scope.length === 0) return json({ extractions: [] });
        q = q.in("uploads.project_id", scope);
      }
      const { data, error } = await q;
      if (error) return fail(500, "DB_ERROR", error.message);
      return json({
        extractions: (data ?? []).map((e) => ({
          id: e.id,
          status: e.status,
          category: e.category,
          created_at: e.created_at,
          family_name: (e.claude_result as { family_name?: string } | null)?.family_name ?? null,
          filename: (e as { uploads?: { filename?: string } }).uploads?.filename ?? null,
        })),
      });
    }

    // POST /v1/extractions {upload_id}
    if (parts.length === 2 && req.method === "POST") {
      // deno-lint-ignore no-explicit-any
      let body: any;
      try {
        body = await req.json();
      } catch {
        return fail(400, "BAD_JSON", "Body must be JSON: {upload_id}");
      }
      const uploadId = body?.upload_id;
      if (!uploadId || !UUID_RE.test(uploadId)) return fail(400, "INVALID_UPLOAD_ID", "upload_id must be a UUID");

      const { data: upload } = await supabase.from("uploads")
        .select("id, storage_key, project_id").eq("id", uploadId).is("deleted_at", null).maybeSingle();
      if (!upload || !inScope(ctx, upload.project_id))
        return fail(404, "UPLOAD_NOT_FOUND", `No upload ${uploadId}`);

      const { data: ins, error: insErr } = await supabase.from("extractions").insert({
        upload_id: uploadId, schema_version: "1.0.0", status: "processing",
      }).select("id").single();
      if (insErr) return fail(500, "DB_ERROR", insErr.message);

      const started = Date.now();
      const { data: blob, error: dlErr } = await supabase.storage.from("uploads").download(upload.storage_key);
      if (dlErr || !blob) {
        await supabase.from("extractions").update({ status: "failed", error_message: "storage download failed" }).eq("id", ins.id);
        return fail(500, "STORAGE_ERROR", dlErr?.message ?? "download failed");
      }
      const buf = new Uint8Array(await blob.arrayBuffer());
      let b64 = "";
      for (let i = 0; i < buf.length; i += 0x8000) {
        b64 += String.fromCharCode(...buf.subarray(i, i + 0x8000));
      }
      b64 = btoa(b64);

      try {
        const extraction = await runExtraction(b64);
        if (!extraction.ok) {
          await supabase.from("extractions").update({ status: "failed", error_message: "extraction not run" }).eq("id", ins.id);
          return extraction.resp;
        }
        // deno-lint-ignore no-explicit-any
        const result = extraction.result as any;
        await supabase.from("extractions").update({
          status: "ready",
          category: result.category ?? null,
          claude_result: result,
          warnings: result.warnings ?? [],
          duration_ms: Date.now() - started,
        }).eq("id", ins.id);
        return json({ id: ins.id, status: "ready", result }, 201);
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        await supabase.from("extractions").update({ status: "failed", error_message: msg }).eq("id", ins.id);
        return fail(502, "EXTRACTION_FAILED", msg);
      }
    }

    const exId = parts[2];
    if (!exId || !UUID_RE.test(exId)) return fail(400, "INVALID_EXTRACTION_ID", "Extraction id must be a UUID");
    const { data: ex } = await supabase.from("extractions")
      .select("id, status, category, claude_result, warnings, error_message, uploads(project_id)")
      .eq("id", exId).maybeSingle();
    if (!ex) return fail(404, "EXTRACTION_NOT_FOUND", `No extraction ${exId}`);
    const exProject = (ex as { uploads?: { project_id?: string } }).uploads?.project_id ?? null;
    if (!inScope(ctx, exProject)) return fail(404, "EXTRACTION_NOT_FOUND", `No extraction ${exId}`);
    // deno-lint-ignore no-explicit-any
    delete (ex as any).uploads;

    // GET /v1/extractions/:id
    if (parts.length === 3 && req.method === "GET") return json(ex);

    // POST /v1/extractions/:id/approve — optional body {result: {...}} carries
    // the reviewer's corrections (Doc 3 human-in-the-loop) and replaces the
    // model output before the AFIS conversion.
    if (parts[3] === "approve" && req.method === "POST") {
      if (ex.status !== "ready" && ex.status !== "approved")
        return fail(409, "NOT_READY", `Extraction is '${ex.status}'`);
      if (!ex.claude_result) return fail(409, "NO_RESULT", "Extraction has no result to approve");

      // deno-lint-ignore no-explicit-any
      let pred = ex.claude_result as any;
      let corrected = false;
      try {
        const body = await req.json();
        if (body?.result && typeof body.result === "object") {
          const problem = predProblem(body.result);
          if (problem) return fail(400, "INVALID_CORRECTION", problem);
          pred = body.result;
          corrected = true;
        }
      } catch { /* empty body -> approve the stored result as-is */ }
      if (corrected) {
        await supabase.from("extractions").update({ claude_result: pred }).eq("id", exId);
        await audit(ctx, "extraction", exId, "extraction_corrected");
      }

      const familyId = crypto.randomUUID();
      const afis = predToAfis(familyId, pred);
      const { data: fam, error: famErr } = await supabase.from("families").insert({
        id: familyId,
        extraction_id: exId,
        project_id: exProject ?? defaultProject(ctx) ?? DEMO_PROJECT,
        family_name: pred.family_name ?? "Extracted Family",
        category: pred.category ?? "Generic Model",
        status: "ready",
        created_by: actorId(ctx),
        afis,
      }).select("id, family_name, category, status").single();
      if (famErr) return fail(500, "DB_ERROR", famErr.message);
      await supabase.from("extractions").update({
        status: "approved",
        approved_at: new Date().toISOString(),
        approved_by: actorId(ctx),
      }).eq("id", exId);
      await audit(ctx, "family", fam.id, "extraction_approved", { extraction_id: exId, family_name: fam.family_name });
      return json({ family: fam, extraction_id: exId }, 201);
    }
    return fail(404, "NOT_FOUND", "Unknown action");
  }

  // ----- tokens (signed-in users mint machine tokens for the Revit worker) -----
  if (resource === "tokens") {
    if (ctx.kind !== "user")
      return fail(403, "FORBIDDEN", "Managing service tokens requires a signed-in user session");

    if (parts.length === 2 && req.method === "GET") {
      const { data, error } = await supabase.from("api_tokens")
        .select("id, name, project_id, created_at, last_used_at, revoked_at")
        .eq("created_by", ctx.userId)
        .order("created_at", { ascending: false });
      if (error) return fail(500, "DB_ERROR", error.message);
      return json({ tokens: data });
    }

    if (parts.length === 2 && req.method === "POST") {
      // deno-lint-ignore no-explicit-any
      let body: any = {};
      try {
        body = await req.json();
      } catch { /* name defaults below */ }
      const name = typeof body?.name === "string" && body.name.trim() ? body.name.trim() : "revit-worker";
      const projectId = typeof body?.project_id === "string" ? body.project_id : defaultProject(ctx);
      if (!projectId) return fail(403, "NO_PROJECT", "Join or create a project before minting a token");
      if (!inScope(ctx, projectId)) return fail(403, "FORBIDDEN", "You are not a member of that project");

      const raw = new Uint8Array(24);
      crypto.getRandomValues(raw);
      const token = "apx_" + Array.from(raw).map((b) => b.toString(16).padStart(2, "0")).join("");
      const { data: row, error } = await supabase.from("api_tokens").insert({
        name,
        token_hash: await sha256hex(token),
        project_id: projectId,
        created_by: ctx.userId,
      }).select("id, name, project_id, created_at").single();
      if (error) return fail(500, "DB_ERROR", error.message);
      await audit(ctx, "api_token", row.id, "token_minted", { name, project_id: projectId });
      // The plaintext exists only in this response; only the hash is stored.
      return json({ ...row, token }, 201);
    }

    if (parts.length === 4 && parts[3] === "revoke" && req.method === "POST") {
      const tokId = parts[2];
      if (!UUID_RE.test(tokId)) return fail(400, "INVALID_TOKEN_ID", "Token id must be a UUID");
      const { data, error } = await supabase.from("api_tokens")
        .update({ revoked_at: new Date().toISOString() })
        .eq("id", tokId).eq("created_by", ctx.userId).is("revoked_at", null)
        .select("id, name, revoked_at").maybeSingle();
      if (error) return fail(500, "DB_ERROR", error.message);
      if (!data) return fail(404, "TOKEN_NOT_FOUND", "No active token of yours with that id");
      await audit(ctx, "api_token", tokId, "token_revoked");
      return json(data);
    }
    return fail(404, "NOT_FOUND", "Unknown action");
  }

  // ----- projects (self-service; memberships otherwise managed by admins) -----
  if (resource === "projects") {
    // /v1/projects/:id/members[...]
    if (parts.length >= 4 && parts[3] === "members") {
      const projId = parts[2];
      if (!UUID_RE.test(projId)) return fail(400, "INVALID_PROJECT_ID", "Project id must be a UUID");
      if (!inScope(ctx, projId)) return fail(404, "PROJECT_NOT_FOUND", `No project ${projId}`);

      const isAdmin = async () => {
        if (ctx.kind !== "user") return ctx.kind !== "anon"; // service tokens act as admin within their scope
        const { data } = await supabase.from("project_members")
          .select("role").eq("project_id", projId).eq("user_id", ctx.userId).maybeSingle();
        return data?.role === "admin";
      };

      if (parts.length === 4 && req.method === "GET") {
        const { data, error } = await supabase.from("project_members")
          .select("user_id, role, added_at, users(email, full_name)")
          .eq("project_id", projId)
          .order("added_at", { ascending: true });
        if (error) return fail(500, "DB_ERROR", error.message);
        return json({
          members: (data ?? []).map((m) => ({
            user_id: m.user_id,
            role: m.role,
            added_at: m.added_at,
            email: (m as { users?: { email?: string } }).users?.email,
            full_name: (m as { users?: { full_name?: string } }).users?.full_name,
          })),
        });
      }

      if (parts.length === 4 && req.method === "POST") {
        if (!(await isAdmin())) return fail(403, "FORBIDDEN", "Only project admins can add members");
        // deno-lint-ignore no-explicit-any
        let body: any;
        try {
          body = await req.json();
        } catch {
          return fail(400, "BAD_JSON", 'Body must be JSON: {"email", "role"?}');
        }
        const email = typeof body?.email === "string" ? body.email.trim().toLowerCase() : "";
        if (!email) return fail(400, "MISSING_FIELDS", "email is required");
        const role = ["viewer", "editor", "admin"].includes(body?.role) ? body.role : "editor";

        const { data: target } = await supabase.from("users")
          .select("id, email").ilike("email", email).maybeSingle();
        if (!target)
          return fail(404, "USER_NOT_FOUND",
            `No Apex user with email '${email}' — they need to sign in once first`);

        const { error: memErr } = await supabase.from("project_members")
          .upsert({ project_id: projId, user_id: target.id, role }, { onConflict: "project_id,user_id" });
        if (memErr) return fail(500, "DB_ERROR", memErr.message);
        await audit(ctx, "project", projId, "member_added", { email, role });
        return json({ project_id: projId, user_id: target.id, email: target.email, role }, 201);
      }

      if (parts.length === 5 && req.method === "DELETE") {
        if (!(await isAdmin())) return fail(403, "FORBIDDEN", "Only project admins can remove members");
        const targetId = parts[4];
        if (!UUID_RE.test(targetId)) return fail(400, "INVALID_USER_ID", "User id must be a UUID");

        const { data: admins } = await supabase.from("project_members")
          .select("user_id").eq("project_id", projId).eq("role", "admin");
        if ((admins ?? []).length === 1 && admins![0].user_id === targetId)
          return fail(409, "LAST_ADMIN", "Cannot remove the project's only admin");

        const { data, error } = await supabase.from("project_members")
          .delete().eq("project_id", projId).eq("user_id", targetId)
          .select("user_id").maybeSingle();
        if (error) return fail(500, "DB_ERROR", error.message);
        if (!data) return fail(404, "MEMBER_NOT_FOUND", "That user is not a member of this project");
        await audit(ctx, "project", projId, "member_removed", { user_id: targetId });
        return json({ project_id: projId, user_id: targetId, removed: true });
      }
      return fail(404, "NOT_FOUND", "Unknown action");
    }

    if (parts.length === 2 && req.method === "GET") {
      let q = supabase.from("projects")
        .select("id, name, client_name, status, created_at")
        .is("deleted_at", null)
        .order("created_at", { ascending: false })
        .limit(100);
      const scope = projectScope(ctx);
      if (scope !== null) {
        if (scope.length === 0) return json({ projects: [] });
        q = q.in("id", scope);
      }
      const { data, error } = await q;
      if (error) return fail(500, "DB_ERROR", error.message);
      return json({ projects: data });
    }

    if (parts.length === 2 && req.method === "POST") {
      // Signed-in users only: a project needs a real owner to administer it.
      if (ctx.kind !== "user")
        return fail(403, "FORBIDDEN", "Creating a project requires a signed-in user session");
      // deno-lint-ignore no-explicit-any
      let body: any;
      try {
        body = await req.json();
      } catch {
        return fail(400, "BAD_JSON", 'Body must be JSON: {"name", "client_name"?}');
      }
      const name = typeof body?.name === "string" ? body.name.trim() : "";
      if (!name) return fail(400, "MISSING_FIELDS", "name is required");

      const { data: proj, error: projErr } = await supabase.from("projects").insert({
        name,
        client_name: typeof body?.client_name === "string" ? body.client_name : null,
        status: "active",
        created_by: ctx.userId,
      }).select("id, name, client_name, status, created_at").single();
      if (projErr) return fail(500, "DB_ERROR", projErr.message);

      const { error: memErr } = await supabase.from("project_members").insert({
        project_id: proj.id, user_id: ctx.userId, role: "admin",
      });
      if (memErr) return fail(500, "DB_ERROR", memErr.message);

      await audit(ctx, "project", proj.id, "project_created", { name: proj.name });
      return json(proj, 201);
    }
    return fail(404, "NOT_FOUND", "Unknown action");
  }

  // ----- jobs (Doc 3 Stage 11 worker queue) -----
  if (resource === "jobs") {
    if (parts.length === 2 && req.method === "GET") {
      // Workers polling is the moment to rescue jobs whose worker died mid-run.
      if (ctx.kind !== "user") {
        const { data: requeued } = await supabase.rpc("requeue_stale_jobs");
        if (requeued) console.log(`requeued ${requeued} stale running job(s)`);
      }
      const status = url.searchParams.get("status") ?? "queued";
      const kind = url.searchParams.get("kind");
      let q = supabase.from("jobs").select("id, kind, entity_id, status, attempt, created_at")
        .eq("status", status).order("created_at", { ascending: true }).limit(20);
      if (kind) q = q.eq("kind", kind);
      const scope = projectScope(ctx);
      if (scope !== null) {
        if (scope.length === 0) return json({ jobs: [] });
        const { data: famIds } = await supabase.from("families")
          .select("id").in("project_id", scope).limit(1000);
        q = q.in("entity_id", (famIds ?? []).map((f) => f.id));
      }
      const { data, error } = await q;
      if (error) return fail(500, "DB_ERROR", error.message);
      return json({ jobs: data });
    }
    const jobId = parts[2];
    if (!jobId || !UUID_RE.test(jobId)) return fail(400, "INVALID_JOB_ID", "Job id must be a UUID");

    // claim/complete are worker verbs — machine tokens only.
    if (ctx.kind === "user")
      return fail(403, "FORBIDDEN", "Job claim/complete requires a service token (apx_...)");

    if (parts[3] === "claim" && req.method === "POST") {
      const { data, error } = await supabase.rpc("claim_job", { jid: jobId });
      if (error) return fail(500, "DB_ERROR", error.message);
      const job = Array.isArray(data) ? data[0] : data;
      if (!job) return fail(409, "NOT_CLAIMABLE", "Job is not queued (already claimed or finished)");
      return json({ id: job.id, kind: job.kind, entity_id: job.entity_id, status: job.status, attempt: job.attempt });
    }
    if (parts[3] === "complete" && req.method === "POST") {
      // deno-lint-ignore no-explicit-any
      let body: any = {};
      try {
        body = await req.json();
      } catch { /* default */ }
      const status = body?.status === "failed" ? "failed" : "succeeded";
      const { data, error } = await supabase.from("jobs")
        .update({
          status,
          finished_at: new Date().toISOString(),
          last_error: status === "failed" ? String(body?.error ?? "unknown") : null,
        })
        .eq("id", jobId).eq("status", "running")
        .select("id, status").maybeSingle();
      if (error) return fail(500, "DB_ERROR", error.message);
      if (!data) return fail(409, "NOT_RUNNING", "Job is not in running state");
      await audit(ctx, "job", jobId, "job_completed", { status });
      return json(data);
    }
    return fail(404, "NOT_FOUND", "Unknown action");
  }

  // ----- families -----
  if (resource !== "families") return fail(404, "NOT_FOUND", "Unknown route");

  if (parts.length === 2 && req.method === "GET") {
    // Cursor pagination (api-design.md): ?limit=&cursor=, cursor from the prior page.
    const limit = Math.min(Math.max(parseInt(url.searchParams.get("limit") ?? "50", 10) || 50, 1), 100);
    let q = supabase
      .from("families")
      .select("id, family_name, category, status, revit_version, updated_at")
      .is("deleted_at", null)
      .order("updated_at", { ascending: false })
      .order("id", { ascending: false })
      .limit(limit);
    const scope = projectScope(ctx);
    if (scope !== null) {
      if (scope.length === 0) return json({ families: [], next_cursor: null });
      q = q.in("project_id", scope);
    }
    const cursor = url.searchParams.get("cursor");
    if (cursor) {
      let ts: string, cid: string;
      try {
        [ts, cid] = atob(cursor).split("|");
        if (!ts || !UUID_RE.test(cid)) throw new Error("bad");
      } catch {
        return fail(400, "INVALID_CURSOR", "cursor is not valid; use next_cursor from the previous page");
      }
      q = q.or(`updated_at.lt.${ts},and(updated_at.eq.${ts},id.lt.${cid})`);
    }
    const { data, error } = await q;
    if (error) return fail(500, "DB_ERROR", error.message);
    const last = data.length === limit ? data[data.length - 1] : null;
    return json({
      families: data,
      next_cursor: last ? btoa(`${last.updated_at}|${last.id}`) : null,
    });
  }

  const id = parts[2];
  if (!id || !UUID_RE.test(id)) return fail(400, "INVALID_FAMILY_ID", "Family id must be a UUID");
  const action = parts[3];

  const { data: fam, error: famErr } = await supabase
    .from("families")
    .select("id, family_name, category, status, afis, rfa_storage_key, rfa_revit_version, project_id")
    .eq("id", id)
    .is("deleted_at", null)
    .maybeSingle();
  if (famErr) return fail(500, "DB_ERROR", famErr.message);
  // Out-of-scope reads 404 rather than 403: don't confirm the family exists.
  if (!fam || !inScope(ctx, fam.project_id)) return fail(404, "FAMILY_NOT_FOUND", `No family ${id}`);

  if (!action && req.method === "GET") {
    if (!fam.afis) return fail(404, "NO_AFIS_DOCUMENT", `Family ${id} has no AFIS document yet`);
    return json(fam.afis);
  }

  if (action === "validate" && req.method === "POST") {
    const findings = runQaPipeline(fam.afis);
    await supabase.from("family_validations").delete().eq("family_id", id);
    const { error: insErr } = await supabase.from("family_validations").insert(
      findings.map((r) => ({
        family_id: id,
        rule: r.rule,
        severity: r.severity,
        passed: r.passed,
        message: r.message,
        details: { category: r.category, path: r.path, fix_hint: r.fix_hint, auto_fixable: r.auto_fixable },
      })),
    );
    if (insErr) return fail(500, "DB_ERROR", insErr.message);

    const errors = findings.filter((r) => !r.passed && r.severity === "error").length;
    const warnings = findings.filter((r) => !r.passed && r.severity === "warning").length;
    const info = findings.filter((r) => !r.passed && r.severity === "info").length;
    const weight = (s: string) => (s === "error" ? 3 : s === "warning" ? 2 : 1);
    const total = findings.reduce((a, r) => a + weight(r.severity), 0);
    const passedW = findings.reduce((a, r) => a + (r.passed ? weight(r.severity) : 0), 0);
    const score = total ? Math.round((passedW / total) * 100) / 100 : 1;

    await supabase.from("families").update({ checklist_result: { passed: errors === 0, score } }).eq("id", id);

    return json({
      object_id: id,
      family_name: fam.family_name,
      passed: errors === 0,
      score,
      summary: { errors, warnings, info },
      findings,
    });
  }

  if (action === "exports" && req.method === "POST") {
    let format = "csv";
    try {
      const body = await req.json();
      if (typeof body?.format === "string") format = body.format.toLowerCase();
    } catch { /* empty body -> default csv */ }
    if (format !== "csv") return fail(400, "UNSUPPORTED_FORMAT", "Only csv is supported today", { supported: ["csv"] });

    // deno-lint-ignore no-explicit-any
    const points: any[] = (fam.afis as any)?.points ?? [];
    const lines = ["id,type,point_code,x_m,y_m,z_m,description"];
    for (const p of points) {
      const [x, y, z] = Array.isArray(p.local) ? p.local : [0, 0, 0];
      lines.push([p.id ?? "", p.type ?? "", p.point_code ?? "", x, y, z, csvEscape(p.description ?? "")].join(","));
    }
    return new Response(lines.join("\n") + "\n", {
      headers: { "Content-Type": "text/csv; charset=utf-8", ...CORS },
    });
  }

  // Worker round-trip: the Revit plugin uploads the .rfa it built for this family.
  if (action === "rfa" && req.method === "POST") {
    // deno-lint-ignore no-explicit-any
    let body: any;
    try {
      body = await req.json();
    } catch {
      return fail(400, "BAD_JSON", "Body must be JSON: {content_base64, revit_version?}");
    }
    if (typeof body?.content_base64 !== "string")
      return fail(400, "MISSING_FIELDS", "content_base64 is required");
    let bytes: Uint8Array;
    try {
      bytes = Uint8Array.from(atob(body.content_base64), (c) => c.charCodeAt(0));
    } catch {
      return fail(400, "BAD_BASE64", "content_base64 is not valid base64");
    }
    if (bytes.length === 0) return fail(400, "EMPTY_FILE", "File is empty");
    if (bytes.length > 100 * 1024 * 1024) return fail(413, "FILE_TOO_LARGE", "Max 100 MB");

    const storageKey = `${id}/${(fam.family_name || "family").replace(/[^\w.\-]/g, "_")}.rfa`;
    const { error: upErr } = await supabase.storage.from("rfa").upload(storageKey, bytes, {
      contentType: "application/octet-stream",
      upsert: true,
    });
    if (upErr) return fail(500, "STORAGE_ERROR", upErr.message);

    const { error: updErr } = await supabase.from("families").update({
      rfa_storage_key: storageKey,
      rfa_size_bytes: bytes.length,
      rfa_revit_version: typeof body?.revit_version === "string" ? body.revit_version : null,
      rfa_uploaded_at: new Date().toISOString(),
    }).eq("id", id);
    if (updErr) return fail(500, "DB_ERROR", updErr.message);
    await audit(ctx, "family", id, "rfa_uploaded", { storage_key: storageKey, size_bytes: bytes.length });
    return json({ family_id: id, rfa_storage_key: storageKey, size_bytes: bytes.length }, 201);
  }

  if (action === "rfa" && req.method === "GET") {
    if (!fam.rfa_storage_key)
      return fail(404, "NO_RFA", `Family ${id} has no built .rfa yet; POST /generate-rfa and run the Revit worker`);
    const { data: blob, error: dlErr } = await supabase.storage.from("rfa").download(fam.rfa_storage_key);
    if (dlErr || !blob) return fail(500, "STORAGE_ERROR", dlErr?.message ?? "download failed");
    return new Response(await blob.arrayBuffer(), {
      headers: {
        "Content-Type": "application/octet-stream",
        "Content-Disposition": `attachment; filename="${fam.rfa_storage_key.split("/").pop()}"`,
        ...CORS,
      },
    });
  }

  if (action === "generate-rfa" && req.method === "POST") {
    const gate = fam.afis ? runQaPipeline(fam.afis) : null;
    const blocked = !gate || gate.some((r) => !r.passed && r.severity === "error");
    if (blocked) {
      return fail(409, "QA_GATE_BLOCKED", "Family has blocking QA errors; run validate and fix before export");
    }
    const { data: job, error: jobErr } = await supabase
      .from("jobs")
      .insert({ kind: "generate_rfa", entity_id: id, status: "queued" })
      .select("id, kind, status, created_at")
      .single();
    if (jobErr) return fail(500, "DB_ERROR", jobErr.message);
    await audit(ctx, "job", job.id, "rfa_generation_queued", { family_id: id });
    return json({ job_id: job.id, status: job.status, note: "RFA generation queued; run Process Queue in the Revit plugin to build it." }, 202);
  }

  return fail(404, "NOT_FOUND", "Unknown action");
});
