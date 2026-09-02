#!/usr/bin/env python3
"""FamilySpec parity check: EXTRACTION_SCHEMA (api/index.ts) vs the schema of record.

The API's Claude structured-output schema is the third place the FamilySpec shape
appears. This check fails (exit 1) the moment it drifts from
schemas/familyspec/familyspec.v1.schema.json, except for the documented
divergences below — each of which is the model-emitter being STRICTER or a
server-owned field the model must not emit. Anything else is drift.

ALLOWED DIVERGENCES (justifications live here, on purpose):
 A1 root 'schema_version': absent from EXTRACTION_SCHEMA — server-stamped after
    validation (the model is not trusted to assert the contract version).
 A2 parameter 'value': file allows string|number|boolean (hand-authored files);
    the model is constrained to string only. Stricter emitter — allowed.
 A3 numeric bounds (dimension exclusiveMinimum, confidence 0..1) and string
    minLength: enforced at runtime by familySpecProblems on every write path;
    kept out of the model schema (structured-output subset stays conservative).

Usage: python3 schemas/familyspec/tools/check_extraction_schema.py
"""
import json
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
API = REPO / "supabase" / "functions" / "api" / "index.ts"
SCHEMA = REPO / "schemas" / "familyspec" / "familyspec.v1.schema.json"


def extract_ts_literal(source: str, const_name: str) -> dict:
    m = re.search(rf"const {const_name}\s*=\s*\{{", source)
    if not m:
        sys.exit(f"FAIL: const {const_name} not found in {API}")
    start = source.index("{", m.start())
    depth, i = 0, start
    in_str = False
    while i < len(source):
        c = source[i]
        if in_str:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                in_str = False
        elif c == '"':
            in_str = True
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                break
        i += 1
    literal = source[start : i + 1]
    # TS object literal -> JSON: quote bare keys (keys never contain colons here).
    literal = re.sub(r"([,{\[]\s*)([A-Za-z_][A-Za-z0-9_]*)\s*:", r'\1"\2":', literal)
    literal = re.sub(r",(\s*[}\]])", r"\1", literal)  # trailing commas
    return json.loads(literal)


def main() -> int:
    ext = extract_ts_literal(API.read_text(), "EXTRACTION_SCHEMA")
    ref = json.loads(SCHEMA.read_text())
    errors = []

    def seteq(a, b, label, allow_extra_in_b=()):
        a, b = set(a), set(b)
        extra_a = a - b
        extra_b = b - a - set(allow_extra_in_b)
        if extra_a or extra_b:
            errors.append(f"{label}: only-in-extraction {sorted(extra_a)} only-in-schema {sorted(extra_b)}")

    # Root: A1 — schema_version is schema-of-record-only.
    seteq(ext["properties"].keys(), ref["properties"].keys(), "root properties",
          allow_extra_in_b=("schema_version",))
    seteq(ext["required"], ref["required"], "root required", allow_extra_in_b=("schema_version",))

    ref_dim = ref["definitions"]["dimension"]
    for d in ("width", "depth", "height"):
        e = ext["properties"]["geometry"]["properties"][d]
        seteq(e["properties"].keys(), ref_dim["properties"].keys(), f"geometry.{d} properties")
        seteq(e["required"], ref_dim["required"], f"geometry.{d} required")
        if e["properties"]["unit"]["enum"] != ref_dim["properties"]["unit"]["enum"]:
            errors.append(f"geometry.{d}.unit enum differs")
    seteq(ext["properties"]["geometry"]["properties"].keys(),
          ref["properties"]["geometry"]["properties"].keys(), "geometry properties")
    seteq(ext["properties"]["geometry"]["required"],
          ref["properties"]["geometry"]["required"], "geometry required")

    # geometry.primitive: schema const must equal the extraction enum's single value.
    ref_prim = ref["properties"]["geometry"]["properties"]["primitive"].get("const")
    ext_prim = ext["properties"]["geometry"]["properties"]["primitive"].get("enum")
    if ext_prim != [ref_prim]:
        errors.append(f"geometry.primitive differs: extraction enum {ext_prim} vs schema const {ref_prim!r}")

    e_par = ext["properties"]["parameters"]["items"]
    r_par = ref["properties"]["parameters"]["items"]
    seteq(e_par["properties"].keys(), r_par["properties"].keys(), "parameter properties")
    seteq(e_par["required"], r_par["required"], "parameter required")
    for enum_field in ("spec_type", "group"):
        if e_par["properties"][enum_field]["enum"] != r_par["properties"][enum_field]["enum"]:
            errors.append(f"parameter {enum_field} enum differs: "
                          f"extraction={e_par['properties'][enum_field]['enum']} "
                          f"schema={r_par['properties'][enum_field]['enum']}")
    # A2: value — extraction must be a SUBSET of the file's union.
    ext_value_types = e_par["properties"]["value"].get("type")
    ref_value_types = r_par["properties"]["value"]["type"]
    ext_set = {ext_value_types} if isinstance(ext_value_types, str) else set(ext_value_types or [])
    if not ext_set <= set(ref_value_types):
        errors.append(f"parameter value type {sorted(ext_set)} is not a subset of schema {ref_value_types}")

    if errors:
        print("EXTRACTION_SCHEMA PARITY FAILED:")
        for e in errors:
            print("  -", e)
        return 1
    print("EXTRACTION_SCHEMA PARITY OK: api/index.ts literal matches the schema of record "
          "(allowed divergences A1-A3 documented in this script).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
