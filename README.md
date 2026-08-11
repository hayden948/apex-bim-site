# APEX BIM Studio

This repo now holds three components:

| Directory | Component |
|---|---|
| `/` (root) | Marketing site (static HTML/CSS, described below) + `app.html` pipeline console |
| `revit-plugin/` | The ApexBimStudio Revit add-in — source, tests, CI. See `revit-plugin/README.md`. |
| `supabase/` | Apex API v1 (edge function + migrations), deployed to the `apex-bim-studio` Supabase project. See `supabase/README.md`. |
| `tests/console/` | Browser tests for the pipeline console (mock API + Playwright, run in CI). |

## The full loop (submittal → placed Revit family)

1. **Sign up / sign in** on `app.html` (the pipeline console) and create a
   project — you become its admin. Add teammates by email via the members API.
2. **Upload** an equipment submittal PDF and run **AI extraction** (Claude reads
   the drawing; needs the `ANTHROPIC_API_KEY` secret on the Supabase project).
   Review the extracted dimensions/parameters with per-field confidence.
3. **Approve** — the extraction becomes an AFIS 1.0 document in the family
   library (metric, NEC 110.26 clearance zone auto-added for electrical gear).
4. **Validate** (Doc 8 QA engine) and **Queue RFA** — QA-gated: no certificate,
   no export.
5. In Revit: **Mint plugin token** in the console (copies to clipboard) →
   **Apex BIM Studio → Settings → Save token from clipboard** → **Generate →
   Process Queue**. The plugin builds the family from AFIS, places/saves it,
   and uploads the built `.rfa` back to the library, where anyone on the
   project can download it.

---

## Marketing site

Production-ready static implementation of the **Revizto-style** APEX BIM Studio homepage
(implemented from the Claude Design handoff bundle).

## Pages

| File                 | Purpose                                                                 |
| -------------------- | ----------------------------------------------------------------------- |
| `index.html`         | Homepage + the interactive product simulation (vanilla JS).             |
| `app.html`           | **Pipeline console** — drives the hosted Apex API end-to-end: sign in (or sign up) with an Apex account, pick/create a project, upload a submittal PDF → AI extraction review → approve into the library → QA validate → queue RFA → download the built family. An `apx_` service token also works (see `supabase/README.md`). |
| `product.html`       | AI Revit Family Generator — problem, 5-step workflow, feature rows, ROI, use cases. |
| `survey.html`        | Survey & Field Layout — nested points, layout workflow, field-format strip, KPIs. |
| `integrations.html`  | Integrations grid (Revit, ACC, BIM 360, Trimble, Navisworks, Procore, Bluebeam, ReCap, Leica/Topcon, API) grouped by category, Live / Coming-soon status. |
| `pricing.html`       | Starter / Professional / Enterprise / Custom tiers, monthly↔annual toggle, feature matrix, FAQ. |
| `demo.html`          | Book-a-demo form (with inline submit handling), video section, ROI, FAQ. |
| `about.html`         | Story, Mission & Vision, values, timeline, partners.                    |

## Shared assets

| File              | Purpose                                                              |
| ----------------- | -------------------------------------------------------------------- |
| `apex-rv.css`     | Core design system — light high-trust AECO SaaS, signature red, blue "intelligence" accent, Revit-UI mockups. |
| `apex-pages.css`  | Sub-page components — page hero, pricing tiers + matrix, FAQ accordion, forms, integration cards, about timeline. |
| `image-slot.js`   | Drag-and-drop image placeholder web component (for swap-in partner logos). |

All pages share one header/nav and footer, and link to each other. The nav: Product · Survey &
Layout · Integrations · Pricing · About, with a red "Book a demo" CTA → `demo.html`.

No build step and no dependencies — fonts load from Google Fonts.

**To view it now:** double-click `index.html` (it opens in your browser straight from disk —
the CSS, JS, and the interactive simulation all work over `file://`; only the drag-in logo
*persistence* needs a host, which doesn't affect anything on the page today).

**To put it online:** drag this whole folder onto [app.netlify.com/drop](https://app.netlify.com/drop)
(or connect it to Vercel / GitHub Pages). It's a plain static site, so any static host works
with zero configuration.

## Sections

Announcement bar · sticky nav · hero with **live interactive simulation** (pick equipment →
scan → generate Revit family → validate 8/8 → export field-points CSV, across Electrical &
Mechanical disciplines) · partners wall · trust stats · 4 alternating feature rows with
Revit-UI mockups · As-Built Intelligence band (red + blue duotone) · 12-tool modeling toolset
(Electrical/Mechanical tabs) · Auto-Route Conduit spotlight · proof cards · integrations
marquee · awards · final CTA · footer.

## Notes carried over from the design

- **Partner logos** (Cache Valley, Taylor, Summit, DP Electric) are original CSS wordmark
  logotypes — *stand-ins*, not the companies' trademarked marks. Replace with official logo
  files only with each company's written permission. `image-slot.js` supports dropping real
  logo files in (persists only inside the Claude Design runtime).
- Product mockups are CSS-drawn stand-ins for real Revit screenshots/photography.
- Toolset feature lists are marketing-level; confirm they match shipped capability before launch.
