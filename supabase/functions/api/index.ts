// Apex BIM Studio API v1 — consumed by the Revit plugin's ApexApiClient and the
// upload→extraction pipeline (Doc 3). Contract: docs/architecture/api-design.md;
// QA rules: Doc 8 + Doc 1 §5. Error envelope: { error: { code, message, details? } }.
//
// Auth (custom — verify_jwt disabled): Authorization: Bearer <token> where token is
//   - an apx_... service token (hashed in public.api_tokens; api-design.md §service tokens), or
//   - the project's legacy anon JWT (SUPABASE_ANON_KEY).
//
// Routes (base = https://<ref>.supabase.co/functions/v1/api; /api/v1/* also accepted):
//   GET  /v1/families                       list library families
//   GET  /v1/families/:id                   AFIS document
//   POST /v1/families/:id/validate          QA Engine: staged rule checks (Doc 8)
//   POST /v1/families/:id/exports           field points export (Doc 7), {"format":"csv"}
//   POST /v1/families/:id/generate-rfa      QA-gated; enqueues a jobs row (Doc 3 Stage 11)
//   POST /v1/families/:id/rfa               worker uploads the built .rfa {"content_base64","revit_version"?}
//   GET  /v1/families/:id/rfa               download the built .rfa (binary)
//   POST /v1/uploads                        {"filename","content_base64"} → storage + uploads row
//   POST /v1/extractions                    {"upload_id"} → Claude extraction → extractions row
//   GET  /v1/extractions/:id                extraction status + result
//   POST /v1/extractions/:id/approve        extraction → AFIS → families row (library)
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
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
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

