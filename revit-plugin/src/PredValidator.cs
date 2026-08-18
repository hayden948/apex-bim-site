using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Apex.BimStudio.Commands;

/// <summary>
/// FamilySpec v1 boundary validator (schema of record:
/// schemas/familyspec/familyspec.v1.schema.json — see DECISION.md).
///
/// Runs on the RAW JsonElement, before deserialization and before any Revit
/// API call, so presence/type/unknown-property problems surface as named-field
/// messages instead of defaults or stack traces. The public rule tables below
/// (property sets, enums, required lists) are contract-tested against the
/// schema file by the test suite; change either side and the suite fails.
///
/// Version policy (Sprint 002, "every change is breaking"):
///  - schema_version "1.0"  -> validate as v1.
///  - schema_version absent -> legacy v0: accepted, but every accommodation is
///    surfaced as a warning naming the file and field (never silent).
///  - any other value       -> hard reject.
/// </summary>
public static class PredValidator
{
    public const string Version = "1.0";

    // Contract-tested against familyspec.v1.schema.json — keep in sync via the suite, not by hand.
    public static readonly string[] RootProperties =
        { "schema_version", "family_name", "category", "family_template", "geometry", "parameters", "warnings" };
    public static readonly string[] RootRequired =
        { "schema_version", "family_name", "category", "geometry", "parameters" };
    public static readonly string[] GeometryProperties = { "primitive", "width", "depth", "height" };
    public static readonly string[] DimensionProperties = { "value", "unit" };
    public static readonly string[] ParameterProperties =
        { "name", "spec_type", "group", "is_instance", "value", "units", "confidence" };
    public static readonly string[] ParameterRequired =
        { "name", "spec_type", "group", "is_instance", "value" };
    public static readonly string[] SpecTypes = { "Text", "Length", "Integer", "Number" };
    public static readonly string[] Groups =
        { "Dimensions", "Electrical", "Electrical - Loads", "Constraints", "Identity Data" };
    public static readonly string[] Units = { "in", "mm", "cm", "m", "ft" };

