# Apex BIM Studio — known limitations (read before the trial)

Setting expectations honestly, before day one. Version 0.5.0-rc1.

## The headline number, honestly

**The success rate on Meta EMT 11990 drawings is NOT YET MEASURED.** The blind-holdout
protocol that produces the trial's defensible number (20–30% of drawings sealed until tuning
stops, then run once) is written and ready — but **zero project drawings have entered the
pipeline yet**, so no honest rate can be quoted. On the six internal fixture drawings the
pipeline passes 6/6 with QA scores 0.88–1.0 — that is a statement about fixtures, not about
your drawings, and we will not present it as one. The holdout number will be measured and
shared before the trial is scored.

## What the product does NOT do yet

- **Equipment classes**: extraction is tuned for pad-mount transformers and panelboard-class
  gear. EMT/conduit runs, raceway, and classes outside box-shaped equipment have no extractor
  and no geometry support (families are parametric BOXES with your equipment's dimensions and
  data — accurate for enclosures; not detailed product geometry).
- **One unit per document**: a combined submittal package with many units in one PDF is not
  split automatically; upload per-unit sections. PDFs above ~20 MB may fail extraction.
- **Category mapping**: every family is currently built on the Electrical Equipment template,
  even when the submittal says e.g. Mechanical Equipment (the category is recorded, not yet
  applied).
- **Wrong-but-plausible values**: validation blocks impossible values (zero sizes, malformed
  fields) and flags low-confidence ones for review — it cannot catch a confidently wrong
  value that looks sensible. The review step before building exists for exactly this; spot
  checks against the submittal are part of the workflow, not optional.
- **Revit versions**: families open in the Revit version they were built with, or newer —
  never older. The build machine's Revit must match or pre-date your project's version.
- **Cloud dependency**: upload/extraction/review run through Apex's cloud (drawings are
  processed by an AI service). Building families from already-downloaded specs works fully
  offline. Data-handling terms for project documents are agreed BEFORE any drawing is
  uploaded.
- **Batch UI**: during a batch, Revit is intentionally busy — the progress window shows
  per-item status, but you cannot use Revit until the batch finishes (it says so before
  starting).

## Support expectations

Every failure produces a named next step on screen and in the build report; every run
produces one log file. "Send the log" is the whole support request.
