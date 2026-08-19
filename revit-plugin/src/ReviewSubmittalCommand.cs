using System;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace Apex.BimStudio.Commands;

/// <summary>
/// Round 4: Review Submittal — the modeler's release valve for a parser miss.
/// Opens one extracted equipment spec, shows every value with the extraction's
/// confidence, lets them correct fields (validated, original kept as .bak),
/// and optionally builds THAT one family immediately after saving.
///
/// Threading: the review dialog is modal and touches no Revit API; the build
/// runs after it closes, on the API thread, exactly like the single-build
/// command. No ExternalEvent needed for this flow.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ReviewSubmittalCommand : IExternalCommand
{
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
            Title = "Open an extracted equipment spec to review",
            Filter = "Extracted equipment spec (*.pred.json)|*.pred.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return Result.Cancelled;

        string fileLabel = Path.GetFileName(dlg.FileName);
        using ApexLog.RunScope run = ApexLog.BeginRun("review-" +
            (fileLabel.Length > 30 ? fileLabel.Substring(0, 30) : fileLabel));

        SpecReviewModel model = SpecReviewModel.Load(dlg.FileName);
        if (model.LoadError != null)
        {
            ApexLog.Error("Review: could not open spec: " + model.LoadError);
            message = model.LoadError;
            TaskDialog.Show("Apex — cannot open this file",
                model.LoadError + "\n\nIf the file came from the Apex portal, download it again; " +
                "if it fails the same way, send the run log to support:\n" + (run.Path ?? "(see the daily Apex log)"));
            return Result.Failed;
        }
        ApexLog.Info($"Review: opened {dlg.FileName} ({model.LowConfidenceCount()} low-confidence value(s)).");

        IntPtr owner = IntPtr.Zero;
        try { owner = commandData.Application.MainWindowHandle; }
        catch { /* older Revit — window just centers on screen */ }

        var win = new SpecReviewWindow(model, owner);
        bool? result = win.ShowDialog();

        if (win.BuildRequested != true || result != true)
        {
            if (win.Saved)
                TaskDialog.Show("Apex — review saved",
                    $"Corrections saved to {fileLabel}.\nThe original extraction is kept as {fileLabel}.bak.\n\n" +
                    "Nothing was built. To build it into the batch's out folder (and update the build " +
                    "report), open it in Review Submittal again and choose Save and Build — or re-run " +
                    "Batch Build on the folder.");
            return win.Saved ? Result.Succeeded : Result.Cancelled;
        }

        // Save-and-build: rebuild exactly this item from the corrected file.
        try
        {
            string text = File.ReadAllText(dlg.FileName);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            };
            PredFamily pred = JsonSerializer.Deserialize<PredFamily>(text, opts)
                ?? throw new InvalidOperationException("The corrected spec deserialized to nothing.");

            string? templatePath = BuildFromPredJsonCommand.ResolveTemplate(app, pred.FamilyTemplate);
            if (templatePath == null)
            {
                message = "Family template not found — this is a machine setting, not a drawing problem. " +
                    $"Revit's Family Template File location is '{app.FamilyTemplatePath ?? "not set"}'. " +
                    "Set it in Options → File Locations, then build again.";
                ApexLog.Error("Review build blocked: " + message);
                TaskDialog.Show("Apex — cannot build on this machine", message);
                return Result.Failed;
            }

            // Same destination as the batch: out\ next to the specs, named
            // after the input file (collision-proof), so the corrected family
            // lands where the batch report and the other families already are.
            string specDir = Path.GetDirectoryName(dlg.FileName) ?? ".";
            string outDir = Path.Combine(specDir, "out");
            Directory.CreateDirectory(outDir);
            string stem = fileLabel.EndsWith(".pred.json", StringComparison.OrdinalIgnoreCase)
                ? fileLabel.Substring(0, fileLabel.Length - ".pred.json".Length)
                : Path.GetFileNameWithoutExtension(fileLabel);
            string outputPath = Path.Combine(outDir,
                BuildFromPredJsonCommand.SafeFileName(stem, "drawing") + ".rfa");

            // Replacing an existing family file is confirmable (round-4 rule).
            if (File.Exists(outputPath))
            {
                var confirm = new TaskDialog("Apex — Review Submittal")
                {
                    MainInstruction = "Replace the existing family file?",
                    MainContent = $"{outputPath}\n\nalready exists (probably from an earlier batch) " +
                        "and will be replaced by this corrected build.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (confirm.Show() != TaskDialogResult.Yes)
                {
                    // Same routing as the other "review saved" dialog: only
                    // Save-and-Build / Batch Build keep the out\ folder and
                    // the build report coherent (V2 third-pass finding 1).
                    TaskDialog.Show("Apex — review saved",
                        $"Your corrections are saved in {fileLabel}; nothing was built. " +
                        "To build it, open it in Review Submittal again and choose Save and Build — " +
                        "or re-run Batch Build on the folder.");
                    return Result.Succeeded;
                }
            }

            BuildFromPredJsonCommand.BuildOutcome outcome =
                BuildFromPredJsonCommand.BuildToFile(app, pred, templatePath, outputPath);

            string checks = outcome.Flex.Width && outcome.Flex.Depth && outcome.Flex.Height && outcome.Flex.Centered
                ? "geometry checks: all passed"
                : "geometry checks: NOT all passed — treat the family as suspect and send the run log to support";
            ApexLog.Info($"Review: rebuilt {fileLabel} → {outcome.OutputPath} " +
                $"(params {outcome.ParamsValued}/{outcome.ParamsAdded}, flex W={outcome.Flex.Width} " +
                $"D={outcome.Flex.Depth} H={outcome.Flex.Height} centered={outcome.Flex.Centered}).");

            // Keep the customer's record honest: if a batch report exists in
            // out\, append the correction so the kept artifact reflects what
            // actually stands on disk now.
            try
            {
                string reportPath = Path.Combine(outDir, "BUILD_REPORT.md");
                if (File.Exists(reportPath))
                {
                    File.AppendAllText(reportPath,
                        $"\n> UPDATE {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC: `{fileLabel}` was corrected in " +
                        $"Review Submittal and rebuilt → `out/{Path.GetFileName(outputPath)}` ({checks}). " +
                        "This supersedes the row above for that drawing.\n");
                }
            }
            catch (Exception ex)
            {
                ApexLog.Warn("Could not append the correction to BUILD_REPORT.md: " + ex.Message);
            }

            TaskDialog.Show("Apex — family built",
                $"Built with your corrections:\n{outcome.OutputPath}\n\n" +
                $"Values set on {outcome.ParamsValued} of {outcome.ParamsAdded} parameters.\n" +
                $"Checks: {checks}.\n" +
                $"Run log: {run.Path ?? "(daily Apex log)"}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Review: build after correction failed.", ex);
            message = "The corrected spec saved, but the build failed: " + ex.Message;
            TaskDialog.Show("Apex — build failed",
                message + "\n\nYour corrections are safe in the file. Send the run log to support:\n" +
                (run.Path ?? "(see the daily Apex log)"));
            return Result.Failed;
        }
    }
}
