using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Apex.BimStudio;

// AFIS (Apex Family Interchange Schema) data model.
// All geometry values are metric (meters) unless a "unit" field says otherwise.

public class AfisObject
{
    [JsonPropertyName("afis_version")] public string AfisVersion { get; set; } = "1.0.0";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("tier")] public string Tier { get; set; } = "type";
    [JsonPropertyName("identity")] public Identity Identity { get; set; } = new Identity();
    [JsonPropertyName("geometry")] public Geometry? Geometry { get; set; }
    [JsonPropertyName("parameters")] public List<Param> Parameters { get; set; } = new List<Param>();
    [JsonPropertyName("connectors")] public List<Connector> Connectors { get; set; } = new List<Connector>();
    [JsonPropertyName("zones")] public List<Zone> Zones { get; set; } = new List<Zone>();
    [JsonPropertyName("points")] public List<Point> Points { get; set; } = new List<Point>();
    [JsonPropertyName("placement")] public Transform? Placement { get; set; }
}

public class Identity
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("family_template")] public string? FamilyTemplate { get; set; }
    [JsonPropertyName("profile")] public string? Profile { get; set; }
}

public class Geometry
{
    [JsonPropertyName("origin")] public double[] Origin { get; set; } = new double[3];
    [JsonPropertyName("bbox")] public Bbox Bbox { get; set; } = new Bbox();
    [JsonPropertyName("reference_planes")] public List<RefPlane> ReferencePlanes { get; set; } = new List<RefPlane>();
    [JsonPropertyName("solids")] public List<Solid> Solids { get; set; } = new List<Solid>();
    [JsonPropertyName("dimensions")] public List<DimensionDef> Dimensions { get; set; } = new List<DimensionDef>();
    [JsonPropertyName("constraints")] public List<ConstraintDef> Constraints { get; set; } = new List<ConstraintDef>();
}

public class Bbox
{
    [JsonPropertyName("min")] public double[] Min { get; set; } = new double[3];
    [JsonPropertyName("max")] public double[] Max { get; set; } = new double[] { 1.0, 1.0, 1.0 };
}

public class RefPlane
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("is_origin")] public bool IsOrigin { get; set; }

    /// <summary>Plane normal axis: "x", "y" or "z". Null on legacy documents,
    /// where placement falls back to the old origin-based guess.</summary>
    [JsonPropertyName("axis")] public string? Axis { get; set; }

    /// <summary>Signed offset from the family origin along Axis, meters.</summary>
    [JsonPropertyName("offset")] public double Offset { get; set; }
}

public class Solid
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "extrusion";
    [JsonPropertyName("depth_param")] public string? DepthParam { get; set; }
}

public class DimensionDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("references")] public string[] References { get; set; } = System.Array.Empty<string>();
    [JsonPropertyName("label_param")] public string? LabelParam { get; set; }
    [JsonPropertyName("value")] public double Value { get; set; }
}

public class ConstraintDef
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("refs")] public string[]? Refs { get; set; }
    [JsonPropertyName("ref")] public string? Ref { get; set; }
}

public class Param
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("data_type")] public string DataType { get; set; } = "Text";
    [JsonPropertyName("binding")] public string Binding { get; set; } = "type";
    [JsonPropertyName("group")] public string Group { get; set; } = "PG_IDENTITY_DATA";
    [JsonPropertyName("is_shared")] public bool IsShared { get; set; }
    [JsonPropertyName("shared_guid")] public string? SharedGuid { get; set; }
    [JsonPropertyName("value")] public object? Value { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
}

public class Connector
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("system")] public string System { get; set; } = "duct";
    [JsonPropertyName("subtype")] public string? Subtype { get; set; }
    [JsonPropertyName("shape")] public string? Shape { get; set; }
    [JsonPropertyName("size")] public ConnSize? Size { get; set; }
    [JsonPropertyName("location")] public double[] Location { get; set; } = new double[3];
    [JsonPropertyName("direction")] public double[] Direction { get; set; } = new double[] { 0.0, 1.0, 0.0 };
    [JsonPropertyName("host_ref")] public string HostRef { get; set; } = "";
    [JsonPropertyName("flow_dir")] public string? FlowDir { get; set; }
}

public class ConnSize
{
    [JsonPropertyName("d")] public double? D { get; set; }
    [JsonPropertyName("w")] public double? W { get; set; }
    [JsonPropertyName("h")] public double? H { get; set; }
}

public class Zone
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "service_access";
    [JsonPropertyName("face")] public string? Face { get; set; }
    [JsonPropertyName("depth")] public double Depth { get; set; }
    [JsonPropertyName("is_blocking")] public bool IsBlocking { get; set; } = true;
}

public class Point
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "layout";
    [JsonPropertyName("local")] public double[] Local { get; set; } = new double[3];
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("point_code")] public string? PointCode { get; set; }
}

public class Transform
{
    [JsonPropertyName("origin")] public double[] Origin { get; set; } = new double[3];
    [JsonPropertyName("rotation")] public double[] Rotation { get; set; } = new double[3];
}
