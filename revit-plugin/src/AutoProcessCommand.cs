using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Apex.BimStudio.Commands;

/// <summary>
/// The plugin half of the autonomous pipeline: while any Revit session sits
/// open with this enabled, the cloud queue drains itself — no clicking. The
/// interval lives in config.json (auto_process_minutes; 0 = off), so the
/// setting survives restarts and can be pre-provisioned on a worker machine.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AutoProcessCommand : IExternalCommand
{
    private const int DefaultMinutes = 5;

    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            ApexConfig cfg = ApexConfig.Load();
            bool turningOn = cfg.AutoProcessMinutes <= 0;
            cfg.AutoProcessMinutes = turningOn ? DefaultMinutes : 0;
            cfg.Save();
            AutoProcessLoop.Reset();

            TaskDialog.Show("Apex Auto Process", turningOn
                ? $"Auto Process is ON: this Revit session now drains the RFA queue every {DefaultMinutes} minutes while it is open.\n\n" +
                  "Leave Revit running (an empty project is fine). Results are logged to %LOCALAPPDATA%\\Apex\\logs; finished families upload back to the library automatically."
                : "Auto Process is OFF. Use Generate → Process Queue to drain jobs manually.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Auto Process toggle failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }
}

/// <summary>
/// Idling-event pump for Auto Process. Revit raises Idling on the UI thread
/// (full API context), so building families here is legal; the work itself is
/// throttled to the configured interval and never shows dialogs — a worker
/// machine just logs.
/// </summary>
public static class AutoProcessLoop
{
    private static DateTime _nextRun = DateTime.MinValue;
    private static bool _running;

    /// <summary>Re-read config on the next idle tick (after the toggle flips it).</summary>
    public static void Reset() => _nextRun = DateTime.MinValue;

    public static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (_running || DateTime.UtcNow < _nextRun) return;
        try
        {
            _running = true;
            ApexConfig cfg = ApexConfig.Load();
            int minutes = cfg.AutoProcessMinutes;
            if (minutes <= 0)
            {
                // Off: check again in a minute rather than reading config every idle event.
                _nextRun = DateTime.UtcNow.AddMinutes(1);
                return;
            }
            _nextRun = DateTime.UtcNow.AddMinutes(minutes);

            if (sender is not UIApplication uiApp) return;
            ProcessQueueCommand.DrainResult r = ProcessQueueCommand.Drain(uiApp.Application);
            if (r.Queued > 0)
                ApexLog.Info($"Auto Process: {r.Summary}");
        }
        catch (Exception ex)
        {
            // Never let the worker take down the Idling pump; try again next interval.
            ApexLog.Error("Auto Process pass failed.", ex);
        }
        finally
        {
            _running = false;
        }
    }
}
