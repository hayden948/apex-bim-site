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
    [JsonPropertyName("family_name")] public string? FamilyName { get; set; }
    [JsonPropertyName("family_template")] public string? FamilyTemplate { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("parameters")] public List<PredParam>? Parameters { get; set; }
    [JsonPropertyName("geometry")] public PredGeometry? Geometry { get; set; }
}

public class PredParam
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("spec_type")] public string? SpecType { get; set; }
    [JsonPropertyName("group")] public string? Group { get; set; }
    [JsonPropertyName("is_instance")] public bool IsInstance { get; set; }
    [JsonPropertyName("value")] public JsonElement ValueRaw { get; set; }
    [JsonPropertyName("units")] public string? Units { get; set; }

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
    private struct FlexResult
    {
        public bool Width;
        public bool Depth;
        public bool Height;
        public bool Centered;
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

        var dlg = new OpenFileDialog
        {
            Title = "Select a .pred.json extraction",
            Filter = "Prediction JSON (*.pred.json)|*.pred.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return Result.Cancelled;

        string inputPath = dlg.FileName;
        PredFamily pred;
        try
        {
            string text = File.ReadAllText(inputPath);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
            };
            pred = JsonSerializer.Deserialize<PredFamily>(text, opts)
                ?? throw new InvalidOperationException("Deserialized to null.");
        }
        catch (Exception ex)
        {
            ApexLog.Error("Could not read .pred.json.", ex);
            message = "Could not read .pred.json: " + ex.Message;
            TaskDialog.Show("Apex M1", message);
            return Result.Failed;
        }

        if (pred.Geometry == null || !string.Equals(pred.Geometry.Primitive, "box", StringComparison.OrdinalIgnoreCase))
        {
            message = "M1 supports geometry.primitive = \"box\" only.";
            TaskDialog.Show("Apex M1", message);
            return Result.Failed;
        }

        string? templatePath = ResolveTemplate(app, pred.FamilyTemplate);
        if (templatePath == null || !File.Exists(templatePath))
        {
            message = "Family template not found.\n\nSearched the Revit family template folder ("
                + (app.FamilyTemplatePath ?? "not configured")
                + ") for '" + (pred.FamilyTemplate ?? DefaultTemplateFileName)
                + "'.\n\nAdjust Revit's Family Template File location or the .pred.json family_template.";
            TaskDialog.Show("Apex M1", message);
            return Result.Failed;
        }

        Document? famDoc = null;
        try
        {
            famDoc = app.NewFamilyDocument(templatePath);
            if (famDoc == null)
            {
                message = "Revit returned a null family document from the template.";
                TaskDialog.Show("Apex M1", message);
                return Result.Failed;
            }

            int paramsAdded = 0;
            int paramsValued = 0;
            FlexResult flex;

            using (var tx = new Transaction(famDoc, "Apex M2: build family from .pred.json"))
            {
                tx.Start();
                try
                {
                    FamilyManager fm = famDoc.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType("Standard");
                    AddParameters(fm, pred.Parameters, ref paramsAdded, ref paramsValued);
                    flex = BuildParametricBox(famDoc, pred.Geometry, fm);
                    tx.Commit();
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            string outputPath = Path.Combine(
                Path.GetDirectoryName(inputPath) ?? ".",
                SafeFileName(pred.FamilyName, Path.GetFileNameWithoutExtension(inputPath)) + ".rfa");
            famDoc.SaveAs(outputPath, new SaveAsOptions { OverwriteExistingFile = true });
            ApexLog.Info("Generated family: " + outputPath);

            var sb = new StringBuilder()
                .AppendLine("Family generated.")
                .AppendLine()
                .AppendLine($"Output: {outputPath}")
                .AppendLine($"Size (in): {Fmt(pred.Geometry.Width)} W × {Fmt(pred.Geometry.Depth)} D × {Fmt(pred.Geometry.Height)} H")
                .AppendLine($"Parameters added: {paramsAdded} of {pred.Parameters?.Count ?? 0} (values set on {paramsValued})")
                .AppendLine($"Flexed: Width={YN(flex.Width)}  Depth={YN(flex.Depth)}  Height={YN(flex.Height)}  (centered: {YN(flex.Centered)})");
            TaskDialog.Show("Apex M2", sb.ToString());
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Family generation failed.", ex);
            message = "Family generation failed: " + ex.Message;
            TaskDialog.Show("Apex M1", message);
            return Result.Failed;
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
