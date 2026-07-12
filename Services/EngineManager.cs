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

        // Crash watchdog state.
        private int _respawns;
        private DateTime _healthySince = DateTime.MinValue;
        private volatile bool _disposing;

        // Readiness/health status for the pane to poll. Volatile string
        // assignment is enough here — we never need read-modify-write.
        private volatile string _status = "starting";
        public string Status { get => _status; private set => _status = value; }

        private static readonly object _logLock = new object();

        private static string EngineRoot => Path.Combine(
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
        /// Revit session), and safe to call again from the crash watchdog.</summary>
        public async Task EnsureRunningAsync()
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
            if (!string.IsNullOrEmpty(cfg.GatewayUrl))
                psi.Environment["BINA_GATEWAY_URL"] = cfg.GatewayUrl;
            if (!string.IsNullOrEmpty(cfg.DeviceToken))
                psi.Environment["BINA_ENGINE_TOKEN"] = cfg.DeviceToken;

            Status = "starting";
            try
            {
                _proc = Process.Start(psi);
                if (_proc == null)
                {
                    Status = "error:spawn-failed";
                    Debug.WriteLine("[BINA] engine Process.Start returned null.");
                    return;
                }

                Directory.CreateDirectory(EngineRoot);
                File.WriteAllText(PidFile, _proc.Id.ToString());
                Debug.WriteLine($"[BINA] engine starting pid={_proc.Id} port={_port} launcher={launcher}");

                _proc.EnableRaisingEvents = true;
                _proc.Exited += OnEngineExited;

                _proc.OutputDataReceived += (s, e) => AppendLog(e.Data);
                _proc.ErrorDataReceived += (s, e) => AppendLog(e.Data);
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Status = "error:spawn-failed";
                Debug.WriteLine("[BINA] engine failed to start: " + ex.Message);
                return;
            }

            // Readiness: poll every 1s up to 20s.
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                if (await IsHealthyAsync())
                {
                    Status = "healthy";
                    _healthySince = DateTime.UtcNow;
                    Debug.WriteLine($"[BINA] engine healthy pid={_proc?.Id} port={_port}");
                    return;
                }
            }

            Status = "error:start-timeout";
            Debug.WriteLine("[BINA] engine did not become healthy within 20s — killing.");
            KillProcessSafely(_proc);
        }

        private async Task<bool> IsHealthyAsync()
        {
            try
            {
                var r = await _http.GetAsync($"http://127.0.0.1:{_port}/health");
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>Crash watchdog. Fires when the spawned process (cmd.exe
        /// wrapping run-engine.cmd, or the legacy exe) exits on its own —
        /// NOT when we kill it ourselves during Dispose (_disposing guard).
        /// A start-timeout kill (see EnsureRunningAsync) is intentionally NOT
        /// guarded — it flows through here and counts against the respawn
        /// cap, which is the desired bounded-retry behavior either way.</summary>
        private async void OnEngineExited(object sender, EventArgs e)
        {
            if (_disposing) return;
            try
            {
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
                await EnsureRunningAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BINA] engine watchdog respawn failed: " + ex.Message);
            }
        }

        private static string NewestEngineLauncher()
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
        /// scripts/build-engine-bundle.ps1) — flat, snake_case, not the
        /// "engine" sub-block UpdateService.EngineFeed merges into
        /// version.json (that's the OTA feed; this is the bundle's own
        /// manifest, read locally next to the launcher).</summary>
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
                if (proc != null && !proc.HasExited) proc.Kill(entireProcessTree: true);
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
                        // layout, not just the retired PyInstaller path.
                        if (p != null && !p.HasExited &&
                            (p.ProcessName.IndexOf("bina-engine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             p.ProcessName.IndexOf("cmd", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            p.Kill(entireProcessTree: true);
                        }
                    }
                    catch { /* pid not alive / not ours — fine */ }
                }
                File.Delete(PidFile);
            }
            catch { /* best-effort */ }
        }

        public void Dispose()
        {
            _disposing = true;
            try
            {
                if (_proc != null && !_proc.HasExited) _proc.Kill(entireProcessTree: true);
                if (File.Exists(PidFile)) File.Delete(PidFile);
            }
            catch { }
        }
    }
}
