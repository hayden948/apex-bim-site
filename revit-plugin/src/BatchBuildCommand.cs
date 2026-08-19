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

        // Round 5: licensed builds only. The message says exactly what to do;
        // scripted (journal) runs must not stall on a dialog.
        ApexLicense.Status lic = ApexLicense.CheckDefault();
        if (lic.State != ApexLicense.State.Valid)
        {
            message = lic.Message;
            ApexLog.Warn("Batch build blocked by license state " + lic.State + ".");
            if (!scripted) TaskDialog.Show("Apex — license", lic.Message);
            return Result.Failed;
        }
        if (!scripted)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Pick any equipment spec — every spec in its folder will be built",
                Filter = "Extracted equipment specs (*.pred.json)|*.pred.json|JSON files (*.json)|*.json",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return Result.Cancelled;
            batchDir = Path.GetDirectoryName(dlg.FileName);
        }
        if (string.IsNullOrWhiteSpace(batchDir) || !Directory.Exists(batchDir))
        {
            message = $"Batch folder not found: '{batchDir}'. Set APEX_BATCH_DIR or pick a file.";
            if (!scripted) TaskDialog.Show("Apex — Batch Build", message);
            return Result.Failed;
        }

        string[] files = Directory.GetFiles(batchDir!, "*.pred.json")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            message = $"No equipment specs (*.pred.json) found in '{batchDir}'.";
            if (!scripted) TaskDialog.Show("Apex — Batch Build", message);
            return Result.Failed;
        }

        // Confirmable before anything is touched (round 4: every destructive
        // action confirmable): the batch replaces earlier results for these
        // drawings, and Revit stays busy until it finishes.
        if (!scripted)
        {
            var confirm = new TaskDialog("Apex — Batch Build")
            {
                MainInstruction = $"Build {files.Length} equipment famil{(files.Length == 1 ? "y" : "ies")}?",
                MainContent =
                    $"Folder: {batchDir}\n\n" +
                    "• Families are written to the 'out' folder; earlier results for these " +
                    "drawings are replaced.\n" +
                    "• One bad drawing never stops the rest — failures are listed in the build " +
                    "report with what to do next.\n" +
                    "• Revit will be busy until the batch finishes; a progress window shows " +
                    "each drawing as it completes.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            };
            if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;
        }

        // One log file per run (round 4, work item 5): everything this batch
        // does lands in its own timestamped file, so support is "send me that
        // one file". The daily rolling log still receives every line.
        using ApexLog.RunScope run = ApexLog.BeginRun("batch-build");

        string outDir = Path.Combine(batchDir!, "out");
        string quarantineDir = Path.Combine(batchDir!, "quarantine");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(quarantineDir);
        string jsonlPath = Path.Combine(batchDir!, "batch-run.jsonl");
        string startedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        ApexLog.Info($"Batch build: {files.Length} spec(s) in {batchDir} (scripted={scripted}).");

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

        // Per-item progress (round 4, work item 2). All Revit API work stays on
        // this (the API) thread; the window is repainted between per-item
        // transactions by a Render-priority dispatcher pump — honest status
        // without input re-entrancy. Scripted runs stay headless.
        BatchProgressWindow? progress = null;
        if (!scripted)
        {
            try
            {
                IntPtr owner = IntPtr.Zero;
                try { owner = commandData.Application.MainWindowHandle; } catch { }
                progress = new BatchProgressWindow(files.Length, batchDir!, owner);
                progress.Show();
                // Modal semantics for the pumped loop: Revit's window is
                // disabled until EndRunUi in the finally below, so pumped
                // messages cannot re-enter Revit mid-transaction.
                progress.BeginRunUi();
                BatchProgressWindow.Pump();
            }
            catch (Exception ex)
            {
                // The batch must run even if the progress UI cannot.
                ApexLog.Warn("Progress window unavailable (continuing headless): " + ex.Message);
                progress = null;
            }
        }

        var rows = new List<BatchRunReport.Row>();
        int index = 0;
        try
        {
        foreach (string file in files)
        {
            index++;
            try { progress?.Starting(index, Path.GetFileName(file)); }
            catch (Exception ex) { ApexLog.Warn("Progress update failed (continuing): " + ex.Message); }

            BatchRunReport.Row row = RunOne(app, file, outDir);
            rows.Add(row);

            try { progress?.Finished(row, index); }
            catch (Exception ex) { ApexLog.Warn("Progress update failed (continuing): " + ex.Message); }
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
        }
        finally
        {
            // Revit's window MUST come back even if the loop dies unexpectedly,
            // and before any dialog below (a disabled owner can't be clicked).
            try { progress?.EndRunUi(); }
            catch (Exception ex) { ApexLog.Warn("Progress teardown failed: " + ex.Message); }
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

        // The report the CUSTOMER keeps, next to the .rfa files (round 4,
        // work item 4) — customer language, per-item results, what to do next.
        string reportPath = Path.Combine(outDir, "BUILD_REPORT.md");
        try
        {
            File.WriteAllText(reportPath, BatchRunReport.BuildCustomerReport(rows, startedUtc, run.Path));
        }
        catch (Exception ex)
        {
            bookkeepingErrors++;
            ApexLog.Error("BUILD_REPORT.md write failed.", ex);
        }

        int ok = rows.Count(r => r.BuildOk);
        int failedCount = rows.Count - ok;
        // Same definition as the report headline and the progress ⚠ flag —
        // failed geometry checks are "needs review", never plain success.
        int review = rows.Count(BatchRunReport.NeedsReview);
        string summary = $"Batch complete: {ok}/{rows.Count} built, {failedCount} failed, {review} built-but-check-values. " +
            $"Report: {reportPath}; matrix: {Path.Combine(batchDir!, "RUN_MATRIX.md")}; " +
            $"failures quarantined under {quarantineDir}." +
            (bookkeepingErrors > 0 ? $" WARNING: {bookkeepingErrors} bookkeeping write(s) failed — see the Apex log." : "");
        ApexLog.Info(summary);

        try { progress?.Close(); }
        catch (Exception ex) { ApexLog.Warn("Progress window close failed: " + ex.Message); }

        if (!scripted)
        {
            var done = new TaskDialog("Apex — Batch Build finished")
            {
                MainInstruction = $"{ok} of {rows.Count} famil{(rows.Count == 1 ? "y" : "ies")} built" +
                    (failedCount > 0 ? $" — {failedCount} failed" : "") +
                    (review > 0 ? $" — {review} built but list values to double-check" : ""),
                MainContent =
                    $"Families and the build report are in:\n{outDir}\n\n" +
                    "BUILD_REPORT.md lists every drawing: what was built with which values, " +
                    "what failed, and what to do about each failure.\n\n" +
                    $"Log for this run: {run.Path ?? "(see the daily Apex log)"}" +
                    (bookkeepingErrors > 0
                        ? $"\n\nWARNING: {bookkeepingErrors} bookkeeping write(s) failed — the log has details."
                        : ""),
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            done.Show();
        }
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
            // An UNREMOVABLE stale output (read-only, open in another program)
            // is a hard machine-class failure HERE, before any Revit call:
            // deterministic classification, a message that names the fix, and
            // the surviving old file is explained rather than looking like a
            // containment bug (round-4 V2 re-review findings 1–2).
            if (File.Exists(outputPath))
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    row.Failure = BatchRunReport.FailureClass.Environment;
                    row.Error = $"The existing family file could not be replaced: {Path.GetFileName(outputPath)} " +
                        "is read-only or open in another program. The OLD file from an earlier run is still " +
                        "there; clear its read-only attribute (or close the program using it) and run again. " +
                        $"({ex.Message})";
                    row.Detail = ex.ToString();
                    return row;
                }
            }

            string text = File.ReadAllText(file);
            using JsonDocument doc = JsonDocument.Parse(text);

            // Customer-report context, captured from the RAW document so a
            // drawing that later fails still gets a recognizable report entry.
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("family_name", out JsonElement fn)
                    && fn.ValueKind == JsonValueKind.String)
                    row.EquipmentName = fn.GetString();
                row.LowConfidenceFields = BatchRunReport.CollectLowConfidence(doc.RootElement);
            }

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

            // A drawing whose overall size disagrees with a same-named
            // Dimensions parameter must not sail through the batch silently
            // (the builder keeps the spec's parameter value; the mismatch is
            // the extraction's problem to surface): counted as warnings, so
            // the row lands in "needs review" and the log names the fields.
            try
            {
                List<string> drift = SpecReviewModel.Load(file).ConsistencyWarnings();
                foreach (string w in drift) ApexLog.Warn($"{row.File}: {w}");
                row.ValidateWarnings += drift.Count;
            }
            catch (Exception ex)
            {
                ApexLog.Warn($"Consistency check failed for {row.File} (continuing): " + ex.Message);
            }

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            };
            PredFamily pred = JsonSerializer.Deserialize<PredFamily>(text, opts)!;
            row.SizeSummary = SizeOf(pred.Geometry);

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

    private static string? SizeOf(PredGeometry? g)
    {
        if (g?.Width == null || g.Depth == null || g.Height == null) return null;
        string Dim(PredDim d) =>
            d.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
            (string.IsNullOrWhiteSpace(d.Unit) ? " in" : " " + d.Unit);
        return $"{Dim(g.Width)} W × {Dim(g.Depth)} D × {Dim(g.Height)} H";
    }
}
