using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.BimStudio;

public class FamilySummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("family_name")] public string FamilyName { get; set; } = "";
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("revit_version")] public string? RevitVersion { get; set; }
}

public class FamilyList
{
    [JsonPropertyName("families")] public List<FamilySummary> Families { get; set; } = new List<FamilySummary>();
}

public class JobSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("entity_id")] public string? EntityId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("attempt")] public int Attempt { get; set; }
}

public class JobList
{
    [JsonPropertyName("jobs")] public List<JobSummary> Jobs { get; set; } = new List<JobSummary>();
}

/// <summary>Thrown when the Apex API returns a non-success status; carries the response body.</summary>
public class ApexApiException : Exception
{
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public ApexApiException(int statusCode, string reason, string responseBody)
        : base($"Apex API returned {statusCode} {reason}: {Truncate(responseBody)}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s.Substring(0, 300) + "…";
}

public class ApexApiClient
{
    private static readonly HttpClient Http = new HttpClient
    {
        // A hung request must never freeze Revit's UI thread for the default 100 s.
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _baseUrl;

    public string? AccessToken { get; set; }

    public ApexApiClient(string? baseUrl = null)
    {
        string url = baseUrl
            ?? Environment.GetEnvironmentVariable("APEX_API_URL")
            ?? "http://localhost:4000";
        _baseUrl = ValidateBaseUrl(url);
        // Stored OAuth token wins; APEX_API_TOKEN is the service-token fallback
        // (e.g. the Supabase publishable key for the hosted Apex API).
        AccessToken = TokenStore.Load()
            ?? Environment.GetEnvironmentVariable("APEX_API_TOKEN");
    }

    /// <summary>
    /// Bearer tokens must not travel over cleartext HTTP. Plain http is allowed only
    /// for loopback (local development); anything else must be https.
    /// </summary>
    private static string ValidateBaseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException($"APEX_API_URL is not a valid absolute URL: '{url}'");
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new ArgumentException(
                $"Refusing plain-HTTP Apex API endpoint '{url}': bearer tokens would be sent in cleartext. Use https:// (http is allowed for localhost only).");
        return url.TrimEnd('/');
    }

    private string FamilyUrl(string id, string suffix = "")
        => _baseUrl + "/v1/families/" + Uri.EscapeDataString(id) + suffix;

    public async Task<List<FamilySummary>> ListFamiliesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/v1/families");
        string body = await SendAsync(req, ct).ConfigureAwait(false);
        FamilyList? list = JsonSerializer.Deserialize<FamilyList>(body, Json);
        return list?.Families ?? new List<FamilySummary>();
    }

    public async Task<AfisObject?> GetFamilyAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, FamilyUrl(id));
        string body = await SendAsync(req, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AfisObject>(body, Json);
    }

    public async Task<string> GenerateRfaAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, FamilyUrl(id, "/generate-rfa"));
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<string> ValidateAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, FamilyUrl(id, "/validate"));
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<string> ExportPointsAsync(string id, string format, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, FamilyUrl(id, "/exports"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { format }, Json),
                Encoding.UTF8,
                "application/json"),
        };
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    // ----- jobs queue (Doc 3 Stage 11: the plugin is the generate_rfa worker) -----

    private string JobUrl(string id, string suffix = "")
        => _baseUrl + "/v1/jobs/" + Uri.EscapeDataString(id) + suffix;

    public async Task<List<JobSummary>> ListJobsAsync(string kind, string status = "queued",
        CancellationToken ct = default)
    {
        string url = _baseUrl + "/v1/jobs?kind=" + Uri.EscapeDataString(kind)
            + "&status=" + Uri.EscapeDataString(status);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        string body = await SendAsync(req, ct).ConfigureAwait(false);
        JobList? list = JsonSerializer.Deserialize<JobList>(body, Json);
        return list?.Jobs ?? new List<JobSummary>();
    }

    /// <summary>Claims a queued job; returns null when another worker got there first (409).</summary>
    public async Task<JobSummary?> ClaimJobAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, JobUrl(id, "/claim"));
        try
        {
            string body = await SendAsync(req, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JobSummary>(body, Json);
        }
        catch (ApexApiException ex) when (ex.StatusCode == 409)
        {
            return null;
        }
    }

    public async Task CompleteJobAsync(string id, bool succeeded, string? error = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, JobUrl(id, "/complete"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { status = succeeded ? "succeeded" : "failed", error }, Json),
                Encoding.UTF8,
                "application/json"),
        };
        await SendAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Authorize(req);
        HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            ApexLog.Warn($"{req.Method} {req.RequestUri} -> {(int)resp.StatusCode}: {body}");
            throw new ApexApiException((int)resp.StatusCode, resp.ReasonPhrase ?? "", body);
        }
        return body;
    }

    private void Authorize(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(AccessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
    }

    /// <summary>
    /// Bridge for Revit's synchronous IExternalCommand context. Runs the task on the
    /// thread pool (avoiding sync-context deadlocks) and enforces an upper wait bound
    /// so a stuck request cannot freeze the Revit UI indefinitely.
    /// </summary>
    public static T RunSync<T>(Func<CancellationToken, Task<T>> action, int timeoutSeconds = 45)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return Task.Run(() => action(cts.Token)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The Apex API did not respond within {timeoutSeconds} seconds.");
        }
    }
}
