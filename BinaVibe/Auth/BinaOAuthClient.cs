// Desktop OAuth (authorization-code + PKCE, loopback redirect) against BINA Cloud.
//
// Identity is bina-ai (the plugin's IdP). Flow:
//   1. addin starts a loopback listener on http://127.0.0.1:<random>/callback/
//   2. addin generates a PKCE verifier/challenge + random state
//   3. addin opens the system browser at the landing page:
//        {webBaseUrl}/login/?redirect_uri=<loopback>&code_challenge=<c>
//                          &code_challenge_method=S256&state=<s>&api=<bina-ai>
//   4. the page logs in/registers against bina-ai, gets a one-time `code`, and
//      redirects to the loopback with ?code=<code>&state=<state>
//   5. addin verifies state, then exchanges the code at bina-ai
//        POST {aiBaseUrl}/auth/token { code, code_verifier }
//        -> { access_token, refresh_token, access_token_expiry, user:{id,name} }
//      and only then answers the browser tab (signed in, or what failed).
//
// For bina-be (BinaOAuthEndpoints.BinaBe) the command first probes the token
// route (CheckSignInAvailableAsync), so a server without it fails fast.
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

    /// <summary>
    /// Which identity provider this client speaks to. The loopback/PKCE dance is
    /// identical; only the paths, the redirect_uri rule and the login-page shape
    /// differ, so both providers share one implementation.
    /// </summary>
    public sealed class BinaOAuthEndpoints
    {
        public string LoginPath { get; set; } = "/login/";
        public string TokenPath { get; set; } = "/auth/token";
        public string RefreshPath { get; set; } = "/auth/refresh";
        public string MePath { get; set; } = "/auth/me";

        /// <summary>
        /// Pass the token-issuing host to the landing page as &amp;api=. The bina-ai
        /// landing page is static and needs telling; the bina-web bridge uses its
        /// own NEXT_PUBLIC_API_URL and ignores the parameter.
        /// </summary>
        public bool SendApiHint { get; set; } = true;

        /// <summary>
        /// Send redirect_uri on the token exchange. bina-be binds the code to it
        /// (RFC 6749 §4.1.3) and rejects an exchange that omits it; bina-ai's
        /// FastAPI model may reject the unknown field, so it stays off there.
        /// </summary>
        public bool SendRedirectUri { get; set; }

        /// <summary>
        /// How long the loopback listener waits for the browser to come back.
        /// The cap exists so a login page that never redirects cannot leave a
        /// listener (and the caller's wait) open forever.
        /// </summary>
        public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Probe the token endpoint before opening the browser, so a server that
        /// has no desktop sign-in route (404) fails in seconds instead of after
        /// the whole LoginTimeout. Off for bina-ai, whose /auth/token has always
        /// been deployed alongside its landing page.
        /// </summary>
        public bool PreflightTokenProbe { get; set; }

        /// <summary>bina-ai: /auth/token, snake_case, &amp;api= hint, no redirect_uri.</summary>
        public static BinaOAuthEndpoints BinaAi() => new BinaOAuthEndpoints();

        /// <summary>bina-be: desktop OAuth under /api/auth/user/oauth/*.</summary>
        public static BinaOAuthEndpoints BinaBe() => new BinaOAuthEndpoints
        {
            LoginPath = "/login",
            // The BINA Cloud login page requires an emailed OTP, so the user has
            // to leave the browser, find the message and type a code. Two minutes
            // is not enough: the listener closed while the code was in flight and
            // the redirect landed on a dead port (ERR_CONNECTION_REFUSED).
            LoginTimeout = TimeSpan.FromMinutes(6),
            TokenPath = "/api/auth/user/oauth/token",
            RefreshPath = "/api/auth/user/oauth/refresh",
            MePath = null,               // no equivalent; the token response carries userId
            SendApiHint = false,
            SendRedirectUri = true,
            PreflightTokenProbe = true
        };
    }

    public enum SignInAvailability { Available, NotDeployed, ServerError, Unreachable }

    public sealed class SignInPreflightResult
    {
        public SignInAvailability Availability { get; }
        public int? StatusCode { get; }
        /// <summary>Drafter-facing reason; null when Available.</summary>
        public string Message { get; }

        public SignInPreflightResult(SignInAvailability availability, int? statusCode, string message)
        {
            Availability = availability;
            StatusCode = statusCode;
            Message = message;
        }
    }

    /// <summary>
    /// Reads the answer to an empty POST on the token endpoint. The route
    /// rejecting the body (400 "code should not be empty", 401, 422...) proves
    /// it is deployed; 404/405 means this server predates desktop sign-in.
    /// Pure so the table is pinned by tests.
    /// </summary>
    public static class SignInPreflight
    {
        public static SignInAvailability Classify(int statusCode)
        {
            if (statusCode == 404 || statusCode == 405) return SignInAvailability.NotDeployed;
            if (statusCode >= 500) return SignInAvailability.ServerError;
            return SignInAvailability.Available;
        }

        public static SignInPreflightResult ForStatus(int statusCode, string apiBaseUrl)
        {
            var availability = Classify(statusCode);
            string host = HostOf(apiBaseUrl);
            string message;
            switch (availability)
            {
                case SignInAvailability.NotDeployed:
                    message = $"Cloud Docs sign-in is not available on this server yet ({host}). " +
                              "Contact BINA support or use a newer server.";
                    break;
                case SignInAvailability.ServerError:
                    message = $"{host} returned a server error (HTTP {statusCode}), so sign-in can't start. " +
                              "Try again in a few minutes; if it keeps happening, contact BINA support.";
                    break;
                default:
                    message = null;
                    break;
            }
            return new SignInPreflightResult(availability, statusCode, message);
        }

        public static SignInPreflightResult ForNetworkFailure(string apiBaseUrl) =>
            new SignInPreflightResult(SignInAvailability.Unreachable, null,
                $"Can't reach {HostOf(apiBaseUrl)}. Check your internet connection or proxy.");

        /// <summary>host[:port] of a base URL, for messages; the raw text if it doesn't parse.</summary>
        public static string HostOf(string url) =>
            Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Authority)
                ? uri.Authority
                : (string.IsNullOrWhiteSpace(url) ? "the server" : url);
    }

    public enum SignInFailure { None, BrowserCancelled, ProviderError, StateMismatch, MissingCode }

    /// <summary>
    /// The loopback redirect, checked BEFORE the browser is told anything: the
    /// page reporting ?error=, a state that isn't ours, or no code at all.
    /// </summary>
    public sealed class SignInCallback
    {
        public string Code { get; private set; }
        public SignInFailure Failure { get; private set; }
        public string Message { get; private set; }
        public bool IsValid => Failure == SignInFailure.None;

        public static SignInCallback Parse(string query, string expectedState)
        {
            string error = GetQueryValue(query, "error");
            if (!string.IsNullOrEmpty(error))
            {
                // bina-web sends access_denied when the user cancels on its
                // consent card — a choice, not a fault.
                if (error == "access_denied")
                    return Fail(SignInFailure.BrowserCancelled, "Sign-in was cancelled in the browser.");
                // URLSearchParams writes spaces as '+'; only prose gets that
                // treatment — a code or state must reach us byte for byte.
                string description = GetQueryValue(query, "error_description")?.Replace('+', ' ');
                return Fail(SignInFailure.ProviderError,
                    $"The sign-in page reported an error: {(string.IsNullOrWhiteSpace(description) ? error : description)}");
            }

            if (GetQueryValue(query, "state") != expectedState)
                return Fail(SignInFailure.StateMismatch, "OAuth state mismatch — login aborted.");

            string code = GetQueryValue(query, "code");
            if (string.IsNullOrEmpty(code))
                return Fail(SignInFailure.MissingCode, "OAuth code missing from redirect.");

            return new SignInCallback { Code = code, Failure = SignInFailure.None };
        }

        private static SignInCallback Fail(SignInFailure failure, string message) =>
            new SignInCallback { Failure = failure, Message = message };

        // Minimal query parser — avoids a System.Web dependency on net8.0-windows.
        internal static string GetQueryValue(string query, string key)
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
    }

    /// <summary>A redirect that arrived but cannot be exchanged (see SignInCallback).</summary>
    public sealed class BinaSignInException : InvalidOperationException
    {
        public SignInFailure Failure { get; }

        public BinaSignInException(SignInFailure failure, string message) : base(message)
        {
            Failure = failure;
        }
    }

    public sealed class BinaOAuthClient
    {
        private readonly string _webBaseUrl;   // landing page, e.g. https://plugins.jkrbinaxone.com
        private readonly string _aiBaseUrl;    // token-issuing API (bina-ai, or bina-be)
        private readonly HttpClient _http;
        private readonly BinaOAuthEndpoints _endpoints;
        private readonly Action<string> _openBrowser;

        /// <summary>Cap on the pre-flight probe; a healthy server answers in well under a second.</summary>
        public static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(8);

        /// <param name="openBrowser">Opens the login URL. Defaults to the system
        /// browser; tests pass a fake that plays the browser's part.</param>
        public BinaOAuthClient(string webBaseUrl, string aiBaseUrl, HttpClient http = null,
            BinaOAuthEndpoints endpoints = null, Action<string> openBrowser = null)
        {
            _webBaseUrl = (webBaseUrl ?? "").TrimEnd('/');
            _aiBaseUrl = (aiBaseUrl ?? "").TrimEnd('/');
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _endpoints = endpoints ?? BinaOAuthEndpoints.BinaAi();
            _openBrowser = openBrowser
                ?? (url => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }));
        }

        /// <summary>The login page of the sign-in in progress (null before one starts).</summary>
        public string LoginUrl { get; private set; }

        /// <summary>Re-opens the current login page — for a closed or lost browser tab.</summary>
        public void OpenLoginPageAgain()
        {
            string url = LoginUrl;
            if (!string.IsNullOrEmpty(url)) _openBrowser(url);
        }

        // ── Pre-flight ──────────────────────────────────────────────────
        /// <summary>
        /// POSTs {} to the token endpoint and classifies the answer (see
        /// SignInPreflight). Never throws for server or network trouble — those
        /// are results; only cancellation of <paramref name="ct"/> propagates.
        /// Providers without PreflightTokenProbe report Available unasked.
        /// </summary>
        public async Task<SignInPreflightResult> CheckSignInAvailableAsync(CancellationToken ct = default)
        {
            if (!_endpoints.PreflightTokenProbe)
                return new SignInPreflightResult(SignInAvailability.Available, null, null);

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(PreflightTimeout);
            try
            {
                using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync($"{_aiBaseUrl}{_endpoints.TokenPath}", content, probeCts.Token)
                    .ConfigureAwait(false);
                return SignInPreflight.ForStatus((int)resp.StatusCode, _aiBaseUrl);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // DNS, refused connection, TLS, proxy, or the probe's own cap.
                return SignInPreflight.ForNetworkFailure(_aiBaseUrl);
            }
        }

        // The wait is capped per provider (BinaOAuthEndpoints.LoginTimeout) and
        // honours ct throughout: cancelling stops the listener (freeing the port)
        // and aborts the token exchange, surfacing as OperationCanceledException.

        // ── Loopback browser flow ───────────────────────────────────────
        public Task<BinaTokenSet> InteractiveLoginAsync(CancellationToken ct = default)
            => InteractiveLoginAsync(_endpoints.LoginTimeout, ct);

        public async Task<BinaTokenSet> InteractiveLoginAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var (verifier, challenge) = PkcePair();
            int port = FindFreePort();
            string redirect = $"http://127.0.0.1:{port}/callback/";

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();

            string state = Guid.NewGuid().ToString("N");
            // Pass &api so the (static) landing page knows which bina-ai to call —
            // handy when bina-ai is a local ngrok tunnel during testing.
            string loginUrl = $"{_webBaseUrl}{_endpoints.LoginPath}"
                + $"?redirect_uri={Uri.EscapeDataString(redirect)}"
                + $"&code_challenge={challenge}"
                + "&code_challenge_method=S256"
                + $"&state={Uri.EscapeDataString(state)}"
                + (_endpoints.SendApiHint ? $"&api={Uri.EscapeDataString(_aiBaseUrl)}" : "");

            LoginUrl = loginUrl;
            _openBrowser(loginUrl);

            HttpListenerContext ctx;
            using (ct.Register(() => { try { listener.Stop(); } catch { } }))
            {
                // Race the callback against a timeout so the caller can never wait
                // indefinitely. WhenAny returns whichever finishes first.
                var contextTask = listener.GetContextAsync();
                // Stopping the listener faults the pending GetContext; observe it
                // so a cancelled or timed-out sign-in leaves no unobserved fault.
                _ = contextTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delayTask = Task.Delay(timeout, timeoutCts.Token);
                var winner = await Task.WhenAny(contextTask, delayTask).ConfigureAwait(false);
                timeoutCts.Cancel();          // cancel the pending delay
                // Checked first: Stop() on cancel also faults contextTask, and that
                // must read as a cancel, not as a listener error.
                ct.ThrowIfCancellationRequested();
                if (winner != contextTask)
                {
                    try { listener.Stop(); } catch { }
                    throw new TimeoutException(
                        $"Browser sign-in timed out after {timeout.TotalSeconds:F0}s. " +
                        "Finish the login in your browser, then click Login again. " +
                        "If the sign-in page didn't load, the site may be unreachable.");
                }
                ctx = await contextTask.ConfigureAwait(false);
            }

            // Validate, then exchange, THEN tell the browser. The tab used to say
            // "You're signed in" before the state was even checked.
            var callback = SignInCallback.Parse(ctx.Request.Url?.Query ?? "", state);
            if (!callback.IsValid)
            {
                if (callback.Failure == SignInFailure.BrowserCancelled)
                    WriteBrowserResponse(ctx, false, "Sign-in cancelled",
                        "Return to Revit — you can sign in again whenever you're ready.");
                else
                    WriteBrowserResponse(ctx, false, "Sign-in failed",
                        "Return to Revit — it shows what went wrong.");
                throw new BinaSignInException(callback.Failure, callback.Message);
            }

            BinaTokenSet tokens;
            try
            {
                tokens = await ExchangeCodeAsync(callback.Code, verifier, redirect, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                bool cancelled = ct.IsCancellationRequested;
                WriteBrowserResponse(ctx, false,
                    cancelled ? "Sign-in cancelled" : "Sign-in failed",
                    cancelled ? "Sign-in was cancelled in Revit." : "Return to Revit — it shows what went wrong.");
                throw;
            }

            WriteBrowserResponse(ctx, true, "You're signed in", "Return to Revit — BINA AI Copilot is ready to go.");
            listener.Stop();
            return tokens;
        }

        private async Task<BinaTokenSet> ExchangeCodeAsync(
            string code, string verifier, string redirectUri, CancellationToken ct)
        {
            // bina-be binds the code to the redirect_uri it was issued for and
            // rejects an exchange that omits it; bina-ai does not accept the field.
            object requestBody = _endpoints.SendRedirectUri
                ? (object)new { code, code_verifier = verifier, redirect_uri = redirectUri }
                : (object)new { code, code_verifier = verifier };
            string payload = JsonConvert.SerializeObject(requestBody);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_aiBaseUrl}{_endpoints.TokenPath}", content, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token exchange failed (HTTP {(int)resp.StatusCode}): {body}");
            return Parse(body);
        }

        public async Task<BinaTokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            string payload = JsonConvert.SerializeObject(new { refresh_token = refreshToken });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_aiBaseUrl}{_endpoints.RefreshPath}", content, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token refresh failed (HTTP {(int)resp.StatusCode}): {body}");
            return Parse(body);
        }

        // GET /auth/me — real display name (already included in the token response
        // too, but exposed for callers that only hold an access token).
        public async Task<string> GetDisplayNameAsync(string accessToken, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_endpoints.MePath)) return null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{_aiBaseUrl}{_endpoints.MePath}");
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
        // bina-ai answers snake_case with a nested user object; bina-be answers
        // camelCase with a flat userId. Read both so one parser serves both.
        private static BinaTokenSet Parse(string json)
        {
            var o = JObject.Parse(json);
            var user = o["user"] as JObject;
            return new BinaTokenSet
            {
                AccessToken = (string)o["access_token"] ?? (string)o["accessToken"] ?? "",
                RefreshToken = (string)o["refresh_token"] ?? (string)o["refreshToken"] ?? "",
                AccessTokenExpiry = (long?)o["access_token_expiry"] ?? (long?)o["accessTokenExpiry"] ?? 0,
                UserId = user != null ? ((int?)user["id"] ?? 0) : ((int?)o["userId"] ?? 0),
                UserName = user != null ? (string)user["name"] : (string)o["userName"],
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

        internal static string RenderCallbackPage(bool success, string heading, string message) =>
            CallbackPage
                .Replace("%%STATE%%", success ? "" : " fail")
                .Replace("%%ICON%%", success ? "M20 6 9 17l-5-5" : "M18 6 6 18M6 6l12 12")
                .Replace("%%HEADING%%", WebUtility.HtmlEncode(heading ?? ""))
                .Replace("%%MESSAGE%%", WebUtility.HtmlEncode(message ?? ""));

        private static void WriteBrowserResponse(HttpListenerContext ctx, bool success, string heading, string message)
        {
            try
            {
                // charset=utf-8 is required — without it the browser guessed
                // latin-1 and rendered the em dash as "â€”".
                string html = RenderCallbackPage(success, heading, message);
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { /* best effort — the browser tab content is cosmetic */ }
        }

        // Branded sign-in result (success, or failure with a cross), styled to match
        // the plugins landing page (BINAXONE tokens: pear accent, warm paper, Plus
        // Jakarta Sans, blurred orbs, 20px glass card). Self-contained — the addin's
        // loopback listener serves it, so everything is inlined; only Google Fonts
        // is remote.
        private const string CallbackPage =
"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>%%HEADING%% — BINAXONE</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Plus+Jakarta+Sans:wght@400;500;600;700&family=JetBrains+Mono:wght@500&display=swap" rel="stylesheet">
<style>
  :root{
    --bg:oklch(93% 0.014 95);
    --card:oklch(99% 0.005 95);
    --ink:oklch(20% 0.012 250);
    --ink-soft:oklch(20% 0.012 250 / 0.64);
    --accent:oklch(86% 0.18 95);
    --accent-deep:oklch(70% 0.16 95);
    --edge:oklch(20% 0.012 250 / 0.08);
  }
  *{box-sizing:border-box;margin:0;padding:0}
  html,body{height:100%}
  body{
    font-family:"Plus Jakarta Sans",ui-sans-serif,system-ui,sans-serif;
    color:var(--ink);
    background:var(--bg);
    display:grid;place-items:center;padding:1.5rem;
  }
  .card{
    width:min(30rem,100%);text-align:center;
    background:var(--card);
    border:1px solid var(--edge);border-radius:20px;
    padding:2.75rem 2.25rem;
    box-shadow:0 20px 50px -24px oklch(20% 0.012 250 / 0.35);
  }
  .brand{display:inline-flex;align-items:center;justify-content:center;gap:.5rem;font-weight:700;font-size:1rem}
  .pip{width:.7rem;height:.7rem;border-radius:50%;background:var(--accent);box-shadow:0 1px 0 0 var(--accent-deep),0 0 18px 2px oklch(86% 0.18 95 / 0.7)}
  .check{width:4.5rem;height:4.5rem;margin:1.75rem auto 0;border-radius:50%;display:grid;place-items:center;background:var(--accent);box-shadow:0 8px 22px -8px var(--accent-deep);animation:pop .45s cubic-bezier(.2,1.3,.4,1) both}
  .check.fail{background:var(--edge);box-shadow:none}
  .check svg{width:2.2rem;height:2.2rem;stroke:var(--ink);stroke-width:3;fill:none;stroke-linecap:round;stroke-linejoin:round}
  h1{margin-top:1.25rem;font-size:1.55rem;font-weight:700}
  p{margin-top:.6rem;color:var(--ink-soft);font-size:.98rem;line-height:1.5}
  .hint{margin-top:1.5rem;font-family:"JetBrains Mono",monospace;font-size:.7rem;letter-spacing:.08em;color:var(--ink-soft);text-transform:uppercase}
  @keyframes pop{from{transform:scale(.4);opacity:0}to{transform:scale(1);opacity:1}}
  @media (prefers-color-scheme:dark){
    :root{
      --bg:oklch(23% 0.02 260);
      --card:oklch(28% 0.02 260);
      --ink:oklch(96% 0.01 95);
      --ink-soft:oklch(96% 0.01 95 / 0.62);
      --edge:oklch(100% 0 0 / 0.08);
    }
  }
</style>
</head>
<body>
  <div class="card">
    <span class="brand"><span class="pip"></span>BINAXONE</span>
    <div class="check%%STATE%%"><svg viewBox="0 0 24 24"><path d="%%ICON%%"/></svg></div>
    <h1>%%HEADING%%</h1>
    <p>%%MESSAGE%%</p>
    <div class="hint">You can close this tab</div>
  </div>
  <script>
    // Strip ?code=... from the address bar (cosmetic + keeps the one-time,
    // already-exchanged PKCE code out of history). No reload — the loopback
    // listener has already stopped, so a real navigation would 404.
    try { history.replaceState(null, "", location.pathname); } catch (e) {}
  </script>
</body>
</html>
""";

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
