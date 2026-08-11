using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.BimStudio;

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
        AccessToken = TokenStore.Load();
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