/** Custom auth: apx_ service tokens (hashed in DB) or the legacy anon key. */
async function authorize(req: Request): Promise<Response | null> {
  const header = req.headers.get("Authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice(7).trim() : "";
  if (!token) return fail(401, "UNAUTHENTICATED", "Missing Authorization: Bearer token");

  if (token.startsWith("apx_")) {
    const hash = await sha256hex(token);
    const { data } = await supabase
      .from("api_tokens")
      .select("id")
      .eq("token_hash", hash)
      .is("revoked_at", null)
      .maybeSingle();
    if (!data) return fail(401, "INVALID_TOKEN", "Unknown or revoked service token");
    await supabase.from("api_tokens").update({ last_used_at: new Date().toISOString() }).eq("id", data.id);
    return null;
  }

  if (token === Deno.env.get("SUPABASE_ANON_KEY")) return null;
  return fail(401, "INVALID_TOKEN", "Token is neither a service token (apx_...) nor the project key");
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
- parameters: engineering values worth carrying into the family (voltage, circuits,
  MCA, MOCP, weight, manufacturer, model number...). Use spec_type Number/Integer
  for numerics, Length for dimensions (with units), Text otherwise. Include a
  confidence 0-1 per parameter.
- warnings: anything ambiguous, missing, or assumed.

Extract only what the document supports; do not invent values.`;

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
      reference_planes: [], solids: [{ id: "s1", method: "extrusion" }], dimensions: [], constraints: [],
    },
    parameters: [
      { name: "Apex_AfisId", data_type: "Text", binding: "type", group: "PG_IDENTITY_DATA", value: familyId },
      // deno-lint-ignore no-explicit-any
      ...(pred.parameters ?? []).map((p: any) => ({
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

  const denied = await authorize(req);
  if (denied) return denied;

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

    const hashHex = Array.from(
      new Uint8Array(await crypto.subtle.digest("SHA-256", bytes.buffer as ArrayBuffer)),
    ).map((b) => b.toString(16).padStart(2, "0")).join("");
    const storageKey = `submittals/${crypto.randomUUID()}/${body.filename.replace(/[^\w.\-]/g, "_")}`;
    const { error: upErr } = await supabase.storage.from("uploads").upload(storageKey, bytes, {
      contentType: "application/pdf",
    });
    if (upErr) return fail(500, "STORAGE_ERROR", upErr.message);

    const { data: row, error: dbErr } = await supabase.from("uploads").insert({
      project_id: DEMO_PROJECT,
      filename: body.filename,
      storage_key: storageKey,
      size_bytes: bytes.length,
      hash_sha256: hashHex,
      mime_type: "application/pdf",
      status: "ready",
      created_by: DEMO_USER,
    }).select("id, filename, size_bytes, status").single();
    if (dbErr) return fail(500, "DB_ERROR", dbErr.message);
    return json(row, 201);
  }

  // ----- extractions -----
  if (resource === "extractions") {
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
        .select("id, storage_key").eq("id", uploadId).is("deleted_at", null).maybeSingle();
      if (!upload) return fail(404, "UPLOAD_NOT_FOUND", `No upload ${uploadId}`);

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
      .select("id, status, category, claude_result, warnings, error_message").eq("id", exId).maybeSingle();
    if (!ex) return fail(404, "EXTRACTION_NOT_FOUND", `No extraction ${exId}`);

    // GET /v1/extractions/:id
    if (parts.length === 3 && req.method === "GET") return json(ex);

    // POST /v1/extractions/:id/approve
    if (parts[3] === "approve" && req.method === "POST") {
      if (ex.status !== "ready" && ex.status !== "approved")
        return fail(409, "NOT_READY", `Extraction is '${ex.status}'`);
      if (!ex.claude_result) return fail(409, "NO_RESULT", "Extraction has no result to approve");

      const familyId = crypto.randomUUID();
      const afis = predToAfis(familyId, ex.claude_result);
      // deno-lint-ignore no-explicit-any
      const pred = ex.claude_result as any;
      const { data: fam, error: famErr } = await supabase.from("families").insert({
        id: familyId,
        extraction_id: exId,
        project_id: DEMO_PROJECT,
        family_name: pred.family_name ?? "Extracted Family",
        category: pred.category ?? "Generic Model",
        status: "ready",
        created_by: DEMO_USER,
        afis,
      }).select("id, family_name, category, status").single();
      if (famErr) return fail(500, "DB_ERROR", famErr.message);
      await supabase.from("extractions").update({ status: "approved", approved_at: new Date().toISOString() }).eq("id", exId);
      return json({ family: fam, extraction_id: exId }, 201);
    }
    return fail(404, "NOT_FOUND", "Unknown action");
  }

  // ----- jobs (Doc 3 Stage 11 worker queue) -----
  if (resource === "jobs") {
    if (parts.length === 2 && req.method === "GET") {
      const status = url.searchParams.get("status") ?? "queued";
      const kind = url.searchParams.get("kind");
      let q = supabase.from("jobs").select("id, kind, entity_id, status, attempt, created_at")
        .eq("status", status).order("created_at", { ascending: true }).limit(20);
      if (kind) q = q.eq("kind", kind);
      const { data, error } = await q;
      if (error) return fail(500, "DB_ERROR", error.message);
      return json({ jobs: data });
    }
    const jobId = parts[2];
    if (!jobId || !UUID_RE.test(jobId)) return fail(400, "INVALID_JOB_ID", "Job id must be a UUID");

    if (parts[3] === "claim" && req.method === "POST") {
      const { data, error } = await supabase.from("jobs")
        .update({ status: "running", started_at: new Date().toISOString() })
        .eq("id", jobId).eq("status", "queued")
        .select("id, kind, entity_id, status").maybeSingle();
      if (error) return fail(500, "DB_ERROR", error.message);
      if (!data) return fail(409, "NOT_CLAIMABLE", "Job is not queued (already claimed or finished)");
      return json(data);
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
      return json(data);
    }
    return fail(404, "NOT_FOUND", "Unknown action");
  }

  // ----- families -----
  if (resource !== "families") return fail(404, "NOT_FOUND", "Unknown route");

  if (parts.length === 2 && req.method === "GET") {
    const { data, error } = await supabase
      .from("families")
      .select("id, family_name, category, status, revit_version, updated_at")
      .is("deleted_at", null)
      .order("updated_at", { ascending: false })
      .limit(100);
    if (error) return fail(500, "DB_ERROR", error.message);
    return json({ families: data });
  }

  const id = parts[2];
  if (!id || !UUID_RE.test(id)) return fail(400, "INVALID_FAMILY_ID", "Family id must be a UUID");
  const action = parts[3];

  const { data: fam, error: famErr } = await supabase
    .from("families")
    .select("id, family_name, category, status, afis, rfa_storage_key, rfa_revit_version")
    .eq("id", id)
    .is("deleted_at", null)
    .maybeSingle();
  if (famErr) return fail(500, "DB_ERROR", famErr.message);
  if (!fam) return fail(404, "FAMILY_NOT_FOUND", `No family ${id}`);

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
    return json({ job_id: job.id, status: job.status, note: "RFA generation queued; run Process Queue in the Revit plugin to build it." }, 202);
  }

  return fail(404, "NOT_FOUND", "Unknown action");
});
