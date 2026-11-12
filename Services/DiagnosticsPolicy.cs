using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// When the add-in uploads diagnostics without the user (OTA self-heal F6):
    /// automatically right after a startup / update / reinstall failure (at most
    /// once per 6 h per machine, state in &lt;root&gt;\diagnostics.json), and once
    /// when the feed says an admin asked for this machine's logs. Also the
    /// X-Bina-Diagnostics-Reason values the server stores with each upload.
    /// Revit-free and network-free; DiagnosticsUploader acts on it.
    /// </summary>
    public static class DiagnosticsPolicy
    {
        public const string ManualReason = "manual";
        public const string RequestedReason = "requested";

        public static readonly TimeSpan AutoInterval = TimeSpan.FromHours(6);

        private static readonly object StateLock = new object();

        /// <summary>The telemetry events that upload logs by themselves. The
        /// loader skipping a bad build is reported as update/bad_build.</summary>
        public static bool IsAutoTrigger(string kind, string stage)
        {
            switch ((kind ?? "") + "/" + (stage ?? ""))
            {
                case "startup/failed":
                case "update/bad_build":
                case "update/stage_failed":
                case "reinstall/failed":
                    return true;
                default:
                    return false;
            }
        }

        public static string AutoReason(string kind, string stage) => $"auto:{kind}/{stage}";

        /// <summary>
        /// Unattended (automatic or admin-requested) uploads run only when the
        /// channel ships DIAGNOSTICS_AUTO on, config.json has not opted this
        /// machine out (DiagnosticsAuto: false), and the build is not Debug.
        /// config.json can only turn it off, never on.
        /// </summary>
        public static bool UnattendedEnabled(bool debugBuild, string envValue, bool? configValue)
        {
            if (debugBuild) return false;
            if (configValue == false) return false;
            var v = (envValue ?? "").Trim();
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }

        /// <summary>
        /// Claim this machine's automatic upload slot: true (and last_auto_at
        /// recorded) when no automatic upload ran in the last 6 h. The claim is
        /// recorded BEFORE uploading, so a failing upload cannot retry in a
        /// loop. When the claim cannot be written the answer is false: an
        /// unthrottled machine is worse than one missing upload.
        /// </summary>
        public static bool TryClaimAutoSlot(string statePath, DateTime utcNow)
        {
            lock (StateLock)
            {
                try
                {
                    var state = Read(statePath);
                    var last = ReadTime(state, "last_auto_at");
                    // A timestamp in the future is a clock that was wrong; ignore it.
                    if (last.HasValue && last.Value <= utcNow && utcNow - last.Value < AutoInterval)
                        return false;
                    state["last_auto_at"] = utcNow.ToString("o");
                    Write(statePath, state);
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>Note an admin-requested upload (information only: it never
        /// consumes the automatic slot). Never throws.</summary>
        public static void RecordRequested(string statePath, DateTime utcNow)
        {
            lock (StateLock)
            {
                try
                {
                    var state = Read(statePath);
                    state["last_requested_at"] = utcNow.ToString("o");
                    Write(statePath, state);
                }
                catch { }
            }
        }

        private static JObject Read(string path)
        {
            try
            {
                if (File.Exists(path) && JToken.Parse(File.ReadAllText(path)) is JObject o) return o;
            }
            catch { }
            return new JObject();
        }

        private static void Write(string path, JObject state)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, state.ToString());
        }

        private static DateTime? ReadTime(JObject state, string key)
        {
            var token = state[key];
            // JToken.Parse turns ISO strings into Date tokens already.
            if (token?.Type == JTokenType.Date) return ((DateTime)token).ToUniversalTime();
            var raw = token?.Type == JTokenType.String ? (string)token : null;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t.ToUniversalTime()
                : (DateTime?)null;
        }
    }
}
