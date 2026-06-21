// Desktop OAuth (authorization-code + PKCE, loopback redirect) for BINA Cloud.
//
// Flow (PRD §5 — AI Usage Credits & Auth Gating):
//   1. addin starts a loopback listener on http://127.0.0.1:<random>/callback/
//   2. addin generates a PKCE verifier/challenge + random state
//   3. addin opens the system browser at the BINA web login:
//        {webBaseUrl}/login?redirect_uri=<loopback>&code_challenge=<c>
//                          &code_challenge_method=S256&state=<s>
//   4. user signs in on the web; the page calls bina-be
//        POST /api/auth/user/oauth/authorize and redirects back to the loopback
//        with ?code=<code>&state=<state>
//   5. addin verifies state, then exchanges the code at bina-be
//        POST /api/auth/user/oauth/token { code, codeVerifier, redirectUri }
//        -> { accessToken, refreshToken, accessTokenExpiry, userId }
//
// No password is ever typed into the plugin (public client + PKCE).

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BinaVibe.Auth
{
    public sealed class BinaTokenSet
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public long AccessTokenExpiry { get; set; }   // unix epoch SECONDS (bina-be `exp`)
        public int UserId { get; set; }
    }

    public sealed class BinaOAuthClient
    {
        private readonly string _webBaseUrl;   // BINA web app (login page), e.g. https://app.bina.cloud
        private readonly string _apiBaseUrl;   // bina-be API, e.g. https://api.bina.cloud
        private readonly HttpClient _http;

        public BinaOAuthClient(string webBaseUrl, string apiBaseUrl, HttpClient http = null)
        {
            _webBaseUrl = (webBaseUrl ?? "").TrimEnd('/');
            _apiBaseUrl = (apiBaseUrl ?? "").TrimEnd('/');
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        // ── Loopback browser flow ───────────────────────────────────────
        public async Task<BinaTokenSet> InteractiveLoginAsync(CancellationToken ct = default)
        {
            var (verifier, challenge) = PkcePair();
            int port = FindFreePort();
            string redirect = $"http://127.0.0.1:{port}/callback/";

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();

            string state = Guid.NewGuid().ToString("N");
            string loginUrl = $"{_webBaseUrl}/login"
                + $"?redirect_uri={Uri.EscapeDataString(redirect)}"
                + $"&code_challenge={challenge}"
                + "&code_challenge_method=S256"
                + $"&state={Uri.EscapeDataString(state)}";

            Process.Start(new ProcessStartInfo { FileName = loginUrl, UseShellExecute = true });

            HttpListenerContext ctx;
            using (ct.Register(() => { try { listener.Stop(); } catch { } }))
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }

            string query = ctx.Request.Url?.Query ?? "";
            string code = GetQueryValue(query, "code");
            string rxState = GetQueryValue(query, "state");

            WriteBrowserResponse(ctx, "Revit Copilot — signed in. You can return to Revit and close this tab.");
            listener.Stop();

            if (rxState != state) throw new InvalidOperationException("OAuth state mismatch — login aborted.");
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("OAuth code missing from redirect.");

            return await ExchangeCodeAsync(code, verifier, redirect, ct).ConfigureAwait(false);
        }

        private async Task<BinaTokenSet> ExchangeCodeAsync(string code, string verifier, string redirect, CancellationToken ct)
        {
            string payload = JsonConvert.SerializeObject(new { code, codeVerifier = verifier, redirectUri = redirect });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_apiBaseUrl}/api/auth/user/oauth/token", content, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token exchange failed (HTTP {(int)resp.StatusCode}): {body}");
            return Parse(body);
        }

        public async Task<BinaTokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            string payload = JsonConvert.SerializeObject(new { refreshToken });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_apiBaseUrl}/api/auth/user/oauth/refresh", content, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token refresh failed (HTTP {(int)resp.StatusCode}): {body}");
            return Parse(body);
        }

        // ── Helpers ─────────────────────────────────────────────────────
        private static BinaTokenSet Parse(string json)
        {
            var o = JObject.Parse(json);
            return new BinaTokenSet
            {
                AccessToken = (string)o["accessToken"] ?? "",
                RefreshToken = (string)o["refreshToken"] ?? "",
                AccessTokenExpiry = (long?)o["accessTokenExpiry"] ?? 0,
                UserId = (int?)o["userId"] ?? 0,
            };
        }

        private static (string verifier, string challenge) PkcePair()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string verifier = Base64UrlEncode(bytes);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            return (verifier, Base64UrlEncode(hash));
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Minimal query parser — avoids a System.Web dependency on net8.0-windows.
        private static string GetQueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (Uri.UnescapeDataString(pair.Substring(0, eq)) == key)
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return null;
        }

        private static void WriteBrowserResponse(HttpListenerContext ctx, string message)
        {
            try
            {
                string html = $"<html><body style='font-family:sans-serif'><h3>{message}</h3></body></html>";
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { /* best effort — the browser tab content is cosmetic */ }
        }

        private static int FindFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
