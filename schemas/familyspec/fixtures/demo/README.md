# Walkthrough demo files (round 4)

Deliberately broken inputs for `revit-plugin/deploy/WALKTHROUGH.md` — copy them into the
walkthrough batch folder as-is. NOT part of the golden/malformed validation sets (the test
suite scans only `../golden` and `../malformed`).

- `zz-depth-miss.pred.json` — parses and looks plausible, but `geometry.depth.value` is 0 and
  the `Depth` parameter carries confidence 0.31: the validator blocks the build naming the
  field, and Review Submittal flags the row CHECK. The real depth is 24 in (see its warning).
- `zz-corrupt.pred.json` — not parseable as JSON at all: exercises the BadInput path
  (quarantine + report entry + batch continues).
