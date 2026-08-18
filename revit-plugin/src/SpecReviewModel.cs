using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Apex.BimStudio.Commands;

/// <summary>
/// Round 4: the review-and-override core, free of any Revit type so the whole
/// load → show-fields-with-confidence → edit → revalidate → save loop is
/// testable off the Revit machine.
///
/// This is the release valve for a parser miss: the modeler sees every
/// extracted value (with the extraction's own confidence where the file
/// carries one), corrects the wrong field, and the corrected file goes through
/// the SAME FamilySpec v1 validator as an untouched one — an override can fix
/// a value, never smuggle an invalid document past the boundary.
///
/// Deliberately narrow edit surface: values and units only (equipment name,
/// box dimensions, parameter values). Structure — spec_type, group,
/// is_instance, the parameter list itself — stays read-only here; a structural
/// mistake is an extraction bug to fix upstream, not something to hand-patch
/// per drawing. No schema fields are added or removed by this class.
///
/// Saving writes the corrected JSON over the original and keeps the original
/// text as "&lt;file&gt;.bak" — first save wins, so the .bak is always the
/// extraction AS DELIVERED, however many correction rounds follow.
/// </summary>
public sealed class SpecReviewModel
{
    /// <summary>Values with confidence below this are flagged for review.</summary>
    public const double LowConfidenceThreshold = 0.8;

    public sealed class Field
    {
        public string Key = "";           // stable edit key, e.g. "geometry.width" or "parameters[3].value"
        public string Label = "";         // what the modeler reads, e.g. "Width" or the parameter name
        public string Value = "";         // current value as text
        public string? Units;             // "in", "A", ... when the file states one
        public double? Confidence;        // extraction confidence when the file states one
        public bool Editable;
        public bool LowConfidence => Confidence.HasValue && Confidence.Value < LowConfidenceThreshold;
    }

    public string SourcePath { get; }
    /// <summary>Null when the file could not be read or parsed; message says why.</summary>
    public string? LoadError { get; }
    /// <summary>The file's own extraction warnings (its "warnings" array), verbatim.</summary>
    public IReadOnlyList<string> ExtractionWarnings { get; } = Array.Empty<string>();

    private readonly JsonNode? _root;
    private readonly string _originalText = "";

    private SpecReviewModel(string path, JsonNode? root, string originalText, string? loadError)
    {
        SourcePath = path;
        _root = root;
        _originalText = originalText;
        LoadError = loadError;
        if (root?["warnings"] is JsonArray warn)
            ExtractionWarnings = warn.OfType<JsonNode>()
                .Where(n => n.GetValueKind() == JsonValueKind.String)
                .Select(n => n.GetValue<string>()).ToList();
    }

