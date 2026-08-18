using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace Apex.BimStudio;

/// <summary>
/// Maps an AFIS object into an open Revit family document: geometry, parameters,
/// layout points, clearance zones, and the AFIS id stamp.
/// </summary>
public static class AfisRevitMapper
{
    /// <summary>
    /// AFIS versions this add-in can build. Every change to AFIS is breaking
    /// (Sprint 002 policy); unknown or missing versions are rejected here —
    /// the single choke point every build path goes through — with a named
    /// message instead of a half-built family.
    /// </summary>
    public static bool SupportsAfisVersion(string? version)
        => version != null && version.StartsWith("1.", StringComparison.Ordinal);

    public static void Apply(Document famDoc, AfisObject obj)
    {
        if (!famDoc.IsFamilyDocument)
            throw new InvalidOperationException("AfisRevitMapper.Apply requires a family document (Family Editor).");
        if (!SupportsAfisVersion(obj.AfisVersion))
            throw new InvalidOperationException(
                $"AFIS document version '{obj.AfisVersion ?? "(missing)"}' is not supported by this add-in " +
                "(expected 1.x). Update the add-in, or re-approve the family with a compatible server.");

        using var tg = new TransactionGroup(famDoc, "Apex: Build family from AFIS");
        tg.Start();
        using (var tx = new Transaction(famDoc, "Apex: Geometry + parameters"))
        {
            tx.Start();
            try
            {
                var builder = new GeometryBuilder(famDoc);
                Extrusion? solid = null;
                if (obj.Geometry != null)
                {
                    builder.CreateReferencePlanes(obj.Geometry);
                    solid = builder.BuildPrimarySolid(obj.Geometry, famDoc.FamilyManager);
                    builder.ConstrainAndDimension(solid, obj.Geometry, famDoc.FamilyManager);
                    builder.CreateConnectors(obj.Connectors, solid, famDoc.FamilyManager);
                }
                WriteParameters(famDoc, obj);
                WriteLayoutPoints(famDoc, obj);
                WriteClearanceZones(famDoc, obj);
                StampAfisId(famDoc, obj);
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                tg.RollBack();
                throw;
            }
        }
        tg.Assimilate();
    }

    private static void WriteParameters(Document doc, AfisObject obj)
    {
        FamilyManager fm = doc.FamilyManager;
        DefinitionFile defFile = doc.Application.OpenSharedParameterFile();

        foreach (Param p in obj.Parameters)
        {
            FamilyParameter fp = fm.get_Parameter(p.Name);
            if (fp == null && p.IsShared && defFile != null)
            {
                ExternalDefinition? ext = defFile.Groups
                    .SelectMany(g => g.Definitions.Cast<ExternalDefinition>())
                    .FirstOrDefault(d => d.GUID.ToString().Equals(p.SharedGuid, StringComparison.OrdinalIgnoreCase));
                if (ext != null)
                    fp = fm.AddParameter(ext, MapGroup(p.Group), p.Binding == "instance");
            }
            if (fp == null && !p.IsShared)
            {
                // Extraction-derived engineering values arrive as plain (non-shared)
                // parameters that don't exist in the template; without this they
                // would all be dropped silently.
                try
                {
                    fp = fm.AddParameter(p.Name, MapGroup(p.Group), MapSpec(p.DataType), p.Binding == "instance");
                }
                catch (Exception ex)
                {
                    ApexLog.Warn($"Could not create AFIS parameter '{p.Name}': " + ex.Message);
                }
            }
            if (fp != null)
                SetValue(fm, fp, p);
        }
    }

    private static ForgeTypeId MapSpec(string dataType) => dataType switch
    {
        "Length" => SpecTypeId.Length,
        "Number" => SpecTypeId.Number,
        "Integer" => SpecTypeId.Int.Integer,
        "YesNo" => SpecTypeId.Boolean.YesNo,
        _ => SpecTypeId.String.Text,
    };

    private static void SetValue(FamilyManager fm, FamilyParameter fp, Param p)
    {
        if (p.Value == null) return;
        string raw = p.Value.ToString() ?? "";
        try
        {
            switch (p.DataType)
            {
                case "Length":
                    // AFIS lengths default to meters; honor an explicit unit when present.
                    fm.Set(fp, UnitConv.ToFeet(
                        Convert.ToDouble(raw, CultureInfo.InvariantCulture), p.Unit, defaultUnit: "m"));
                    break;
                case "Number":
                case "Integer":
                    fm.Set(fp, Convert.ToDouble(raw, CultureInfo.InvariantCulture));
                    break;
                case "YesNo":
                    fm.Set(fp, raw == "true" || raw == "1" ? 1 : 0);
                    break;
                default:
                    fm.Set(fp, raw);
                    break;
            }
        }
        catch (Exception ex)
        {
            ApexLog.Warn($"Could not set AFIS parameter '{p.Name}': " + ex.Message);
        }
    }

