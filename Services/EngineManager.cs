// EngineManager — owns the local BINA Copilot Engine process lifecycle (Phase 4 / v2).
//
// The engine is bina-ai's app/engine packaged as a self-contained bundle
// (embeddable Python + app source + run-engine.cmd launcher), shipped
// alongside the add-in and versioned under
//   %LocalAppData%\Bina\RevitSync\engine\<ver>\
// newest-semver-wins (mirrors BinaLoader). This manager:
//   - generates a per-boot secret (replaces the Phase-1 shared-config secret),
//   - health-checks 127.0.0.1:<port>/health and spawns the engine if absent,
//   - hands the engine its secret + port + gateway config via environment
//     (the engine reads BINA_ENGINE_SECRET / BINA_ENGINE_PORT / BINA_GATEWAY_URL /
//     BINA_ENGINE_TOKEN — see app/engine/config.py),
//   - refuses to start an engine bundle that requires a newer add-in
//     (engine-version.json's min_addin_version vs UpdateService.CurrentVersion),
//   - records a pidfile so a stale engine from a crashed Revit is killed,
//   - watches the spawned process and respawns on crash (capped, backed off),
//   - exposes Status ("healthy" | "starting" | "error:<reason>") for the pane
//     to poll,
//   - stops the engine on add-in shutdown.
//
// Windows-only path; loopback-only; the secret is never logged.
//
// Launch mechanics: run-engine.cmd is a batch file, not a PE — .NET's
// Process.Start (UseShellExecute=false, required so we can set Environment[]
// and redirect stdio) cannot CreateProcess a .cmd directly. We wrap it with
// `cmd.exe /c "<path>"`, which is the standard, always-works way to launch a
// batch file under UseShellExecute=false. The legacy bina-engine.exe fallback
// (real PE) is launched directly, no cmd.exe wrapper.
//
// Because cmd.exe /c runs the .cmd's last line (python.exe -m uvicorn ...)
// synchronously (no `start`), our _proc (the cmd.exe host) only exits when
// the actual server exits — so Process.Exited still fires at the right time
// for the crash watchdog. But it also means a plain Kill() on _proc only
// terminates cmd.exe and can leak the uvicorn child (see
// scripts/build-engine-bundle.ps1's -Smoke cleanup, which has to walk
// Win32_Process to find and kill that child explicitly). We avoid that leak
// here with Process.Kill(entireProcessTree: true) (available on our net8.0/
// net10.0 TFMs) everywhere we deliberately kill the engine.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    public sealed class EngineManager : IDisposable
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private readonly int _port;
        private readonly string _secret;
        private Process _proc;

        // Provider keys stripped from the engine's environment in gateway mode
        // (see the spawn path). MUST stay in sync with _PROVIDER_KEY_ENVS in
        // bina-ai's app/engine/config.py — that list is what the engine's
        // poison-pill checks, and a key we miss here still bricks the start.
        private static readonly string[] ProviderKeyEnvs =
        {
            "DEEPSEEK_API_KEY", "OPENAI_API_KEY",
            "AZURE_OPENAI_API_KEY", "GATEWAY_UPSTREAM_KEY",
        };

        // Crash watchdog state. _respawns/_healthySince are only mutated while
        // holding _gate (single-flight), so no interlocked ops needed.
        private int _respawns;
        private DateTime _healthySince = DateTime.MinValue;
        private volatile bool _disposing;

        // Single-flight gate: watchdog respawns queue behind any in-flight
        // EnsureRunningAsync instead of racing it (a second concurrent attempt
        // used to overwrite _proc, letting the first attempt's readiness
        // timeout kill the second's healthy respawn → cascading Exited →
        // false crash-loop). SemaphoreSlim, NOT lock() — the body awaits.
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        // Readiness/health status for the pane to poll. Volatile string
        // assignment is enough here — we never need read-modify-write.
        private volatile string _status = "starting";
        public string Status { get => _status; private set => _status = value; }

        private static readonly object _logLock = new object();

        // internal (not private): BinaConfig.ApplyDefaults reuses this exact
        // probe to decide whether a local engine bundle is installed before
        // auto-flipping EngineMode/EngineAutoSpawn on first run — see
        // BinaConfig.cs. Do not duplicate the probe logic there.
        internal static string EngineRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bina", "RevitSync", "engine");
        private static string PidFile => Path.Combine(EngineRoot, "engine.pid");
        private static string LogsDir => Path.Combine(EngineRoot, "logs");

        public EngineManager(int port, string secret)
        {
            _port = port;
            _secret = secret ?? "";
        }

        /// <summary>Ensure an engine is answering on the loopback port; spawn the
        /// newest installed version if not. Idempotent — safe to call at startup
        /// even if a healthy engine is already running (e.g. left by a prior
        /// Revit session), and safe to call again from the crash watchdog.
        /// Single-flight: concurrent callers queue on _gate.</summary>
        public async Task EnsureRunningAsync()
        {
            await _gate.WaitAsync();
            try
            {
                await EnsureRunningCoreAsync();
            }
            finally
            {
                // Dispose() may have disposed _gate while we were in flight.
                try { _gate.Release(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>The actual ensure/spawn/readiness body. MUST be called with
        /// _gate held (EnsureRunningAsync or the watchdog's gated section).</summary>
        private async Task EnsureRunningCoreAsync()
        {
            KillStaleFromPidFile();
            if (await IsHealthyAsync())
            {
                Status = "healthy";   // something already serving — reuse it
                return;
            }

            var launcher = NewestEngineLauncher();
            if (launcher == null)
            {
                Status = "error:not-installed";
                Debug.WriteLine("[BINA] engine launcher not found under " + EngineRoot + " — not started.");
                return;
            }

            if (TooOldForEngine(launcher, out var gateReason))
            {
                Status = "error:addin-too-old";
                Debug.WriteLine("[BINA] engine version gate refused start: " + gateReason);
                return;
            }

            var isCmd = string.Equals(Path.GetExtension(launcher), ".cmd", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(launcher),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (isCmd)
            {
                // .cmd is not a PE; CreateProcess can't launch it directly under
                // UseShellExecute=false, so route it through cmd.exe /c.
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c \"{launcher}\"";
            }
            else
            {
                psi.FileName = launcher;   // legacy bina-engine.exe (PyInstaller) — real PE
            }

            // The engine reads these from the environment (app/engine/config.py).
            psi.Environment["BINA_ENGINE"] = "1";
            psi.Environment["BINA_ENGINE_SECRET"] = _secret;
            psi.Environment["BINA_ENGINE_PORT"] = _port.ToString();

            // Gateway env (colocate pipeline): defensive reads — Task 4 may not
            // have added these BinaConfig properties on every machine yet.
            var cfg = BinaConfig.Load();
            if (!string.IsNullOrEmpty(cfg.ResolvedGatewayUrl))
                psi.Environment["BINA_GATEWAY_URL"] = cfg.ResolvedGatewayUrl;
            if (!string.IsNullOrEmpty(cfg.DeviceToken))
                psi.Environment["BINA_ENGINE_TOKEN"] = cfg.DeviceToken;

            // Colocate tracing (2026-08-18): the engine holds NO Langfuse
            // credentials (poison-pill design). Its Langfuse client is
            // pointed at the GATEWAY's tracing proxy instead, authenticating
            // with the machine's own device token as the "public key" —
            // /gateway/langfuse validates the token and forwards to the real
            // Langfuse host with server-side creds. Without this, every
            // colocate turn is invisible to tracing (the 2026-08-18 blind
            // debugging session). Both gateway URL + token required.
            if (!string.IsNullOrEmpty(cfg.ResolvedGatewayUrl) && !string.IsNullOrEmpty(cfg.DeviceToken))
            {
                psi.Environment["LANGFUSE_BASE_URL"] =
                    cfg.ResolvedGatewayUrl.TrimEnd('/') + "/gateway/langfuse";
                psi.Environment["LANGFUSE_PUBLIC_KEY"] = cfg.DeviceToken;
                psi.Environment["LANGFUSE_SECRET_KEY"] = "engine";
            }

            // Poison-pill compatibility (2026-08-25). The engine refuses to
            // start when a provider key is visible in its environment AND a
            // gateway is configured — app/engine/config.py's
            // assert_no_provider_keys, enforcing the colocate invariant that a
            // gateway-configured desktop must never hold keys that let it
            // bypass the gateway and talk to a provider directly.
            //
            // Process.Start hands the child OUR environment, so any
            // user- or machine-scope OPENAI_API_KEY on the box (a developer's
            // shell key, a leftover from the pre-gateway dev path) reached the
            // engine and killed every start with "engine refuses to start:
            // provider key(s) present on a gateway-configured machine".
            //
            // Strip them from the CHILD environment only. The engine then
            // genuinely holds no provider credentials — which is what the pill
            // is protecting — while the key stays available to everything else
            // on the machine. Same posture as the Langfuse block above: the
            // engine authenticates to the gateway, never to a provider.
            if (!string.IsNullOrEmpty(cfg.ResolvedGatewayUrl))
            {
                foreach (var key in ProviderKeyEnvs)
                {
                    if (psi.Environment.Remove(key))
                        Debug.WriteLine("[BINA] stripped " + key + " from engine env (gateway mode).");
                }
            }

            Status = "starting";
            // Track the process THIS attempt started in a local — even if a
            // queued attempt later replaces _proc, our timeout path can only
            // ever kill our own spawn, never a sibling's healthy one.
            Process proc;
            try
            {
                // Dispose-safe spawn window: Dispose() doesn't take _gate (a
                // sync Dispose blocking Revit shutdown for up to 20s+ of
                // readiness polling would be worse), so a fast startup→teardown
                // could otherwise land Dispose BEFORE _proc is assigned and
                // orphan the just-spawned engine forever (pidfile already
                // deleted by Dispose). Check right before Start...
                if (_disposing)
                {
                    Debug.WriteLine("[BINA] disposing — not spawning engine.");
                    return;
                }
                proc = Process.Start(psi);
                if (proc == null)
                {
                    Status = "error:spawn-failed";
                    Debug.WriteLine("[BINA] engine Process.Start returned null.");
                    return;
                }
                _proc = proc;
                // ...and re-check right after the assignment: a Dispose that
                // ran in the check→Start instant missed this proc (it read the
                // old _proc), so reap it here ourselves.
                if (_disposing)
                {
                    Debug.WriteLine("[BINA] disposed during spawn — killing engine.");
                    KillProcessSafely(proc);
                    return;
                }

                Directory.CreateDirectory(EngineRoot);
                File.WriteAllText(PidFile, proc.Id.ToString());
                Debug.WriteLine($"[BINA] engine starting pid={proc.Id} port={_port} launcher={launcher}");

                // Subscribe BEFORE enabling events — the reverse order has a
                // window where an instant crash raises no Exited callback.
                proc.Exited += OnEngineExited;
                proc.EnableRaisingEvents = true;

                proc.OutputDataReceived += (s, e) => AppendLog(e.Data);
                proc.ErrorDataReceived += (s, e) => AppendLog(e.Data);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Status = "error:spawn-failed";
                Debug.WriteLine("[BINA] engine failed to start: " + ex.Message);
                Services.TelemetryService.Track("engine", "spawn_failed",
                    new { error_class = ex.GetType().Name });
                return;
            }

            // Readiness: poll every 1s against a hard 60s wall-clock deadline
            // (a fixed iteration count could stretch further when each health
            // probe eats its full 2s HttpClient timeout).
            // 60s, was 20s (2026-08-19): a cold engine-bundle first boot on a
            // drafter box (AV scan + python import) routinely exceeds 20s —
            // same cost class as the measured 58s first-regen tax. At 20s this
            // KILLED a healthy-but-slow engine, the watchdog counted the kill
            // as a crash, and three rounds later Status locked at crash-loop —
            // every turn then died with "connection refused localhost:48810".
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(1000);
                if (await IsHealthyAsync())
                {
                    Status = "healthy";
                    _healthySince = DateTime.UtcNow;
                    Debug.WriteLine($"[BINA] engine healthy pid={proc.Id} port={_port}");
                    return;
                }
            }

            Status = "error:start-timeout";
            Debug.WriteLine("[BINA] engine did not become healthy within 60s — killing.");
            KillProcessSafely(proc);
        }

        private async Task<bool> IsHealthyAsync()
        {
            try
            {
                var r = await _http.GetAsync($"http://127.0.0.1:{_port}/health");
                if (!r.IsSuccessStatusCode) return false;
                // Shape check: a foreign process squatting on our port that
                // happens to answer 200 must NOT be attached as our engine
                // (app/engine/main.py health returns {"engine": true}).
                var body = await r.Content.ReadAsStringAsync();
                return body.Contains("\"engine\"");
            }
            catch { return false; }
        }

        /// <summary>Crash watchdog. Fires when the spawned process (cmd.exe
        /// wrapping run-engine.cmd, or the legacy exe) exits on its own —
        /// NOT when we kill it ourselves during Dispose (_disposing guard).
        /// A start-timeout kill (see EnsureRunningCoreAsync) is intentionally
        /// NOT guarded — it flows through here and counts against the respawn
        /// cap, which is the desired bounded-retry behavior either way.
        /// Single-flight: all bookkeeping + the respawn run under _gate, so a
        /// respawn queues behind any in-flight ensure attempt instead of
        /// racing it. Calls EnsureRunningCoreAsync directly (SemaphoreSlim is
        /// not reentrant — going through EnsureRunningAsync would deadlock).</summary>
        private async void OnEngineExited(object sender, EventArgs e)
        {
            if (_disposing) return;
            try
            {
                await _gate.WaitAsync();
                try
                {
                    if (_disposing) return;

                    // Stale event: a queued/newer attempt already replaced the
                    // process this handler belongs to — its exit is history,
                    // not a crash of the CURRENT engine. Don't burn the cap.
                    if (!ReferenceEquals(sender, _proc)) return;

                    if (_healthySince != DateTime.MinValue &&
                        DateTime.UtcNow - _healthySince >= TimeSpan.FromMinutes(10))
                    {
                        _respawns = 0;   // 10 minutes of healthy uptime forgives past crashes
                    }
                    _healthySince = DateTime.MinValue;

                    if (_respawns >= 3)
                    {
                        Status = "error:crash-loop";
                        Debug.WriteLine("[BINA] engine crash-loop — giving up after 3 respawn attempts.");
                        return;
                    }

                    _respawns++;
                    Debug.WriteLine($"[BINA] engine exited unexpectedly — respawn attempt {_respawns}/3.");
                    await EnsureRunningCoreAsync();
                }
                finally
                {
                    // Dispose() may have disposed _gate while we were in flight.
                    try { _gate.Release(); } catch (ObjectDisposedException) { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BINA] engine watchdog respawn failed: " + ex.Message);
                Services.TelemetryService.Track("engine", "respawn_failed",
                    new { error_class = ex.GetType().Name });
            }
        }

        // internal (not private): see EngineRoot's comment above — reused by
        // BinaConfig.ApplyDefaults as the "is an engine bundle installed?" probe.
        internal static string NewestEngineLauncher()
        {
            if (!Directory.Exists(EngineRoot)) return null;
            return Directory.GetDirectories(EngineRoot)
                .Select(d => new { dir = d, name = Path.GetFileName(d) })
                .Where(x => Version.TryParse(x.name, out _))
                .OrderByDescending(x => Version.Parse(x.name))
                .SelectMany(x => new[]
                {
                    Path.Combine(x.dir, "run-engine.cmd"),
                    Path.Combine(x.dir, "bina-engine.exe"),   // legacy PyInstaller probe
                })
                .FirstOrDefault(File.Exists);
        }

        /// <summary>Version gate: refuse to spawn an engine bundle whose
        /// engine-version.json declares a min_addin_version newer than this
        /// add-in (UpdateService.CurrentVersion). Missing/unparseable manifest
        /// = no floor, best-effort (never blocks a start over a read glitch).</summary>
        private static bool TooOldForEngine(string launcher, out string reason)
        {
            reason = null;
            try
            {
                var dir = Path.GetDirectoryName(launcher);
                var manifestPath = Path.Combine(dir ?? "", "engine-version.json");
                if (!File.Exists(manifestPath)) return false;

                var manifest = JsonConvert.DeserializeObject<EngineVersionManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.MinAddinVersion)) return false;
                if (!Version.TryParse(manifest.MinAddinVersion, out var floor)) return false;

                var current = UpdateService.CurrentVersion;
                if (current < floor)
                {
                    reason = $"engine {manifest.EngineVersion} requires addin >= {floor}, running {current}";
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BINA] engine-version.json read failed (non-blocking): " + ex.Message);
                return false;
            }
        }

        /// <summary>Shape of engine-version.json (see
        /// scripts/build-engine-bundle.ps1) — flat, snake_case, distinct
        /// from UpdateService.UpdateFeed's EngineVersion/EngineUrl/
        /// EngineSha256 fields (that's the OTA feed; this is the bundle's
        /// own manifest, read locally next to the launcher).</summary>
        private sealed class EngineVersionManifest
        {
            [JsonProperty("engine_version")] public string EngineVersion { get; set; }
            [JsonProperty("git_sha")] public string GitSha { get; set; }
            [JsonProperty("min_addin_version")] public string MinAddinVersion { get; set; }
            [JsonProperty("python")] public string Python { get; set; }
            [JsonProperty("built_at")] public string BuiltAt { get; set; }
        }

        private static void AppendLog(string line)
        {
            if (line == null) return;
            try
            {
                Directory.CreateDirectory(LogsDir);
                var path = Path.Combine(LogsDir, $"engine-{DateTime.Now:yyyyMMdd}.log");
                lock (_logLock)
                {
                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
                }
            }
            catch { /* best-effort — never let logging break the engine */ }
        }

        private static void KillProcessSafely(Process proc)
        {
            try
            {
                if (proc != null && !proc.HasExited) RuntimeCompat.KillTree(proc);
            }
            catch { /* best-effort */ }
        }

        private static void KillStaleFromPidFile()
        {
            try
            {
                if (!File.Exists(PidFile)) return;
                if (int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
                {
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        // v2 launches via cmd.exe (run-engine.cmd) or, legacy,
                        // bina-engine.exe directly — accept either name so the
                        // stale-kill safety net still works with the new bundle
                        // layout. "cmd" must be an EXACT match (ProcessName has
                        // no .exe suffix): a substring test also hits e.g.
                        // cmdagent.exe, and a recycled PID would then kill an
                        // innocent process WITH its whole tree.
                        var isOurs = p != null && !p.HasExited &&
                            (string.Equals(p.ProcessName, "cmd", StringComparison.OrdinalIgnoreCase) ||
                             p.ProcessName.IndexOf("bina-engine", StringComparison.OrdinalIgnoreCase) >= 0);

                        // Recycled-PID guard: the pidfile is written right AFTER
                        // Process.Start, so the real engine's start time precedes
                        // the file's write time. A process that started after it
                        // merely reuses the PID — leave it alone.
                        if (isOurs &&
                            p.StartTime.ToUniversalTime()
                                > File.GetLastWriteTimeUtc(PidFile).AddSeconds(5))
                        {
                            isOurs = false;
                        }

                        if (isOurs) RuntimeCompat.KillTree(p);
                    }
                    catch { /* pid not alive / not ours — fine */ }
                }
                File.Delete(PidFile);
            }
            catch { /* best-effort */ }
        }

        public void Dispose()
        {
            // Flag FIRST: Core checks it before Process.Start and re-checks
            // after the _proc assignment, so a spawn racing this Dispose is
            // either never started or reaped by Core itself.
            _disposing = true;
            try
            {
                var p = _proc;   // single read of the shared field
                if (p != null && !p.HasExited) RuntimeCompat.KillTree(p);
                if (File.Exists(PidFile)) File.Delete(PidFile);
            }
            catch { }
            // After the kill logic: any queued watchdog WaitAsync throws
            // ObjectDisposedException into its own catch-all and exits.
            try { _gate.Dispose(); } catch { }
        }
    }
}
