using System;
using System.IO;

namespace Apex.BimStudio;

/// <summary>
/// Minimal rolling file logger. Writes to %LOCALAPPDATA%\Apex\logs\ApexBimStudio-yyyyMMdd.log.
/// Never throws: a broken log path must not take Revit down with it.
/// </summary>
public static class ApexLog
{
    private static readonly object Gate = new object();
    private static string? _dir;

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
            }
        }
        catch
        {
            // Logging must never throw inside Revit.
        }
    }
}
