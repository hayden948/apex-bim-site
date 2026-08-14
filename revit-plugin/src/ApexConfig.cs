using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apex.BimStudio;

/// <summary>
/// Workstation configuration at %LOCALAPPDATA%\Apex\config.json. Values here win
/// over environment variables so the Settings command can point a machine at the
/// right API without editing system env vars or restarting Revit. The bearer
/// token is NOT kept here — it lives DPAPI-encrypted in TokenStore.
/// </summary>
public class ApexConfig
{
    [JsonPropertyName("api_url")] public string? ApiUrl { get; set; }

    /// <summary>
    /// Minutes between automatic queue drains while Revit sits open (the
    /// Idling-loop worker). 0 = off; toggled by the Auto Process command.
    /// </summary>
    [JsonPropertyName("auto_process_minutes")] public int AutoProcessMinutes { get; set; }

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", "config.json");

    public static ApexConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<ApexConfig>(File.ReadAllText(ConfigPath)) ?? new ApexConfig();
        }
        catch (Exception ex)
        {
            ApexLog.Warn("Could not read config.json; using defaults: " + ex.Message);
        }
        return new ApexConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Safe-to-display form of a secret: prefix + length, never the value.</summary>
public static class SecretText
{
    public static string Describe(string token)
    {
        if (string.IsNullOrEmpty(token)) return "(empty)";
        int keep = Math.Min(8, token.Length);
        return token.Substring(0, keep) + "… (" + token.Length + " chars)";
    }
}
