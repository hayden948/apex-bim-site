using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace Apex.BimStudio;

/// <summary>
/// Builds parametric family geometry (reference planes, primary solid, dimensions,
/// constraints, MEP connectors) from an AFIS geometry description. AFIS lengths are
/// meters; Revit internal units are feet.
/// </summary>
public class GeometryBuilder
{
    private readonly Document _doc;
    private readonly Dictionary<string, ReferencePlane> _planes = new Dictionary<string, ReferencePlane>();

    public GeometryBuilder(Document famDoc)
    {
        _doc = famDoc;
    }

    /// <summary>Meters to Revit internal feet.</summary>
    public static double M(double meters) => meters * UnitConv.MetersToFeet;

    public void CreateReferencePlanes(Geometry geom)
    {
        View view = GetPlanView();
        foreach (RefPlane rp in geom.ReferencePlanes)
        {
            XYZ o = ToXyz(rp);
            XYZ normal = NormalOf(rp);
            XYZ bubble, free, cut;
            if (Math.Abs(normal.X) > 0.5)
            {
                bubble = new XYZ(o.X, o.Y - 1.0, 0.0);
                free = new XYZ(o.X, o.Y + 1.0, 0.0);
                cut = XYZ.BasisZ;
            }
            else if (Math.Abs(normal.Y) > 0.5)
            {
                bubble = new XYZ(o.X - 1.0, o.Y, 0.0);
                free = new XYZ(o.X + 1.0, o.Y, 0.0);
                cut = XYZ.BasisZ;
            }
            else
            {
                bubble = new XYZ(o.X - 1.0, 0.0, o.Z);
                free = new XYZ(o.X + 1.0, 0.0, o.Z);
                cut = XYZ.BasisY;
            }

            ReferencePlane plane = _doc.FamilyCreate.NewReferencePlane(bubble, free, cut, view);
            plane.Name = SafeName(rp.Name);
            _planes[rp.Id] = plane;
        }
    }

