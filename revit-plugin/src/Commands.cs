using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Apex.BimStudio.Commands;

internal static class Session
{
    public static readonly ApexApiClient Api = new ApexApiClient();

    /// <summary>Set when a family is selected/synced from the Apex library. Never defaulted.</summary>
    public static string? ActiveFamilyId;

    /// <summary>
    /// Commands that need an active family must fail with a clear message instead of
    /// silently using a placeholder id (the old build fell back to a hardcoded demo GUID).
    /// </summary>
    public static string RequireActiveFamilyId()
    {
        return ActiveFamilyId
            ?? throw new InvalidOperationException(
                "No active Apex family selected. Use Library/Sync to choose a family first.");
    }
}

[Transaction(TransactionMode.Manual)]
public class SignInCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            if (!OAuthPkce.IsConfigured)
            {
                TaskDialog.Show("Apex Sign In",
                    "Sign-in is not configured on this machine.\n\n" +
                    "Set APEX_AUTH_URL, APEX_TOKEN_URL and APEX_CLIENT_ID, then try again.");
                return Result.Cancelled;
            }

            string token = ApexApiClient.RunSync(_ => OAuthPkce.SignInAsync(), timeoutSeconds: 240);
            Session.Api.AccessToken = token;
            TaskDialog.Show("Apex", "Signed in to Apex. Token stored securely (Windows DPAPI).");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Sign-in failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public class SyncCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "Library sync is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class GenerateFromSubmittalCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "Submittal-to-family generation is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class GenerateFromLibraryCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            Document? doc = c.Application.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsFamilyDocument)
            {
                TaskDialog.Show("Apex", "Open a family document (Family Editor) to build from AFIS.");
                return Result.Cancelled;
            }

            string id = Session.RequireActiveFamilyId();
            AfisObject? obj = ApexApiClient.RunSync(ct => Session.Api.GetFamilyAsync(id, ct));
            if (obj == null)
            {
                m = "Family not found";
                return Result.Failed;
            }

            AfisRevitMapper.Apply(doc, obj);
            TaskDialog.Show("Apex", "Built '" + obj.Identity.Name + "' from AFIS into the family.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Generate-from-library failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public class OpenLibraryCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "The Apex library panel is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class PlaceFamilyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "Family placement is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class RunQaCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            string id = Session.RequireActiveFamilyId();
            string result = ApexApiClient.RunSync(ct => Session.Api.ValidateAsync(id, ct));
            TaskDialog.Show("Apex QA (Doc 8)", result);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("QA validation failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public class VerifyClearancesCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "Clearance clash-checking is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class ManagePointsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "Point management is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class ExportLayoutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            string id = Session.RequireActiveFamilyId();
            string csv = ApexApiClient.RunSync(ct => Session.Api.ExportPointsAsync(id, "csv", ct));
            TaskDialog.Show("Apex Layout Export (Doc 7)", csv);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            ApexLog.Error("Layout export failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public class GenerateScheduleCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        TaskDialog.Show("Apex", "Schedule generation is not available in this build yet.");
        return Result.Cancelled;
    }
}

[Transaction(TransactionMode.Manual)]
public class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        string version = typeof(AboutCommand).Assembly.GetName().Version?.ToString(3) ?? "?";
        string revit = c.Application.Application.VersionNumber;
        TaskDialog.Show("Apex BIM Studio",
            $"Apex BIM Studio plugin v{version} — running in Revit {revit}. (Doc 4)");
        return Result.Succeeded;
    }
}
