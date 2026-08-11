using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Apex.BimStudio.Commands;

/// <summary>
/// The plugin side of the generate_rfa job queue (Doc 3 Stage 11). RFA files can
/// only be produced inside a running Revit, so the cloud API enqueues jobs and this
/// command drains them: claim → fetch AFIS → build → save .rfa → complete.
/// Claim/complete are atomic on the server (queued→running→succeeded/failed), so
/// several machines can run this concurrently without double-building a job.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ProcessQueueCommand : IExternalCommand
{
    private const int MaxJobsPerRun = 5;

    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            Autodesk.Revit.ApplicationServices.Application app = c.Application.Application;

            List<JobSummary> queued = ApexApiClient.RunSync(ct =>
                Session.Api.ListJobsAsync("generate_rfa", "queued", ct));
            if (queued.Count == 0)
            {
                TaskDialog.Show("Apex Queue", "No queued RFA-generation jobs.");
                return Result.Succeeded;
            }

            string outDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", "rfa");
            System.IO.Directory.CreateDirectory(outDir);

            var report = new List<string>();
            int built = 0, failed = 0, skipped = 0;

            foreach (JobSummary job in queued.Take(MaxJobsPerRun))
            {
                JobSummary? claimed = ApexApiClient.RunSync(ct => Session.Api.ClaimJobAsync(job.Id, ct));
                if (claimed == null)
                {
                    skipped++; // another worker claimed it between list and claim
                    continue;
                }

                try
                {
                    string rfaPath = BuildRfa(app, claimed, outDir);
                    ApexApiClient.RunSync<object?>(async ct =>
                    {
                        await Session.Api.CompleteJobAsync(claimed.Id, succeeded: true, ct: ct);
                        return null;
                    });
                    built++;
                    report.Add($"OK    {claimed.Id.Substring(0, 8)}  →  {rfaPath}");
                }
                catch (Exception ex)
                {
                    ApexLog.Error($"Job {claimed.Id} failed.", ex);
                    try
                    {
                        ApexApiClient.RunSync<object?>(async ct =>
                        {
                            await Session.Api.CompleteJobAsync(claimed.Id, succeeded: false, error: ex.Message, ct: ct);
                            return null;
                        });
                    }
                    catch (Exception completeEx)
                    {
                        // The job stays 'running' server-side; surface both errors in the log.
                        ApexLog.Error($"Could not mark job {claimed.Id} failed.", completeEx);
                    }
                    failed++;
                    report.Add($"FAIL  {claimed.Id.Substring(0, 8)}  {ex.Message}");
                }
            }

            string summary = $"Processed {built + failed} job(s): {built} built, {failed} failed"
                + (skipped > 0 ? $", {skipped} taken by another worker" : "")
                + (queued.Count > MaxJobsPerRun ? $". {queued.Count - MaxJobsPerRun} still queued — run again." : ".");
            ApexLog.Info("Process Queue: " + summary);
            TaskDialog.Show("Apex Queue (Doc 3 Stage 11)",
                summary + (report.Count > 0 ? "\n\n" + string.Join("\n", report) : ""));
            return failed == 0 ? Result.Succeeded : Result.Failed;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Process Queue failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }

    /// <summary>Fetches the job's AFIS document and builds it into a saved .rfa.</summary>
    private static string BuildRfa(Autodesk.Revit.ApplicationServices.Application app,
        JobSummary job, string outDir)
    {
        if (string.IsNullOrEmpty(job.EntityId))
            throw new InvalidOperationException("Job has no entity_id (family) to build.");

        AfisObject obj = ApexApiClient.RunSync(ct => Session.Api.GetFamilyAsync(job.EntityId!, ct))
            ?? throw new InvalidOperationException($"Family {job.EntityId} has no AFIS document.");

        string? templatePath = BuildFromPredJsonCommand.ResolveTemplate(app, obj.Identity.FamilyTemplate);
        if (templatePath == null)
            throw new InvalidOperationException(
                $"No family template found for '{obj.Identity.FamilyTemplate ?? "default"}'. " +
                "Check Revit's Family Template File location.");

        string rfaPath = System.IO.Path.Combine(outDir,
            BuildFromPredJsonCommand.SafeFileName(obj.Identity.Name, obj.Id) + ".rfa");

        Document? famDoc = null;
        try
        {
            famDoc = app.NewFamilyDocument(templatePath);
            AfisRevitMapper.Apply(famDoc, obj);
            famDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
        }
        finally
        {
            try
            {
                famDoc?.Close(false);
            }
            catch
            {
                // Already closed; nothing to release.
            }
        }
        return rfaPath;
    }
}