    public static SpecReviewModel Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new SpecReviewModel(path, null, "", $"Could not read '{path}': {ex.Message}");
        }
        try
        {
            JsonNode? root = JsonNode.Parse(text);
            if (root is not JsonObject)
                return new SpecReviewModel(path, null, text,
                    $"'{Path.GetFileName(path)}' is not an equipment spec (the file must contain a single JSON object).");
            return new SpecReviewModel(path, root, text, null);
        }
        catch (JsonException ex)
        {
            return new SpecReviewModel(path, null, text,
                $"'{Path.GetFileName(path)}' is not readable as JSON: {ex.Message}");
        }
    }

    /// <summary>Current document as indented JSON (what Save writes, what the build consumes).</summary>
    public string ToJson() => _root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";

    /// <summary>Run the FamilySpec v1 boundary validator on the CURRENT (possibly edited) state.</summary>
    public PredValidator.Result Validate()
    {
        if (_root == null)
        {
            var r = new PredValidator.Result();
            r.Errors.Add(LoadError ?? "File not loaded.");
            return r;
        }
        using JsonDocument doc = JsonDocument.Parse(ToJson());
        return PredValidator.Validate(doc.RootElement, Path.GetFileName(SourcePath));
    }

    /// <summary>
    /// The field list the review grid shows, rebuilt from current state on each call.
    /// Order: identity, dimensions, then parameters in file order.
    /// </summary>
    public List<Field> BuildFields()
    {
        var fields = new List<Field>();
        if (_root == null) return fields;

        fields.Add(new Field
        {
            Key = "family_name",
            Label = "Equipment name",
            Value = StringOf(_root["family_name"]),
            Editable = true,
        });
        fields.Add(new Field
        {
            // "(from the submittal)": the current builder always uses the
            // Electrical Equipment template regardless of this value —
            // category-driven template selection is logged ledger debt, and
            // the label must not imply the value is honored.
            Key = "category",
            Label = "Category (from the submittal)",
            Value = StringOf(_root["category"]),
            Editable = false,
        });
        if (_root["family_template"] != null)
        {
            fields.Add(new Field
            {
                Key = "family_template",
                Label = "Family template",
                Value = StringOf(_root["family_template"]),
                Editable = false,
            });
        }

        foreach (string dim in new[] { "width", "depth", "height" })
        {
            JsonNode? d = _root["geometry"]?[dim];
            fields.Add(new Field
            {
                Key = $"geometry.{dim}",
                // "Overall": the same drawing often also carries a
                // Dimensions-group PARAMETER with the same name — two rows
                // both called "Depth" would be indistinguishable in the grid.
                Label = "Overall " + dim,
                Value = StringOf(d?["value"]),
                Units = d?["unit"] is JsonNode u && u.GetValueKind() == JsonValueKind.String ? u.GetValue<string>() : null,
                // v1 geometry carries no confidence of its own; when the extraction
                // also emitted the same dimension as a "Dimensions" parameter, show
                // that parameter's confidence here (display only, no schema change).
                Confidence = DimensionParameterConfidence(dim),
                Editable = true,
            });
        }

        if (_root["parameters"] is JsonArray pars)
        {
            for (int i = 0; i < pars.Count; i++)
            {
                JsonNode? p = pars[i];
                if (p is not JsonObject) continue;
                double? conf = null;
                if (p["confidence"] is JsonNode c && c.GetValueKind() == JsonValueKind.Number)
                    conf = c.GetValue<double>();
                fields.Add(new Field
                {
                    Key = $"parameters[{i}].value",
                    Label = StringOf(p["name"]),
                    Value = StringOf(p["value"]),
                    Units = p["units"] is JsonNode un && un.GetValueKind() == JsonValueKind.String ? un.GetValue<string>() : null,
                    Confidence = conf,
                    Editable = true,
                });
            }
        }
        return fields;
    }

    /// <summary>How many fields the review should flag (confidence below threshold).</summary>
    public int LowConfidenceCount() => BuildFields().Count(f => f.LowConfidence);

    /// <summary>
    /// Apply one edit. Returns null on success, otherwise a message in the
    /// modeler's language saying exactly what to type instead. Never throws.
    /// Accepted keys: family_name, geometry.{width|depth|height}[.unit],
    /// parameters[i].value, parameters[i].units.
    /// </summary>
    public string? TrySet(string key, string newValue)
    {
        if (_root == null) return LoadError ?? "File not loaded.";
        newValue = (newValue ?? "").Trim();

        if (key == "family_name")
        {
            if (newValue.Length == 0) return "Equipment name cannot be empty.";
            _root["family_name"] = newValue;
            return null;
        }

        if (key.StartsWith("geometry.", StringComparison.Ordinal))
        {
            string rest = key.Substring("geometry.".Length);
            bool unitEdit = rest.EndsWith(".unit", StringComparison.Ordinal);
            string dim = unitEdit ? rest.Substring(0, rest.Length - ".unit".Length) : rest;
            if (dim is not ("width" or "depth" or "height")) return $"Unknown field '{key}'.";
            if (_root["geometry"] is not JsonObject geom)
                return "This file has no geometry section; it cannot be corrected here.";
            if (geom[dim] is not JsonObject d)
            {
                // The dimension object may be missing entirely (that is the miss
                // being corrected) — create the container so the fix can land.
                d = new JsonObject();
                geom[dim] = d;
            }
            if (unitEdit)
            {
                if (!PredValidator.Units.Contains(newValue))
                    return $"Unit must be one of {string.Join(", ", PredValidator.Units)} (you typed '{newValue}').";
                d["unit"] = newValue;
                return null;
            }
            if (!double.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0))
                return $"{char.ToUpperInvariant(dim[0]) + dim.Substring(1)} must be a number greater than 0 " +
                       $"(you typed '{newValue}'). Use digits and a period, e.g. 5.75";
            d["value"] = v;
            return null;
        }

        if (key.StartsWith("parameters[", StringComparison.Ordinal))
        {
            int close = key.IndexOf(']');
            if (close < 0 || !int.TryParse(key.Substring(11, close - 11), out int idx))
                return $"Unknown field '{key}'.";
            string tail = key.Substring(close + 1);
            if (_root["parameters"] is not JsonArray pars || idx < 0 || idx >= pars.Count
                || pars[idx] is not JsonObject par)
                return "That parameter no longer exists in the file.";
            string label = StringOf(par["name"]);

            if (tail == ".units")
            {
                if (newValue.Length == 0) { par.Remove("units"); return null; }
                par["units"] = newValue;
                return null;
            }
            if (tail != ".value") return $"Unknown field '{key}'.";

            // Preserve the original JSON kind so an edit never changes a field's
            // type out from under the schema (numbers stay numbers, text stays text).
            JsonValueKind kind = par["value"]?.GetValueKind() ?? JsonValueKind.String;
            switch (kind)
            {
                case JsonValueKind.Number:
                    if (!double.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                        return $"'{label}' holds a number (you typed '{newValue}'). Use digits and a period, e.g. 42.5";
                    par["value"] = num;
                    return null;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    if (!bool.TryParse(newValue, out bool b))
                        return $"'{label}' holds true or false (you typed '{newValue}').";
                    par["value"] = b;
                    return null;
                default:
                    if (newValue.Length == 0) return $"'{label}' cannot be empty.";
                    par["value"] = newValue;
                    return null;
            }
        }

        return $"Unknown field '{key}'.";
    }

    /// <summary>
    /// Validate, then write the corrected file — refusing to save an invalid
    /// document. The original text is kept as SourcePath + ".bak" (written once;
    /// later saves never overwrite the as-delivered extraction). Returns null on
    /// success, otherwise the reason the save did not happen.
    /// </summary>
    public string? TrySave()
    {
        if (_root == null) return LoadError ?? "File not loaded.";
        PredValidator.Result check = Validate();
        if (!check.IsValid)
            return "Not saved — the corrected spec still has problems:\n  " +
                   string.Join("\n  ", check.Errors.Take(6));
        try
        {
            string bak = SourcePath + ".bak";
            if (!File.Exists(bak)) File.WriteAllText(bak, _originalText);
            File.WriteAllText(SourcePath, ToJson() + Environment.NewLine);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not write the corrected file: {ex.Message}";
        }
    }

    /// <summary>"Depth", "Apex_Depth", "APEX_depth" all name the depth dimension.</summary>
    private static bool NamesDimension(string? paramName, string dim)
    {
        if (string.IsNullOrEmpty(paramName)) return false;
        string n = paramName!;
        if (n.StartsWith("Apex_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(5);
        return string.Equals(n, dim, StringComparison.OrdinalIgnoreCase);
    }

    private double? DimensionParameterConfidence(string dim)
    {
        if (_root?["parameters"] is not JsonArray pars) return null;
        foreach (JsonNode? p in pars)
        {
            if (p is not JsonObject o) continue;
            if (o["group"] is not JsonNode g || g.GetValueKind() != JsonValueKind.String
                || g.GetValue<string>() != "Dimensions") continue;
            if (!NamesDimension(StringOf(o["name"]), dim)) continue;
            if (o["confidence"] is JsonNode c && c.GetValueKind() == JsonValueKind.Number)
                return c.GetValue<double>();
        }
        return null;
    }

    /// <summary>
    /// Non-blocking cross-checks the validator cannot make (it has no field
    /// semantics): when the drawing carries BOTH an overall dimension and a
    /// same-named Dimensions parameter and their numbers disagree, say so —
    /// otherwise a modeler fixing one of the two "Depth"s ships the other one
    /// wrong. Returned as plain-language warnings for the review status box.
    /// </summary>
    public List<string> ConsistencyWarnings()
    {
        var warnings = new List<string>();
        if (_root == null) return warnings;
        if (_root["parameters"] is not JsonArray pars) return warnings;
        foreach (string dim in new[] { "width", "depth", "height" })
        {
            JsonNode? d = _root["geometry"]?[dim];
            JsonNode? g = d?["value"];
            if (g == null || g.GetValueKind() != JsonValueKind.Number) continue;
            string geomUnit = d?["unit"] is JsonNode gu && gu.GetValueKind() == JsonValueKind.String
                ? gu.GetValue<string>() : "in";
            double geomFeet = UnitConv.ToFeet(g.GetValue<double>(), geomUnit, "in");
            foreach (JsonNode? p in pars)
            {
                if (p is not JsonObject o || !NamesDimension(StringOf(o["name"]), dim)) continue;
                // Same scope as the confidence borrow: only Dimensions-group
                // parameters describe the box (an Electrical "Width" is a
                // different animal — V2 re-review finding 8).
                if (o["group"] is not JsonNode grp || grp.GetValueKind() != JsonValueKind.String
                    || grp.GetValue<string>() != "Dimensions") continue;
                JsonNode? v = o["value"];
                double paramVal;
                if (v == null) continue;
                if (v.GetValueKind() == JsonValueKind.Number) paramVal = v.GetValue<double>();
                else if (v.GetValueKind() == JsonValueKind.String
                    && double.TryParse(v.GetValue<string>(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double parsed)) paramVal = parsed;
                else continue;
                // Compare in feet so 610 mm and 24 in agree (unit-blind raw
                // comparison was V2 re-review finding 8). Length parameters
                // default to inches, matching the builder's convention.
                string paramUnit = o["units"] is JsonNode un && un.GetValueKind() == JsonValueKind.String
                    ? un.GetValue<string>() : "in";
                double paramFeet = UnitConv.ToFeet(paramVal, paramUnit, "in");
                if (Math.Abs(paramFeet - geomFeet) > 0.005)
                {
                    warnings.Add($"Overall {dim} is {StringOf(g)} {geomUnit} but the '{StringOf(o["name"])}' " +
                        $"parameter says {StringOf(v)} {paramUnit} — if you corrected one, correct the other to match.");
                }
            }
        }
        return warnings;
    }

    private static string StringOf(JsonNode? n)
    {
        if (n == null) return "";
        return n.GetValueKind() switch
        {
            JsonValueKind.String => n.GetValue<string>(),
            JsonValueKind.Number => n.GetValue<double>().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => n.ToJsonString(),
        };
    }
}
