using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Diagnostics upload (OTA self-heal F5 + F6): builds the scrubbed
    /// DiagnosticsBundle zip and POSTs it (multipart, field "file") to
    /// &lt;AI host&gt;/addin/revit/diagnostics with X-Bina-Install-Id and
    /// X-Bina-Diagnostics-Reason — the same bina-ai host telemetry goes to.
    /// Works without sign-in and while a forced update locks the add-in: that
    /// is exactly when it is needed.
    ///
    /// Three triggers:
    /// - manual: "Send diagnostics" (returns the upload id for the user);
    /// - auto:&lt;kind&gt;/&lt;stage&gt;: TelemetryService reports a failure event
    ///   (DiagnosticsPolicy.IsAutoTrigger), max 1 per 6 h per machine;
    /// - requested: the feed answered diagnostics_requested: true, once per session.
    /// The unattended two never run from Debug builds or when config.json says
    /// "DiagnosticsAuto": false, run on a background thread and never throw.
    /// </summary>
    internal static class DiagnosticsUploader
    {
        /// <summary>Let the failing code path finish writing its logs first.</summary>
        private static readonly TimeSpan AutoDelay = TimeSpan.FromSeconds(15);

        private static int _unattendedBusy;
        private static int _requestedThisSession;

        private static string StatePath => Path.Combine(UpdateService.RootDir, "diagnostics.json");

        public static async Task<string> SendAsync(string reason = DiagnosticsPolicy.ManualReason)
        {
            var root = UpdateService.RootDir;
            var zip = await Task.Run(() =>
                DiagnosticsBundle.Build(root, PluginLogs(root), DiagnosticsScrubber.ForCurrentMachine()));

            var cfg = BinaConfig.Load();
            var endpoint = (cfg.ResolvedCloudBaseUrl ?? "").TrimEnd('/') + "/addin/revit/diagnostics";

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            using (var form = new MultipartFormDataContent())
            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                var file = new ByteArrayContent(zip);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                form.Add(file, "file", "diagnostics.zip");
                form.Add(new StringContent(UpdateService.CurrentVersion?.ToString() ?? ""), "addin_version");
                form.Add(new StringContent(UpdateService.LoaderVersion), "loader_version");
                form.Add(new StringContent(UpdateService.HostVersion ?? ""), "host_version");
                req.Content = form;
                UpdateService.AddFleetHeaders(req.Headers);
                req.Headers.TryAddWithoutValidation("X-Bina-Diagnostics-Reason", reason);

                using (var resp = await http.SendAsync(req))
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"upload failed: HTTP {(int)resp.StatusCode}");
                    string id = null;
                    try { id = (string)JObject.Parse(body)["id"]; } catch { }
                    TelemetryService.Track("diagnostics", "sent", new { bytes = zip.Length, reason });
                    return string.IsNullOrWhiteSpace(id) ? "(no id returned)" : id;
                }
            }
        }

        /// <summary>TelemetryService hook: a failure event uploads the logs by
        /// itself (subject to the 6 h throttle). Returns immediately.</summary>
        internal static void OnTelemetryEvent(string kind, string stage)
        {
            try
            {
                if (!DiagnosticsPolicy.IsAutoTrigger(kind, stage)) return;
                RunUnattended(DiagnosticsPolicy.AutoReason(kind, stage), AutoDelay, claim: () =>
                    DiagnosticsPolicy.TryClaimAutoSlot(StatePath, DateTime.UtcNow));
            }
            catch { }
        }

        /// <summary>Feed hook: an admin asked for this machine's logs. Uploads
        /// once per session; the server clears the request when it arrives.</summary>
        internal static void OnDiagnosticsRequested()
        {
            try
            {
                if (Interlocked.Exchange(ref _requestedThisSession, 1) == 1) return;
                RunUnattended(DiagnosticsPolicy.RequestedReason, TimeSpan.Zero, claim: () =>
                {
                    DiagnosticsPolicy.RecordRequested(StatePath, DateTime.UtcNow);
                    return true;
                });
            }
            catch { }
        }

        private static void RunUnattended(string reason, TimeSpan delay, Func<bool> claim)
        {
            Task.Run(async () =>
            {
                if (Interlocked.Exchange(ref _unattendedBusy, 1) == 1)
                {
                    Log($"{reason}: another upload is running, skipped");
                    return;
                }
                try
                {
                    if (!Enabled()) return;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
                    if (!claim())
                    {
                        Log($"{reason}: skipped (an automatic upload ran in the last 6 h)");
                        return;
                    }
                    var id = await SendAsync(reason).ConfigureAwait(false);
                    Log($"{reason}: uploaded, id {id}");
                }
                catch (Exception ex)
                {
                    Log($"{reason}: upload failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _unattendedBusy, 0);
                }
            });
        }

        private static bool Enabled()
        {
#if DEBUG
            const bool debugBuild = true;
#else
            const bool debugBuild = false;
#endif
            bool? optOut = null;
            try { optOut = BinaConfig.Load().DiagnosticsAuto; } catch { }
            return DiagnosticsPolicy.UnattendedEnabled(debugBuild, BinaConfig.DEFAULT_DIAGNOSTICS_AUTO, optOut);
        }

        private static void Log(string message)
        {
            try
            {
                var root = UpdateService.RootDir;
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "updater.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [diagnostics] {message}{Environment.NewLine}");
            }
            catch { }
        }

        /// <summary>Plugin logs outside the root's top level: the logs\ tree
        /// (CAD to BIM, newest engine logs).</summary>
        private static IEnumerable<string> PluginLogs(string root)
        {
            try
            {
                var logs = Path.Combine(root, "logs");
                if (!Directory.Exists(logs)) return Enumerable.Empty<string>();
                return Directory.GetFiles(logs, "*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(3)
                    .ToList();
            }
            catch { return Enumerable.Empty<string>(); }
        }
    }
}
