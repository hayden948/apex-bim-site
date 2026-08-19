using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace Apex.BimStudio.Commands;

public class PredFamily
{
    [JsonPropertyName("schema_version")] public string? SchemaVersion { get; set; }
    [JsonPropertyName("family_name")] public string? FamilyName { get; set; }
    [JsonPropertyName("family_template")] public string? FamilyTemplate { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("parameters")] public List<PredParam>? Parameters { get; set; }
    [JsonPropertyName("geometry")] public PredGeometry? Geometry { get; set; }
    [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
}

public class PredParam
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("spec_type")] public string? SpecType { get; set; }
    [JsonPropertyName("group")] public string? Group { get; set; }
    [JsonPropertyName("is_instance")] public bool IsInstance { get; set; }
    [JsonPropertyName("value")] public JsonElement ValueRaw { get; set; }
    [JsonPropertyName("units")] public string? Units { get; set; }
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }

    [JsonIgnore]
    public string? Value => ValueRaw.ValueKind switch
    {
        JsonValueKind.String => ValueRaw.GetString(),
        JsonValueKind.Number => ValueRaw.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        _ => ValueRaw.GetRawText(),
    };
}

public class PredGeometry
{
    [JsonPropertyName("primitive")] public string? Primitive { get; set; }
    [JsonPropertyName("width")] public PredDim? Width { get; set; }
    [JsonPropertyName("height")] public PredDim? Height { get; set; }
    [JsonPropertyName("depth")] public PredDim? Depth { get; set; }
}

public class PredDim
{
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
}

