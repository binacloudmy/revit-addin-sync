using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace RevitWebAppSync
{
    public class BinaConfig
    {
        public string Email { get; set; }
        public string Password { get; set; }
        public int ProjectId { get; set; }
        public int UserId { get; set; }
        public int? OrgId { get; set; }   // organisation/team id, when the user belongs to one

        // Uniform installed-rate markup (%) applied by the cost match
        // pipeline on top of N3C material rates. 0 = raw material prices.
        // Surfaced on the cost dashboard total ("incl. N% markup") so the
        // drafter always knows what the number contains.
        [JsonProperty("costMarkupPct")]
        public double CostMarkupPct { get; set; } = 0;

        // Session data
        public string UserName { get; set; }
        public string ProjectName { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime TokenExpiry { get; set; }

        // BINA Cloud (bina-be) session — kept SEPARATE from the bina-ai session
        // above. The two services issue their own tokens: bina-ai signs its own,
        // bina-be signs HS256 `access_${JWT_SECRET}`. Overwriting one with the
        // other logs the user out of Copilot/JKR or out of Cloud Docs depending
        // on which button they pressed last, so they never share a field.
        // [JsonIgnore]: these live in the Windows Credential Manager, not in
        // config.json. The file sits unencrypted in %APPDATA% and is trivially
        // readable by anything running as the user.
        [JsonIgnore]
        public string BeAccessToken { get; set; }
        [JsonIgnore]
        public string BeRefreshToken { get; set; }
        [JsonIgnore]
        public DateTime BeTokenExpiry { get; set; }

        /// <summary>
        /// Display name for the Cloud Docs account. Separate from UserName, which
        /// belongs to the bina-ai session — the two can be different people, and
        /// showing the wrong one on a Cloud Docs screen is actively misleading.
        /// </summary>
        public string BeUserName { get; set; }

        /// <summary>Persist the Cloud Docs session to the credential store.</summary>
        public void SaveBinaCloudTokens()
        {
            try
            {
                BinaVibe.Auth.SecureTokenStore.SaveCloudDocs(new BinaVibe.Auth.BinaTokenSet
                {
                    AccessToken = BeAccessToken ?? "",
                    RefreshToken = BeRefreshToken ?? "",
                    AccessTokenExpiry = BeTokenExpiry == DateTime.MinValue
                        ? 0
                        : new DateTimeOffset(BeTokenExpiry.ToUniversalTime()).ToUnixTimeSeconds(),
                    UserId = UserId
                });
            }
            catch
            {
                // A machine where the credential store is unavailable still works
                // for the length of the session; the user signs in again next time.
            }
        }

        /// <summary>Restore the Cloud Docs session from the credential store.</summary>
        private void LoadBinaCloudTokens()
        {
            try
            {
                var tokens = BinaVibe.Auth.SecureTokenStore.LoadCloudDocs();
                if (tokens == null || string.IsNullOrEmpty(tokens.AccessToken)) return;

                BeAccessToken = tokens.AccessToken;
                BeRefreshToken = tokens.RefreshToken;
                BeTokenExpiry = tokens.AccessTokenExpiry > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(tokens.AccessTokenExpiry).LocalDateTime
                    : DateTime.MinValue;
            }
            catch
            {
                // Treated as "not signed in to Cloud Docs".
            }
        }

        // Backend URLs — overridable via config.json so the addin doesn't need
        // a rebuild when ngrok tunnels rotate. Empty/missing values fall back
        // to the DEFAULT_* constants below.
        public string AIBaseUrl { get; set; }
        public string ApiBaseUrl { get; set; }

        // BINA web app base (the browser login page for the desktop OAuth/PKCE
        // flow). Overridable via config.json; falls back to DEFAULT_LOGIN_WEB_URL.
        public string LoginWebUrl { get; set; }

        // Dev opt-in: by default a ngrok AIBaseUrl is ignored (see ResolvedAIBaseUrl)
        // because stale tunnels left in config.json 502. Set this true in config.json
        // to deliberately point the AI calls at a live ngrok tunnel (e.g. a local
        // bina-ai backend during development). Default false = unchanged behavior.
        public bool AllowNgrokAIBaseUrl { get; set; }

        // Same opt-in for the bina-be API base. Set true in config.json to point
        // Cloud Docs / sync calls at a tunnelled local bina-be (the usual setup
        // is Revit on Windows against a developer's Mac over ngrok).
        public bool AllowNgrokApiBaseUrl { get; set; }

        // BINA web app origin (app-stg.binacloud.ai / app.binacloud.ai). This is the page
        // that runs the bina-be desktop-OAuth bridge: it authorizes against its
        // own NEXT_PUBLIC_API_URL and redirects back to our loopback with a code.
        // Distinct from LoginWebUrl, which is the bina-ai plugins landing page.
        public string CloudWebUrl { get; set; }

        // UAT opt-in: by default a config.json override pointing at one of OUR
        // hosts (UrlResolution.OurHosts) follows the embedded .env (that is how
        // the fleet migrates across backend cutovers). Set this true in config.json
        // to deliberately steer THIS machine at a specific backend (e.g. a
        // Release build against staging during UAT). Default false = env wins.
        public bool AllowBackendOverride { get; set; }

        // Download Model pulls the parameters BINA holds for the model right
        // after opening it. Set false in config.json to download and open only
        // (the Sync Parameters button still works).
        // Nullable on purpose: "AutoSyncParameters": null (or a hand-edited
        // blank) must not fail the whole config load and drop ApiBaseUrl etc.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? AutoSyncParameters { get; set; }

        [JsonIgnore]
        public bool AutoSyncParametersEnabled => AutoSyncParameters ?? true;

        // OTA update feed (version.json). Empty default = updater disabled
        // until a host is chosen; overridable via config.json like the URLs
        // above, so enabling updates later needs no rebuild.
        public string UpdateFeedUrl { get; set; }

        // OTA self-heal F6: per-machine opt-out of unattended diagnostics
        // uploads (after a failure, or when an admin requests logs). Only
        // "DiagnosticsAuto": false changes anything; config.json cannot turn
        // it on for a channel that ships DIAGNOSTICS_AUTO off.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? DiagnosticsAuto { get; set; }

        // CAD to BIM pane settings (Services/CadToBim/CadToBimSettings.cs) share
        // this file. Held opaque so Save() carries the key through untouched;
        // the settings class does its own read-modify-write on the same path
        // and owns the shape. Never read this from add-in code — use
        // CadToBimSettings.Load().
        [JsonProperty("cadToBim", NullValueHandling = NullValueHandling.Ignore)]
        public Newtonsoft.Json.Linq.JObject CadToBim { get; set; }

        // Device-token pairing for the retired local engine. Nothing in the
        // add-in uses these any more except BrowserLoginCommand's device-token
        // mint (orchestrator-owned; see the W5 report). Kept so that file still
        // builds; delete with the mint. The engine settings themselves
        // (EngineMode, ports, secret, auto-spawn) were removed with the engine
        // (lean-harness spec §7.1, §17, D11); config.json keys they left behind
        // are ignored on read and dropped on the next save.
        public string GatewayUrl { get; set; }
        public string DeviceToken { get; set; }
        // Unix epoch SECONDS the DeviceToken expires at (from the gateway's
        // expires_at). Nullable: absent on configs whose token was minted
        // before expiry persistence landed. BrowserLoginCommand re-mints when
        // this is within 3 days of now (proactive refresh, no scheduler).
        public long? DeviceTokenExpiresAt { get; set; }

        // Full sign-in endpoint. Defaults to BASE_URL/api/auth/user/sign-in, but
        // can be pinned independently via the LOGIN_URL env key or config.json
        // (e.g. auth split onto its own host).
        public string LoginUrl { get; set; }

        // Zero-config release (setup exe -> Login -> works, no hand-edited
        // config.json): stamped the first time ApplyDefaults() runs so it
        // never re-runs on subsequent Load() calls. Nullable, not a bool —
        // Newtonsoft can't tell "absent from JSON" from "explicitly false"
        // for a plain bool, which would make an idempotence/overwrite gate
        // built on a bool ambiguous on configs written before this field
        // existed. A null DateTime is unambiguous either way (missing key
        // deserializes to null, same as an explicit `null` in the file), so
        // it doubles as both the one-time gate AND an audit timestamp.
        public DateTime? AutoConfiguredAt { get; set; }

        // Defaults now come from the embedded .env (.env.local on Debug,
        // .env.production on Release). The string literals below are last-resort
        // fallbacks if the key is missing from the env file. API + AI + login all
        // share BASE_URL — they're the same host. config.json still overrides.
        public static string DEFAULT_AI_BASE_URL =>
            Env("BASE_URL") ?? "https://bina-ai-prod-ready.binacloud.ai";
        // Colocated-engine gateway (inference + device-token mint). A SEPARATE
        // key from BASE_URL: the staging channel authenticates against prod
        // (accounts live there) but must run inference on the staging gateway,
        // the only one with GATEWAY_INFERENCE_ENABLED=1 and the gateway routers
        // deployed. Until 2026-08-27 ResolvedGatewayUrl fell back to
        // DEFAULT_AI_BASE_URL, so every staging engine turn reached prod's
        // gateway and died 404 "inference gateway disabled" after a 60s cold
        // start. Falls back to BASE_URL when the key is absent, so a channel
        // file without it behaves exactly as before.
        public static string DEFAULT_GATEWAY_URL =>
            Env("GATEWAY_URL") ?? DEFAULT_AI_BASE_URL;
        // bina-be, the BINA Cloud REST API. This is a DIFFERENT service from
        // bina-ai: it serves /api/cloud-docs/* and /api/system/*, which bina-ai
        // does not implement at all. It aliased DEFAULT_AI_BASE_URL from 52bd3b4
        // (2026-05-11) until now, so every Cloud Docs / sync call 404'd against
        // bina-ai — that is why plugin syncs stopped landing. Falls back to
        // BASE_URL when API_BASE_URL is absent so an env file without the new key
        // behaves exactly as before.
        public static string DEFAULT_API_BASE_URL =>
            Env("API_BASE_URL") ?? Env("BASE_URL") ?? "https://api.binacloud.ai";

        // BINA web origin that hosts the desktop-OAuth bridge page (/login).
        public static string DEFAULT_CLOUD_WEB_URL =>
            Env("CLOUD_WEB_URL") ?? Env("LOGIN_WEB_URL") ?? "https://app.binacloud.ai";
        // BINA web login origin for the desktop OAuth browser flow. Override via
        // the LOGIN_WEB_URL env key or config.json once the real origin is known.
        public static string DEFAULT_LOGIN_WEB_URL =>
            Env("LOGIN_WEB_URL") ?? "https://plugins.jkrbinaxone.com";
        public static string DEFAULT_UPDATE_FEED_URL =>
            Env("UPDATE_FEED_URL")
            ?? "https://github.com/binacloudmy/revit-addin-sync/releases/latest/download/version.json";
        // Second feed host, tried when UPDATE_FEED_URL does not answer (OTA
        // self-heal F3). Blank = no fallback. Deliberately NOT overridable from
        // config.json: it is the rescue path when that pin is what's broken.
        public static string DEFAULT_UPDATE_FEED_URL_FALLBACK =>
            Env("UPDATE_FEED_URL_FALLBACK");
        // Unattended diagnostics uploads (OTA self-heal F6); "true" on the
        // deployed channels. See Services/DiagnosticsPolicy.UnattendedEnabled.
        public static string DEFAULT_DIAGNOSTICS_AUTO =>
            Env("DIAGNOSTICS_AUTO");
        // LOGIN_URL (full) is optional; when unset the sign-in URL derives from
        // BASE_URL + LOGIN_PATH (LOGIN_PATH defaults to /api/auth/user/sign-in).
        public static string DEFAULT_LOGIN_PATH =>
            Env("LOGIN_PATH") ?? "/api/auth/user/sign-in";

        // --- Embedded .env loader (build-config selected, parsed once) ---
        private static readonly Lazy<Dictionary<string, string>> _env =
            new Lazy<Dictionary<string, string>>(LoadEnv);

        private static string Env(string key)
        {
            var v = _env.Value.TryGetValue(key, out var val) ? val : null;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        /// <summary>
        /// Which channel this binary was built as, and therefore which .env is
        /// baked into it. Shown by <see cref="DescribeEndpoints"/> — "which
        /// backend is this install talking to" was previously answerable only
        /// by disassembling the DLL.
        /// </summary>
        public static string Channel =>
#if DEBUG
            "Debug (.env.local)";
#elif STAGING
            "Staging (.env.staging)";
#else
            "Release (.env.production)";
#endif

        private static Dictionary<string, string> LoadEnv()
        {
#if DEBUG
            const string resource = "env.local";
#elif STAGING
            const string resource = "env.staging";
#else
            const string resource = "env.production";
#endif
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(resource);
                if (stream == null)
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                using var reader = new StreamReader(stream);
                // Parser lives in Services/EnvFile.cs so EnvChannelTests lints
                // the .env files with the same code that reads them at runtime.
                return Services.EnvFile.Parse(reader);
            }
            catch
            {
                /* fall back to literals above */
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Every endpoint this build actually resolved, after config.json
        /// overrides and the UrlResolution rules have been applied. Logged at
        /// startup and attached to login failures: a wrong or stale host is the
        /// most common cause of "signed in but nothing works", and until now
        /// nothing surfaced which host was in play.
        /// </summary>
        public string DescribeEndpoints() =>
            "channel        : " + Channel + "\n" +
            "AI base        : " + ResolvedAIBaseUrl + "\n" +
            "auth base      : " + ResolvedAuthBaseUrl + "\n" +
            "bina-be API    : " + ResolvedApiBaseUrl + "\n" +
            "AI login page  : " + ResolvedLoginWebUrl + "\n" +
            "CDE login page : " + ResolvedCloudWebUrl + "\n" +
            "update feed    : " + ResolvedUpdateFeedUrl + "\n" +
            "feed fallback  : " + (DEFAULT_UPDATE_FEED_URL_FALLBACK ?? "(none)");

        // --- Env-first resolution -------------------------------------------
        // Rules live in Services/UrlResolution.cs (pure, unit-tested): a
        // persisted override pointing at one of OUR *.azurewebsites.net hosts
        // follows the embedded .env; only genuinely custom values (self-
        // hosted, dev tunnel, localhost engine) are honored from config.json.
        // This is what moves an already-configured fleet across a backend
        // cutover with nothing but a new build.

        [JsonIgnore]
        public string ResolvedAIBaseUrl =>
            Services.UrlResolution.ResolveAIBase(
                AIBaseUrl, AllowNgrokAIBaseUrl, DEFAULT_AI_BASE_URL,
                AllowBackendOverride);

        // Gateway base the engine + device-token flows must use. Empty stays
        // empty (gateway features are gated on it being configured at all).
        [JsonIgnore]
        public string ResolvedGatewayUrl =>
            Services.UrlResolution.ResolveGateway(
                GatewayUrl, DEFAULT_GATEWAY_URL, AllowBackendOverride);

        [JsonIgnore]
        public string ResolvedCloudBaseUrl =>
            // The CLOUD bina-ai host, for features the local engine does not
            // serve. In engine mode AIBaseUrl points at the LOCAL engine
            // (localhost:48810), which mounts ONLY the tool loop + feedback —
            // auth (PKCE 404, first zero-config UAT 2026-07-13), JKR/fire
            // compliance ("Scan failed: NotFound", same day), cost analysis
            // and /credits/balance all live cloud-side only.
            // resolvedGateway deliberately NOT passed (2026-08-27). "Gateway
            // wins" was harmless while gateway == BASE_URL; with GATEWAY_URL
            // now a separate key it silently moved login, credits, compliance,
            // telemetry and cost to the staging gateway - reversing the 08-22
            // decision that the cloud half lives on BASE_URL (prod: accounts
            // are there). The gateway is for inference + the device-token
            // mint only (ResolvedGatewayUrl); everything else follows BASE_URL.
            Services.UrlResolution.ResolveCloudBase(
                null, ResolvedAIBaseUrl, DEFAULT_AI_BASE_URL);

        // Token-issuing base (login page api= param, /auth/*). Named alias so
        // auth call sites read as auth; it IS the cloud base.
        [JsonIgnore]
        public string ResolvedAuthBaseUrl => ResolvedCloudBaseUrl;

        // Bomba compliance: the colocated engine mounts /v1/compliance/bomba-*
        // itself (rules are a repo JSON, no DB — bina-ai c2d8b7e), so engine
        // mode keeps the scan on-box; cloud-only seats fall through to the
        // cloud base like every other compliance surface.
        [JsonIgnore]
        public string ResolvedBombaBaseUrl =>
            Services.UrlResolution.ResolveBombaBase(ResolvedAIBaseUrl, ResolvedCloudBaseUrl);

        // bina-be REST API (/api/cloud-docs/*, /api/system/*, /api/auth/user/*).
        // Uses the ngrok-aware resolver so a Windows Revit box can be aimed at a
        // developer's local bina-be with AllowNgrokApiBaseUrl=true.
        [JsonIgnore]
        public string ResolvedApiBaseUrl =>
            Services.UrlResolution.ResolveBinaBeBase(
                ApiBaseUrl, AllowNgrokApiBaseUrl, DEFAULT_API_BASE_URL, AllowBackendOverride);

        // Web origin hosting the bina-be desktop-OAuth bridge (/login).
        [JsonIgnore]
        public string ResolvedCloudWebUrl =>
            Services.UrlResolution.ResolveLoginWeb(
                CloudWebUrl, DEFAULT_CLOUD_WEB_URL, AllowBackendOverride);

        /// <summary>True when a BINA Cloud (bina-be) session is stored.</summary>
        public bool IsBinaCloudLoggedIn() => !string.IsNullOrEmpty(BeAccessToken);

        // Login must open the real web origin (plugins.jkrbinaxone.com),
        // never a dead local page left by dev testing.
        [JsonIgnore]
        public string ResolvedLoginWebUrl =>
            Services.UrlResolution.ResolveLoginWeb(
                LoginWebUrl, DEFAULT_LOGIN_WEB_URL, AllowBackendOverride);

        [JsonIgnore]
        public string ResolvedUpdateFeedUrl =>
            Services.UrlResolution.ResolveUpdateFeed(
                UpdateFeedUrl, DEFAULT_UPDATE_FEED_URL, AllowBackendOverride);

        // Sign-in URL. config.json LoginUrl > LOGIN_URL env > base + default path.
        [JsonIgnore]
        public string ResolvedLoginUrl =>
            !string.IsNullOrWhiteSpace(LoginUrl) ? LoginUrl
            : Env("LOGIN_URL")
              ?? (ResolvedApiBaseUrl.TrimEnd('/') + "/" + DEFAULT_LOGIN_PATH.TrimStart('/'));

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitWebAppSync",
            "config.json"
        );

        public static BinaConfig Load()
        {
            BinaConfig cfg = null;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    cfg = JsonConvert.DeserializeObject<BinaConfig>(json);
                }

                // Cloud Docs tokens are [JsonIgnore]; they come from the
                // credential store, not the file.
                cfg?.LoadBinaCloudTokens();
            }
            catch (Exception ex)
            {
            }

            cfg = cfg ?? new BinaConfig();

            // Zero-config release: fill blank/absent values ONCE (see
            // ApplyDefaults' own doc comment for the exact rules + the
            // one-time gate). Runs for both a brand-new config.json (fresh
            // install) and a pre-existing one that predates this field.
            cfg.ApplyDefaults();

            // Repairs that must reach ALREADY-configured boxes, which
            // ApplyDefaults' one-time gate can never touch. Every load,
            // idempotent, writes only on an actual change — see ApplyHeals.
            cfg.ApplyHeals();

            return cfg;
        }

        /// <summary>
        /// Zero-config first-run self-configuration (drafter runs the setup
        /// exe -> opens Revit -> clicks Login -> everything works, no hand
        /// edits to config.json). Fills ONLY blank/absent values and never
        /// touches anything the user (or a prior run) already set — see
        /// AutoConfiguredAt's doc comment for why the gate is a nullable
        /// timestamp rather than a bool. Saves at most once per config file.
        /// </summary>
        private void ApplyDefaults()
        {
            if (AutoConfiguredAt.HasValue) return;   // already ran once — never re-run

            AutoConfiguredAt = DateTime.UtcNow;
            Save();
        }

        /// <summary>
        /// Self-heal pass for configs that ApplyDefaults can never reach
        /// (ApplyDefaults is one-shot, so a repair added there only lands on
        /// machines configured after it shipped). Runs on EVERY Load, so each
        /// rule must be idempotent and self-terminating, touch only stale or
        /// incoherent values, and save only when something changed.
        /// </summary>
        private void ApplyHeals()
        {
            var changed = false;

            // The local engine is gone (lean-harness spec §7.1, D11). Engine mode
            // pointed AIBaseUrl at it (http://localhost:48810); left there, the
            // copilot and every bina-ai feature would dial a port nothing listens
            // on, and release builds refuse non-BINA hosts anyway. Clear it so
            // the channel's BASE_URL applies. A developer's own localhost backend
            // on another port is left alone.
            if (IsRetiredEngineUrl(AIBaseUrl))
            {
                AIBaseUrl = null;
                changed = true;
            }

            if (changed) Save();
        }

        internal static bool IsRetiredEngineUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
            return (u.Host == "localhost" || u.Host == "127.0.0.1") && u.Port == RetiredEnginePort;
        }

        private const int RetiredEnginePort = 48810;

        public void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
            }
        }

        public bool IsValid()
        {
            return !string.IsNullOrEmpty(Email) && !string.IsNullOrEmpty(Password) && ProjectId > 0 && UserId > 0;
        }

        /// <summary>
        /// The bina-ai (Copilot/JKR) session — the counterpart of
        /// <see cref="IsBinaCloudLoggedIn"/>. Token-presence only: UserName is a
        /// display nicety, and ProjectId belongs to the Cloud Docs session (the
        /// AI browser login deliberately stopped setting it — see
        /// BrowserLoginCommand). Requiring them here meant an AI-only sign-in
        /// could never pass the Copilot auth gate (2026-08-18).
        /// </summary>
        public bool IsLoggedIn() => !string.IsNullOrEmpty(AccessToken);

        public void ClearSession()
        {
            Email = null;
            Password = null;
            UserName = null;
            ProjectName = null;
            AccessToken = null;
            RefreshToken = null;
            TokenExpiry = DateTime.MinValue;
            ProjectId = 0;
            UserId = 0;
            ClearBinaCloudSession();
        }

        /// <summary>
        /// Drop only the BINA Cloud (bina-be) session, leaving the bina-ai session
        /// intact — signing out of Cloud Docs must not sign the user out of
        /// Copilot/JKR, and vice versa.
        /// </summary>
        public void ClearBinaCloudSession()
        {
            try { BinaVibe.Auth.SecureTokenStore.ClearCloudDocs(); } catch { }
            BeAccessToken = null;
            BeRefreshToken = null;
            BeTokenExpiry = DateTime.MinValue;
            BeUserName = null;
            ProjectId = 0;
            ProjectName = null;
        }
    }
}