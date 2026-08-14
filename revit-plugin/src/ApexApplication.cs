using System;
using System.Reflection;
using Apex.BimStudio.Commands;
using Autodesk.Revit.UI;

namespace Apex.BimStudio;

public class ApexApplication : IExternalApplication
{
    private const string Tab = "Apex BIM Studio";

    /// <summary>
    /// Prototype commands (stubs that only show a dialog) are hidden by default so a
    /// demo build doesn't pretend to have features it lacks. Set APEX_SHOW_PROTOTYPES=1
    /// to get the full ribbon back for internal demos.
    /// </summary>
    private static bool ShowPrototypes =>
        Environment.GetEnvironmentVariable("APEX_SHOW_PROTOTYPES") == "1";

    public Result OnStartup(UIControlledApplication app)
    {
        try
        {
            try
            {
                app.CreateRibbonTab(Tab);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Tab already exists (e.g. duplicate .addin registration) — reuse it.
                ApexLog.Warn($"Ribbon tab '{Tab}' already exists; reusing.");
            }

            string asm = Assembly.GetExecutingAssembly().Location;

            RibbonPanel account = Panel(app, "Account");
            AddButton(account, asm, "ApexSettings", "Settings", typeof(SettingsCommand),
                "Set the Apex API URL and service token for this workstation");
            AddButton(account, asm, "ApexSignIn", "Sign In", typeof(SignInCommand),
                "Sign in to Apex (OAuth PKCE)");
            AddButton(account, asm, "ApexSync", "Sync", typeof(SyncCommand),
                "Sync the cloud AFIS library and choose the active family");

            RibbonPanel gen = Panel(app, "Generate");
            if (ShowPrototypes)
                AddButton(gen, asm, "ApexGenSubmittal", "From Submittal", typeof(GenerateFromSubmittalCommand),
                    "Generate a family from a PDF submittal");
            AddButton(gen, asm, "ApexGenLibrary", "From Library", typeof(GenerateFromLibraryCommand),
                "Build the active Apex library family and place it (project) or build into the open family (Family Editor)");
            AddButton(gen, asm, "ApexProcessQueue", "Process Queue", typeof(ProcessQueueCommand),
                "Build queued RFA-generation jobs from the Apex cloud queue into .rfa files");
            AddButton(gen, asm, "ApexAutoProcess", "Auto Process", typeof(AutoProcessCommand),
                "Toggle the background worker: while Revit is open, drain the RFA queue automatically every few minutes");

            // The autonomous worker: while this Revit session idles, drain the
            // cloud queue on a timer (config.auto_process_minutes; 0 = off).
            app.Idling += AutoProcessLoop.OnIdling;

            AddButton(Panel(app, "M1"), asm, "ApexBuildFromJson", "Build from JSON", typeof(BuildFromPredJsonCommand),
                "Build a family (.rfa) from a local .pred.json extraction");

            RibbonPanel fam = Panel(app, "Families");
            if (ShowPrototypes)
                AddButton(fam, asm, "ApexLibrary", "Library", typeof(OpenLibraryCommand),
                    "Open the Apex family library");
            AddButton(fam, asm, "ApexPlace", "Place", typeof(PlaceFamilyCommand),
                "Place the selected family");

            RibbonPanel validate = Panel(app, "Validate");
            AddButton(validate, asm, "ApexQa", "Run QA", typeof(RunQaCommand),
                "Run the Apex QA Engine: local checks in the Family Editor, cloud QA otherwise (Doc 8)");
            AddButton(validate, asm, "ApexClearance", "Verify Clearances", typeof(VerifyClearancesCommand),
                "Clash-check AFIS clearance zones");

            RibbonPanel layout = Panel(app, "Layout & Survey");
            if (ShowPrototypes)
                AddButton(layout, asm, "ApexPoints", "Points", typeof(ManagePointsCommand),
                    "Manage survey/layout/anchor points");
            AddButton(layout, asm, "ApexExport", "Export Layout", typeof(ExportLayoutCommand),
                "Export field points of placed Apex families (CSV, shared coordinates)");

            AddButton(Panel(app, "Schedules"), asm, "ApexSchedule", "Generate", typeof(GenerateScheduleCommand),
                "Generate a schedule from Apex shared parameters");

            AddButton(Panel(app, "Help"), asm, "ApexAbout", "About", typeof(AboutCommand),
                "About Apex BIM Studio");

            ApexLog.Info($"Apex BIM Studio loaded (prototypes {(ShowPrototypes ? "shown" : "hidden")}).");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            // A ribbon failure must not block Revit startup entirely.
            ApexLog.Error("OnStartup failed.", ex);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

    private static RibbonPanel Panel(UIControlledApplication app, string name)
    {
        try
        {
            return app.CreateRibbonPanel(Tab, name);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Panel already exists — find and reuse it.
            foreach (RibbonPanel p in app.GetRibbonPanels(Tab))
                if (p.Name == name)
                    return p;
            throw;
        }
    }

    private static void AddButton(RibbonPanel panel, string asm, string name, string text, Type cmd, string tooltip)
    {
        var data = new PushButtonData(name, text, asm, cmd.FullName);
        if (panel.AddItem(data) is PushButton btn)
            btn.ToolTip = tooltip;
    }
}
