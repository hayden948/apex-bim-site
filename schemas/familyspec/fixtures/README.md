# FamilySpec v1 fixtures

## golden/ — valid documents the test suite must accept, with provenance

| Fixture | Provenance |
|---|---|
| `nq430-panelboard.pred.json` | The live pipeline's HUMAN-APPROVED extraction of the real Square D NQ430L2C submittal (studio DB extraction `92dcd601-eb54-46f5-83c6-d49b3aa43ce2`, status `approved`), `schema_version` stamped. 18 reviewed parameters — the strongest regression anchor available in-repo. |
| `panelboard-example.pred.json` | The repo's original hand-authored example (`revit-plugin/examples/panelboard.pred.json`), upgraded to v1 (same content, both copies updated together). |
| `demo-switchboard-sb2.pred.json` | Live DB seed review row `dddd5555-…`, stamped. |
| `hidden-ahu9.pred.json` | Live DB seed review row `eeee6666-…` — Mechanical category, exercises the non-electrical (no NEC zone) branch downstream. |
| `transformer-t1-corrected.pred.json` | Live DB row `ffff7777-…` — a human-corrected result (exercises the corrections path shape). |
| `zgsl-padmount-transformer.pred.json` | Derived by hand from the parser repo's verified extraction figures for the ZGSL-H pad-mount transformer drawing (`apex-parser-service/parser/tests/test_transformer.py` expected values: 1500 kVA, 34.5 kV/480 V, 2340×2025×1990 mm, 6260 kg). mm units — exercises unit conversion. Derivation, not a parser output: the parser emits the quarantined FamilySpec shape, not v1. |

The 19 Sprint 001 M1 transformer inputs remain MISSING from version control (ship register
row #5 / LEDGER round 1). This set is the interim regression net until the operator supplies
them; when they arrive they join `golden/` unchanged except a stamped `schema_version`.

## malformed/ — documents the validator must REJECT with a named-field message

Each `X.pred.json` has an `X.pred.json.expected` file containing a substring that must appear
in at least one error message (asserted by the test suite). This is the "invalid input produces
a named-field error" guarantee, kept honest per fixture.

## expected/ — round-trip goldens

`expected/<fixture>.afis.json` is the AFIS 1.0 document the DEPLOYED approve pipeline produced
for the fixture, normalized (volatile fields listed in `expected/NORMALIZATION.md`). Regenerate
only via `schemas/familyspec/tools/roundtrip.py` and only when a schema round authorizes the
change — a diff here is a regression, not a rebaseline opportunity.
