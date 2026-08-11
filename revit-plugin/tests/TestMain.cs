using System;
using System.Text.Json;
using Apex.BimStudio;
using Apex.BimStudio.Commands;

class TestMain
{
    static int _failures;

    static void AssertEq(double actual, double expected, string label)
    {
        bool ok = Math.Abs(actual - expected) < 1e-9;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}: got {actual}, want {expected}");
        if (!ok) _failures++;
    }

    static void AssertTrue(bool cond, string label)
    {
        Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {label}");
        if (!cond) _failures++;
    }

    static int Main()
    {
        // UnitConv: declared units
        AssertEq(UnitConv.ToFeet(24, "in", "in"), 2.0, "24 in = 2 ft");
        AssertEq(UnitConv.ToFeet(1, "m", "in"), 3.280839895, "1 m -> ft");
        AssertEq(UnitConv.ToFeet(1000, "mm", "in"), 3.280839895, "1000 mm -> ft");
        AssertEq(UnitConv.ToFeet(100, "cm", "in"), 3.280839895, "100 cm -> ft");
        AssertEq(UnitConv.ToFeet(5, "ft", "in"), 5.0, "5 ft = 5 ft");
        // default-unit fallback (old behavior preserved)
        AssertEq(UnitConv.ToFeet(24, null, "in"), 2.0, "24 (no unit, default in) = 2 ft");
        AssertEq(UnitConv.ToFeet(2, "", "m"), 2 * 3.280839895, "2 (no unit, default m)");
        // unknown unit falls back to default instead of crashing
        AssertEq(UnitConv.ToFeet(12, "furlongs", "in"), 1.0, "unknown unit -> default in");

        // PredParam.Value extraction across JSON kinds
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
        var p1 = JsonSerializer.Deserialize<PredParam>("{\"name\":\"A\",\"value\":\"12.5\"}", opts)!;
        AssertTrue(p1.Value == "12.5", "string value -> \"12.5\"");
        var p2 = JsonSerializer.Deserialize<PredParam>("{\"name\":\"B\",\"value\":42}", opts)!;
        AssertTrue(p2.Value == "42", "number value -> \"42\"");
        var p3 = JsonSerializer.Deserialize<PredParam>("{\"name\":\"C\",\"value\":true}", opts)!;
        AssertTrue(p3.Value == "true", "true -> \"true\"");
        var p4 = JsonSerializer.Deserialize<PredParam>("{\"name\":\"D\",\"value\":null}", opts)!;
        AssertTrue(p4.Value == null, "null -> null");
        var p5 = JsonSerializer.Deserialize<PredParam>("{\"name\":\"E\"}", opts)!;
        AssertTrue(p5.Value == null, "missing -> null");

        // PredFamily round-trip with snake_case names
        string predJson = @"{
          ""family_name"": ""Panelboard-A"",
          ""family_template"": ""Electrical Equipment.rft"",
          ""category"": ""Electrical Equipment"",
          ""geometry"": { ""primitive"": ""box"",
            ""width"": {""value"": 20, ""unit"": ""in""},
            ""depth"": {""value"": 5.75, ""unit"": ""in""},
            ""height"": {""value"": 26, ""unit"": ""in""} },
          ""parameters"": [
            {""name"": ""Apex_Width"", ""spec_type"": ""Length"", ""group"": ""Dimensions"", ""is_instance"": false, ""value"": 20, ""units"": ""in""},
            {""name"": ""Voltage"", ""spec_type"": ""Number"", ""group"": ""Electrical"", ""value"": ""208""}
          ]
        }";
        var fam = JsonSerializer.Deserialize<PredFamily>(predJson, opts)!;
        AssertTrue(fam.FamilyName == "Panelboard-A", "family_name binds");
        AssertTrue(fam.Geometry?.Primitive == "box", "geometry.primitive binds");
        AssertEq(fam.Geometry!.Width!.Value, 20, "width.value binds");
        AssertTrue(fam.Geometry.Width.Unit == "in", "width.unit binds");
        AssertTrue(fam.Parameters!.Count == 2, "parameters bind");
        AssertTrue(fam.Parameters[0].Units == "in", "param units binds");

        // AFIS model round-trip
        string afisJson = @"{
          ""afis_version"": ""1.0.0"", ""id"": ""fam-123"", ""tier"": ""type"",
          ""identity"": {""name"": ""AHU-1"", ""category"": ""Mechanical Equipment"", ""family_template"": ""t.rft""},
          ""geometry"": {""bbox"": {""min"": [0,0,0], ""max"": [1.2, 0.8, 2.0]},
            ""reference_planes"": [{""id"": ""rp1"", ""name"": ""Left"", ""is_origin"": true}],
            ""constraints"": [{""type"": ""equality"", ""refs"": [""a"",""b"",""c""]}]},
          ""connectors"": [{""id"": ""c1"", ""system"": ""duct"", ""shape"": ""round"", ""size"": {""d"": 0.3}, ""location"": [0,0,1], ""direction"": [0,0,1]}],
          ""zones"": [{""id"": ""z1"", ""type"": ""service_access"", ""face"": ""front"", ""depth"": 0.9}],
          ""points"": [{""id"": ""p1"", ""type"": ""anchor"", ""local"": [0.6, 0.4, 0], ""point_code"": ""AHU1-A""}]
        }";
        var afis = JsonSerializer.Deserialize<AfisObject>(afisJson, opts)!;
        AssertTrue(afis.Id == "fam-123", "afis id binds");
        AssertTrue(afis.Identity.Name == "AHU-1", "identity.name binds");
        AssertEq(afis.Geometry!.Bbox.Max[2], 2.0, "bbox.max binds");
        AssertTrue(afis.Geometry.ReferencePlanes[0].IsOrigin, "is_origin binds");
        AssertTrue(afis.Connectors[0].Size!.D == 0.3, "connector size binds");
        AssertTrue(afis.Zones[0].Face == "front", "zone face binds");
        AssertTrue(afis.Points[0].PointCode == "AHU1-A", "point_code binds");
        AssertTrue(new Bbox().Max[0] == 1.0, "Bbox default max {1,1,1} preserved");

        // ApexApiClient URL validation
        try { var _ = new ApexApiClient("http://api.example.com"); Console.WriteLine("FAIL  non-localhost http accepted"); _failures++; }
        catch (ArgumentException) { Console.WriteLine("PASS  non-localhost http rejected"); }
        try { var _ = new ApexApiClient("http://localhost:4000"); Console.WriteLine("PASS  localhost http accepted"); }
        catch (Exception e) { Console.WriteLine("FAIL  localhost http rejected: " + e.Message); _failures++; }
        try { var _ = new ApexApiClient("https://api.apexbim.example"); Console.WriteLine("PASS  https accepted"); }
        catch (Exception e) { Console.WriteLine("FAIL  https rejected: " + e.Message); _failures++; }


        // FamilySummary/FamilyList binding (Sync command payload)
        string listJson = @"{""families"":[{""id"":""a11ce000-0000-4000-8000-000000000001"",""family_name"":""Panelboard 208V 42ckt"",""category"":""Electrical Equipment"",""status"":""ready"",""revit_version"":""2025""}]}";
        var flist = JsonSerializer.Deserialize<FamilyList>(listJson, opts)!;
        AssertTrue(flist.Families.Count == 1, "family list binds");
        AssertTrue(flist.Families[0].FamilyName == "Panelboard 208V 42ckt", "family_name binds in summary");
        AssertTrue(flist.Families[0].RevitVersion == "2025", "revit_version binds in summary");

        // JobSummary/JobList binding (Process Queue worker payload)
        string jobsJson = @"{""jobs"":[{""id"":""749cc9c8-2a1f-4231-88fc-06987fc85a9d"",""kind"":""generate_rfa"",""entity_id"":""44d3d55d-dd6c-4011-acee-e23e47a394c4"",""status"":""queued"",""attempt"":1,""created_at"":""2026-08-11T21:18:35Z""}]}";
        var jlist = JsonSerializer.Deserialize<JobList>(jobsJson, opts)!;
        AssertTrue(jlist.Jobs.Count == 1, "job list binds");
        AssertTrue(jlist.Jobs[0].Kind == "generate_rfa", "job kind binds");
        AssertTrue(jlist.Jobs[0].EntityId == "44d3d55d-dd6c-4011-acee-e23e47a394c4", "job entity_id binds");
        AssertTrue(jlist.Jobs[0].Attempt == 1, "job attempt binds");
        var jnone = JsonSerializer.Deserialize<JobList>(@"{""jobs"":[]}", opts)!;
        AssertTrue(jnone.Jobs.Count == 0, "empty job list binds");

        // QaResult binding (cloud validate payload)
        string qaJson = @"{""object_id"":""44d3d55d-dd6c-4011-acee-e23e47a394c4"",""family_name"":""Panelboard"",""passed"":false,""score"":0.86,""summary"":{""errors"":1,""warnings"":2,""info"":0},""findings"":[{""rule"":""Z-1"",""severity"":""error"",""category"":""spatial"",""passed"":false,""message"":""missing NEC zone"",""fix_hint"":""Add electrical_nec zone""}]}";
        var qa = JsonSerializer.Deserialize<QaResult>(qaJson, opts)!;
        AssertTrue(qa.Passed == false, "qa passed binds");
        AssertEq(qa.Score, 0.86, "qa score binds");
        AssertTrue(qa.Summary.Errors == 1 && qa.Summary.Warnings == 2, "qa summary binds");
        AssertTrue(qa.Findings.Count == 1 && qa.Findings[0].Rule == "Z-1", "qa finding rule binds");
        AssertTrue(qa.Findings[0].FixHint == "Add electrical_nec zone", "qa fix_hint binds");

        Console.WriteLine(_failures == 0 ? "\nALL TESTS PASSED" : $"\n{_failures} FAILURES");
        return _failures == 0 ? 0 : 1;
    }
}
