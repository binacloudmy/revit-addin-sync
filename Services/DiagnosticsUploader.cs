using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// "Send diagnostics" (OTA self-heal F5): builds the DiagnosticsBundle zip
    /// and POSTs it (multipart, field "file") to
    /// &lt;AI host&gt;/addin/revit/diagnostics with X-Bina-Install-Id — the same
    /// bina-ai host telemetry goes to. Returns the server's upload id, which the
    /// user reads out to support. Works without sign-in and while a forced
    /// update locks the add-in: that is exactly when it is needed.
    /// </summary>
    internal static class DiagnosticsUploader
    {
        public static async Task<string> SendAsync()
        {
            var root = UpdateService.RootDir;
            var zip = await Task.Run(() => DiagnosticsBundle.Build(root, PluginLogs(root)));

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

                using (var resp = await http.SendAsync(req))
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"upload failed: HTTP {(int)resp.StatusCode}");
                    string id = null;
                    try { id = (string)JObject.Parse(body)["id"]; } catch { }
                    TelemetryService.Track("diagnostics", "sent", new { bytes = zip.Length });
                    return string.IsNullOrWhiteSpace(id) ? "(no id returned)" : id;
                }
            }
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