    public Extrusion BuildPrimarySolid(Geometry geom, FamilyManager fm)
    {
        double[] min = geom.Bbox.Min;
        double[] max = geom.Bbox.Max;
        double w = max[0] - min[0];
        double d = max[1] - min[1];
        double h = max[2] - min[2];

        SketchPlane baseSketch = SketchPlane.Create(_doc,
            Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0.0, 0.0, M(min[2]))));
        CurveArray rect = RectangleLoop(M(min[0]), M(min[1]), M(max[0]), M(max[1]));
        var loops = new CurveArrArray();
        loops.Append(rect);
        Extrusion solid = _doc.FamilyCreate.NewExtrusion(true, loops, baseSketch, M(h));

        // Drive the extrusion's top with the declared height parameter so Height
        // flexes vertically (plan-view dimensions can only drive Width/Depth).
        // Ensure the declared name, not a hard-coded "Height", so an AFIS with
        // depth_param "Overall Height" still gets a driven extrusion.
        string heightParam = geom.Solids.FirstOrDefault()?.DepthParam ?? "Height";
        EnsureLengthParam(fm, heightParam, h);
        EnsureLengthParam(fm, "Width", w);
        EnsureLengthParam(fm, "Depth", d);

        try
        {
            FamilyParameter? hp = fm.get_Parameter(heightParam);
            Parameter end = solid.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            if (hp != null && end != null)
                fm.AssociateElementParameterToFamilyParameter(end, hp);
        }
        catch (Exception ex)
        {
            ApexLog.Warn($"Could not associate extrusion height with '{heightParam}': " + ex.Message);
        }
        return solid;
    }

    public void ConstrainAndDimension(Extrusion solid, Geometry geom, FamilyManager fm)
    {
        View view = GetPlanView();

        foreach (PlanarFace face in HostFaces(solid))
        {
            ReferencePlane? plane = PlaneMatchingFace(face);
            if (plane == null) continue;
            try
            {
                Reference faceRef = face.Reference;
                Reference planeRef = plane.GetReference();
                if (faceRef != null && planeRef != null)
                {
                    Dimension align = _doc.FamilyCreate.NewAlignment(view, planeRef, faceRef);
                    if (align != null) align.IsLocked = true;
                }
            }
            catch (Exception ex)
            {
                ApexLog.Warn("Alignment failed for a face: " + ex.Message);
            }
        }

        foreach (DimensionDef dim in geom.Dimensions)
        {
            ReferencePlane? refA = ResolvePlane(dim.References is { Length: > 0 } ? dim.References[0] : null);
            ReferencePlane? refB = ResolvePlane(dim.References is { Length: > 1 } ? dim.References[1] : null);
            if (refA == null || refB == null) continue;

            Dimension? d = CreateDimensionBetween(view, refA, refB);
            if (d == null) continue;

            if (!string.IsNullOrEmpty(dim.LabelParam))
            {
                // Create the label parameter if the AFIS names one the template
                // (or BuildPrimarySolid) didn't already provide.
                FamilyParameter? fp = fm.get_Parameter(dim.LabelParam)
                    ?? EnsureLengthParam(fm, dim.LabelParam!, dim.Value);
                if (fp != null) d.FamilyLabel = fp;
            }
            else
            {
                try
                {
                    d.IsLocked = true;
                }
                catch (Exception ex)
                {
                    ApexLog.Warn($"Could not lock dimension '{dim.Id}': " + ex.Message);
                }
            }
        }

        foreach (ConstraintDef con in geom.Constraints ?? new List<ConstraintDef>())
        {
            if (con.Type != "equality" || con.Refs == null || con.Refs.Length < 3) continue;

            var refs = new ReferenceArray();
            foreach (string rid in con.Refs)
            {
                ReferencePlane? rp = ResolvePlane(rid);
                if (rp?.GetReference() != null) refs.Append(rp.GetReference());
            }
            if (refs.Size < 3) continue;

            try
            {
                Dimension eq = _doc.FamilyCreate.NewDimension(view, EqualityLineFor(refs), refs);
                if (eq != null) eq.AreSegmentsEqual = true;
            }
            catch (Exception ex)
            {
                ApexLog.Warn("Equality constraint failed: " + ex.Message);
            }
        }
    }

    public void CreateConnectors(IEnumerable<Connector> connectors, Extrusion host, FamilyManager fm)
    {
        List<PlanarFace> faces = HostFaces(host);
        foreach (Connector c in connectors)
        {
            var origin = new XYZ(M(c.Location[0]), M(c.Location[1]), M(c.Location[2]));
            var dir = new XYZ(c.Direction[0], c.Direction[1], c.Direction[2]);
            PlanarFace? face = FaceByNormal(faces, dir) ?? NearestFace(faces, origin);
            Reference? faceRef = face?.Reference;
            if (faceRef == null) continue;

            try
            {
                ConnectorElement? ce = c.System switch
                {
                    "duct" => ConnectorElement.CreateDuctConnector(_doc, DuctSystemType.SupplyAir, ConnectorProfileType.Rectangular, faceRef),
                    "pipe" => ConnectorElement.CreatePipeConnector(_doc, PipeSystemType.SupplyHydronic, faceRef),
                    "electrical" => ConnectorElement.CreateElectricalConnector(_doc, ElectricalSystemType.PowerBalanced, faceRef),
                    _ => null,
                };
                if (ce != null) BindConnectorSize(ce, c, fm);
            }
            catch (Exception ex)
            {
                ApexLog.Warn($"Connector '{c.Id}' creation failed: " + ex.Message);
            }
        }
    }

    private void BindConnectorSize(ConnectorElement ce, Connector c, FamilyManager fm)
    {
        try
        {
            if (c.Shape == "round" && c.Size?.D is double d)
            {
                FamilyParameter? p = EnsureConnLengthParam(fm, "Conn_" + c.Id + "_D", d);
                Parameter cp = ce.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER);
                if (cp != null && p != null) fm.AssociateElementParameterToFamilyParameter(cp, p);
                return;
            }

            if (c.Size?.W is double w && c.Size?.H is double h)
            {
                FamilyParameter? pw = EnsureConnLengthParam(fm, "Conn_" + c.Id + "_W", w);
                FamilyParameter? ph = EnsureConnLengthParam(fm, "Conn_" + c.Id + "_H", h);
                Parameter cw = ce.get_Parameter(BuiltInParameter.CONNECTOR_WIDTH);
                Parameter chp = ce.get_Parameter(BuiltInParameter.CONNECTOR_HEIGHT);
                if (cw != null && pw != null) fm.AssociateElementParameterToFamilyParameter(cw, pw);
                if (chp != null && ph != null) fm.AssociateElementParameterToFamilyParameter(chp, ph);
            }
        }
        catch (Exception ex)
        {
            ApexLog.Warn($"Connector '{c.Id}' size binding failed: " + ex.Message);
        }
    }

    private static CurveArray RectangleLoop(double x0, double y0, double x1, double y1)
    {
        const double z = 0.0;
        var p0 = new XYZ(x0, y0, z);
        var p1 = new XYZ(x1, y0, z);
        var p2 = new XYZ(x1, y1, z);
        var p3 = new XYZ(x0, y1, z);
        var arr = new CurveArray();
        arr.Append(Line.CreateBound(p0, p1));
        arr.Append(Line.CreateBound(p1, p2));
        arr.Append(Line.CreateBound(p2, p3));
        arr.Append(Line.CreateBound(p3, p0));
        return arr;
    }

    private FamilyParameter? EnsureLengthParam(FamilyManager fm, string name, double meters)
    {
        try
        {
            FamilyParameter p = fm.get_Parameter(name)
                ?? fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
            fm.Set(p, M(meters));
            return p;
        }
        catch (Exception ex)
        {
            ApexLog.Warn($"Could not set parameter '{name}': " + ex.Message);
            return fm.get_Parameter(name);
        }
    }

    private ReferencePlane? ResolvePlane(string? id)
        => id != null && _planes.TryGetValue(id, out ReferencePlane? p) ? p : null;

    private Dimension? CreateDimensionBetween(View view, ReferencePlane a, ReferencePlane b)
    {
        Reference ra = a.GetReference();
        Reference rb = b.GetReference();
        if (ra == null || rb == null) return null;
        var refs = new ReferenceArray();
        refs.Append(ra);
        refs.Append(rb);
        Line line = Line.CreateBound(a.FreeEnd, b.FreeEnd);
        try
        {
            return _doc.FamilyCreate.NewDimension(view, line, refs);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The plane a face should lock to: same orientation AND coincident. Matching
    /// on normal alone would lock e.g. the right face to the Left plane, gluing the
    /// box to the wrong side and making Width flex move the whole solid.
    /// </summary>
    private ReferencePlane? PlaneMatchingFace(PlanarFace face)
    {
        ReferencePlane? best = null;
        double bestDist = 0.01; // must effectively lie on the plane (internal feet)
        foreach (ReferencePlane rp in _planes.Values)
        {
            XYZ n = rp.Normal.Normalize();
            if (Math.Abs(n.DotProduct(face.FaceNormal.Normalize())) < 0.7) continue;
            double dist = Math.Abs(n.DotProduct(face.Origin - rp.BubbleEnd));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = rp;
            }
        }
        return best;
    }

    /// <summary>Dimension line for an equality constraint, running across the
    /// planes (i.e. along the first plane's normal), not always along X.</summary>
    private Line EqualityLineFor(ReferenceArray refs)
    {
        XYZ dir = XYZ.BasisX;
        foreach (ReferencePlane rp in _planes.Values)
        {
            if (rp.GetReference()?.ElementId == refs.get_Item(0)?.ElementId)
            {
                dir = rp.Normal.Normalize();
                break;
            }
        }
        return Line.CreateBound(-dir, dir);
    }

    private FamilyParameter? EnsureConnLengthParam(FamilyManager fm, string name, double meters)
    {
        FamilyParameter p = fm.get_Parameter(name)
            ?? fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
        try
        {
            fm.Set(p, M(meters));
        }
        catch (Exception ex)
        {
            ApexLog.Warn($"Could not set parameter '{name}': " + ex.Message);
        }
        return p;
    }

    private static PlanarFace? FaceByNormal(List<PlanarFace> faces, XYZ dir)
    {
        if (dir.IsZeroLength()) return null;
        XYZ d = dir.Normalize();
        return faces.OrderByDescending(f => f.FaceNormal.Normalize().DotProduct(d)).FirstOrDefault();
    }

    private List<PlanarFace> HostFaces(Extrusion solid)
    {
        var opt = new Options { ComputeReferences = true };
        var faces = new List<PlanarFace>();
        foreach (GeometryObject g in solid.get_Geometry(opt))
        {
            if (g is not Autodesk.Revit.DB.Solid s) continue;
            foreach (Face face in s.Faces)
                if (face is PlanarFace pf)
                    faces.Add(pf);
        }
        return faces;
    }

    private static PlanarFace? NearestFace(List<PlanarFace> faces, XYZ p)
        => faces.OrderBy(f => f.Origin.DistanceTo(p)).FirstOrDefault();

    private View GetPlanView()
    {
        var views = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>();
        return views.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan && !v.IsTemplate)
            ?? new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().First(v => !v.IsTemplate);
    }

    private static XYZ ToXyz(RefPlane rp) => NormalOf(rp) * M(rp.Offset);

    private static XYZ NormalOf(RefPlane rp) => rp.Axis switch
    {
        "x" => XYZ.BasisX,
        "y" => XYZ.BasisY,
        "z" => XYZ.BasisZ,
        // Legacy documents without axis: keep the old origin-based guess.
        _ => rp.IsOrigin ? XYZ.BasisX : XYZ.BasisY,
    };

    private static string SafeName(string n)
        => string.IsNullOrWhiteSpace(n) ? Guid.NewGuid().ToString("N").Substring(0, 6) : n;
}
