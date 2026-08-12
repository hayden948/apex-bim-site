// Contract mock of the Apex API (supabase/functions/api) for console browser
// tests: same routes, auth classes, and response shapes, no network egress.
import http from "node:http";

const APX_TOKEN = "apx_3bad07ea1b0a26a9016d2f653b558218eee0a1b178f308a0";
const USER_JWT = "user-jwt-abc123";
const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, content-type, apikey",
  "Access-Control-Allow-Methods": "GET, POST, DELETE, OPTIONS",
};
const fam = {
  id: "44d3d55d-dd6c-4011-acee-e23e47a394c4",
  family_name: "Square D NQ442L2C Panelboard",
  category: "Electrical Equipment",
  status: "ready",
};
const projects = [{ id: "00000000-0000-4000-8000-000000000002", name: "Apex Demo Library", client_name: null, status: "active" }];
const j = (res, code, body) => {
  res.writeHead(code, { "Content-Type": "application/json", ...CORS });
  res.end(JSON.stringify(body));
};

http.createServer(async (req, res) => {
  if (req.method === "OPTIONS") { res.writeHead(204, CORS); return res.end(); }
  let body = "";
  for await (const c of req) body += c;
  const p = new URL(req.url, "http://x").pathname.replace(/^\/functions\/v1\/api/, "");

  // Supabase auth endpoints (apikey header, no bearer)
  if (p === "/auth/v1/token") {
    const b = JSON.parse(body);
    if (b.email === "qa@apex.test" && b.password === "pw123456")
      return j(res, 200, { access_token: USER_JWT, refresh_token: "r1", token_type: "bearer" });
    return j(res, 400, { error_description: "Invalid login credentials" });
  }
  if (p === "/auth/v1/signup")
    return j(res, 200, { id: "u2", email: JSON.parse(body).email }); // confirmation required -> no session

  const auth = req.headers.authorization || "";
  const isUser = auth === "Bearer " + USER_JWT;
  if (auth !== "Bearer " + APX_TOKEN && !isUser)
    return j(res, 401, { error: { code: "INVALID_TOKEN", message: "Unknown or revoked service token" } });

  if (p === "/v1/families" && req.method === "GET") return j(res, 200, { families: [fam], next_cursor: null });
  if (p === "/v1/projects" && req.method === "GET") return j(res, 200, { projects });
  if (p === "/v1/projects" && req.method === "POST") {
    if (!isUser) return j(res, 403, { error: { code: "FORBIDDEN", message: "Creating a project requires a signed-in user session" } });
    const np = { id: "2bcc843d-7634-4cbc-8eed-4d317e2f286f", name: JSON.parse(body).name, client_name: null, status: "active" };
    projects.unshift(np);
    return j(res, 201, np);
  }
  if (p === "/v1/tokens" && req.method === "POST") {
    if (!isUser) return j(res, 403, { error: { code: "FORBIDDEN", message: "Managing service tokens requires a signed-in user session" } });
    const b = JSON.parse(body || "{}");
    return j(res, 201, { id: "t1", name: b.name || "revit-worker", project_id: b.project_id || projects[projects.length - 1].id, token: "apx_minted_secret_42" });
  }
  if (p === "/v1/uploads") {
    const b = JSON.parse(body);
    return j(res, 201, { id: "288d6230-0000-4000-8000-000000000000", filename: b.filename, size_bytes: 1024, status: "ready", project_id: b.project_id ?? null });
  }
  const pendingResult = {
    family_name: fam.family_name, category: fam.category,
    geometry: { primitive: "box", width: { value: 20, unit: "in" }, depth: { value: 5.75, unit: "in" }, height: { value: 44, unit: "in" } },
    parameters: [{ name: "Apex_Voltage", spec_type: "Text", group: "Electrical", is_instance: false, value: "208Y/120V", confidence: 0.98 }],
  };
  if (p === "/v1/extractions" && req.method === "GET")
    return j(res, 200, { extractions: [{ id: "73b8653b-0000-4000-8000-000000000000", status: "ready", category: fam.category, family_name: fam.family_name, filename: "pending.pdf", created_at: "2026-08-11T00:00:00Z" }] });
  if (/^\/v1\/extractions\/[0-9a-f-]+$/.test(p) && req.method === "GET")
    return j(res, 200, { id: p.split("/").pop(), status: "ready", claude_result: pendingResult });
  if (p === "/v1/extractions" && req.method === "POST")
    return j(res, 201, { id: "73b8653b-0000-4000-8000-000000000000", status: "ready", result: {
      family_name: fam.family_name, category: fam.category,
      geometry: { primitive: "box", width: { value: 20, unit: "in" }, depth: { value: 5.75, unit: "in" }, height: { value: 44, unit: "in" } },
      parameters: [{ name: "Apex_Voltage", spec_type: "Text", group: "Electrical", is_instance: false, value: "208Y/120V", confidence: 0.98 }],
      warnings: ["Depth read from side elevation"] } });
  if (p.endsWith("/approve")) {
    // Corrections arrive as {result}; echo the corrected name back like the real API.
    let corrected = null;
    try { corrected = JSON.parse(body).result || null; } catch { /* empty body ok */ }
    if (corrected && (typeof corrected.family_name !== "string" || !(corrected.geometry?.width?.value > 0)))
      return j(res, 400, { error: { code: "INVALID_CORRECTION", message: "bad corrected result" } });
    const famOut = { ...fam, family_name: corrected?.family_name ?? fam.family_name };
    return j(res, 201, { family: famOut, extraction_id: "73b8653b-0000-4000-8000-000000000000" });
  }
  if (p.endsWith("/validate")) return j(res, 200, { object_id: fam.id, family_name: fam.family_name, passed: true, score: 1, summary: { errors: 0, warnings: 0, info: 0 }, findings: [{ rule: "S-1", severity: "error", passed: true, message: "ok" }] });
  if (p.endsWith("/generate-rfa")) return j(res, 202, { job_id: "749cc9c8-0000-4000-8000-000000000000", status: "queued", note: "queued" });
  if (p.endsWith("/rfa") && req.method === "GET") {
    res.writeHead(200, { "Content-Type": "application/octet-stream", ...CORS });
    return res.end(Buffer.from("RFA-test-bytes"));
  }
  j(res, 404, { error: { code: "NOT_FOUND", message: "Unknown route " + p } });
}).listen(4600, () => console.log("mock api listening on 4600"));
