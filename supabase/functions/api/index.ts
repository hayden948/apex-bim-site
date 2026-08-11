// Apex BIM Studio API v1 — consumed by the Revit plugin's ApexApiClient.
// Contract: docs/architecture/api-design.md; QA rules: Doc 8 (Family QA Engine)
// + Doc 1 §5 rule catalog. Error envelope: { error: { code, message, details? } }.
//
// Routes (base = https://<ref>.supabase.co/functions/v1/api — both /v1/* and
// /api/v1/* prefixes accepted):
//   GET  /v1/families                      list library families
//   GET  /v1/families/:id                  AFIS document
//   POST /v1/families/:id/validate         QA Engine: staged rule checks (Doc 8)
//   POST /v1/families/:id/exports          field points export (Doc 7), {"format":"csv"}
//   POST /v1/families/:id/generate-rfa     enqueue RFA generation job (Doc 3 Stage 11)
import { createClient } from "jsr:@supabase/supabase-js@2";

const supabase = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const NEC_MIN_CLEARANCE_M = 0.9144; // NEC 110.26 working space, 36 in

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function fail(status: number, code: string, message: string, details?: unknown): Response {
  return json({ error: { code, message, details } }, status);
}

function csvEscape(s: string): string {
  return /[",\n]/.test(s) ? '"' + s.replaceAll('"', '""') + '"' : s;
}

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

// Staged S→P→G→E→Z→L pipeline per Doc 8 §3. Schema failures short-circuit.
// deno-lint-ignore no-explicit-any
function runQaPipeline(afis: any): Finding[] {
  const f: Finding[] = [];
  const add = (rule: string, severity: Finding["severity"], category: string, path: string,
    passed: boolean, message: string, fix_hint?: string, auto_fixable?: boolean) =>
    f.push({ rule, severity, category, path, passed, message, fix_hint, auto_fixable });

  // S — schema/structural (fail-fast stage)
  add("S-1", "error", "schema", "$", !!afis,
    afis ? "AFIS document present" : "No AFIS document stored for this family",
    "Re-run extraction/generation to produce an AFIS document");
  if (!afis) return f;
  const versionOk = typeof afis.afis_version === "string" && afis.afis_version.startsWith("1.");
  add("S-2", "error", "schema", "$.afis_version", versionOk,
    versionOk ? `AFIS version ${afis.afis_version}` : "afis_version missing or unsupported");
  if (!versionOk) return f;

  // P — profile completeness
  add("P-1", "error", "profile", "$.identity.name", !!afis.identity?.name,
    afis.identity?.name ? "identity.name present" : "identity.name is required");
  add("P-2", "error", "profile", "$.identity.category", !!afis.identity?.category,
    afis.identity?.category ? "identity.category present" : "identity.category is required");
  const params: any[] = afis.parameters ?? [];
  add("P-3", "warning", "profile", "$.parameters",
    params.some((p) => p?.name === "Apex_AfisId"),
    "Families should carry an Apex_AfisId stamp parameter",
    "Add an Apex_AfisId Text parameter holding the family id", true);

  // G — geometric integrity
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

  // Z — spatial / clearance (Doc 8 §4.1 example: Z-1 NEC 110.26)
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

  // L — field layout (Doc 7)
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

Deno.serve(async (req: Request) => {
  const url = new URL(req.url);
  // Accept both the function mount (/api/...) and the canonical /api/v1 base path.
  const parts = url.pathname.split("/").filter(Boolean);
  while (parts.length && parts[0] === "api") parts.shift();

  if (parts[0] !== "v1" || parts[1] !== "families") {
    return fail(404, "NOT_FOUND", "Unknown route; try /v1/families");
  }

  // GET /v1/families
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
    .select("id, family_name, category, status, afis")
    .eq("id", id)
    .is("deleted_at", null)
    .maybeSingle();
  if (famErr) return fail(500, "DB_ERROR", famErr.message);
  if (!fam) return fail(404, "FAMILY_NOT_FOUND", `No family ${id}`);

  // GET /v1/families/:id -> AFIS document
  if (!action && req.method === "GET") {
    if (!fam.afis) return fail(404, "NO_AFIS_DOCUMENT", `Family ${id} has no AFIS document yet`);
    return json(fam.afis);
  }

  // POST /v1/families/:id/validate -> Doc 8 finding object (Doc 1 §5.8 shape)
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
      passed: errors === 0, // ERROR findings block export (Doc 8 §3.1)
      score,
      summary: { errors, warnings, info },
      findings,
    });
  }

  // POST /v1/families/:id/exports  {"format":"csv"}
  if (action === "exports" && req.method === "POST") {
    let format = "csv";
    try {
      const body = await req.json();
      if (typeof body?.format === "string") format = body.format.toLowerCase();
    } catch { /* empty body -> default csv */ }
    if (format !== "csv") return fail(400, "UNSUPPORTED_FORMAT", "Only csv is supported today", { supported: ["csv"] });

    const points: any[] = fam.afis?.points ?? [];
    const lines = ["id,type,point_code,x_m,y_m,z_m,description"];
    for (const p of points) {
      const [x, y, z] = Array.isArray(p.local) ? p.local : [0, 0, 0];
      lines.push([p.id ?? "", p.type ?? "", p.point_code ?? "", x, y, z, csvEscape(p.description ?? "")].join(","));
    }
    return new Response(lines.join("\n") + "\n", {
      headers: { "Content-Type": "text/csv; charset=utf-8" },
    });
  }

  // POST /v1/families/:id/generate-rfa -> enqueue job (Doc 3 Stage 11 worker processes it)
  if (action === "generate-rfa" && req.method === "POST") {
    const gate = fam.afis ? runQaPipeline(fam.afis) : null;
    const blocked = !gate || gate.some((r) => !r.passed && r.severity === "error");
    if (blocked) {
      // No certificate, no export — no exceptions (Doc 8 §6).
      return fail(409, "QA_GATE_BLOCKED", "Family has blocking QA errors; run validate and fix before export");
    }
    const { data: job, error: jobErr } = await supabase
      .from("jobs")
      .insert({ kind: "generate_rfa", entity_id: id, status: "queued" })
      .select("id, kind, status, created_at")
      .single();
    if (jobErr) return fail(500, "DB_ERROR", jobErr.message);
    return json({ job_id: job.id, status: job.status, note: "RFA generation queued; a Revit worker must process the jobs table." }, 202);
  }

  return fail(404, "NOT_FOUND", "Unknown action");
});
