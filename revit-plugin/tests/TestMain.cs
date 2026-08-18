using System;
using System.Collections.Generic;
using System.Linq;
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

        // ApexConfig round-trip (config.json wins over env for the API URL)
        var cfg = new ApexConfig { ApiUrl = "https://example.apexbim.test/api" };
        cfg.Save();
        try
        {
            var loaded = ApexConfig.Load();
            AssertTrue(loaded.ApiUrl == "https://example.apexbim.test/api", "config round-trips api_url");
            var client = new ApexApiClient(); // no arg -> reads config first
            AssertTrue(true, "client constructs from config URL");
        }
        finally
        {
            System.IO.File.Delete(ApexConfig.ConfigPath);
        }
        AssertTrue(ApexConfig.Load().ApiUrl == null, "missing config -> null api_url");

        // RefPlane axis/offset (parametric placement) + legacy fallback
        string planesJson = @"{""afis_version"":""1.0.0"",""id"":""f"",""identity"":{""name"":""X"",""category"":""C""},
          ""geometry"":{""bbox"":{""min"":[-0.25,-0.07,0],""max"":[0.25,0.07,1.1]},
            ""reference_planes"":[
              {""id"":""rp-left"",""name"":""Left"",""axis"":""x"",""offset"":-0.25},
              {""id"":""rp-legacy"",""name"":""Old"",""is_origin"":true}],
            ""solids"":[{""id"":""s1"",""method"":""extrusion"",""depth_param"":""Height""}],
            ""dimensions"":[{""id"":""dim-w"",""references"":[""rp-left"",""rp-right""],""label_param"":""Width"",""value"":0.5}]}}";
        var pdoc = JsonSerializer.Deserialize<AfisObject>(planesJson, opts)!;
        AssertTrue(pdoc.Geometry!.ReferencePlanes[0].Axis == "x", "ref plane axis binds");
        AssertEq(pdoc.Geometry.ReferencePlanes[0].Offset, -0.25, "ref plane offset binds");
        AssertTrue(pdoc.Geometry.ReferencePlanes[1].Axis == null, "legacy plane has null axis");
        AssertTrue(pdoc.Geometry.Solids[0].DepthParam == "Height", "solid depth_param binds");
        AssertTrue(pdoc.Geometry.Dimensions[0].LabelParam == "Width", "dimension label_param binds");

        // Token display helper never leaks the secret
        string desc = SecretText.Describe("apx_0123456789abcdef0123456789abcdef");
        AssertTrue(desc.StartsWith("apx_0123") && !desc.Contains("abcdef0123"), "token describe shows prefix only");
        AssertTrue(SecretText.Describe("") == "(empty)", "empty token describe");

        RunFamilySpecValidatorTests(opts);
        RunSchemaContractTest();
        RunFixtureTests();

        Console.WriteLine(_failures == 0 ? "\nALL TESTS PASSED" : $"\n{_failures} FAILURES");
        return _failures == 0 ? 0 : 1;
    }

    // ---------- FamilySpec v1: boundary validator behavior ----------

    static void RunFamilySpecValidatorTests(JsonSerializerOptions opts)
    {
        PredValidator.Result V(string json) =>
            PredValidator.Validate(JsonDocument.Parse(json).RootElement, "test.pred.json");

        string valid = @"{""schema_version"":""1.0"",""family_name"":""F"",""category"":""Electrical Equipment"",
          ""geometry"":{""primitive"":""box"",""width"":{""value"":20,""unit"":""in""},
            ""depth"":{""value"":5,""unit"":""in""},""height"":{""value"":26,""unit"":""in""}},
          ""parameters"":[{""name"":""Voltage"",""spec_type"":""Number"",""group"":""Electrical"",""is_instance"":false,""value"":208}]}";
        AssertTrue(V(valid).IsValid, "v1 valid doc passes");
        AssertTrue(V(valid).Warnings.Count == 0, "v1 valid doc has no warnings");

        var legacy = V(valid.Replace(@"""schema_version"":""1.0"",", ""));
        AssertTrue(legacy.IsValid && legacy.IsLegacyV0, "missing schema_version -> legacy v0 accepted");
        AssertTrue(legacy.Warnings.Any(w => w.Contains("schema_version missing")), "legacy v0 warns by name");

        var future = V(valid.Replace(@"""schema_version"":""1.0""", @"""schema_version"":""2.0"""));
        AssertTrue(!future.IsValid && future.Errors[0].Contains("schema_version"), "unknown version rejected, field named");

        var negDim = V(valid.Replace(@"""value"":20", @"""value"":-4"));
        AssertTrue(!negDim.IsValid, "negative dimension rejected");
        AssertTrue(negDim.Errors.Any(e => e.Contains("geometry.width.value") && e.Contains("-4")),
            "dimension error names field and value");

        var badSpec = V(valid.Replace(@"""spec_type"":""Number""", @"""spec_type"":""Nummber"""));
        AssertTrue(!badSpec.IsValid && badSpec.Errors.Any(e => e.Contains("spec_type") && e.Contains("Nummber")),
            "unknown spec_type rejected by name (no silent Text coercion)");

        var unknownField = V(valid.Replace(@"""family_name"":""F"",", @"""family_name"":""F"",""familly_notes"":""x"","));
        AssertTrue(!unknownField.IsValid && unknownField.Errors.Any(e => e.Contains("familly_notes")),
            "unknown v1 field rejected by name");

        var noUnit = V(valid.Replace(@"""width"":{""value"":20,""unit"":""in""}", @"""width"":{""value"":20}"));
        AssertTrue(!noUnit.IsValid && noUnit.Errors.Any(e => e.Contains("geometry.width.unit")),
            "v1 missing unit rejected by name");

        var arrayRoot = V("[1,2,3]");
        AssertTrue(!arrayRoot.IsValid && arrayRoot.Errors[0].Contains("root"), "non-object root rejected");

        // Adversarial-review follow-ups (round 2 V2 findings 3/4/6):
        var upperBox = V(valid.Replace(@"""primitive"":""box""", @"""primitive"":""BOX"""));
        AssertTrue(!upperBox.IsValid && upperBox.Errors.Any(e => e.Contains("primitive")),
            "primitive is case-sensitive per schema const (BOX rejected)");

        var s2r = V(valid.Replace(@"""family_name"":""F"",", @"""family_name"":""F"",""product_type"":""pad_mount_transformer"","));
        AssertTrue(!s2r.IsValid && s2r.Errors.Any(e => e.Contains("shop2revit")),
            "shop2revit-shaped doc named as wrong contract, not field soup");

        AssertTrue(AfisRevitMapper.SupportsAfisVersion("1.0.0"), "afis 1.x supported");
        AssertTrue(!AfisRevitMapper.SupportsAfisVersion("2.0.0"), "afis 2.x rejected at build boundary");
        AssertTrue(!AfisRevitMapper.SupportsAfisVersion(null), "missing afis_version rejected at build boundary");
        var noVer = JsonSerializer.Deserialize<AfisObject>(@"{""id"":""x"",""identity"":{""name"":""N"",""category"":""C""}}", opts)!;
        AssertTrue(noVer.AfisVersion == null, "absent afis_version stays null (no masking default)");
    }

    // ---------- FamilySpec v1: schema-of-record <-> C# contract ----------

    static string? FindRepoFile(string relative)
    {
        string? env = Environment.GetEnvironmentVariable("APEX_REPO_ROOT");
        foreach (string? start in new[] { env, AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start == null ? null : new System.IO.DirectoryInfo(start);
            while (dir != null)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, relative);
                if (System.IO.File.Exists(candidate) || System.IO.Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }

    static string[] JsonNames(Type t) => t.GetProperties()
        .Select(p => (p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
            .FirstOrDefault() as System.Text.Json.Serialization.JsonPropertyNameAttribute)?.Name)
        .Where(n => n != null).Select(n => n!).ToArray();

    static void AssertSetEq(IEnumerable<string> a, IEnumerable<string> b, string label)
    {
        var sa = a.OrderBy(x => x).ToArray();
        var sb = b.OrderBy(x => x).ToArray();
        bool ok = sa.SequenceEqual(sb);
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}" + (ok ? "" :
            $": only-in-first [{string.Join(", ", sa.Except(sb))}] only-in-second [{string.Join(", ", sb.Except(sa))}]"));
        if (!ok) _failures++;
    }

    static void RunSchemaContractTest()
    {
        string? schemaPath = FindRepoFile(System.IO.Path.Combine("schemas", "familyspec", "familyspec.v1.schema.json"));
        AssertTrue(schemaPath != null, "schema of record located (set APEX_REPO_ROOT if this fails)");
        if (schemaPath == null) return;

        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(schemaPath));
        JsonElement root = doc.RootElement;
        string[] Names(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();
        string[] Strings(JsonElement arr) => arr.EnumerateArray().Select(e => e.GetString()!).ToArray();
        JsonElement props = root.GetProperty("properties");

        // Root object: schema properties == C# DTO names == validator's table.
        AssertSetEq(Names(props), JsonNames(typeof(PredFamily)), "contract: root props == PredFamily DTO");
        AssertSetEq(Names(props), PredValidator.RootProperties, "contract: root props == validator table");
        AssertSetEq(Strings(root.GetProperty("required")), PredValidator.RootRequired, "contract: root required");

        JsonElement geom = props.GetProperty("geometry").GetProperty("properties");
        AssertSetEq(Names(geom), JsonNames(typeof(PredGeometry)).Concat(new[] { "primitive" }).Distinct(),
            "contract: geometry props == PredGeometry DTO");
        AssertSetEq(Names(geom), PredValidator.GeometryProperties, "contract: geometry props == validator table");

        JsonElement dim = root.GetProperty("definitions").GetProperty("dimension").GetProperty("properties");
        AssertSetEq(Names(dim), JsonNames(typeof(PredDim)), "contract: dimension props == PredDim DTO");
        AssertSetEq(Names(dim), PredValidator.DimensionProperties, "contract: dimension props == validator table");

        JsonElement par = props.GetProperty("parameters").GetProperty("items").GetProperty("properties");
        AssertSetEq(Names(par), JsonNames(typeof(PredParam)), "contract: parameter props == PredParam DTO");
        AssertSetEq(Names(par), PredValidator.ParameterProperties, "contract: parameter props == validator table");
        AssertSetEq(Strings(props.GetProperty("parameters").GetProperty("items").GetProperty("required")),
            PredValidator.ParameterRequired, "contract: parameter required");

        AssertSetEq(Strings(par.GetProperty("spec_type").GetProperty("enum")), PredValidator.SpecTypes,
            "contract: spec_type enum");
        AssertSetEq(Strings(par.GetProperty("group").GetProperty("enum")), PredValidator.Groups,
            "contract: group enum");
        AssertSetEq(Strings(dim.GetProperty("unit").GetProperty("enum")), PredValidator.Units,
            "contract: unit enum");
        AssertTrue(root.GetProperty("properties").GetProperty("schema_version").GetProperty("const").GetString()
            == PredValidator.Version, "contract: schema_version const");
    }

    // ---------- FamilySpec v1: fixtures ----------

    static void RunFixtureTests()
    {
        string? goldenDir = FindRepoFile(System.IO.Path.Combine("schemas", "familyspec", "fixtures", "golden"));
        string? malformedDir = FindRepoFile(System.IO.Path.Combine("schemas", "familyspec", "fixtures", "malformed"));
        AssertTrue(goldenDir != null && malformedDir != null, "fixture directories located");
        if (goldenDir == null || malformedDir == null) return;

        int goldens = 0;
        foreach (string f in System.IO.Directory.GetFiles(goldenDir, "*.pred.json"))
        {
            var r = PredValidator.Validate(
                JsonDocument.Parse(System.IO.File.ReadAllText(f)).RootElement, System.IO.Path.GetFileName(f));
            AssertTrue(r.IsValid, $"golden fixture valid: {System.IO.Path.GetFileName(f)}"
                + (r.IsValid ? "" : " :: " + string.Join(" | ", r.Errors)));
            goldens++;
        }
        AssertTrue(goldens >= 4, $"golden fixture count >= 4 (got {goldens})");

        int malformed = 0;
        foreach (string f in System.IO.Directory.GetFiles(malformedDir, "*.pred.json"))
        {
            string name = System.IO.Path.GetFileName(f);
            string expectPath = f + ".expected";
            var r = PredValidator.Validate(JsonDocument.Parse(System.IO.File.ReadAllText(f)).RootElement, name);
            AssertTrue(!r.IsValid, $"malformed fixture rejected: {name}");
            if (System.IO.File.Exists(expectPath))
            {
                string needle = System.IO.File.ReadAllText(expectPath).Trim();
                AssertTrue(r.Errors.Any(e => e.Contains(needle)),
                    $"malformed fixture error names field: {name} (expects substring '{needle}')"
                    + (r.Errors.Any(e => e.Contains(needle)) ? "" : " :: got: " + string.Join(" | ", r.Errors)));
            }
            malformed++;
        }
        AssertTrue(malformed >= 4, $"malformed fixture count >= 4 (got {malformed})");
    }
}
