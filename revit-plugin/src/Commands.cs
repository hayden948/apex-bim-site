using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Apex.BimStudio.Commands;

internal static class Session
{
    public static readonly ApexApiClient Api = new ApexApiClient();

    /// <summary>Set when a family is selected/synced from the Apex library. Never defaulted.</summary>
    public static string? ActiveFamilyId;

    /// <summary>
    /// Commands that need an active family must fail with a clear message instead of
    /// silently using a placeholder id (the old build fell back to a hardcoded demo GUID).
    /// </summary>
    public static string RequireActiveFamilyId()
    {
        return ActiveFamilyId
            ?? throw new InvalidOperationException(
                "No active Apex family selected. Use Library/Sync to choose a family first.");
    }
}

[Transaction(TransactionMode.Manual)]
public class SignInCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            if (!OAuthPkce.IsConfigured)
            {
                TaskDialog.Show("Apex Sign In",
                    "Sign-in is not configured on this machine.\n\n" +
                    "Set APEX_AUTH_URL, APEX_TOKEN_URL and APEX_CLIENT_ID, then try again.");
                return Result.Cancelled;
            }

            string token = ApexApiClient.RunSync(_ => OAuthPkce.SignInAsync(), timeoutSeconds: 240);
            Session.Api.AccessToken = token;
            TaskDialog.Show("Apex", "Signed in to Apex. Token stored securely (Windows DPAPI).");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Sign-in failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public class SyncCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            List<FamilySummary> families = ApexApiClient.RunSync(ct => Session.Api.ListFamiliesAsync(ct));
            if (families.Count == 0)
            {
                TaskDialog.Show("Apex Sync", "The Apex library is empty — no families to sync yet.");
                return Result.Succeeded;
            }

            int pick = PickFamily(families);
            if (pick < 0) return Result.Cancelled;

            Session.ActiveFamilyId = families[pick].Id;
            ApexLog.Info($"Active Apex family set to {families[pick].Id} ({families[pick].FamilyName}).");
            TaskDialog.Show("Apex Sync",
                $"Active family: {families[pick].FamilyName}\n\n" +
                "Use Generate → From Library (in the Family Editor) to build it, " +
                "or Place / Run QA / Export Layout against it.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Library sync failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }

    /// <summary>
    /// TaskDialog offers four command links, so page in threes with the fourth
    /// link reserved for "More…" whenever families remain. Returns -1 on cancel.
    /// </summary>
    private static int PickFamily(List<FamilySummary> families)
    {
        const int PageSize = 3;
        for (int start = 0; ; start = (start + PageSize) % Math.Max(families.Count, 1))
        {
            var dlg = new TaskDialog("Apex Sync")
            {
                MainInstruction = $"Synced {families.Count} famil{(families.Count == 1 ? "y" : "ies")} from the Apex library.",
                MainContent = "Choose the family to make active (used by Place / QA / Export):",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            int shown = Math.Min(families.Count - start, PageSize);
            for (int i = 0; i < shown; i++)
            {
                FamilySummary f = families[start + i];
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1 + i,
                    f.FamilyName, $"{f.Category ?? "?"} · {f.Status ?? "?"} · Revit {f.RevitVersion ?? "?"}");
            }
            bool hasMore = families.Count > PageSize;
            if (hasMore)
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "More…",
                    $"Showing {start + 1}–{start + shown} of {families.Count}");

            TaskDialogResult result = dlg.Show();
            int link = result switch
            {
                TaskDialogResult.CommandLink1 => 0,
                TaskDialogResult.CommandLink2 => 1,
                TaskDialogResult.CommandLink3 => 2,
                TaskDialogResult.CommandLink4 => 3,
                _ => -1,
            };
            if (link < 0) return -1;
            if (link == 3 && hasMore) continue; // next page
            if (link < shown) return start + link;
            return -1;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public class GenerateFromSubmittalCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "Submittal-to-family generation is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class GenerateFromLibraryCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            UIDocument? uidoc = c.Application.ActiveUIDocument;
            Document? doc = uidoc?.Document;
            if (doc == null)
            {
                TaskDialog.Show("Apex", "Open a document first.");
                return Result.Cancelled;
            }

            string id = Session.RequireActiveFamilyId();
            AfisObject? obj = ApexApiClient.RunSync(ct => Session.Api.GetFamilyAsync(id, ct));
            if (obj == null)
            {
                m = "Family not found";
                return Result.Failed;
            }

            // Family Editor: build the AFIS object into the open family.
            if (doc.IsFamilyDocument)
            {
                AfisRevitMapper.Apply(doc, obj);
                TaskDialog.Show("Apex", "Built '" + obj.Identity.Name + "' from AFIS into the family.");
                return Result.Succeeded;
            }

            // Project: build in a background family document, load, and place (M2 flow).
            return BuildLoadAndPlace(c.Application.Application, uidoc!, doc, obj, ref m);
        }
        catch (Exception ex)
        {
            ApexLog.Error("Generate-from-library failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }

    private static Result BuildLoadAndPlace(Autodesk.Revit.ApplicationServices.Application app,
        UIDocument uidoc, Document project, AfisObject obj, ref string m)
    {
        string? templatePath = BuildFromPredJsonCommand.ResolveTemplate(app, obj.Identity.FamilyTemplate);
        if (templatePath == null)
        {
            m = "No family template found for '" + (obj.Identity.FamilyTemplate ?? "default") + "'. " +
                "Check Revit's Family Template File location.";
            return Result.Failed;
        }

        string libDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", "library");
        System.IO.Directory.CreateDirectory(libDir);
        string rfaPath = System.IO.Path.Combine(libDir,
            BuildFromPredJsonCommand.SafeFileName(obj.Identity.Name, obj.Id) + ".rfa");

        Document? famDoc = null;
        try
        {
            famDoc = app.NewFamilyDocument(templatePath);
            AfisRevitMapper.Apply(famDoc, obj);
            famDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
        }
        finally
        {
            try
            {
                famDoc?.Close(false);
            }
            catch
            {
                // Already closed; nothing to release.
            }
        }
        ApexLog.Info("Built library family to " + rfaPath);

        Family? family;
        using (var tx = new Transaction(project, "Apex: Load library family"))
        {
            tx.Start();
            if (!project.LoadFamily(rfaPath, out family) || family == null)
            {
                // Same name already loaded — find it and continue to placement.
                family = new FilteredElementCollector(project)
                    .OfClass(typeof(Family)).Cast<Family>()
                    .FirstOrDefault(f => f.Name == System.IO.Path.GetFileNameWithoutExtension(rfaPath));
            }
            tx.Commit();
        }
        if (family == null)
        {
            m = "Family was generated (" + rfaPath + ") but could not be loaded into the project.";
            return Result.Failed;
        }

        FamilySymbol? symbol = family.GetFamilySymbolIds()
            .Select(sid => project.GetElement(sid))
            .OfType<FamilySymbol>()
            .FirstOrDefault();
        if (symbol == null)
        {
            m = "Loaded family has no placeable types.";
            return Result.Failed;
        }

        if (!symbol.IsActive)
        {
            using var tx = new Transaction(project, "Apex: Activate family type");
            tx.Start();
            symbol.Activate();
            tx.Commit();
        }

        try
        {
            uidoc.PromptForFamilyInstancePlacement(symbol);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // User placed zero-or-more instances then pressed Esc — normal exit.
        }
        TaskDialog.Show("Apex",
            "Built and loaded '" + obj.Identity.Name + "' from the Apex library.\nSaved: " + rfaPath);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public class OpenLibraryCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "The Apex library panel is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class PlaceFamilyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            UIDocument? uidoc = c.Application.ActiveUIDocument;
            Document? doc = uidoc?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("Apex", "Open a project document to place a family.");
                return Result.Cancelled;
            }

            // Prefer the family stamped with the active Apex id; otherwise fall back to
            // the most recently loaded family symbol so the button is still useful.
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();
            if (symbols.Count == 0)
            {
                TaskDialog.Show("Apex", "No loadable family types found in this project. Load a family first.");
                return Result.Cancelled;
            }

            FamilySymbol? symbol = null;
            if (Session.ActiveFamilyId != null)
            {
                symbol = symbols.FirstOrDefault(s =>
                    s.LookupParameter("Apex_AfisId")?.AsString() == Session.ActiveFamilyId);
                if (symbol == null)
                {
                    TaskDialog.Show("Apex",
                        "The active Apex family is not loaded in this project yet. " +
                        "Placing the most recent family type instead.");
                }
            }
            symbol ??= symbols[symbols.Count - 1];

            if (!symbol.IsActive)
            {
                using var tx = new Transaction(doc, "Apex: Activate family type");
                tx.Start();
                symbol.Activate();
                tx.Commit();
            }

            // Hands control to Revit's normal placement flow (its own transaction).
            uidoc!.PromptForFamilyInstancePlacement(symbol);
            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Family placement failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public class RunQaCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            Document? doc = c.Application.ActiveUIDocument?.Document;

            // In the Family Editor: run the local QA checks against the open family.
            if (doc != null && doc.IsFamilyDocument)
                return RunLocalQa(doc);

            // Otherwise: cloud QA Engine against the active library family (Doc 8).
            string id = Session.RequireActiveFamilyId();
            QaResult? qa = ApexApiClient.RunSync(ct => Session.Api.ValidateParsedAsync(id, ct));
            if (qa == null)
            {
                m = "The QA Engine returned an unreadable response.";
                return Result.Failed;
            }

            var lines = new List<string>();
            foreach (QaFinding f in qa.Findings.Where(f => !f.Passed))
            {
                lines.Add($"{f.Rule} [{f.Severity.ToUpperInvariant()}]  {f.Message}");
                if (!string.IsNullOrEmpty(f.FixHint))
                    lines.Add($"        fix: {f.FixHint}");
            }
            string verdict = qa.Passed
                ? $"PASSED — score {qa.Score:0.##}."
                : $"FAILED — score {qa.Score:0.##}: {qa.Summary.Errors} error(s), " +
                  $"{qa.Summary.Warnings} warning(s). Export is gated (Doc 8 §6).";
            TaskDialog.Show($"Apex QA (Doc 8) — {qa.FamilyName ?? id}",
                verdict + (lines.Count > 0 ? "\n\n" + string.Join("\n", lines)
                    : "\n\nAll checks passed."));
            return qa.Passed ? Result.Succeeded : Result.Failed;
        }
        catch (Exception ex)
        {
            ApexLog.Error("QA validation failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }

    /// <summary>
    /// Local slice of the Doc 8 pipeline for the open family document:
    /// P-rules (required Apex parameters), G-rules (geometry present, flex test).
    /// The flex test runs inside a transaction that is always rolled back.
    /// </summary>
    private static Result RunLocalQa(Document doc)
    {
        var findings = new List<string>();
        int errors = 0;
        void Check(string rule, bool passed, string okMsg, string failMsg, bool isError = true)
        {
            findings.Add($"{(passed ? "PASS" : isError ? "ERROR" : "WARN")}  {rule}: {(passed ? okMsg : failMsg)}");
            if (!passed && isError) errors++;
        }

        FamilyManager fm = doc.FamilyManager;

        // P — profile completeness
        foreach (string p in new[] { "Apex_Width", "Apex_Depth", "Apex_Height" })
            Check($"P-dim ({p})", fm.get_Parameter(p) != null,
                "parameter present", "required dimension parameter missing");
        Check("P-3 (Apex_AfisId)", fm.get_Parameter("Apex_AfisId") != null,
            "stamp parameter present", "families should carry an Apex_AfisId stamp", isError: false);

        // G — geometry present
        var solids = new FilteredElementCollector(doc)
            .OfClass(typeof(Extrusion)).Cast<Extrusion>().ToList();
        Check("G-solid", solids.Count > 0,
            $"{solids.Count} extrusion(s) found", "family has no solid geometry");

        // G-flex — drive Apex_Width +10% inside a rolled-back transaction and
        // verify the geometry actually moves (the M2 acceptance check).
        FamilyParameter widthParam = fm.get_Parameter("Apex_Width");
        if (widthParam != null && solids.Count > 0 && fm.CurrentType != null)
        {
            bool flexed = false;
            using (var tx = new Transaction(doc, "Apex QA: flex test (rolled back)"))
            {
                tx.Start();
                try
                {
                    double? before = fm.CurrentType.AsDouble(widthParam);
                    if (before is double w0 && w0 > 0)
                    {
                        BoundingBoxXYZ? bb0 = solids[0].get_BoundingBox(null);
                        fm.Set(widthParam, w0 * 1.1);
                        doc.Regenerate();
                        BoundingBoxXYZ? bb1 = solids[0].get_BoundingBox(null);
                        if (bb0 != null && bb1 != null)
                            flexed = Math.Abs((bb1.Max.X - bb1.Min.X) - (bb0.Max.X - bb0.Min.X)) > 1e-6;
                    }
                }
                catch (Exception ex)
                {
                    ApexLog.Warn("Flex test failed to run: " + ex.Message);
                }
                finally
                {
                    tx.RollBack();
                }
            }
            Check("G-flex (Apex_Width)", flexed,
                "geometry follows the width parameter", "changing Apex_Width did not move geometry");
        }

        string verdict = errors == 0
            ? "PASSED — no blocking errors."
            : $"FAILED — {errors} blocking error(s). Export would be gated (Doc 8 §6).";
        TaskDialog.Show("Apex QA (Doc 8) — local checks",
            verdict + "\n\n" + string.Join("\n", findings));
        return errors == 0 ? Result.Succeeded : Result.Failed;
    }
}

[Transaction(TransactionMode.Manual)]
public class VerifyClearancesCommand : IExternalCommand
{
    private const string ClearanceSubcategory = "Apex_Clearance";

    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            Document? doc = c.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("Apex", "Open a project document to verify clearances.");
                return Result.Cancelled;
            }

            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .ToList();

            int zonesChecked = 0;
            var clashes = new List<string>();
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine };

            foreach (FamilyInstance inst in instances)
            {
                foreach (Autodesk.Revit.DB.Solid zone in ClearanceSolids(doc, inst, opt))
                {
                    zonesChecked++;
                    var filter = new ElementIntersectsSolidFilter(zone);
                    var hits = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .WhereElementIsViewIndependent()
                        .WherePasses(filter)
                        .Where(el => el.Id != inst.Id)
                        .ToList();
                    foreach (Element hit in hits)
                        clashes.Add($"{inst.Name} ({inst.Id}) clearance blocked by {hit.Name} ({hit.Id})");
                }
            }

            if (zonesChecked == 0)
            {
                TaskDialog.Show("Apex Clearances",
                    $"No '{ClearanceSubcategory}' zones found in placed families. " +
                    "Generate families with clearance zones first (Doc 6).");
                return Result.Cancelled;
            }

            string summary = clashes.Count == 0
                ? $"Checked {zonesChecked} clearance zone(s): no obstructions found."
                : $"Checked {zonesChecked} clearance zone(s): {clashes.Count} obstruction(s):\n\n"
                  + string.Join("\n", clashes.Take(20))
                  + (clashes.Count > 20 ? $"\n… and {clashes.Count - 20} more (see log)." : "");
            foreach (string cl in clashes) ApexLog.Warn("Clearance clash: " + cl);
            TaskDialog.Show("Apex Clearances (Doc 6)", summary);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Clearance verification failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }

    /// <summary>Solids drawn on the Apex_Clearance subcategory within a placed instance.</summary>
    private static IEnumerable<Autodesk.Revit.DB.Solid> ClearanceSolids(Document doc, FamilyInstance inst, Options opt)
    {
        GeometryElement? ge = inst.get_Geometry(opt);
        if (ge == null) yield break;

        foreach (GeometryObject go in ge)
        {
            if (go is not GeometryInstance gi) continue;
            foreach (GeometryObject o in gi.GetInstanceGeometry())
            {
                if (o is not Autodesk.Revit.DB.Solid s || s.Volume < 1e-9) continue;
                if (doc.GetElement(o.GraphicsStyleId) is GraphicsStyle gs
                    && gs.GraphicsStyleCategory?.Name == ClearanceSubcategory)
                {
                    yield return s;
                }
            }
        }
    }
}

