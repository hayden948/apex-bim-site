-- The Revit plugin consumes AFIS (Apex Family Interchange Schema) documents.
-- Store the canonical AFIS JSON alongside each family record.
ALTER TABLE public.families ADD COLUMN IF NOT EXISTS afis jsonb;
COMMENT ON COLUMN public.families.afis IS 'Canonical AFIS 1.0 document served to the Revit plugin (GET /v1/families/{id}). Metric units.';
