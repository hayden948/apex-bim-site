using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Apex.BimStudio.Commands;

/// <summary>
/// Workstation setup without env vars: shows the effective API URL and token
/// state, and takes new values from the clipboard (TaskDialog has no text input,
/// and the console's "Mint plugin token" flow puts the apx_ token on the
/// clipboard anyway). URL goes to config.json; the token is DPAPI-encrypted.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
    {
        try
        {
            ApexConfig cfg = ApexConfig.Load();
            string effectiveUrl = cfg.ApiUrl
                ?? Environment.GetEnvironmentVariable("APEX_API_URL")
                ?? "http://localhost:4000 (dev default)";
            string? stored = TokenStore.Load();
            string tokenState = stored != null
                ? $"stored (DPAPI): {SecretText.Describe(stored)}"
                : Environment.GetEnvironmentVariable("APEX_API_TOKEN") is { Length: > 0 } envTok
                    ? $"from APEX_API_TOKEN env: {SecretText.Describe(envTok)}"
                    : "none — Sync will fail until one is set";

            var dlg = new TaskDialog("Apex Settings")
            {
                MainInstruction = "Apex API connection",
                MainContent = $"API URL:  {effectiveUrl}\nToken:  {tokenState}",
                CommonButtons = TaskDialogCommonButtons.Close,
                FooterText = "Mint a token in the Apex web console (Pipeline Console → Mint plugin token), copy it, then use the first option.",
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Save token from clipboard", "Stores the copied apx_… token DPAPI-encrypted for this Windows user");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Set API URL from clipboard", "Stores the copied https:// URL in %LOCALAPPDATA%\\Apex\\config.json");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
                "Clear stored token", "Removes the DPAPI-stored token (env APEX_API_TOKEN still applies)");

            switch (dlg.Show())
            {
                case TaskDialogResult.CommandLink1:
                {
                    string text = ClipboardText();
                    if (text.StartsWith("sb_secret_", StringComparison.Ordinal))
                    {
                        // The Supabase secret key grants full admin access to the
                        // database — it must never be used (or stored) as a bearer token.
                        TaskDialog.Show("Apex Settings",
                            "That is the Supabase SECRET key — never use it as the plugin token.\n\n" +
                            "Mint an apx_… token in the Apex web console instead, and consider " +
                            "rotating the secret key since it was on the clipboard.");
                        return Result.Cancelled;
                    }
                    if (!text.StartsWith("apx_", StringComparison.Ordinal) &&
                        !text.StartsWith("sb_publishable_", StringComparison.Ordinal))
                    {
                        TaskDialog.Show("Apex Settings",
                            "The clipboard does not hold an Apex token (expected it to start with 'apx_').");
                        return Result.Cancelled;
                    }
                    TokenStore.Save(text);
                    Session.ReloadApi();
                    TaskDialog.Show("Apex Settings", $"Token saved: {SecretText.Describe(text)}. The plugin will use it immediately.");
                    return Result.Succeeded;
                }
                case TaskDialogResult.CommandLink2:
                {
                    string text = ClipboardText();
                    if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
                        (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
                    {
                        TaskDialog.Show("Apex Settings",
                            "The clipboard does not hold a usable API URL (must be https://, or http on localhost).");
                        return Result.Cancelled;
                    }
                    cfg.ApiUrl = text.TrimEnd('/');
                    cfg.Save();
                    Session.ReloadApi();
                    TaskDialog.Show("Apex Settings", $"API URL saved:\n{cfg.ApiUrl}");
                    return Result.Succeeded;
                }
                case TaskDialogResult.CommandLink3:
                    TokenStore.Clear();
                    Session.ReloadApi();
                    TaskDialog.Show("Apex Settings", "Stored token cleared.");
                    return Result.Succeeded;
                default:
                    return Result.Cancelled;
            }
        }
        catch (Exception ex)
        {
            ApexLog.Error("Settings command failed.", ex);
            m = ex.Message;
            return Result.Failed;
        }
    }

    private static string ClipboardText()
    {
        try
        {
            return (System.Windows.Clipboard.GetText() ?? "").Trim();
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Clipboard read failed: " + ex.Message);
            return "";
        }
    }

}
