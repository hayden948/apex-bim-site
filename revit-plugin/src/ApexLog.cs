using System;
using System.IO;

namespace Apex.BimStudio;

/// <summary>
/// Minimal rolling file logger. Writes to %LOCALAPPDATA%\Apex\logs\ApexBimStudio-yyyyMMdd.log.
/// Never throws: a broken log path must not take Revit down with it.
///
/// Per-run scope (round 4): a command that represents one operator "run" (a batch build,
/// a single build) opens a scope with <see cref="BeginRun"/>. While the scope is open,
/// every line is ALSO written to its own timestamped file (run-yyyyMMdd-HHmmss-name.log),
/// so a support request is "send me that one file" — it contains the whole run and nothing
/// else. The daily rolling log keeps receiving everything as before. Disposing the scope
/// writes a closing line and returns the file path (shown to the operator in the summary).
/// </summary>
public static class ApexLog
{
    private static readonly object Gate = new object();
    private static string? _dir;
    private static string? _runFile;

    /// <summary>
    /// One-run log scope. Create with <see cref="BeginRun"/>; dispose when the run ends.
    /// <see cref="Path"/> is null only if the log directory itself is unusable.
    /// </summary>
    public sealed class RunScope : IDisposable
    {
        internal RunScope(string? path) { Path = path; }
        public string? Path { get; }
        public void Dispose()
        {
            Info("Run log closed.");
            lock (Gate) { if (_runFile == Path) _runFile = null; }
        }
    }

    /// <summary>Open a per-run log file. Never throws; on failure the scope is inert.</summary>
    public static RunScope BeginRun(string runName)
    {
        try
        {
            string? dir = LogDir;
            if (dir == null) return new RunScope(null);
            string safe = string.Concat((runName ?? "run").Split(System.IO.Path.GetInvalidFileNameChars(),
                StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (safe.Length == 0) safe = "run";
            if (safe.Length > 40) safe = safe.Substring(0, 40);
            string file = System.IO.Path.Combine(dir, $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.log");
            lock (Gate) { _runFile = file; }
            Info($"Run log opened: {runName}");
            return new RunScope(file);
        }
        catch
        {
            return new RunScope(null);
        }
    }

    private static string? LogDir
    {
        get
        {
            if (_dir != null) return _dir;
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _dir = Path.Combine(root, "Apex", "logs");
                Directory.CreateDirectory(_dir);
            }
            catch
            {
                _dir = null;
            }
            return _dir;
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            string? dir = LogDir;
            if (dir == null) return;
            string file = Path.Combine(dir, $"ApexBimStudio-{DateTime.Now:yyyyMMdd}.log");
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(file, line);
                if (_runFile != null)
                {
                    try { File.AppendAllText(_runFile, line); }
                    catch { /* run log lost, daily log still has the line */ }
                }
            }
        }
        catch
        {
            // Logging must never throw inside Revit.
        }
    }
}