[Transaction(TransactionMode.Manual)]
public class ManagePointsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "Point management is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class ExportLayoutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            // Cloud export when an Apex family is actively selected; otherwise a local
            // CSV of placed Apex family instance positions, which works offline.
            if (Session.ActiveFamilyId != null)
            {
                string csv = ApexApiClient.RunSync(ct =>
                    Session.Api.ExportPointsAsync(Session.ActiveFamilyId!, "csv", ct));
                TaskDialog.Show("Apex Layout Export (Doc 7)", csv);
                return Result.Succeeded;
            }

            Document? doc = c.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("Apex", "Open a project document to export layout points.");
                return Result.Cancelled;
            }
            return ExportLocalPoints(doc, ref m);
        }
        catch (Exception ex)
        {
            ApexLog.Error("Layout export failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }

    /// <summary>
    /// Exports the placement point of every family instance stamped with Apex_AfisId
    /// as a field-layout CSV (id, family/type, easting, northing, elevation in both
    /// feet and meters, using the project's shared coordinates).
    /// </summary>
    private static Result ExportLocalPoints(Document doc, ref string m)
    {
        var stamped = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Select(fi => (Instance: fi,
                AfisId: fi.LookupParameter("Apex_AfisId")?.AsString()
                    ?? fi.Symbol?.LookupParameter("Apex_AfisId")?.AsString()))
            .Where(t => !string.IsNullOrEmpty(t.AfisId) && t.Instance.Location is LocationPoint)
            .ToList();

        if (stamped.Count == 0)
        {
            TaskDialog.Show("Apex Layout Export (Doc 7)",
                "No placed Apex families (Apex_AfisId) with point locations found in this project.");
            return Result.Cancelled;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Apex layout points",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "apex-layout-points.csv",
        };
        if (dlg.ShowDialog() != true) return Result.Cancelled;

        // Shared coordinates: survey crews stake out against the survey point, not
        // Revit's internal origin.
        ProjectPosition pos = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
        double cos = Math.Cos(pos.Angle);
        double sin = Math.Sin(pos.Angle);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("afis_id,element_id,family,type,easting_ft,northing_ft,elevation_ft,easting_m,northing_m,elevation_m");
        foreach ((FamilyInstance fi, string? afisId) in stamped)
        {
            XYZ p = ((LocationPoint)fi.Location).Point;
            double east = p.X * cos - p.Y * sin + pos.EastWest;
            double north = p.X * sin + p.Y * cos + pos.NorthSouth;
            double elev = p.Z + pos.Elevation;
            const double ftToM = 1.0 / UnitConv.MetersToFeet;
            sb.AppendLine(string.Join(",",
                Csv(afisId!), fi.Id.ToString(), Csv(fi.Symbol?.FamilyName ?? ""), Csv(fi.Name),
                east.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                north.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                elev.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                (east * ftToM).ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                (north * ftToM).ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                (elev * ftToM).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));
        }
        System.IO.File.WriteAllText(dlg.FileName, sb.ToString());
        ApexLog.Info($"Exported {stamped.Count} layout point(s) to {dlg.FileName}");
        TaskDialog.Show("Apex Layout Export (Doc 7)",
            $"Exported {stamped.Count} point(s) to:\n{dlg.FileName}");
        return Result.Succeeded;
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

[Transaction(TransactionMode.Manual)]
public class GenerateScheduleCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            UIDocument? uidoc = c.Application.ActiveUIDocument;
            Document? doc = uidoc?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("Apex", "Open a project document to generate a schedule.");
                return Result.Cancelled;
            }

            ViewSchedule schedule;
            int fieldsAdded = 0;
            using (var tx = new Transaction(doc, "Apex: Generate schedule"))
            {
                tx.Start();
                var catId = new ElementId(BuiltInCategory.OST_ElectricalEquipment);
                schedule = ViewSchedule.CreateSchedule(doc, catId);
                try
                {
                    schedule.Name = "Apex Equipment Schedule";
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // A schedule with that name already exists; keep the generated name.
                }

                foreach (SchedulableField sf in schedule.Definition.GetSchedulableFields())
                {
                    string name = sf.GetName(doc);
                    // Apex shared parameters first-class; plus the standard identifying columns.
                    bool wanted = name.StartsWith("Apex_", StringComparison.Ordinal)
                        || name == "Family and Type" || name == "Mark" || name == "Count";
                    if (!wanted) continue;
                    try
                    {
                        schedule.Definition.AddField(sf);
                        fieldsAdded++;
                    }
                    catch (Exception ex)
                    {
                        ApexLog.Warn($"Could not add schedule field '{name}': " + ex.Message);
                    }
                }
                tx.Commit();
            }

            uidoc!.ActiveView = schedule;
            TaskDialog.Show("Apex",
                $"Created '{schedule.Name}' with {fieldsAdded} column(s). " +
                "Apex_* shared parameters appear as columns once families using them are loaded.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Schedule generation failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        string version = typeof(AboutCommand).Assembly.GetName().Version?.ToString(3) ?? "?";
        string revit = c.Application.Application.VersionNumber;
        TaskDialog.Show("Apex BIM Studio",
            $"Apex BIM Studio plugin v{version} — running in Revit {revit}. (Doc 4)");
        return Result.Succeeded;
    }
}
