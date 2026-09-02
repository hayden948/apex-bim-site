# Round-trip golden normalization

`expected/<fixture>.afis.json` holds `{qa_score_expected, afis}` where `afis` is the document
the DEPLOYED approve pipeline (`api` v27) produced for the fixture, normalized as follows.
Every difference between a live round-trip and these files, other than the two below, is a
regression — investigate, do not rebaseline.

1. **Family UUID → `<FAMILY_ID>`**: the family id is minted per approve (`crypto.randomUUID()`)
   and appears exactly twice — `$.id` and the server-stamped `Apex_AfisId` parameter value.
   Both are placeholdered.
2. **Unique-name suffix stripped**: when the demo library already holds the fixture's name,
   the approve retry appends " (N)" to `$.identity.name` (and the `families.family_name`
   column). Normalized back to the base name.

There are NO other volatile fields: AFIS carries no timestamps, and predToAfis is a pure
function of (familyId, FamilySpec). `qa_score_expected` is the deterministic QA engine score.

Baseline provenance: generated 2026-08-17 against deployed api v27; each file verified
content-identical to the live output by an in-database jsonb equality check
(6/6 `expected_matches_live = true`, pasted in LEDGER Round 2 Cycle 3).
