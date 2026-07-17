using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Fleet telemetry sender — fire-and-forget lifecycle events to bina-ai
    /// POST /telemetry/events (anonymous; startup fires before login).
    ///
    /// Contract: NEVER throws, NEVER blocks the caller (Track enqueues and
    /// returns). Events survive offline/firewalled sessions in a JSONL spool
    /// (%LocalAppData%\Bina\RevitSync\telemetry.spool.jsonl, capped at 500)
    /// and drain on the next successful flush. PII: exception TYPE NAMES only.
    /// </summary>
    internal static class TelemetryService
    {
        private const int SpoolCap = 500;
        private const int BatchMax = 50;

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private static readonly ConcurrentQueue<TelemetryEvent> _queue =
            new ConcurrentQueue<TelemetryEvent>();
        private static readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);
        private static readonly string SpoolPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bina", "RevitSync", "telemetry.spool.jsonl");

        private static Timer _timer;
        private static string _machineId = "";
        private static string _operator = "";
        private static string _addinVersion = "";
        private static string _revitYear = "";
        private static string _endpoint = "";

        internal static void Init(string revitYear)
        {
            try
            {
                _machineId = TelemetryIdentity.MachineId(
                    Environment.MachineName, Environment.UserName);
                // Human-readable operator label so alerts can say WHO is broken
                // (works pre-login — startup failures included). Deliberate
                // identity capture, see TelemetryEvent PII note.
                _operator = Environment.UserName + "@" + Environment.MachineName;
                _addinVersion = UpdateService.CurrentVersion?.ToString() ?? "";
                _revitYear = revitYear ?? "";
                _endpoint = (BinaConfig.Load().ResolvedCloudBaseUrl ?? "").TrimEnd('/')
                            + "/telemetry/events";
                LoadSpool();
                _timer = new Timer(_ => { var __ = FlushAsync(); },
                                   null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
            catch { /* telemetry must never break startup */ }
        }

        internal static void Track(string kind, string stage, object payload = null)
        {
            try
            {
                _queue.Enqueue(TelemetryEvent.Create(
                    kind, stage, _machineId, _operator, _addinVersion, _revitYear,
                    engineVersion: "", payload: payload, utcNow: DateTime.UtcNow));
                var __ = FlushAsync();
            }
            catch { /* never throws */ }
        }

        /// <summary>Synchronous best-effort drain for paths where the process
        /// is about to die (fatal OnStartup, OnShutdown).</summary>
        internal static void FlushBlocking(TimeSpan timeout)
        {
            // Loop: one FlushAsync sends at most one 50-event batch — a busy
            // session's shutdown drain must not silently drop events 51+.
            try
            {
                var deadline = DateTime.UtcNow + timeout;
                while (!_queue.IsEmpty)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    if (!FlushAsync().Wait(remaining)) break;
                }
            }
            catch { }
        }

        private static async Task FlushAsync()
        {
            if (string.IsNullOrEmpty(_endpoint)) return;
            if (!await _flushGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                var batch = new List<TelemetryEvent>();
                while (batch.Count < BatchMax && _queue.TryDequeue(out var ev))
                    batch.Add(ev);
                if (batch.Count == 0) return;

                try
                {
                    var content = new StringContent(
                        TelemetryBatch.ToJson(batch), Encoding.UTF8, "application/json");
                    var resp = await _http.PostAsync(_endpoint, content).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        Spool(batch);
                }
                catch
                {
                    Spool(batch);   // offline / firewalled — retry next session
                }
            }
            catch { /* never throws */ }
            finally { _flushGate.Release(); }
        }

        private static void Spool(List<TelemetryEvent> batch)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SpoolPath));
                var lines = new List<string>();
                if (File.Exists(SpoolPath))
                    lines.AddRange(File.ReadAllLines(SpoolPath));
                lines.AddRange(batch.Select(e => JsonConvert.SerializeObject(e)));
                if (lines.Count > SpoolCap)
                    lines = lines.Skip(lines.Count - SpoolCap).ToList();
                File.WriteAllLines(SpoolPath, lines);
            }
            catch { }
        }

        private static void LoadSpool()
        {
            try
            {
                if (!File.Exists(SpoolPath)) return;
                foreach (var line in File.ReadAllLines(SpoolPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var ev = JsonConvert.DeserializeObject<TelemetryEvent>(line);
                    if (ev != null) _queue.Enqueue(ev);
                }
                File.Delete(SpoolPath);
            }
            catch { }
        }
    }
}
