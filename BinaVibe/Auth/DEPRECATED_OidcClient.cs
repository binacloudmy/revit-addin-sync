// DEPRECATED (2026-06-04): superseded by McpTunnelClient (WSS) + BinaConfig. 0 references. Kept for history; safe to delete.
// OIDC PKCE flow for BINA Cloud SSO (PRD §10.9 FR-AUTH-01/03).
//
// First launch:
//   1. addin opens system browser → BINA Cloud /authorize?...&code_challenge=X
//   2. user logs in, /callback redirects to http://localhost:<random>/?code=Y
//      (loopback redirect is standard for desktop apps + PKCE)
//   3. addin's tiny loopback listener catches the code, exchanges for
//      access+refresh tokens at /token
//   4. refresh token stored in Windows Credential Manager
//   5. access token attached as Bearer on every bina-ai call AND in the
//      WSS tunnel URL query.
//
// Subsequent launches:
//   - refresh token retrieved from Credential Manager
//   - silently refreshed at startup; access token cached in-memory
//
// **Awaiting your BINA Cloud OIDC client_id / discovery URL.** Defaults
// below are placeholders — wire real values via env or BinaConfig.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace BinaVibe.Auth
{
    public sealed class OidcConfig
    {
        public string Authority { get; init; } = "https://auth.bina.cloud";  // placeholder
        public string ClientId { get; init; } = "bina-vibe-revit";           // placeholder
        public string Scope { get; init; } = "openid profile email offline_access bina-vibe";
        public string Audience { get; init; } = "bina-vibe-api";
    }

    public sealed class TokenSet
    {
        public string AccessToken { get; init; } = "";
        public string? RefreshToken { get; init; }
        public string? IdToken { get; init; }
        public DateTime AccessTokenExpiresAt { get; init; }
        public string? Subject { get; init; }
    }

    public sealed class OidcClient
    {
        private readonly OidcConfig _cfg;
        private readonly HttpClient _http;

        public OidcClient(OidcConfig cfg, HttpClient? http = null)
        {
            _cfg = cfg;
            _http = http ?? new HttpClient();
        }

        // ── PKCE helpers ────────────────────────────────────────────────

        private static (string verifier, string challenge) PkcePair()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var verifier = Base64UrlEncode(bytes);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            return (verifier, Base64UrlEncode(hash));
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // ── Loopback browser flow ───────────────────────────────────────

        public async Task<TokenSet> InteractiveLoginAsync()
        {
            var (verifier, challenge) = PkcePair();
            var port = FindFreePort();
            var redirect = $"http://localhost:{port}/";

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();

            var state = Guid.NewGuid().ToString("N");
            var url = $"{_cfg.Authority}/oauth/authorize"
                   + $"?response_type=code"
                   + $"&client_id={Uri.EscapeDataString(_cfg.ClientId)}"
                   + $"&redirect_uri={Uri.EscapeDataString(redirect)}"
                   + $"&scope={Uri.EscapeDataString(_cfg.Scope)}"
                   + $"&audience={Uri.EscapeDataString(_cfg.Audience)}"
                   + $"&code_challenge={challenge}"
                   + $"&code_challenge_method=S256"
                   + $"&state={state}";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });

            var ctx = await listener.GetContextAsync().ConfigureAwait(false);
            var q = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
            var code = q["code"];
            var rxState = q["state"];

            var html = "<html><body><h3>Revit Copilot — signed in.</h3>You can close this tab.</body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
            listener.Stop();

            if (rxState != state) throw new InvalidOperationException("OIDC state mismatch");
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("OIDC code missing");

            return await ExchangeCodeAsync(code, verifier, redirect).ConfigureAwait(false);
        }

        private async Task<TokenSet> ExchangeCodeAsync(string code, string verifier, string redirect)
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = _cfg.ClientId,
                ["code"] = code,
                ["redirect_uri"] = redirect,
                ["code_verifier"] = verifier,
            });

            using var resp = await _http.PostAsync($"{_cfg.Authority}/oauth/token", body).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseTokenResponse(json);
        }

        public async Task<TokenSet> RefreshAsync(string refreshToken)
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _cfg.ClientId,
                ["refresh_token"] = refreshToken,
            });
            using var resp = await _http.PostAsync($"{_cfg.Authority}/oauth/token", body).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseTokenResponse(json);
        }

        private static TokenSet ParseTokenResponse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var expiresIn = root.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var sec) ? sec : 3600;
            return new TokenSet
            {
                AccessToken = root.GetProperty("access_token").GetString() ?? "",
                RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                IdToken = root.TryGetProperty("id_token", out var it) ? it.GetString() : null,
                AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 30),  // 30s buffer
                Subject = root.TryGetProperty("sub", out var sb) ? sb.GetString() : null,
            };
        }

        private static int FindFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public static AuthenticationHeaderValue Bearer(string accessToken) =>
            new("Bearer", accessToken);
    }
}