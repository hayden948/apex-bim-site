using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Apex.BimStudio;

/// <summary>
/// Revit-free half of the batch harness: per-drawing run records, the JSONL
/// log line format, the RUN_MATRIX markdown, the failure taxonomy, and the
/// quarantine naming rules. Kept free of Revit types so the test suite can
/// exercise every code path without a Revit session (the Revit-dependent
/// executor is BatchBuildCommand).
/// </summary>
public static class BatchRunReport
{
    /// <summary>
    /// Failure taxonomy (round-3 brief): where in the pipeline a drawing died.
    /// Order matters only for display; classification rules are in Classify().
    /// </summary>
    public enum FailureClass
    {
        None,
        BadInput,        // unreadable file, invalid JSON
        SchemaViolation, // FamilySpec v1 validation errors (named fields)
        Environment,     // template not found, disk, config
        RevitApi,        // Autodesk API threw during build
        Unknown,
    }

    public sealed class Row
    {
        public string File = "";
        public bool ValidateOk;
        public int ValidateWarnings;
        public bool BuildOk;
        public FailureClass Failure = FailureClass.None;
        public string? Error;
        public int ParamsAdded;
        public int ParamsValued;
        public bool FlexWidth, FlexDepth, FlexHeight, Centered;
        public long WallMs;
        /// <summary>Relative to the batch folder (e.g. "out/x.rfa") — absolute
        /// paths in the matrix would make the two-copy determinism diff
        /// impossible to pass (adversarial finding 4).</summary>
        public string? RfaPath;
        /// <summary>Full exception detail incl. stack — jsonl + quarantine
        /// marker only, never the matrix.</summary>
        public string? Detail;
        /// <summary>Customer-report fields (round 4): what the modeler calls the
        /// thing, its box size, and which extracted values deserve a second look.</summary>
        public string? EquipmentName;
        public string? SizeSummary;
        public string[] LowConfidenceFields = Array.Empty<string>();
    }

    /// <summary>Classify an exception into the taxonomy without referencing Revit types.</summary>
    public static FailureClass Classify(Exception ex)
    {
        string type = ex.GetType().FullName ?? "";
        if (ex is JsonException) return FailureClass.BadInput;
        if (ex is System.IO.IOException || ex is UnauthorizedAccessException) return FailureClass.Environment;
        if (type.StartsWith("Autodesk.Revit.Exceptions", StringComparison.Ordinal)) return FailureClass.RevitApi;
        if (ex.Message.IndexOf("template", StringComparison.OrdinalIgnoreCase) >= 0) return FailureClass.Environment;
        return FailureClass.Unknown;
    }

    /// <summary>One JSON line per drawing — the debug-without-reproducing record.</summary>
    public static string ToJsonLine(Row r) => JsonSerializer.Serialize(new
    {
        file = r.File,
        validate_ok = r.ValidateOk,
        validate_warnings = r.ValidateWarnings,
        build_ok = r.BuildOk,
        failure_class = r.Failure == FailureClass.None ? null : r.Failure.ToString(),
        error = r.Error,
        params_added = r.ParamsAdded,
        params_valued = r.ParamsValued,
        flex = new { width = r.FlexWidth, depth = r.FlexDepth, height = r.FlexHeight, centered = r.Centered },
        wall_ms = r.WallMs,
        rfa = r.RfaPath,
        detail = r.Detail,
        low_confidence = r.LowConfidenceFields.Length == 0 ? null : r.LowConfidenceFields,
    });

    /// <summary>
    /// Quarantine marker path for a failed input (never looks like a finished
    /// .rfa). Long input names are truncated with a stable hash suffix so the
    /// marker write itself cannot die on MAX_PATH (adversarial finding 2).
    /// </summary>
    public static string QuarantineMarkerName(string inputFileName)
    {
        const int MaxBase = 120;
        if (inputFileName.Length <= MaxBase) return inputFileName + ".FAILED.txt";
        uint hash = 2166136261;
        foreach (char c in inputFileName) hash = (hash ^ c) * 16777619;
        return inputFileName.Substring(0, 100) + "~" + hash.ToString("x8") + ".FAILED.txt";
    }

