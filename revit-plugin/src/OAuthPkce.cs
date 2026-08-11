using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Apex.BimStudio;

/// <summary>
/// OAuth 2.0 Authorization Code + PKCE flow using the system browser and a
/// loopback redirect (RFC 8252). Configured via environment variables:
///   APEX_AUTH_URL   - authorization endpoint (e.g. https://auth.apex.example/authorize)
///   APEX_TOKEN_URL  - token endpoint        (e.g. https://auth.apex.example/oauth/token)
///   APEX_CLIENT_ID  - public client id
/// The resulting access token is persisted via <see cref="TokenStore"/> (DPAPI).
/// </summary>
public static class OAuthPkce
{
    private const int CallbackTimeoutSeconds = 180;

    public static bool IsConfigured =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APEX_AUTH_URL")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APEX_TOKEN_URL")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APEX_CLIENT_ID"));

    /// <summary>Runs the interactive sign-in. Returns the access token, or throws.</summary>
    public static async Task<string> SignInAsync()
    {
        string authUrl = RequireEnv("APEX_AUTH_URL");
        string tokenUrl = RequireEnv("APEX_TOKEN_URL");
        string clientId = RequireEnv("APEX_CLIENT_ID");

        string verifier = Base64Url(RandomBytes(32));
        string challenge;
        using (var sha = SHA256.Create())
            challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomBytes(16));

        int port = FreeTcpPort();
        string redirectUri = $"http://127.0.0.1:{port}/callback/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        string url = authUrl
            + "?response_type=code"
            + "&client_id=" + Uri.EscapeDataString(clientId)
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&code_challenge=" + challenge
            + "&code_challenge_method=S256"
            + "&state=" + state
            + "&scope=" + Uri.EscapeDataString("openid offline_access apex.api");

        ApexLog.Info("Opening browser for Apex sign-in.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        Task<HttpListenerContext> ctxTask = listener.GetContextAsync();
        Task done = await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromSeconds(CallbackTimeoutSeconds))).ConfigureAwait(false);
        if (done != ctxTask)
            throw new TimeoutException("Sign-in was not completed in the browser (timed out).");

        HttpListenerContext ctx = ctxTask.Result;
        string? code = ctx.Request.QueryString["code"];
        string? gotState = ctx.Request.QueryString["state"];
        string? error = ctx.Request.QueryString["error"];

        string html = error == null
            ? "<html><body><h2>Signed in to Apex.</h2>You can close this tab and return to Revit.</body></html>"
            : $"<html><body><h2>Sign-in failed.</h2>{WebUtility.HtmlEncode(error)}</body></html>";
        byte[] buf = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html";
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();

        if (error != null) throw new InvalidOperationException("Authorization server returned: " + error);
        if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("No authorization code received.");
        if (gotState != state) throw new InvalidOperationException("OAuth state mismatch — possible CSRF; aborting.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var form = new FormUrlEncodedContent(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "authorization_code"),
            new System.Collections.Generic.KeyValuePair<string, string>("code", code!),
            new System.Collections.Generic.KeyValuePair<string, string>("redirect_uri", redirectUri),
            new System.Collections.Generic.KeyValuePair<string, string>("client_id", clientId),
            new System.Collections.Generic.KeyValuePair<string, string>("code_verifier", verifier),
        });
        HttpResponseMessage resp = await http.PostAsync(tokenUrl, form).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ApexApiException((int)resp.StatusCode, resp.ReasonPhrase ?? "", body);

        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out JsonElement tokenEl))
            throw new InvalidOperationException("Token endpoint response contained no access_token.");
        string token = tokenEl.GetString() ?? throw new InvalidOperationException("access_token was null.");

        TokenStore.Save(token);
        ApexLog.Info("Apex sign-in completed; token stored (DPAPI).");
        return token;
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Environment variable {name} is not set.");

    private static byte[] RandomBytes(int count)
    {
        byte[] bytes = new byte[count];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return bytes;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