/// <summary>
/// M1/M2: builds a parametric box family (.rfa) from a local .pred.json extraction —
/// labeled Width/Depth/Height dimensions, face locks, centering, and family parameters.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class BuildFromPredJsonCommand : IExternalCommand
{
    internal struct FlexResult
    {
        public bool Width;
        public bool Depth;
        public bool Height;
        public bool Centered;
    }

    /// <summary>Result of one family build (shared by the single-file and batch commands).</summary>
    internal struct BuildOutcome
    {
        public int ParamsAdded;
        public int ParamsValued;
        public int ParamsTotal;
        public FlexResult Flex;
        public string OutputPath;
    }

    private enum SpecKind
    {
        Text,
        Length,
        Integer,
        Number,
    }

    // .pred.json lengths default to inches (submittal drawings are imperial).
    private const string DefaultLengthUnit = "in";
    private const string DefaultTemplateFileName = "Electrical Equipment.rft";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Application app = commandData.Application.Application;

        ApexLicense.Status lic = ApexLicense.CheckDefault();
        if (lic.State != ApexLicense.State.Valid)
        {
            message = lic.Message;
            TaskDialog.Show("Apex — license", lic.Message);
            return Result.Failed;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Select an extracted equipment spec to build",
            Filter = "Extracted equipment spec (*.pred.json)|*.pred.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return Result.Cancelled;

        string inputPath = dlg.FileName;
        string fileLabel = Path.GetFileName(inputPath);
        using ApexLog.RunScope run = ApexLog.BeginRun("build-" +
            (fileLabel.Length > 30 ? fileLabel.Substring(0, 30) : fileLabel));
        PredFamily pred;
        PredValidator.Result check;
        try
        {
            string text = File.ReadAllText(inputPath);
            using JsonDocument doc = JsonDocument.Parse(text);

            // FamilySpec v1 boundary validation BEFORE deserialization and any
            // Revit call: invalid input gets a named-field message, not a
            // stack trace or a silent default (schemas/familyspec/DECISION.md).
            check = PredValidator.Validate(doc.RootElement, fileLabel);
            if (!check.IsValid)
            {
                string detail = string.Join("\n", check.Errors.Take(12))
                    + (check.Errors.Count > 12 ? $"\n…and {check.Errors.Count - 12} more." : "");
                ApexLog.Warn($"FamilySpec validation failed for {fileLabel}:\n{detail}");
                message = $"This spec has problems that must be fixed before building:\n\n{detail}\n\n" +
                    "Open it with Review Submittal to correct the named fields, then build again.";
                TaskDialog.Show("Apex — this spec has problems", message);
                return Result.Failed;
            }
            foreach (string w in check.Warnings) ApexLog.Warn(w);

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
            };
            pred = JsonSerializer.Deserialize<PredFamily>(text, opts)
                ?? throw new InvalidOperationException("Deserialized to null.");
        }
        catch (JsonException ex)
        {
            ApexLog.Error("Could not parse .pred.json.", ex);
            message = $"{fileLabel} could not be read as an equipment spec: {ex.Message}\n\n" +
                "Re-download it from the Apex portal; if it fails again, send the run log to support.";
            TaskDialog.Show("Apex — cannot read this file", message);
            return Result.Failed;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Could not read .pred.json.", ex);
            message = "Could not read the spec file: " + ex.Message;
            TaskDialog.Show("Apex — Build Family", message);
            return Result.Failed;
        }

        if (pred.Geometry == null)
        {
            // Unreachable after validation; keeps the null-flow explicit.
            message = $"{fileLabel}: geometry is required.";
            TaskDialog.Show("Apex — this spec has problems", message);
            return Result.Failed;
        }

        string? templatePath = ResolveTemplate(app, pred.FamilyTemplate);
        if (templatePath == null || !File.Exists(templatePath))
        {
            message = "Family template not found — this is a machine setting, not a drawing problem." +
                "\n\nSearched the Revit family template folder ("
                + (app.FamilyTemplatePath ?? "not configured")
                + ") for '" + (pred.FamilyTemplate ?? DefaultTemplateFileName)
                + "'.\n\nSet Revit's Family Template File location (Options → File Locations), then build again.";
            TaskDialog.Show("Apex — cannot build on this machine", message);
            return Result.Failed;
        }

        try
        {
            string outputPath = Path.Combine(
                Path.GetDirectoryName(inputPath) ?? ".",
                SafeFileName(pred.FamilyName, Path.GetFileNameWithoutExtension(inputPath)) + ".rfa");

            // Round 4: replacing an existing family file is confirmable.
            if (File.Exists(outputPath))
            {
                var confirm = new TaskDialog("Apex — Build Family")
                {
                    MainInstruction = "Replace the existing family file?",
                    MainContent = $"{outputPath}\n\nalready exists and will be replaced by this build.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;
            }

            BuildOutcome outcome = BuildToFile(app, pred, templatePath, outputPath);
            ApexLog.Info("Generated family: " + outcome.OutputPath);

            var sb = new StringBuilder()
                .AppendLine("Family built.")
                .AppendLine()
                .AppendLine($"Output: {outcome.OutputPath}")
                .AppendLine($"Size (in): {Fmt(pred.Geometry.Width)} W × {Fmt(pred.Geometry.Depth)} D × {Fmt(pred.Geometry.Height)} H")
                .AppendLine($"Parameters added: {outcome.ParamsAdded} of {outcome.ParamsTotal} (values set on {outcome.ParamsValued})")
                .AppendLine($"Geometry checks: width resize {YN(outcome.Flex.Width)}, depth resize {YN(outcome.Flex.Depth)}, " +
                    $"height resize {YN(outcome.Flex.Height)}, centered {YN(outcome.Flex.Centered)}")
                .AppendLine()
                .AppendLine($"Run log: {run.Path ?? "(daily Apex log)"}");
            TaskDialog.Show("Apex — family built", sb.ToString());
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Family generation failed.", ex);
            message = "The build failed: " + ex.Message +
                $"\n\nNo new family file was written. Run log: {run.Path ?? "(daily Apex log)"}";
            TaskDialog.Show("Apex — build failed", message);
            return Result.Failed;
        }
    }

    /// <summary>
    /// Build one family from a validated FamilySpec and save it to outputPath.
    /// Shared by the single-file command and BatchBuildCommand. Throws on any
    /// failure AFTER deleting a partially written .rfa — a caller never finds
    /// a corrupt file that looks finished (round-3 failure containment).
    /// </summary>
    internal static BuildOutcome BuildToFile(Application app, PredFamily pred, string templatePath, string outputPath)
    {
        Document? famDoc = null;
        bool attemptedSave = false;
        try
        {
            famDoc = app.NewFamilyDocument(templatePath)
                ?? throw new InvalidOperationException("Revit returned a null family document from the template.");

            int paramsAdded = 0;
            int paramsValued = 0;
            FlexResult flex;

            using (var tx = new Transaction(famDoc, "Apex: build family from .pred.json"))
            {
                tx.Start();
                try
                {
                    FamilyManager fm = famDoc.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType("Standard");
                    AddParameters(fm, pred.Parameters, ref paramsAdded, ref paramsValued);
                    // The parametric-box promise must not depend on the spec's
                    // parameter list: the labeled dimensions below need family
                    // parameters named Apex_Width/Apex_Depth/Apex_Height, and
                    // most extractions don't carry them (round-4 adversarial
                    // finding: 5 of 6 golden fixtures would have failed every
                    // flex check). Create the missing ones from the geometry.
                    EnsureDimensionParameters(fm, pred.Geometry!);
                    flex = BuildParametricBox(famDoc, pred.Geometry!, fm);
                    tx.Commit();
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            attemptedSave = true;
            famDoc.SaveAs(outputPath, new SaveAsOptions { OverwriteExistingFile = true });
            return new BuildOutcome
            {
                ParamsAdded = paramsAdded,
                ParamsValued = paramsValued,
                ParamsTotal = pred.Parameters?.Count ?? 0,
                Flex = flex,
                OutputPath = outputPath,
            };
        }
        catch
        {
            // Containment: never leave a PARTIAL .rfa behind — but only touch
            // the file if this build actually attempted to write it. Deleting
            // on earlier failures would destroy a pre-existing family this
            // build never produced (round-4 adversarial finding 3).
            if (attemptedSave)
            {
                try
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                }
                catch (Exception cleanupEx)
                {
                    ApexLog.Warn("Could not remove partial output: " + cleanupEx.Message);
                }
            }
            throw;
        }
        finally
        {
            try
            {
                famDoc?.Close(false);
            }
            catch
            {
                // Closing an already-closed doc throws; nothing to do.
            }
        }
    }

    /// <summary>
    /// Create (and value from the box geometry) the three dimension parameters
    /// the labeled dimensions bind to, when the spec didn't supply them. A
    /// parameter the SPEC already carries is left untouched — the extraction's
    /// value must never be silently overwritten by geometry (V2 re-review
    /// finding 5); a disagreement between the two is surfaced by the
    /// consistency check, not resolved here.
    /// </summary>
    private static void EnsureDimensionParameters(FamilyManager fm, PredGeometry geom)
    {
        void Ensure(string name, PredDim? dim)
        {
            try
            {
                FamilyParameter existing = fm.get_Parameter(name);
                if (existing != null)
                {
                    ApexLog.Info($"Dimension parameter '{name}' supplied by the spec — keeping its value.");
                    return;
                }
                FamilyParameter fp = fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
                double feet = DimToFeet(dim);
                if (fp != null && feet > 0.0) fm.Set(fp, feet);
            }
            catch (Exception ex)
            {
                // A failed dimension parameter degrades to an unflexed check,
                // reported honestly in the build checks — never a build abort.
                ApexLog.Warn($"Dimension parameter '{name}' could not be ensured: " + ex.Message);
            }
        }
        Ensure("Apex_Width", geom.Width);
        Ensure("Apex_Depth", geom.Depth);
        Ensure("Apex_Height", geom.Height);
    }

    private static FlexResult BuildParametricBox(Document doc, PredGeometry geom, FamilyManager fm)
    {
        double w = DimToFeet(geom.Width);
        double d = DimToFeet(geom.Depth);
        double h = DimToFeet(geom.Height);
        if (w <= 0.0 || d <= 0.0 || h <= 0.0)
            throw new InvalidOperationException(
                $"Box dimensions must be positive (got W={w:F3} D={d:F3} H={h:F3} ft).");

        double hx = w / 2.0;
        double hy = d / 2.0;
        var loop = new CurveArray();
        loop.Append(Line.CreateBound(new XYZ(-hx, -hy, 0.0), new XYZ(hx, -hy, 0.0)));
        loop.Append(Line.CreateBound(new XYZ(hx, -hy, 0.0), new XYZ(hx, hy, 0.0)));
        loop.Append(Line.CreateBound(new XYZ(hx, hy, 0.0), new XYZ(-hx, hy, 0.0)));
        loop.Append(Line.CreateBound(new XYZ(-hx, hy, 0.0), new XYZ(-hx, -hy, 0.0)));
        var profile = new CurveArrArray();
        profile.Append(loop);

        SketchPlane sketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
        Extrusion extrusion = doc.FamilyCreate.NewExtrusion(true, profile, sketchPlane, h);
        doc.Regenerate();

        var result = default(FlexResult);
        View? view = GetFamilyPlanView(doc);

        try
        {
            FamilyParameter hParam = fm.get_Parameter("Apex_Height");
            Parameter endParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            if (hParam != null && endParam != null)
            {
                fm.AssociateElementParameterToFamilyParameter(endParam, hParam);
                result.Height = true;
            }
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Height association failed: " + ex.Message);
        }

        if (view != null)
        {
            try
            {
                ReferencePlane rpLeft = NewSidePlane(doc, view, normalToX: true, -hx, "Apex Left");
                ReferencePlane rpRight = NewSidePlane(doc, view, normalToX: true, hx, "Apex Right");
                ReferencePlane rpFront = NewSidePlane(doc, view, normalToX: false, -hy, "Apex Front");
                ReferencePlane rpBack = NewSidePlane(doc, view, normalToX: false, hy, "Apex Back");
                doc.Regenerate();

                LockFacesToPlanes(doc, view, extrusion, rpLeft, rpRight, rpFront, rpBack);
                doc.Regenerate();

                result.Width = LabelSpan(doc, view, rpLeft, rpRight, axis: 0, fm, "Apex_Width");
                result.Depth = LabelSpan(doc, view, rpFront, rpBack, axis: 1, fm, "Apex_Depth");

                ReferencePlane? centerLR = FindCenterPlane(doc, centerLR: true);
                ReferencePlane? centerFB = FindCenterPlane(doc, centerLR: false);
                bool eqW = centerLR != null && Equalize(doc, view, rpLeft, centerLR, rpRight, axis: 0);
                bool eqD = centerFB != null && Equalize(doc, view, rpFront, centerFB, rpBack, axis: 1);
                result.Centered = eqW && eqD;
                doc.Regenerate();
            }
            catch (Exception ex)
            {
                ApexLog.Warn("Constraint pass failed: " + ex.Message);
            }
        }

        return result;
    }

    private static ReferencePlane NewSidePlane(Document doc, View view, bool normalToX, double offset, string name)
    {
        XYZ bubble, free;
        if (normalToX)
        {
            bubble = new XYZ(offset, -1.0, 0.0);
            free = new XYZ(offset, 1.0, 0.0);
        }
        else
        {
            bubble = new XYZ(-1.0, offset, 0.0);
            free = new XYZ(1.0, offset, 0.0);
        }
        ReferencePlane rp = doc.FamilyCreate.NewReferencePlane(bubble, free, XYZ.BasisZ, view);
        try
        {
            rp.Name = name;
        }
        catch (Exception ex)
        {
            ApexLog.Warn($"Could not name reference plane '{name}': " + ex.Message);
        }
        return rp;
    }

    private static void LockFacesToPlanes(Document doc, View view, Extrusion extrusion,
        ReferencePlane left, ReferencePlane right, ReferencePlane front, ReferencePlane back)
    {
        foreach (PlanarFace face in HostVerticalFaces(extrusion))
        {
            XYZ n = face.FaceNormal.Normalize();
            ReferencePlane? rp =
                n.X > 0.7 ? right :
                n.X < -0.7 ? left :
                n.Y > 0.7 ? back :
                n.Y < -0.7 ? front : null;
            if (rp == null) continue;

            try
            {
                Reference planeRef = rp.GetReference();
                Reference faceRef = face.Reference;
                if (planeRef != null && faceRef != null)
                {
                    Dimension align = doc.FamilyCreate.NewAlignment(view, planeRef, faceRef);
                    if (align != null) align.IsLocked = true;
                }
            }
            catch (Exception ex)
            {
                ApexLog.Warn("Face lock failed: " + ex.Message);
            }
        }
    }

    private static bool LabelSpan(Document doc, View view, ReferencePlane a, ReferencePlane b,
        int axis, FamilyManager fm, string paramName)
    {
        try
        {
            var refs = new ReferenceArray();
            Reference ra = a.GetReference();
            Reference rb = b.GetReference();
            if (ra == null || rb == null) return false;
            refs.Append(ra);
            refs.Append(rb);

            Line line = axis == 0
                ? Line.CreateBound(new XYZ(-10.0, 2.0, 0.0), new XYZ(10.0, 2.0, 0.0))
                : Line.CreateBound(new XYZ(2.0, -10.0, 0.0), new XYZ(2.0, 10.0, 0.0));
            Dimension dim = doc.FamilyCreate.NewDimension(view, line, refs);
            FamilyParameter p = fm.get_Parameter(paramName);
            if (dim != null && p != null)
            {
                dim.FamilyLabel = p;
                return true;
            }
        }
        catch (Exception ex)
        {
            ApexLog.Warn($"Labeling span '{paramName}' failed: " + ex.Message);
        }
        return false;
    }

    private static bool Equalize(Document doc, View view, ReferencePlane a, ReferencePlane mid,
        ReferencePlane b, int axis)
    {
        try
        {
            var refs = new ReferenceArray();
            Reference ra = a.GetReference();
            Reference rm = mid.GetReference();
            Reference rb = b.GetReference();
            if (ra == null || rm == null || rb == null) return false;
            refs.Append(ra);
            refs.Append(rm);
            refs.Append(rb);

            Line line = axis == 0
                ? Line.CreateBound(new XYZ(-10.0, 3.0, 0.0), new XYZ(10.0, 3.0, 0.0))
                : Line.CreateBound(new XYZ(3.0, -10.0, 0.0), new XYZ(3.0, 10.0, 0.0));
            Dimension dim = doc.FamilyCreate.NewDimension(view, line, refs);
            if (dim != null)
            {
                dim.AreSegmentsEqual = true;
                return true;
            }
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Equalize failed: " + ex.Message);
        }
        return false;
    }

    private static ReferencePlane? FindCenterPlane(Document doc, bool centerLR)
    {
        ReferencePlane? best = null;
        double bestDist = 0.015626; // ~3/16" tolerance from the family origin
        foreach (ReferencePlane rp in new FilteredElementCollector(doc)
                     .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
        {
            XYZ n = rp.Normal.Normalize();
            bool matchesAxis = centerLR ? Math.Abs(n.X) > 0.7 : Math.Abs(n.Y) > 0.7;
            if (!matchesAxis) continue;

            double dist = Math.Abs(centerLR ? rp.BubbleEnd.X : rp.BubbleEnd.Y);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = rp;
            }
        }
        return best;
    }

    private static List<PlanarFace> HostVerticalFaces(Extrusion extrusion)
    {
        var opt = new Options { ComputeReferences = true };
        var faces = new List<PlanarFace>();
        foreach (GeometryObject g in extrusion.get_Geometry(opt))
        {
            if (g is not Autodesk.Revit.DB.Solid s) continue;
            foreach (Face face in s.Faces)
                if (face is PlanarFace pf && Math.Abs(pf.FaceNormal.Z) < 0.5)
                    faces.Add(pf);
        }
        return faces;
    }

    private static View? GetFamilyPlanView(Document doc)
    {
        var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().ToList();
        return plans.FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
            ?? (View?)plans.FirstOrDefault(v => !v.IsTemplate);
    }

    private static string YN(bool b) => b ? "yes" : "no";

    private static void AddParameters(FamilyManager fm, List<PredParam>? parameters, ref int added, ref int valued)
    {
        if (parameters == null) return;
        foreach (PredParam p in parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            try
            {
                FamilyParameter fp = fm.get_Parameter(p.Name);
                if (fp == null)
                {
                    ForgeTypeId groupId = MapGroup(p.Group);
                    ForgeTypeId specId = MapSpec(p.SpecType);
                    fp = fm.AddParameter(p.Name, groupId, specId, p.IsInstance);
                    added++;
                }
                if (fp != null && SetValue(fm, fp, p)) valued++;
            }
            catch (Exception ex)
            {
                ApexLog.Warn($"Parameter '{p.Name}' failed: " + ex.Message);
            }
        }
    }

    private static bool SetValue(FamilyManager fm, FamilyParameter fp, PredParam p)
    {
        if (p.Value == null) return false;
        string raw = p.Value;
        try
        {
            switch (Classify(p.SpecType))
            {
                case SpecKind.Length:
                    if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double length))
                    {
                        // Honor the declared units field; default is inches for .pred.json.
                        fm.Set(fp, UnitConv.ToFeet(length, p.Units, DefaultLengthUnit));
                        return true;
                    }
                    return false;
                case SpecKind.Integer:
                    if (int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out int i))
                    {
                        fm.Set(fp, i);
                        return true;
                    }
                    return false;
                case SpecKind.Number:
                    if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double n))
                    {
                        fm.Set(fp, n);
                        return true;
                    }
                    return false;
                default:
                    fm.Set(fp, raw);
                    return true;
            }
        }
        catch (Exception ex)
        {
            ApexLog.Warn($"Setting value for '{p.Name}' failed: " + ex.Message);
            return false;
        }
    }

    private static SpecKind Classify(string? specType) => (specType ?? "").Trim() switch
    {
        "Length" => SpecKind.Length,
        "Integer" => SpecKind.Integer,
        "Number" => SpecKind.Number,
        _ => SpecKind.Text,
    };

    private static ForgeTypeId MapSpec(string? specType) => Classify(specType) switch
    {
        SpecKind.Length => SpecTypeId.Length,
        SpecKind.Integer => SpecTypeId.Int.Integer,
        SpecKind.Number => SpecTypeId.Number,
        _ => SpecTypeId.String.Text,
    };

    private static ForgeTypeId MapGroup(string? group) => (group ?? "").Trim() switch
    {
        "Dimensions" => GroupTypeId.Geometry,
        "Electrical" => GroupTypeId.Electrical,
        "Electrical - Loads" => GroupTypeId.Electrical,
        "Constraints" => GroupTypeId.Constraints,
        _ => GroupTypeId.IdentityData,
    };

    private static double DimToFeet(PredDim? dim)
        => dim == null ? 0.0 : UnitConv.ToFeet(dim.Value, dim.Unit, DefaultLengthUnit);

    /// <summary>
    /// Resolve the family template against the Revit installation actually running,
    /// instead of a hardcoded RVT-2025 English-Imperial path. Order:
    /// 1. an absolute path from the .pred.json, if it exists;
    /// 2. the file name searched (recursively) under Revit's configured family template folder;
    /// 3. the default Electrical Equipment template in that folder.
    /// Returns null when nothing is found.
    /// </summary>
    internal static string? ResolveTemplate(Application app, string? familyTemplate)
    {
        if (!string.IsNullOrWhiteSpace(familyTemplate)
            && Path.IsPathRooted(familyTemplate)
            && File.Exists(familyTemplate))
        {
            return familyTemplate;
        }

        string? templateDir = app.FamilyTemplatePath;
        if (string.IsNullOrWhiteSpace(templateDir) || !Directory.Exists(templateDir))
        {
            ApexLog.Warn("Revit family template path is not configured or missing: " + templateDir);
            return null;
        }

        string fileName = string.IsNullOrWhiteSpace(familyTemplate)
            ? DefaultTemplateFileName
            : Path.GetFileName(familyTemplate);

        string direct = Path.Combine(templateDir, fileName);
        if (File.Exists(direct)) return direct;

        try
        {
            string? found = Directory
                .EnumerateFiles(templateDir, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found != null) return found;

            if (fileName != DefaultTemplateFileName)
                return Directory
                    .EnumerateFiles(templateDir, DefaultTemplateFileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Template search failed: " + ex.Message);
        }
        return null;
    }

    internal static string SafeFileName(string? preferred, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred!;
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static string Fmt(PredDim? dim)
        => dim?.Value.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
}