    private static void WriteLayoutPoints(Document doc, AfisObject obj)
    {
        EnsureSubcategory(doc, "Apex_Layout");
        foreach (Point pt in obj.Points)
        {
            var xyz = new XYZ(
                GeometryBuilder.M(pt.Local[0]),
                GeometryBuilder.M(pt.Local[1]),
                GeometryBuilder.M(pt.Local[2]));
            try
            {
                SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, xyz));
            }
            catch (Exception ex)
            {
                ApexLog.Warn($"Layout point '{pt.Id}' failed: " + ex.Message);
            }
        }
    }

    private static void WriteClearanceZones(Document doc, AfisObject obj)
    {
        Category? sub = EnsureSubcategory(doc, "Apex_Clearance");
        if (obj.Geometry == null) return;

        foreach (Zone z in obj.Zones)
        {
            try
            {
                CurveArray? loop = FaceOffsetRectangle(obj.Geometry, z);
                if (loop == null) continue;

                SketchPlane sketch = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                var arr = new CurveArrArray();
                arr.Append(loop);
                double height = GeometryBuilder.M(obj.Geometry.Bbox.Max[2] - obj.Geometry.Bbox.Min[2]);
                Extrusion solid = doc.FamilyCreate.NewExtrusion(true, arr, sketch, height);
                if (sub != null) solid.Subcategory = sub;
            }
            catch (Exception ex)
            {
                ApexLog.Warn($"Clearance zone '{z.Id}' failed: " + ex.Message);
            }
        }
    }

    private static Category? EnsureSubcategory(Document doc, string name)
    {
        Category? parent = doc.OwnerFamily?.FamilyCategory
            ?? doc.Settings.Categories.Cast<Category>()
#if REVIT_2024_OR_GREATER
                .FirstOrDefault(c => c.Id.Value == (long)BuiltInCategory.OST_GenericModel);
#else
                .FirstOrDefault(c => c.Id.IntegerValue == (int)BuiltInCategory.OST_GenericModel);
#endif
        if (parent == null) return null;

        foreach (Category sc in parent.SubCategories)
            if (sc.Name == name)
                return sc;

        try
        {
            return doc.Settings.Categories.NewSubcategory(parent, name);
        }
        catch (Exception ex)
        {
            ApexLog.Warn($"Could not create subcategory '{name}': " + ex.Message);
            return null;
        }
    }

    private static CurveArray? FaceOffsetRectangle(Geometry geom, Zone z)
    {
        double[] min = geom.Bbox.Min;
        double[] max = geom.Bbox.Max;
        double x0 = GeometryBuilder.M(min[0]);
        double y0 = GeometryBuilder.M(min[1]);
        double x1 = GeometryBuilder.M(max[0]);
        double y1 = GeometryBuilder.M(max[1]);
        double d = GeometryBuilder.M(z.Depth);

        switch (z.Face)
        {
            case "front":
                y1 = y0;
                y0 -= d;
                break;
            case "back":
                y0 = y1;
                y1 += d;
                break;
            case "left":
                x1 = x0;
                x0 -= d;
                break;
            case "right":
                x0 = x1;
                x1 += d;
                break;
            default:
                return null;
        }

        var p0 = new XYZ(x0, y0, 0.0);
        var p1 = new XYZ(x1, y0, 0.0);
        var p2 = new XYZ(x1, y1, 0.0);
        var p3 = new XYZ(x0, y1, 0.0);
        var arr = new CurveArray();
        arr.Append(Line.CreateBound(p0, p1));
        arr.Append(Line.CreateBound(p1, p2));
        arr.Append(Line.CreateBound(p2, p3));
        arr.Append(Line.CreateBound(p3, p0));
        return arr;
    }

    private static void StampAfisId(Document doc, AfisObject obj)
    {
        FamilyManager fm = doc.FamilyManager;
        FamilyParameter p = fm.get_Parameter("Apex_AfisId");
        if (p != null) fm.Set(p, obj.Id);
    }

    private static ForgeTypeId MapGroup(string group) => group switch
    {
        "PG_GEOMETRY" => GroupTypeId.Geometry,
        "PG_ELECTRICAL" => GroupTypeId.Electrical,
        "PG_STRUCTURAL" => GroupTypeId.Structural,
        "PG_MECHANICAL" => GroupTypeId.Mechanical,
        _ => GroupTypeId.IdentityData,
    };
}