    /// <summary>
    /// RUN_MATRIX.md content: one row per drawing (no cherry-picking — the
    /// caller passes every attempted file), then the taxonomy counts, then the
    /// honest denominator line. Success rate is computed over ALL rows.
    /// </summary>
    public static string BuildMatrix(IReadOnlyList<Row> rows, string runLabel, string startedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Batch run matrix — {runLabel}");
        sb.AppendLine();
        sb.AppendLine($"Started (UTC): {startedUtc}. Files attempted: {rows.Count} (every file in the batch");
        sb.AppendLine("folder — none skipped, none cherry-picked). A drawing counts as SUCCESS only if it");
        sb.AppendLine("validated AND built AND saved a .rfa.");
        sb.AppendLine();
        sb.AppendLine("| file | validate | build | failure class | params (set/added) | flexed W/D/H/centered | wall ms | output |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (Row r in rows)
        {
            string flex = $"{YN(r.FlexWidth)}/{YN(r.FlexDepth)}/{YN(r.FlexHeight)}/{YN(r.Centered)}";
            sb.AppendLine($"| {r.File} | {(r.ValidateOk ? "ok" : "FAIL")}" +
                (r.ValidateWarnings > 0 ? $" ({r.ValidateWarnings} warn)" : "") +
                $" | {(r.BuildOk ? "ok" : "FAIL")} | {(r.Failure == FailureClass.None ? "-" : r.Failure.ToString())}" +
                $" | {r.ParamsValued}/{r.ParamsAdded} | {flex} | {r.WallMs} | {(r.BuildOk ? r.RfaPath : Short(r.Error))} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Failure taxonomy");
        sb.AppendLine();
        var counts = rows.Where(r => r.Failure != FailureClass.None)
            .GroupBy(r => r.Failure).OrderByDescending(g => g.Count());
        if (!counts.Any()) sb.AppendLine("No failures in this batch.");
        foreach (var g in counts) sb.AppendLine($"- {g.Key}: {g.Count()}");
        sb.AppendLine();
        int ok = rows.Count(r => r.BuildOk);
        int pct = rows.Count == 0 ? 0 : (int)Math.Round(100.0 * ok / rows.Count);
        sb.AppendLine($"**{ok}/{rows.Count} succeeded ({pct.ToString(CultureInfo.InvariantCulture)}%) " +
            "over every attempted file.** This is the rate for THIS input set only; it is not a claim " +
            "about unseen drawings.");
        return sb.ToString();
    }

    /// <summary>
    /// BUILD_REPORT.md — the artifact the CUSTOMER keeps next to the family
    /// files (round 4, work item 4). Everything in it is written in the
    /// modeler's language: equipment, drawing, family — no schema or pipeline
    /// jargon, no stack traces (those live in the run log and quarantine
    /// markers, which the failed items point at). It states, per item: what was
    /// created, from which drawing file, at what size, how many values were
    /// set, which build checks passed, and — honestly — which extracted values
    /// carry low confidence and deserve a manual glance.
    /// </summary>
    public static string BuildCustomerReport(IReadOnlyList<Row> rows, string startedUtc, string? runLogPath)
    {
        int built = rows.Count(r => r.BuildOk);
        int failed = rows.Count - built;
        int review = rows.Count(NeedsReview);

        var sb = new StringBuilder();
        sb.AppendLine("# Family build report");
        sb.AppendLine();
        sb.AppendLine($"Run started {startedUtc} UTC — Apex BIM Studio.");
        sb.AppendLine();
        sb.AppendLine($"**{built} of {rows.Count} drawings built into Revit families.** " +
            (failed > 0 ? $"{failed} failed — each failed item below says what to do next. " : "") +
            (review > 0
                ? (review == 1
                    ? "1 built family lists values worth double-checking before use."
                    : $"{review} built families list values worth double-checking before use.")
                : ""));
        sb.AppendLine();
        sb.AppendLine("Keep this file with the .rfa files: it records what was built, from which");
        sb.AppendLine("drawing, and with which values, so the result can be defended without");
        sb.AppendLine("reconstructing the run.");
        sb.AppendLine();

        foreach (Row r in rows)
        {
            string name = string.IsNullOrWhiteSpace(r.EquipmentName) ? r.File : r.EquipmentName!;
            sb.AppendLine($"## {name}");
            sb.AppendLine();
            sb.AppendLine($"- Drawing file: `{r.File}`");
            if (r.BuildOk)
            {
                sb.AppendLine($"- Result: **BUILT** → `{r.RfaPath}`");
                if (!string.IsNullOrWhiteSpace(r.SizeSummary)) sb.AppendLine($"- Size: {r.SizeSummary}");
                sb.AppendLine($"- Values set: {r.ParamsValued} of {r.ParamsAdded} parameters");
                sb.AppendLine($"- Build checks: {FlexSummary(r)}");
                if (!FlexOk(r))
                    sb.AppendLine("- What to do: rebuild just this item (Review Submittal → Save and Build); " +
                        "if the checks fail again, do not use the family — send the run log to support.");
                if (r.LowConfidenceFields.Length > 0)
                {
                    sb.AppendLine($"- **Check these values** (the extraction was less than " +
                        $"{(int)(100 * LowConfidenceNote)} percent sure): {string.Join(", ", r.LowConfidenceFields)}. " +
                        "Open the drawing's spec in Review Submittal to confirm or correct them.");
                }
                if (r.ValidateWarnings > 0)
                    sb.AppendLine($"- {r.ValidateWarnings} note(s) were logged for this drawing — see the run log.");
            }
            else
            {
                sb.AppendLine("- Result: **NOT BUILT** — no family file was produced for this drawing.");
                sb.AppendLine($"- Why: {Plain(r)}");
                sb.AppendLine($"- What to do: {NextStep(r.Failure)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(runLogPath != null
            ? $"Full technical log for this run: `{runLogPath}` — if you contact support, send that one file."
            : "The run log could not be created on this machine; the daily Apex log " +
              @"(%LOCALAPPDATA%\Apex\logs) has this run's lines.");
        return sb.ToString();
    }

    // Kept as a constant so the report text and SpecReviewModel.LowConfidenceThreshold
    // can be asserted equal by the test suite rather than drifting silently.
    public const double LowConfidenceNote = 0.8;

    /// <summary>
    /// Names of parameters whose extraction confidence is below the threshold —
    /// the "check these values" list on the customer report. Reads the RAW
    /// document so it works even when the build later fails.
    /// </summary>
    public static string[] CollectLowConfidence(JsonElement root, double threshold = LowConfidenceNote)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("parameters", out JsonElement pars)
            || pars.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var names = new List<string>();
        foreach (JsonElement p in pars.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            if (!p.TryGetProperty("confidence", out JsonElement c)
                || c.ValueKind != JsonValueKind.Number
                || !c.TryGetDouble(out double conf) || conf >= threshold) continue;
            names.Add(p.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "(unnamed)" : "(unnamed)");
        }
        return names.ToArray();
    }

    /// <summary>All four geometry checks passed on a built row.</summary>
    public static bool FlexOk(Row r) => r.FlexWidth && r.FlexDepth && r.FlexHeight && r.Centered;

    /// <summary>
    /// The failure class in the modeler's words — for every surface a customer
    /// sees (progress window, dialogs). The enum names stay in RUN_MATRIX.md
    /// and the jsonl, which are operator/support artifacts.
    /// </summary>
    public static string CustomerClass(FailureClass f) => f switch
    {
        FailureClass.BadInput => "unreadable file",
        FailureClass.SchemaViolation => "the spec has invalid fields",
        FailureClass.Environment => "machine setup problem",
        FailureClass.RevitApi => "Revit rejected the build",
        _ => "unexpected error",
    };

    /// <summary>
    /// A built row the modeler should look at before using the family:
    /// extraction notes, low-confidence values, or failed geometry checks.
    /// Shared by the report headline, the progress window, and the batch
    /// summary so "needs review" means the same thing everywhere.
    /// </summary>
    public static bool NeedsReview(Row r)
        => r.BuildOk && (r.ValidateWarnings > 0 || r.LowConfidenceFields.Length > 0 || !FlexOk(r));

    private static string FlexSummary(Row r)
    {
        if (r.FlexWidth && r.FlexDepth && r.FlexHeight && r.Centered)
            return "geometry resizes correctly on width, depth, and height, and stays centered — all passed";
        var bad = new List<string>();
        if (!r.FlexWidth) bad.Add("width resize");
        if (!r.FlexDepth) bad.Add("depth resize");
        if (!r.FlexHeight) bad.Add("height resize");
        if (!r.Centered) bad.Add("centering");
        return "FAILED: " + string.Join(", ", bad) +
            " — the family was built but its geometry did not verify; treat it as suspect.";
    }

    private static string Plain(Row r)
        => string.IsNullOrWhiteSpace(r.Error) ? "no further detail was captured" : r.Error!;

    private static string NextStep(FailureClass f) => f switch
    {
        FailureClass.BadInput =>
            "The drawing's spec file could not be read. Re-download or re-export it from the " +
            "Apex portal; if it fails again, send the run log to support.",
        FailureClass.SchemaViolation =>
            "The spec is missing or has invalid fields (named above). Open it with Review " +
            "Submittal, correct the named fields, and rebuild this one item.",
        FailureClass.Environment =>
            "This is a machine setup problem, not a drawing problem (the message above names " +
            "it — usually Revit's family template folder). Fix the setting and run the batch " +
            "again; already-built families are simply rebuilt.",
        FailureClass.RevitApi =>
            "Revit itself rejected the build. Rebuild just this item once; if it fails the " +
            "same way, send the run log and the quarantine file to support.",
        _ =>
            "An unexpected error occurred. Send the run log and the quarantine file to support.",
    };

    private static string YN(bool b) => b ? "y" : "n";

    private static string Short(string? s)
        => string.IsNullOrEmpty(s) ? "" : (s!.Length > 80 ? s.Substring(0, 77) + "..." : s).Replace("|", "\\|").Replace("\n", " ");
}