    public sealed class Result
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool IsLegacyV0 { get; internal set; }
        public bool IsValid => Errors.Count == 0;
    }

    public static Result Validate(JsonElement root, string source)
    {
        var r = new Result();

        if (root.ValueKind != JsonValueKind.Object)
        {
            r.Errors.Add($"{source}: document root must be a JSON object (got {Kind(root)}).");
            return r;
        }

        // --- schema_version gate ---
        if (root.TryGetProperty("schema_version", out JsonElement ver))
        {
            if (ver.ValueKind != JsonValueKind.String || ver.GetString() != Version)
            {
                r.Errors.Add($"{source}: schema_version must be \"{Version}\" (got {Raw(ver)}). " +
                    "This add-in reads Apex equipment specs v1 only — a newer file needs a newer add-in.");
                return r;
            }
        }
        else
        {
            r.IsLegacyV0 = true;
            r.Warnings.Add($"{source}: schema_version missing — treated as legacy v0. " +
                $"Add \"schema_version\": \"{Version}\" to make this file a v1 document.");
        }

        CheckUnknown(root, RootProperties, "", source, r);

        RequireString(root, "family_name", source, r);
        RequireString(root, "category", source, r);
        if (root.TryGetProperty("family_template", out JsonElement ft) && ft.ValueKind != JsonValueKind.String)
            r.Errors.Add($"{source}: family_template must be a string (got {Kind(ft)}).");

        // --- geometry ---
        if (!root.TryGetProperty("geometry", out JsonElement geom))
        {
            r.Errors.Add($"{source}: geometry is required (object with primitive/width/depth/height).");
        }
        else if (geom.ValueKind != JsonValueKind.Object)
        {
            r.Errors.Add($"{source}: geometry must be an object (got {Kind(geom)}).");
        }
        else
        {
            CheckUnknown(geom, GeometryProperties, "geometry.", source, r);
            // Exact match: the schema of record is const "box" (case-sensitive).
            if (!geom.TryGetProperty("primitive", out JsonElement prim)
                || prim.ValueKind != JsonValueKind.String
                || !string.Equals(prim.GetString(), "box", StringComparison.Ordinal))
            {
                r.Errors.Add($"{source}: geometry.primitive must be \"box\" " +
                    $"(got {(geom.TryGetProperty("primitive", out JsonElement p2) ? Raw(p2) : "nothing")}). " +
                    "v1 supports box geometry only.");
            }
            foreach (string dim in new[] { "width", "depth", "height" })
                ValidateDimension(geom, dim, source, r);
        }

        // --- parameters ---
        if (!root.TryGetProperty("parameters", out JsonElement pars))
        {
            r.Errors.Add($"{source}: parameters is required (array; use [] when the drawing yields none).");
        }
        else if (pars.ValueKind != JsonValueKind.Array)
        {
            r.Errors.Add($"{source}: parameters must be an array (got {Kind(pars)}).");
        }
        else
        {
            int i = 0;
            foreach (JsonElement item in pars.EnumerateArray())
            {
                ValidateParameter(item, i, source, r);
                i++;
            }
        }

        if (root.TryGetProperty("warnings", out JsonElement warn)
            && (warn.ValueKind != JsonValueKind.Array
                || warn.EnumerateArray().Any(w => w.ValueKind != JsonValueKind.String)))
        {
            r.Errors.Add($"{source}: warnings must be an array of strings.");
        }

        return r;
    }

    private static void ValidateDimension(JsonElement geom, string name, string source, Result r)
    {
        string path = $"geometry.{name}";
        if (!geom.TryGetProperty(name, out JsonElement dim))
        {
            r.Errors.Add($"{source}: {path} is required ({{\"value\": <number > 0>, \"unit\": \"{string.Join("|", Units)}\"}}).");
            return;
        }
        if (dim.ValueKind != JsonValueKind.Object)
        {
            r.Errors.Add($"{source}: {path} must be an object (got {Kind(dim)}).");
            return;
        }
        CheckUnknown(dim, DimensionProperties, path + ".", source, r);

        if (!dim.TryGetProperty("value", out JsonElement v) || v.ValueKind != JsonValueKind.Number
            || !v.TryGetDouble(out double d) || !(d > 0))
        {
            r.Errors.Add($"{source}: {path}.value must be a number > 0 " +
                $"(got {(dim.TryGetProperty("value", out JsonElement v2) ? Raw(v2) : "nothing")}).");
        }

        if (dim.TryGetProperty("unit", out JsonElement u))
        {
            if (u.ValueKind != JsonValueKind.String || !Units.Contains(u.GetString()))
                r.Errors.Add($"{source}: {path}.unit must be one of {string.Join("/", Units)} (got {Raw(u)}).");
        }
        else if (r.IsLegacyV0)
        {
            r.Warnings.Add($"{source}: {path}.unit missing — legacy v0 file, assuming inches.");
        }
        else
        {
            r.Errors.Add($"{source}: {path}.unit is required (one of {string.Join("/", Units)}).");
        }
    }

    private static void ValidateParameter(JsonElement item, int index, string source, Result r)
    {
        string path = $"parameters[{index}]";
        if (item.ValueKind != JsonValueKind.Object)
        {
            r.Errors.Add($"{source}: {path} must be an object (got {Kind(item)}).");
            return;
        }
        CheckUnknown(item, ParameterProperties, path + ".", source, r);

        string label = item.TryGetProperty("name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String
            ? $"{path} ('{nm.GetString()}')" : path;

        if (nm.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nm.GetString()))
            r.Errors.Add($"{source}: {path}.name must be a non-empty string.");

        CheckEnum(item, "spec_type", SpecTypes, label, source, r);
        CheckEnum(item, "group", Groups, label, source, r);

        if (!item.TryGetProperty("is_instance", out JsonElement inst))
        {
            if (r.IsLegacyV0)
                r.Warnings.Add($"{source}: {label}.is_instance missing — legacy v0 file, assuming false (type parameter).");
            else
                r.Errors.Add($"{source}: {label}.is_instance is required (true = instance parameter, false = type).");
        }
        else if (inst.ValueKind != JsonValueKind.True && inst.ValueKind != JsonValueKind.False)
        {
            r.Errors.Add($"{source}: {label}.is_instance must be true or false (got {Raw(inst)}).");
        }

        if (!item.TryGetProperty("value", out JsonElement val))
        {
            r.Errors.Add($"{source}: {label}.value is required (string, number, or boolean).");
        }
        else if (val.ValueKind is not (JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False))
        {
            r.Errors.Add($"{source}: {label}.value must be a string, number, or boolean (got {Kind(val)}).");
        }

        if (item.TryGetProperty("units", out JsonElement un) && un.ValueKind != JsonValueKind.String)
            r.Errors.Add($"{source}: {label}.units must be a string (got {Kind(un)}).");

        if (item.TryGetProperty("confidence", out JsonElement conf))
        {
            if (conf.ValueKind != JsonValueKind.Number || !conf.TryGetDouble(out double c) || c < 0 || c > 1)
                r.Errors.Add($"{source}: {label}.confidence must be a number between 0 and 1 (got {Raw(conf)}).");
        }
    }

    private static void CheckEnum(JsonElement obj, string prop, string[] allowed, string label, string source, Result r)
    {
        if (!obj.TryGetProperty(prop, out JsonElement v))
        {
            r.Errors.Add($"{source}: {label}.{prop} is required (one of: {string.Join(", ", allowed)}).");
            return;
        }
        if (v.ValueKind != JsonValueKind.String || !allowed.Contains(v.GetString()))
            r.Errors.Add($"{source}: {label}.{prop} must be one of {string.Join(", ", allowed)} (got {Raw(v)}).");
    }

    // Telltale keys of the retired legacy exporter's format (internally:
    // shop2revit), which claims the same "1.0" identifier for a different
    // shape — name the wrong-format situation in the customer's language
    // instead of producing field-soup errors. These messages reach the
    // customer's screen and kept report: no repo paths, no internal codenames.
    private static readonly string[] Shop2RevitKeys = { "product_type", "source", "bill_of_materials", "series" };

    private static void CheckUnknown(JsonElement obj, string[] known, string prefix, string source, Result r)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (known.Contains(p.Name)) continue;
            string msg = prefix.Length == 0 && Shop2RevitKeys.Contains(p.Name)
                ? $"{source}: field '{p.Name}' comes from a different, unsupported export format — this is " +
                  "not an Apex equipment spec. Download the spec from the Apex portal instead."
                : $"{source}: unknown field '{prefix}{p.Name}' — not part of an Apex equipment spec " +
                  $"(v{Version}). Remove it or fix the spelling.";
            if (r.IsLegacyV0) r.Warnings.Add(msg);
            else r.Errors.Add(msg);
        }
    }

    private static string Kind(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    private static string Raw(JsonElement e)
    {
        string raw = e.GetRawText();
        return raw.Length > 40 ? raw.Substring(0, 37) + "..." : raw;
    }

    private static void RequireString(JsonElement root, string prop, string source, Result r)
    {
        if (!root.TryGetProperty(prop, out JsonElement v)
            || v.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(v.GetString()))
        {
            r.Errors.Add($"{source}: {prop} must be a non-empty string " +
                $"(got {(root.TryGetProperty(prop, out JsonElement v2) ? Raw(v2) : "nothing")}).");
        }
    }
}
