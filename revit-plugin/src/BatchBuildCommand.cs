using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace Apex.BimStudio.Commands;

/// <summary>
/// Batch harness (round 3): build EVERY .pred.json in a folder into .rfa files
/// with per-drawing isolation. One bad drawing never aborts the batch and never
/// leaves output that looks finished:
///  - each file is validated (FamilySpec v1, named-field errors) before any
///    Revit call, then built in its own family document inside try/catch;
///  - failures write a quarantine marker (quarantine/&lt;file&gt;.FAILED.txt) with the
///    failure class, message, and stack — partial .rfa files are deleted;
///  - every file appends one JSON line to batch-run.jsonl (debug without
///    reproducing) and the run ends by writing RUN_MATRIX.md with the taxonomy
///    counts and the honest all-files denominator.
///
/// Scriptable path: set APEX_BATCH_DIR to the folder and drive Revit with the
/// journal in revit-plugin/deploy/batch/ — no dialog is shown when the variable
/// is set. Interactive path: pick any .pred.json; its folder becomes the batch.
/// Files are processed in ordinal filename order (deterministic).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class BatchBuildCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Application app = commandData.Application.Application;

        string? batchDir = Environment.GetEnvironmentVariable("APEX_BATCH_DIR");
        bool scripted = !string.IsNullOrWhiteSpace(batchDir);
        if (!scripted)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Pick any .pred.json — its folder becomes the batch",
                Filter = "Prediction JSON (*.pred.json)|*.pred.json|JSON files (*.json)|*.json",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return Result.Cancelled;
            batchDir = Path.GetDirectoryName(dlg.FileName);
        }
        if (string.IsNullOrWhiteSpace(batchDir) || !Directory.Exists(batchDir))
        {
            message = $"Batch folder not found: '{batchDir}'. Set APEX_BATCH_DIR or pick a file.";
            if (!scripted) TaskDialog.Show("Apex Batch", message);
            return Result.Failed;
        }

        string[] files = Directory.GetFiles(batchDir!, "*.pred.json")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            message = $"No .pred.json files in '{batchDir}'.";
            if (!scripted) TaskDialog.Show("Apex Batch", message);
            return Result.Failed;
        }

        string outDir = Path.Combine(batchDir!, "out");
        string quarantineDir = Path.Combine(batchDir!, "quarantine");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(quarantineDir);
        string jsonlPath = Path.Combine(batchDir!, "batch-run.jsonl");
        string startedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        // Fresh-run semantics (adversarial finding 3): this run's records only —
        // no mixed logs, no stale markers, no stale outputs from earlier runs
        // masquerading as this run's results. The jsonl starts with a header
        // line carrying the folder (kept OUT of the matrix so the two-copy
        // determinism diff can pass).
        int bookkeepingErrors = 0;
        try
        {
            File.WriteAllText(jsonlPath,
                $"{{\"run\":\"apex-batch\",\"folder\":{JsonSerializer.Serialize(batchDir)},\"started_utc\":\"{startedUtc}\",\"files\":{files.Length}}}"
                + Environment.NewLine);
            foreach (string stale in Directory.GetFiles(quarantineDir, "*.FAILED.txt")) File.Delete(stale);
        }
        catch (Exception ex)
        {
            bookkeepingErrors++;
            ApexLog.Warn("Batch pre-run cleanup failed (continuing): " + ex.Message);
        }

        var rows = new List<BatchRunReport.Row>();
        foreach (string file in files)
        {
            BatchRunReport.Row row = RunOne(app, file, outDir);
            rows.Add(row);
            // Bookkeeping must never kill the batch (adversarial finding 2):
            // a locked jsonl or an unwritable marker is logged and counted,
            // and the run continues.
            try
            {
                // Append after each drawing so a hard crash still leaves the
                // log for every file processed so far.
                File.AppendAllText(jsonlPath, BatchRunReport.ToJsonLine(row) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                bookkeepingErrors++;
                ApexLog.Warn($"jsonl append failed for {row.File} (continuing): " + ex.Message);
            }
            if (row.Failure != BatchRunReport.FailureClass.None)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(quarantineDir, BatchRunReport.QuarantineMarkerName(Path.GetFileName(file))),
                        $"FAILED — not delivered.\nfile: {file}\nclass: {row.Failure}\nerror: {row.Error}\n" +
                        $"validate_ok: {row.ValidateOk}\nwall_ms: {row.WallMs}\nutc: {DateTime.UtcNow:O}\n\n" +
                        $"detail:\n{row.Detail ?? "(none)"}\n");
                }
                catch (Exception ex)
                {
                    bookkeepingErrors++;
                    ApexLog.Warn($"quarantine marker failed for {row.File} (continuing): " + ex.Message);
                }
            }
        }

        try
        {
            string matrix = BatchRunReport.BuildMatrix(rows, "Apex batch build", startedUtc);
            File.WriteAllText(Path.Combine(batchDir!, "RUN_MATRIX.md"), matrix);
        }
        catch (Exception ex)
        {
            bookkeepingErrors++;
            ApexLog.Error("RUN_MATRIX.md write failed.", ex);
        }

        int ok = rows.Count(r => r.BuildOk);
        string summary = $"Batch complete: {ok}/{rows.Count} built. " +
            $"Matrix: {Path.Combine(batchDir!, "RUN_MATRIX.md")}; failures quarantined under {quarantineDir}." +
            (bookkeepingErrors > 0 ? $" WARNING: {bookkeepingErrors} bookkeeping write(s) failed — see the Apex log." : "");
        ApexLog.Info(summary);
        if (!scripted) TaskDialog.Show("Apex Batch", summary);
        // Scripted runs read the exit state from RUN_MATRIX.md / jsonl, not a dialog.
        return Result.Succeeded;
    }

    /// <summary>Process one drawing in full isolation; never throws.</summary>
    private static BatchRunReport.Row RunOne(Application app, string file, string outDir)
    {
        var row = new BatchRunReport.Row { File = Path.GetFileName(file) };
        var sw = Stopwatch.StartNew();
        // Output name derives from the INPUT FILENAME, which is unique within
        // the folder by construction — family_name-derived names could collide
        // across drawings, letting one drawing's failure delete (or its success
        // overwrite) another's finished .rfa (adversarial finding 1).
        string stem = Path.GetFileName(file).EndsWith(".pred.json", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(file).Substring(0, Path.GetFileName(file).Length - ".pred.json".Length)
            : Path.GetFileNameWithoutExtension(file);
        string outputPath = Path.Combine(outDir, BuildFromPredJsonCommand.SafeFileName(stem, "drawing") + ".rfa");
        try
        {
            // Fresh-run semantics: a stale output from an earlier run must not
            // survive a run in which this input fails (adversarial finding 3).
            try
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
            catch (Exception ex)
            {
                ApexLog.Warn($"Could not remove stale output {outputPath}: " + ex.Message);
            }

            string text = File.ReadAllText(file);
            using JsonDocument doc = JsonDocument.Parse(text);

            PredValidator.Result check = PredValidator.Validate(doc.RootElement, row.File);
            row.ValidateWarnings = check.Warnings.Count;
            foreach (string w in check.Warnings) ApexLog.Warn(w);
            if (!check.IsValid)
            {
                row.ValidateOk = false;
                row.Failure = BatchRunReport.FailureClass.SchemaViolation;
                row.Error = string.Join(" | ", check.Errors.Take(6));
                return row;
            }
            row.ValidateOk = true;

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            };
            PredFamily pred = JsonSerializer.Deserialize<PredFamily>(text, opts)!;

            string? templatePath = BuildFromPredJsonCommand.ResolveTemplate(app, pred.FamilyTemplate);
            if (templatePath == null)
            {
                row.Failure = BatchRunReport.FailureClass.Environment;
                row.Error = $"Family template not found (looked for '{pred.FamilyTemplate ?? "default"}' " +
                    $"under '{app.FamilyTemplatePath ?? "unset"}'). Fix Revit's Family Template File location.";
                return row;
            }

            BuildFromPredJsonCommand.BuildOutcome outcome =
                BuildFromPredJsonCommand.BuildToFile(app, pred, templatePath, outputPath);

            row.BuildOk = true;
            row.ParamsAdded = outcome.ParamsAdded;
            row.ParamsValued = outcome.ParamsValued;
            row.FlexWidth = outcome.Flex.Width;
            row.FlexDepth = outcome.Flex.Depth;
            row.FlexHeight = outcome.Flex.Height;
            row.Centered = outcome.Flex.Centered;
            // Relative path only — absolute paths would poison the two-copy
            // determinism diff (adversarial finding 4).
            row.RfaPath = "out/" + Path.GetFileName(outputPath);
        }
        catch (Exception ex)
        {
            row.Failure = row.ValidateOk || row.Failure == BatchRunReport.FailureClass.None
                ? BatchRunReport.Classify(ex)
                : row.Failure;
            row.Error = ex.Message;
            row.Detail = ex.ToString();
            ApexLog.Error($"Batch item failed: {row.File}", ex);
        }
        finally
        {
            sw.Stop();
            row.WallMs = sw.ElapsedMilliseconds;
        }
        return row;
    }
}
