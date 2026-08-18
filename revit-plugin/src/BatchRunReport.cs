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
        public string? RfaPath;
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
    });

    /// <summary>Quarantine marker path for a failed input (never looks like a finished .rfa).</summary>
    public static string QuarantineMarkerName(string inputFileName)
        => inputFileName + ".FAILED.txt";

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

    private static string YN(bool b) => b ? "y" : "n";

    private static string Short(string? s)
        => string.IsNullOrEmpty(s) ? "" : (s!.Length > 80 ? s.Substring(0, 77) + "..." : s).Replace("|", "\\|").Replace("\n", " ");
}
