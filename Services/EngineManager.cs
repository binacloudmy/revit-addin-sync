// EngineManager — owns the local BINA Copilot Engine process lifecycle (Phase 4).
//
// The engine is bina-ai's app/engine packaged as bina-engine.exe (PyInstaller),
// shipped alongside the add-in and versioned under
//   %LocalAppData%\Bina\RevitSync\engine\<ver>\bina-engine.exe
// newest-semver-wins (mirrors BinaLoader). This manager:
//   - generates a per-boot secret (replaces the Phase-1 shared-config secret),
//   - health-checks 127.0.0.1:<port>/health and spawns the engine if absent,
//   - hands the engine its secret + port via environment (the engine reads
//     BINA_ENGINE_SECRET / BINA_ENGINE_PORT),
//   - records a pidfile so a stale engine from a crashed Revit is killed,
//   - stops the engine on add-in shutdown.
//
// Windows-only path; loopback-only; the secret is never logged.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    public sealed class EngineManager : IDisposable
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private readonly int _port;
        private readonly string _secret;
        private Process _proc;

        private static string EngineRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bina", "RevitSync", "engine");
        private static string PidFile => Path.Combine(EngineRoot, "engine.pid");

        public EngineManager(int port, string secret)
        {
            _port = port;
            _secret = secret ?? "";
        }

        /// <summary>Ensure an engine is answering on the loopback port; spawn the
        /// newest installed version if not. Idempotent — safe to call at startup
        /// even if a healthy engine is already running (e.g. left by a prior
        /// Revit session).</summary>
        public async Task EnsureRunningAsync()
        {
            KillStaleFromPidFile();
            if (await IsHealthyAsync()) return;   // something already serving — reuse it

            var exe = NewestEngineExe();
            if (exe == null)
            {
                Debug.WriteLine("[BINA] engine exe not found under " + EngineRoot + " — not started.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            // The engine reads these from the environment (app/engine/config.py).
            psi.Environment["BINA_ENGINE"] = "1";
            psi.Environment["BINA_ENGINE_SECRET"] = _secret;
            psi.Environment["BINA_ENGINE_PORT"] = _port.ToString();

            try
            {
                _proc = Process.Start(psi);
                if (_proc != null)
                {
                    Directory.CreateDirectory(EngineRoot);
                    File.WriteAllText(PidFile, _proc.Id.ToString());
                    Debug.WriteLine($"[BINA] engine started pid={_proc.Id} port={_port}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BINA] engine failed to start: " + ex.Message);
            }
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

        private static string NewestEngineExe()
        {
            if (!Directory.Exists(EngineRoot)) return null;
            // version dirs sorted by semver-ish; newest wins (mirror BinaLoader).
            var newest = Directory.GetDirectories(EngineRoot)
                .Select(d => new { dir = d, name = Path.GetFileName(d) })
                .Where(x => Version.TryParse(x.name, out _))
                .OrderByDescending(x => Version.Parse(x.name))
                .Select(x => Path.Combine(x.dir, "bina-engine.exe"))
                .FirstOrDefault(File.Exists);
            return newest;
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
                        if (p != null && !p.HasExited &&
                            p.ProcessName.IndexOf("bina-engine", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            p.Kill();
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
            try
            {
                if (_proc != null && !_proc.HasExited) _proc.Kill();
                if (File.Exists(PidFile)) File.Delete(PidFile);
            }
            catch { }
        }
    }
}
