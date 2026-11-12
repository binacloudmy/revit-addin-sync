using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    public enum ReinstallDecision
    {
        /// <summary>No min_loader_version, or the running loader already meets it.</summary>
        NotNeeded,
        /// <summary>Loader too old but the feed carries no usable installer (url + sha256).</summary>
        MissingInstaller,
        /// <summary>Debug builds never reinstall themselves.</summary>
        DebugBuild,
        /// <summary>Three tries for this installer version already — stop, report.</summary>
        AttemptsExhausted,
        /// <summary>Last try was under six hours ago.</summary>
        TooSoon,
        Schedule,
    }

    /// <summary>
    /// Pure decisions behind the silent self-reinstall (OTA self-heal F2): the
    /// OTA zip can only replace the plugin payload, never the loader shim in
    /// Revit's Addins folder, the .addin manifests, the TrustedPublisher cert
    /// or the engine logon task. When the feed says the loader is too old
    /// (min_loader_version), the plugin downloads the setup EXE and runs it
    /// silently after Revit exits. No IO, no Revit — see SelfReinstall for the
    /// side-effecting half.
    /// </summary>
    public static class ReinstallPolicy
    {
        public const int MaxAttempts = 3;
        public static readonly TimeSpan MinInterval = TimeSpan.FromHours(6);

        private static readonly Regex Sha256Hex = new Regex("^[0-9a-fA-F]{64}$");

        public static ReinstallDecision Decide(
            string loaderVersion, string minLoaderVersion, string installerUrl, string installerSha256,
            string installerVersion, bool isDebugBuild, ReinstallState state, DateTime utcNow)
        {
            if (!TryParse3(minLoaderVersion, out var min)) return ReinstallDecision.NotNeeded;
            TryParse3(NormalizeLoaderVersion(loaderVersion), out var loader);
            if (loader >= min) return ReinstallDecision.NotNeeded;

            if (!IsHttps(installerUrl) || string.IsNullOrWhiteSpace(installerSha256) ||
                !Sha256Hex.IsMatch(installerSha256.Trim()))
                return ReinstallDecision.MissingInstaller;

            if (isDebugBuild) return ReinstallDecision.DebugBuild;

            if (state != null && string.Equals(state.Version, installerVersion, StringComparison.OrdinalIgnoreCase))
            {
                if (state.Attempts >= MaxAttempts) return ReinstallDecision.AttemptsExhausted;
                if (utcNow - state.LastAttempt.ToUniversalTime() < MinInterval) return ReinstallDecision.TooSoon;
            }

            return ReinstallDecision.Schedule;
        }

        /// <summary>The loader's published version (AppDomain data
        /// "Bina.LoaderVersion") as MAJOR.MINOR.PATCH. Loaders from before this
        /// contract publish nothing: "0.0.0", i.e. older than any minimum.</summary>
        public static string NormalizeLoaderVersion(object published)
        {
            return TryParse3(published as string, out var v)
                ? v.ToString(3)
                : "0.0.0";
        }

        private static bool TryParse3(string s, out Version v)
        {
            v = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(s) || !Version.TryParse(s.Trim(), out var parsed)) return false;
            v = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }

        private static bool IsHttps(string url) =>
            !string.IsNullOrWhiteSpace(url) &&
            Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) &&
            u.Scheme == Uri.UriSchemeHttps;

        /// <summary>PowerShell single-quoted literal: the only escape is '' for '.</summary>
        public static string PsQuote(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

        /// <summary>
        /// The hidden helper: wait for THIS Revit to exit, then for any other
        /// Revit (every session holds BinaLoader.dll open, and a locked loader
        /// is exactly what the reinstall must replace), then run setup silently.
        /// </summary>
        public static string BuildHelperScript(int hostPid, string installerPath, string logPath)
        {
            var args = "'/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS'";
            if (!string.IsNullOrWhiteSpace(logPath))
                args += "," + PsQuote("/LOG=\"" + logPath + "\"");

            var sb = new StringBuilder();
            sb.Append("Wait-Process -Id ").Append(hostPid.ToString(CultureInfo.InvariantCulture))
              .Append(" -ErrorAction SilentlyContinue\n");
            sb.Append("while (Get-Process -Name Revit -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 20 }\n");
            sb.Append("Start-Process -FilePath ").Append(PsQuote(installerPath))
              .Append(" -ArgumentList ").Append(args).Append(" -Wait\n");
            return sb.ToString();
        }

        /// <summary>powershell.exe arguments. -EncodedCommand (UTF-16LE base64)
        /// carries the script as opaque data, so no path in it — spaces, quotes,
        /// non-ASCII profile names — ever passes through command-line parsing.</summary>
        public static string BuildPowerShellArguments(string script) =>
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>&lt;root&gt;\reinstall.json — the loop guard.</summary>
    public sealed class ReinstallState
    {
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("attempts")] public int Attempts { get; set; }
        [JsonProperty("last_attempt")] public DateTime LastAttempt { get; set; }

        public static ReinstallState Next(ReinstallState current, string version, DateTime utcNow)
        {
            var same = current != null && string.Equals(current.Version, version, StringComparison.OrdinalIgnoreCase);
            return new ReinstallState
            {
                Version = version,
                Attempts = same ? current.Attempts + 1 : 1,
                LastAttempt = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            };
        }

        public string ToJson() => JsonConvert.SerializeObject(this, new JsonSerializerSettings
        {
            DateFormatString = "yyyy-MM-dd'T'HH:mm:ss'Z'",
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        });

        /// <summary>Null for missing or corrupt content — never throws.</summary>
        public static ReinstallState Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonConvert.DeserializeObject<ReinstallState>(json, new JsonSerializerSettings
                {
                    DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                });
            }
            catch { return null; }
        }
    }
}
