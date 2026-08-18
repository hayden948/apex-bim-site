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
                    "Nothing was built. Use Build Family (or Batch Build for the folder) when ready.");
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

            string outputPath = Path.Combine(
                Path.GetDirectoryName(dlg.FileName) ?? ".",
                BuildFromPredJsonCommand.SafeFileName(pred.FamilyName, Path.GetFileNameWithoutExtension(dlg.FileName)) + ".rfa");

            BuildFromPredJsonCommand.BuildOutcome outcome =
                BuildFromPredJsonCommand.BuildToFile(app, pred, templatePath, outputPath);

            ApexLog.Info($"Review: rebuilt {fileLabel} → {outcome.OutputPath} " +
                $"(params {outcome.ParamsValued}/{outcome.ParamsAdded}, flex W={outcome.Flex.Width} " +
                $"D={outcome.Flex.Depth} H={outcome.Flex.Height} centered={outcome.Flex.Centered}).");
            TaskDialog.Show("Apex — family built",
                $"Built with your corrections:\n{outcome.OutputPath}\n\n" +
                $"Values set on {outcome.ParamsValued} of {outcome.ParamsAdded} parameters.\n" +
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
