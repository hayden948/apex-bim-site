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
            // Round 5: the add-in ships its own dependency set (System.Text.Json
            // chain on net48, BouncyCastle on both). Revit loads add-ins via
            // LoadFrom, which USUALLY probes our folder for dependents — but a
            // version mismatch or another add-in's loader can break that. This
            // hook makes "our folder first" explicit; it only ever answers for
            // files we actually ship and logs when it fires.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromAddinFolder;
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
                "Connect this workstation to your Apex project (server address and access token)");
            AddButton(account, asm, "ApexSignIn", "Sign In", typeof(SignInCommand),
                "Sign in to your Apex account");
            AddButton(account, asm, "ApexSync", "Sync", typeof(SyncCommand),
                "Get your project's equipment library from Apex and choose the active family");

            RibbonPanel gen = Panel(app, "Generate");
            if (ShowPrototypes)
                AddButton(gen, asm, "ApexGenSubmittal", "From Submittal", typeof(GenerateFromSubmittalCommand),
                    "Generate a family from a PDF submittal");
            AddButton(gen, asm, "ApexGenLibrary", "From Library", typeof(GenerateFromLibraryCommand),
                "Build the active library family and place it (project) or build into the open family (Family Editor)");
            AddButton(gen, asm, "ApexProcessQueue", "Process Queue", typeof(ProcessQueueCommand),
                "Build the families your Apex project has queued for this workstation");
            AddButton(gen, asm, "ApexAutoProcess", "Auto Process", typeof(AutoProcessCommand),
                "While Revit is open, automatically build queued families every few minutes (toggle)");

            // The autonomous worker: while this Revit session idles, drain the
            // cloud queue on a timer (config.auto_process_minutes; 0 = off).
            app.Idling += AutoProcessLoop.OnIdling;

            // Round 4: the panel a first-time CVE modeler works from, in their
            // order of use — review what was extracted, build one, build all.
            // These commands create their own family documents, so they are
            // available at Revit's start screen too (zero-document): without an
            // availability class Revit greys external commands there and the
            // first-time user's first click is a dead button.
            RibbonPanel build = Panel(app, "Submittals");
            AddButton(build, asm, "ApexReviewSubmittal", "Review Submittal", typeof(ReviewSubmittalCommand),
                "Check the values Apex extracted from a submittal before building — every value is shown " +
                "with how sure the extraction was; correct any field and build that one family",
                zeroDoc: true);
            AddButton(build, asm, "ApexBuildFromJson", "Build Family", typeof(BuildFromPredJsonCommand),
                "Build one Revit family from a submittal's extracted equipment file (downloaded from " +
                "the Apex portal)", zeroDoc: true);
            AddButton(build, asm, "ApexBatchBuild", "Batch Build", typeof(BatchBuildCommand),
                "Build every equipment spec in a folder into families. One bad drawing never stops the " +
                "rest; a build report next to the families says what was built, what failed, and what to do",
                zeroDoc: true);

            RibbonPanel fam = Panel(app, "Families");
            if (ShowPrototypes)
                AddButton(fam, asm, "ApexLibrary", "Library", typeof(OpenLibraryCommand),
                    "Open the Apex family library");
            AddButton(fam, asm, "ApexPlace", "Place", typeof(PlaceFamilyCommand),
                "Place the selected family");

            RibbonPanel validate = Panel(app, "Validate");
            AddButton(validate, asm, "ApexQa", "Run QA", typeof(RunQaCommand),
                "Check a family against the Apex quality rules (geometry, parameters, identity) — " +
                "local checks in the Family Editor, cloud checks otherwise");
            AddButton(validate, asm, "ApexClearance", "Verify Clearances", typeof(VerifyClearancesCommand),
                "Clash-check the working clearance zones of placed equipment");

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

    public Result OnShutdown(UIControlledApplication app)
    {
        AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromAddinFolder;
        return Result.Succeeded;
    }

    private static System.Reflection.Assembly? ResolveFromAddinFolder(object? sender, ResolveEventArgs args)
    {
        try
        {
            string name = new System.Reflection.AssemblyName(args.Name).Name + ".dll";
            string? dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dir == null) return null;
            string candidate = System.IO.Path.Combine(dir, name);
            if (System.IO.File.Exists(candidate))
            {
                ApexLog.Info("Resolved dependency from the add-in folder: " + name);
                return Assembly.LoadFrom(candidate);
            }
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Dependency resolution failed for '" + args.Name + "': " + ex.Message);
        }
        return null;
    }

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

    private static void AddButton(RibbonPanel panel, string asm, string name, string text, Type cmd, string tooltip,
        bool zeroDoc = false)
    {
        var data = new PushButtonData(name, text, asm, cmd.FullName);
        if (zeroDoc) data.AvailabilityClassName = typeof(CommandAlwaysAvailable).FullName;
        if (panel.AddItem(data) is PushButton btn)
            btn.ToolTip = tooltip;
    }
}

/// <summary>
/// Availability for commands that need no open document (they create their own
/// family documents): keeps the Submittals buttons clickable at Revit's
/// zero-document start screen, where external commands are otherwise disabled.
/// </summary>
public class CommandAlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, Autodesk.Revit.DB.CategorySet selectedCategories)
        => true;
}
