// Desktop OAuth (authorization-code + PKCE, loopback redirect) against BINA Cloud.
//
// Identity is bina-ai (the plugin's IdP). Flow:
//   1. addin starts a loopback listener on http://127.0.0.1:<random>/callback/
//   2. addin generates a PKCE verifier/challenge + random state
//   3. addin opens the system browser at the landing page (path configurable,
//      defaults to /login/ for returning users — /signup/ only registers NEW
//      accounts and hangs the loopback for an existing email):
//        {webBaseUrl}{landingPath}?redirect_uri=<loopback>&code_challenge=<c>
//                          &code_challenge_method=S256&state=<s>&api=<bina-ai>
//   4. the page logs in/registers against bina-ai, gets a one-time `code`, and
//      redirects to the loopback with ?code=<code>&state=<state>
//   5. addin verifies state, then exchanges the code at bina-ai
//        POST {aiBaseUrl}/auth/token { code, code_verifier }
//        -> { access_token, refresh_token, access_token_expiry, user:{id,name} }
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
        public long AccessTokenExpiry { get; set; }   // unix epoch SECONDS
        public int UserId { get; set; }
        public string UserName { get; set; }
    }

    public sealed class BinaOAuthClient
    {
        private readonly string _webBaseUrl;   // landing page, e.g. https://revit.bina.cloud
        private readonly string _aiBaseUrl;    // bina-ai API (issues tokens), e.g. ngrok/staging
        private readonly string _landingPath;  // sign-in page under _webBaseUrl, e.g. "/login/"
        private readonly HttpClient _http;

        public BinaOAuthClient(string webBaseUrl, string aiBaseUrl, string landingPath = "/login/", HttpClient http = null)
        {
            _webBaseUrl = (webBaseUrl ?? "").TrimEnd('/');
            _aiBaseUrl = (aiBaseUrl ?? "").TrimEnd('/');
            _landingPath = NormalizePath(landingPath);
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        // Ensure the configured path is "/x/" — a leading slash so it appends to the
        // origin, and a trailing slash so static hosting serves the page (and the
        // "?query" lands cleanly) instead of 404-ing on "/login?...".
        private static string NormalizePath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "/login/";
            p = p.Trim();
            if (!p.StartsWith("/")) p = "/" + p;
            if (!p.EndsWith("/")) p += "/";
            return p;
        }

        // Hard cap on how long the loopback wait may block. The caller runs this on
        // Revit's UI thread via .GetResult(), so without a timeout a browser that
        // never redirects back (wrong/undeployed login page, connection refused)
        // would freeze Revit FOREVER with no recovery. 120s is enough to finish a
        // real sign-in; on expiry we stop the listener and throw so the command
        // shows a friendly error and Revit becomes responsive again.
        private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(120);

        // ── Loopback browser flow ───────────────────────────────────────
        public Task<BinaTokenSet> InteractiveLoginAsync(CancellationToken ct = default)
            => InteractiveLoginAsync(LoginTimeout, ct);

        public async Task<BinaTokenSet> InteractiveLoginAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            var (verifier, challenge) = PkcePair();
            int port = FindFreePort();
            string redirect = $"http://127.0.0.1:{port}/callback/";

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();

            string state = Guid.NewGuid().ToString("N");
            // Pass &api so the (static) landing page knows which bina-ai to call —
            // handy when bina-ai is a local ngrok tunnel during testing.
            string loginUrl = $"{_webBaseUrl}{_landingPath}"
                + $"?redirect_uri={Uri.EscapeDataString(redirect)}"
                + $"&code_challenge={challenge}"
                + "&code_challenge_method=S256"
                + $"&state={Uri.EscapeDataString(state)}"
                + $"&api={Uri.EscapeDataString(_aiBaseUrl)}";

            Process.Start(new ProcessStartInfo { FileName = loginUrl, UseShellExecute = true });

            HttpListenerContext ctx;
            using (ct.Register(() => { try { listener.Stop(); } catch { } }))
            {
                // Race the callback against a timeout so the UI thread can never hang
                // indefinitely. WhenAny returns whichever finishes first.
                var contextTask = listener.GetContextAsync();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delayTask = Task.Delay(timeout, timeoutCts.Token);
                var winner = await Task.WhenAny(contextTask, delayTask).ConfigureAwait(false);
                if (winner != contextTask)
                {
                    try { listener.Stop(); } catch { }
                    throw new TimeoutException(
                        $"Browser sign-in timed out after {timeout.TotalSeconds:F0}s. " +
                        "Finish the login in your browser, then click Login again. " +
                        "If the sign-in page didn't load, the site may be unreachable.");
                }
                timeoutCts.Cancel();          // cancel the pending delay
                ctx = contextTask.Result;     // already completed
            }

            string query = ctx.Request.Url?.Query ?? "";
            string code = GetQueryValue(query, "code");
            string rxState = GetQueryValue(query, "state");

            WriteBrowserResponse(ctx, "Revit Copilot — signed in. You can return to Revit and close this tab.");
            listener.Stop();

            if (rxState != state) throw new InvalidOperationException("OAuth state mismatch — login aborted.");
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("OAuth code missing from redirect.");

            return await ExchangeCodeAsync(code, verifier, ct).ConfigureAwait(false);
        }

        private async Task<BinaTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
        {
            string payload = JsonConvert.SerializeObject(new { code, code_verifier = verifier });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_aiBaseUrl}/auth/token", content, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token exchange failed (HTTP {(int)resp.StatusCode}): {body}");
            return Parse(body);
        }

        public async Task<BinaTokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            string payload = JsonConvert.SerializeObject(new { refresh_token = refreshToken });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_aiBaseUrl}/auth/refresh", content, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token refresh failed (HTTP {(int)resp.StatusCode}): {body}");
            return Parse(body);
        }

        // GET /auth/me — real display name (already included in the token response
        // too, but exposed for callers that only hold an access token).
        public async Task<string> GetDisplayNameAsync(string accessToken, CancellationToken ct = default)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{_aiBaseUrl}/auth/me");
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var o = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                var name = (string)o["name"];
                var email = (string)o["email"];
                if (!string.IsNullOrWhiteSpace(name)) return name;
                return string.IsNullOrWhiteSpace(email) ? null : email;
            }
            catch
            {
                return null;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────
        private static BinaTokenSet Parse(string json)
        {
            var o = JObject.Parse(json);
            var user = o["user"] as JObject;
            return new BinaTokenSet
            {
                AccessToken = (string)o["access_token"] ?? "",
                RefreshToken = (string)o["refresh_token"] ?? "",
                AccessTokenExpiry = (long?)o["access_token_expiry"] ?? 0,
                UserId = user != null ? ((int?)user["id"] ?? 0) : 0,
                UserName = user != null ? (string)user["name"] : null,
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
