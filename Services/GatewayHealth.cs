// GatewayHealth — can the cloud gateway run engine-mode inference right now?
//
// Engine mode sends every model call to <gateway>/gateway/v1/chat/completions.
// That route answers 404 "inference gateway disabled" unless the backend runs
// with GATEWAY_INFERENCE_ENABLED=1, and the add-in used to switch to engine
// mode on any config that had a gateway URL. 0.0.86 boxes then could not
// answer at all until config.json was edited by hand.
//
// Two rules use the result:
//   - BinaConfig.ApplyHeals switches a box to engine mode only when the last
//     check (saved to gateway-health.json by an earlier Revit session) passed.
//     A check never runs inside Load(): Load is synchronous and runs every turn.
//   - ToolLoopService runs a turn on the cloud when the add-in owns the engine
//     but the gateway can't serve it, so boxes switched before this fix answer
//     again without a hand edit.
//
// Classify* and the decision helpers are pure (Tests/GatewayHealthTests.cs).

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync.Services
{
    public enum GatewayState
    {
        /// <summary>Never checked, or the answer made no sense.</summary>
        Unknown,
        /// <summary>The gateway runs inference.</summary>
        Healthy,
        /// <summary>The gateway answers, but inference is off (the 404).</summary>
        InferenceDisabled,
        /// <summary>No answer: DNS, TLS, timeout, connection refused.</summary>
        Unreachable,
    }

    public sealed class GatewayHealthRecord
    {
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("state")] public GatewayState State { get; set; }
        [JsonProperty("checked_utc")] public DateTime CheckedUtc { get; set; }
        [JsonProperty("detail")] public string Detail { get; set; }
    }

    public static class GatewayHealth
    {
        /// <summary>A saved "healthy" older than this no longer earns the
        /// switch to engine mode; the next session re-checks first.</summary>
        public static readonly TimeSpan FlipMaxAge = TimeSpan.FromHours(24);

        /// <summary>How long a turn-time answer is reused in this process.
        /// Healthy is trusted longer; a bad answer is re-asked sooner so a
        /// fixed gateway is picked up within a couple of minutes.</summary>
        public static readonly TimeSpan HealthyTtl = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan UnhealthyTtl = TimeSpan.FromMinutes(2);

        public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        // ─── Pure ─────────────────────────────────────────────────────────

        /// <summary>GET /gateway/health. Null means the route is missing (an
        /// older backend): fall back to <see cref="ClassifyChatProbe"/>.</summary>
        public static GatewayState? ClassifyHealth(int status, string body)
        {
            if (status == 404) return null;
            if (status < 200 || status >= 300) return GatewayState.Unknown;
            try
            {
                var j = JObject.Parse(body ?? "");
                var on = j["inference_enabled"];
                if (on == null || on.Type != JTokenType.Boolean) return GatewayState.Unknown;
                return on.Value<bool>() ? GatewayState.Healthy : GatewayState.InferenceDisabled;
            }
            catch { return GatewayState.Unknown; }
        }

        /// <summary>Unauthenticated POST to /gateway/v1/chat/completions, for
        /// backends without /gateway/health. The route checks the inference
        /// flag BEFORE the bearer, so: 404 = off (or no gateway at all),
        /// 401/402/403 = on and guarding the money path. Nothing is billed:
        /// no token, no upstream call.</summary>
        public static GatewayState ClassifyChatProbe(int status)
        {
            if (status == 404) return GatewayState.InferenceDisabled;
            if (status == 401 || status == 402 || status == 403) return GatewayState.Healthy;
            return GatewayState.Unknown;
        }

        /// <summary>Does a saved check allow the switch to engine mode? Only a
        /// recent Healthy for the SAME gateway URL counts.</summary>
        public static bool AllowsEngineMode(GatewayHealthRecord rec, string gatewayUrl, DateTime nowUtc)
        {
            if (rec == null || rec.State != GatewayState.Healthy) return false;
            if (string.IsNullOrWhiteSpace(gatewayUrl)) return false;
            if (!SameUrl(rec.Url, gatewayUrl)) return false;
            var age = nowUtc - rec.CheckedUtc;
            return age >= TimeSpan.Zero && age <= FlipMaxAge;
        }

        /// <summary>Should a turn on an engine-mode box go to the cloud?
        /// Only when the add-in owns the engine (auto-spawn: it hands the
        /// engine exactly this gateway) and the gateway is known not to serve
        /// inference. Unknown keeps the engine path, so a probe glitch never
        /// moves a working box.</summary>
        public static bool TurnGoesToCloud(bool engineAutoSpawn, string gatewayUrl, GatewayState state)
        {
            if (!engineAutoSpawn || string.IsNullOrWhiteSpace(gatewayUrl)) return false;
            return state == GatewayState.InferenceDisabled || state == GatewayState.Unreachable;
        }

        public static bool IsFresh(GatewayHealthRecord rec, string gatewayUrl, DateTime nowUtc)
        {
            if (rec == null || !SameUrl(rec.Url, gatewayUrl)) return false;
            var ttl = rec.State == GatewayState.Healthy ? HealthyTtl : UnhealthyTtl;
            var age = nowUtc - rec.CheckedUtc;
            return age >= TimeSpan.Zero && age <= ttl;
        }

        public static bool SameUrl(string a, string b) =>
            string.Equals((a ?? "").Trim().TrimEnd('/'), (b ?? "").Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);

        // ─── Saved result (gateway-health.json next to config.json) ────────

        private static readonly string RecordPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitWebAppSync", "gateway-health.json");

        // Read once per process: the switch to engine mode decides the
        // transport App.OnStartup boots, so it must not change mid-session
        // when this session's own check lands. That check counts next start.
        private static readonly Lazy<GatewayHealthRecord> _startupRecord =
            new Lazy<GatewayHealthRecord>(ReadRecord);

        public static GatewayHealthRecord StartupRecord => _startupRecord.Value;

        private static GatewayHealthRecord _current;   // this process's latest check
        private static readonly object _gate = new object();

        private static GatewayHealthRecord ReadRecord()
        {
            try
            {
                if (!File.Exists(RecordPath)) return null;
                return JsonConvert.DeserializeObject<GatewayHealthRecord>(File.ReadAllText(RecordPath));
            }
            catch { return null; }
        }

        private static void WriteRecord(GatewayHealthRecord rec)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecordPath));
                File.WriteAllText(RecordPath, JsonConvert.SerializeObject(rec, Formatting.Indented));
            }
            catch { /* the next session just checks again */ }
        }

        // ─── Probe ────────────────────────────────────────────────────────

        /// <summary>This process's answer for <paramref name="gatewayUrl"/>,
        /// re-probing when the last one is older than its TTL. Never throws.</summary>
        public static async Task<GatewayHealthRecord> GetAsync(string gatewayUrl)
        {
            GatewayHealthRecord cur;
            lock (_gate) cur = _current;
            if (IsFresh(cur, gatewayUrl, DateTime.UtcNow)) return cur;
            return await ProbeAsync(gatewayUrl).ConfigureAwait(false);
        }

        /// <summary>Ask the gateway now, save the answer, log it when it
        /// changed. Never throws.</summary>
        public static async Task<GatewayHealthRecord> ProbeAsync(string gatewayUrl)
        {
            var rec = new GatewayHealthRecord
            {
                Url = (gatewayUrl ?? "").TrimEnd('/'),
                CheckedUtc = DateTime.UtcNow,
            };
            if (string.IsNullOrWhiteSpace(gatewayUrl))
            {
                rec.State = GatewayState.Unknown;
                rec.Detail = "no gateway configured";
                return rec;
            }

            try
            {
                using (var http = new HttpClient { Timeout = ProbeTimeout })
                {
                    var health = await http.GetAsync(rec.Url + "/gateway/health").ConfigureAwait(false);
                    var body = await health.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var state = ClassifyHealth((int)health.StatusCode, body);
                    if (state.HasValue)
                    {
                        rec.State = state.Value;
                        rec.Detail = "GET /gateway/health -> " + (int)health.StatusCode;
                    }
                    else
                    {
                        using (var probe = new StringContent("{}", Encoding.UTF8, "application/json"))
                        {
                            var chat = await http.PostAsync(rec.Url + "/gateway/v1/chat/completions", probe)
                                .ConfigureAwait(false);
                            rec.State = ClassifyChatProbe((int)chat.StatusCode);
                            rec.Detail = "no /gateway/health; POST /gateway/v1/chat/completions -> " + (int)chat.StatusCode;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                rec.State = GatewayState.Unreachable;
                rec.Detail = ex.GetType().Name + ": " + Trim(ex.InnerException?.Message ?? ex.Message);
            }

            GatewayHealthRecord prev;
            lock (_gate) { prev = _current; _current = rec; }
            if (prev == null || prev.State != rec.State || !SameUrl(prev.Url, rec.Url))
                Log($"gateway {rec.Url} -> {rec.State} ({rec.Detail})");
            WriteRecord(rec);
            return rec;
        }

        private static string Trim(string s) =>
            string.IsNullOrEmpty(s) || s.Length <= 160 ? s : s.Substring(0, 160);

        // ─── session.log ──────────────────────────────────────────────────

        private static volatile bool _flipBlockLogged;

        /// <summary>Once per process: why a box with a gateway stayed on the cloud.</summary>
        public static void LogFlipBlocked(string gatewayUrl, GatewayHealthRecord rec)
        {
            if (_flipBlockLogged) return;
            _flipBlockLogged = true;
            string why;
            if (rec == null) why = "no gateway check yet (runs in the background; counts from the next Revit start)";
            else if (!SameUrl(rec.Url, gatewayUrl)) why = $"last check was for another gateway ({rec.Url})";
            else if (rec.State != GatewayState.Healthy) why = $"last check {rec.State} at {rec.CheckedUtc:u} ({rec.Detail})";
            else why = $"last healthy check is too old ({rec.CheckedUtc:u})";
            Log($"engine mode NOT enabled for {gatewayUrl}: {why}. Staying on cloud.");
        }

        public static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bina", "RevitSync");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "session.log"),
                    $"{DateTime.Now:HH:mm:ss} [engine] {message}{Environment.NewLine}");
            }
            catch { /* diagnostics must never break a turn */ }
        }
    }
}
