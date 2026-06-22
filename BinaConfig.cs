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

        // Session data
        public string UserName { get; set; }
        public string ProjectName { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime TokenExpiry { get; set; }

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

        // OTA update feed (version.json). Empty default = updater disabled
        // until a host is chosen; overridable via config.json like the URLs
        // above, so enabling updates later needs no rebuild.
        public string UpdateFeedUrl { get; set; }

        // Full sign-in endpoint. Defaults to BASE_URL/api/auth/user/sign-in, but
        // can be pinned independently via the LOGIN_URL env key or config.json
        // (e.g. auth split onto its own host).
        public string LoginUrl { get; set; }

        // Defaults now come from the embedded .env (.env.local on Debug,
        // .env.production on Release). The string literals below are last-resort
        // fallbacks if the key is missing from the env file. API + AI + login all
        // share BASE_URL — they're the same host. config.json still overrides.
        public static string DEFAULT_AI_BASE_URL =>
            Env("BASE_URL") ?? "https://bina-ai-staging.azurewebsites.net";
        public static string DEFAULT_API_BASE_URL => DEFAULT_AI_BASE_URL;
        // BINA web login origin for the desktop OAuth browser flow. Override via
        // the LOGIN_WEB_URL env key or config.json once the real origin is known.
        public static string DEFAULT_LOGIN_WEB_URL =>
            Env("LOGIN_WEB_URL") ?? "https://plugins.jkrbinaxone.com";
        public static string DEFAULT_UPDATE_FEED_URL =>
            Env("UPDATE_FEED_URL")
            ?? "https://github.com/binacloudmy/revit-addin-sync/releases/latest/download/version.json";
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

        private static Dictionary<string, string> LoadEnv()
        {
#if DEBUG
            const string resource = "env.local";
#else
            const string resource = "env.production";
#endif
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(resource);
                if (stream == null) return map;

                using var reader = new StreamReader(stream);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    var eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = t.Substring(0, eq).Trim();
                    var val = t.Substring(eq + 1).Trim().Trim('"');
                    map[k] = val;
                }
            }
            catch { /* fall back to literals above */ }
            return map;
        }

        [JsonIgnore]
        public string ResolvedAIBaseUrl
        {
            // Ignore stale ngrok overrides left in config.json — the addin now
            // targets the cloud (DEFAULT_AI_BASE_URL = staging). A leftover ngrok
            // AIBaseUrl points at a dead local tunnel (HTTP 502 / ERR_NGROK_8012),
            // so by default we honor only a real, non-ngrok custom override.
            // Devs can opt in to a live ngrok tunnel via AllowNgrokAIBaseUrl=true.
            get
            {
                if (string.IsNullOrWhiteSpace(AIBaseUrl)) return DEFAULT_AI_BASE_URL;
                bool isNgrok = AIBaseUrl.IndexOf("ngrok", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isNgrok && !AllowNgrokAIBaseUrl) return DEFAULT_AI_BASE_URL;
                return AIBaseUrl;
            }
        }

        [JsonIgnore]
        public string ResolvedApiBaseUrl =>
            !string.IsNullOrWhiteSpace(ApiBaseUrl) ? ApiBaseUrl : DEFAULT_API_BASE_URL;

        [JsonIgnore]
        public string ResolvedLoginWebUrl =>
            !string.IsNullOrWhiteSpace(LoginWebUrl) ? LoginWebUrl : DEFAULT_LOGIN_WEB_URL;

        [JsonIgnore]
        public string ResolvedUpdateFeedUrl =>
            !string.IsNullOrWhiteSpace(UpdateFeedUrl) ? UpdateFeedUrl : DEFAULT_UPDATE_FEED_URL;

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
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    return JsonConvert.DeserializeObject<BinaConfig>(json);
                }
            }
            catch (Exception ex)
            {
            }

            return new BinaConfig();
        }

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

        public bool IsLoggedIn()
        {
            return !string.IsNullOrEmpty(AccessToken)
                && !string.IsNullOrEmpty(UserName)
                && ProjectId > 0;
        }

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
        }
    }
}