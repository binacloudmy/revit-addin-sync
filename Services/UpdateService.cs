using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// OTA update pipeline (download side — BinaLoader is the apply side).
    ///
    /// Startup check hits the feed (bina-ai /addin/version.json). When a newer
    /// build exists and the feed marks it mandatory (default), the plugin is
    /// GATED: every ribbon command calls <see cref="EnsureUpToDate"/> first and
    /// bails until the user downloads the update (UpdateWindow) and restarts
    /// Revit. Non-mandatory updates stage silently and toast once.
    ///
    /// Staged builds land in %LocalAppData%\Bina\RevitSync\versions\&lt;ver&gt;\
    /// with a .complete marker; nothing running is ever touched, so no
    /// reinstall and no admin rights.
    ///
    /// Feed JSON: { "version": "0.0.2", "url": "https://.../x.zip",
    ///              "sha256": "...", "notes": "...", "mandatory": true }
    /// </summary>
    public static class UpdateService
    {
        private const string CompleteMarker = ".complete";

        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bina", "RevitSync");

        private static readonly string VersionsDir = Path.Combine(Root, "versions");
        private static readonly string StagingDir = Path.Combine(Root, "staging");
        private static readonly string LogPath = Path.Combine(Root, "updater.log");

        private static UIControlledApplication _app;
        private static volatile UpdateFeed _pending;   // newer build available
        private static volatile bool _staged;          // it is on disk, restart applies it
        private static bool _notified;

        /// <summary>Newer build waiting (for UI: version, notes…). Null = up to date.</summary>
        public static UpdateFeed Pending => _pending;

        /// <summary>True once the pending build is fully staged on disk.</summary>
        public static bool IsStaged => _staged;

        public static Version CurrentVersion => GetCurrentVersion();

        public static void Start(UIControlledApplication application)
        {
            _app = application;
            var feedUrl = BinaConfig.Load().ResolvedUpdateFeedUrl;
            if (string.IsNullOrWhiteSpace(feedUrl))
            {
                Log("no update feed configured — updater disabled");
                return;
            }

            application.Idling += OnIdling;

            Task.Run(async () =>
            {
                try
                {
                    await CheckAsync(feedUrl);

                    // Non-mandatory updates keep the old silent behavior; the
                    // Idling hook only toasts. Mandatory ones wait for the
                    // Idling hook to raise the blocking UpdateWindow.
                    if (_pending != null && !_pending.Mandatory)
                        await StageAsync(null);
                }
                catch (Exception ex)
                {
                    Log($"update check failed: {ex}");
                }
            });
        }

        /// <summary>
        /// Command gate. Call first in every IExternalCommand.Execute:
        /// returns true when the running build is usable; otherwise shows the
        /// update UI (or the restart nag once staged) and returns false.
        /// </summary>
        public static bool EnsureUpToDate()
        {
            var pending = _pending;
            if (pending == null || !pending.Mandatory)
                return true;

            if (_staged)
            {
                TaskDialog.Show("BINA Sync",
                    $"Update {pending.Version} is installed.\n\nPlease restart Revit to continue using BINA Sync.");
                return false;
            }

            ShowUpdateWindow();
            return false;
        }

        /// <summary>Download + verify + stage the pending build, reporting
        /// (0..1, status) progress. Used by UpdateWindow's Update button.</summary>
        public static Task StageAsync(IProgress<(double Fraction, string Status)> progress) =>
            StageCoreAsync(_pending ?? throw new InvalidOperationException("no pending update"), progress);

        private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_pending == null || _notified)
                return;

            _notified = true;
            try { _app.Idling -= OnIdling; } catch { }

            if (_pending.Mandatory)
                ShowUpdateWindow();
            else if (_staged)
                TaskDialog.Show("BINA Sync",
                    $"Update {_pending.Version} downloaded.\n\nIt will take effect the next time you start Revit.");
        }

        private static void ShowUpdateWindow()
        {
            try
            {
                new UI.UpdateWindow().ShowDialog();
            }
            catch (Exception ex)
            {
                Log($"update window failed: {ex}");
            }
        }

        private static async Task CheckAsync(string feedUrl)
        {
            using var http = NewHttp();
            var feed = JsonConvert.DeserializeObject<UpdateFeed>(await http.GetStringAsync(feedUrl));
            if (feed?.Version == null || feed.Url == null)
            {
                Log($"malformed feed at {feedUrl}");
                return;
            }

            if (!Version.TryParse(feed.Version, out var remote))
            {
                Log($"unparseable feed version '{feed.Version}'");
                return;
            }

            // Stage the engine payload independently of the add-in version — the
            // engine can update on its own cadence. Best-effort, never blocks.
            await StageEngineIfPresentAsync(feed.Engine);

            var current = GetCurrentVersion();
            if (remote <= current)
            {
                Log($"up to date (current {current}, feed {remote})");
                return;
            }

            if (File.Exists(Path.Combine(VersionsDir, remote.ToString(), CompleteMarker)))
            {
                Log($"{remote} already staged");
                _staged = true;
            }

            Log($"update available: {remote} (current {current}, mandatory {feed.Mandatory})");
            _pending = feed;
        }

        private static async Task StageCoreAsync(UpdateFeed feed,
            IProgress<(double, string)> progress)
        {
            var remote = Version.Parse(feed.Version);
            var targetDir = Path.Combine(VersionsDir, remote.ToString());
            if (File.Exists(Path.Combine(targetDir, CompleteMarker)))
            {
                _staged = true;
                return;
            }

            Log($"staging {remote} from {feed.Url}");
            Directory.CreateDirectory(StagingDir);
            var zipPath = Path.Combine(StagingDir, $"{remote}.zip");
            var extractDir = Path.Combine(StagingDir, remote.ToString());

            try
            {
                using var http = NewHttp();
                using (var response = await http.GetAsync(feed.Url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? -1L;
                    await using var download = await response.Content.ReadAsStreamAsync();
                    await using var zipStream = File.Create(zipPath);

                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await download.ReadAsync(buffer)) > 0)
                    {
                        await zipStream.WriteAsync(buffer.AsMemory(0, read));
                        done += read;
                        if (total > 0)
                            progress?.Report(((double)done / total * 0.9,
                                $"Downloading… {done / 1048576.0:F1} / {total / 1048576.0:F1} MB"));
                    }
                }

                progress?.Report((0.92, "Verifying…"));
                if (!string.IsNullOrWhiteSpace(feed.Sha256))
                {
                    using var file = File.OpenRead(zipPath);
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
                    if (!actual.Equals(feed.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"download corrupted (SHA256 mismatch) — try again");
                }

                progress?.Report((0.95, "Installing…"));
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, recursive: true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                // Marker is written BEFORE the move so the folder is never visible
                // under versions\ in a half-staged state; the move itself is atomic
                // on the same volume.
                File.WriteAllText(Path.Combine(extractDir, CompleteMarker), feed.Version);

                Directory.CreateDirectory(VersionsDir);
                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, recursive: true); // stale incomplete leftover
                Directory.Move(extractDir, targetDir);

                Log($"staged {remote} → {targetDir}");
                _staged = true;
                progress?.Report((1.0, "Done"));
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }

        private static HttpClient NewHttp() =>
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        /// <summary>Effective running version. Prefer the versions\&lt;ver&gt;\ folder
        /// name we were loaded from (survives builds that forget to bump
        /// AssemblyVersion); fall back to the assembly version.</summary>
        private static Version GetCurrentVersion()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var parent = Path.GetDirectoryName(dir);
            if (string.Equals(parent, VersionsDir, StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(Path.GetFileName(dir), out var fromDir))
                return fromDir;

            return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [updater] {message}{Environment.NewLine}");
            }
            catch { }
        }

        public sealed class UpdateFeed
        {
            [JsonProperty("version")] public string Version { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("sha256")] public string Sha256 { get; set; }
            [JsonProperty("notes")] public string Notes { get; set; }

            // Missing flag = mandatory: the backend and addin move together,
            // so a stale client is broken by default unless the feed opts out.
            [JsonProperty("mandatory")] public bool Mandatory { get; set; } = true;

            // Phase 4: optional Copilot Engine payload, shipped in the SAME
            // version.json. Staged into engine\<ver>\ hot-safe, like the addin.
            [JsonProperty("engine")] public EngineFeed Engine { get; set; }
        }

        public sealed class EngineFeed
        {
            [JsonProperty("version")] public string Version { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("sha256")] public string Sha256 { get; set; }
            // Refuse an engine build that needs a newer addin than is running
            // (wire-format compatibility). Empty = no floor.
            [JsonProperty("minAddinVersion")] public string MinAddinVersion { get; set; }
        }

        private static readonly string EngineDir = Path.Combine(Root, "engine");

        /// <summary>Stage the engine payload if the feed carries a newer one and
        /// this add-in satisfies its minAddinVersion. Self-contained (does not
        /// touch the add-in staging path); newest-semver hot-safe: never
        /// overwrites a running engine\&lt;ver&gt;\ dir. Best-effort — a failed
        /// engine stage never blocks the add-in update.</summary>
        private static async Task StageEngineIfPresentAsync(EngineFeed engine)
        {
            if (engine?.Version == null || engine.Url == null) return;
            if (!Version.TryParse(engine.Version, out var remote))
            {
                Log($"engine: unparseable version '{engine.Version}'");
                return;
            }

            // compatibility floor: don't stage an engine that needs a newer addin
            if (!string.IsNullOrWhiteSpace(engine.MinAddinVersion)
                && Version.TryParse(engine.MinAddinVersion, out var floor)
                && GetCurrentVersion() < floor)
            {
                Log($"engine {remote} needs addin >= {floor}; running {GetCurrentVersion()} — skipped");
                return;
            }

            var targetDir = Path.Combine(EngineDir, remote.ToString());
            if (File.Exists(Path.Combine(targetDir, CompleteMarker)))
            {
                Log($"engine {remote} already staged");
                return;
            }

            try
            {
                Directory.CreateDirectory(StagingDir);
                var zipPath = Path.Combine(StagingDir, $"engine-{remote}.zip");
                using (var http = NewHttp())
                {
                    var bytes = await http.GetByteArrayAsync(engine.Url);
                    if (!string.IsNullOrWhiteSpace(engine.Sha256))
                    {
                        using var sha = System.Security.Cryptography.SHA256.Create();
                        var got = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                        if (!string.Equals(got, engine.Sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                        {
                            Log($"engine {remote} sha256 mismatch — refused");
                            return;
                        }
                    }
                    File.WriteAllBytes(zipPath, bytes);
                }

                var extractDir = Path.Combine(StagingDir, $"engine-{remote}");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

                Directory.CreateDirectory(EngineDir);
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                Directory.Move(extractDir, targetDir);          // atomic-ish final move
                File.WriteAllText(Path.Combine(targetDir, CompleteMarker), remote.ToString());
                try { File.Delete(zipPath); } catch { }
                Log($"engine {remote} staged — EngineManager picks it up next Revit start");
            }
            catch (Exception ex)
            {
                Log($"engine {remote} stage failed (non-blocking): {ex.Message}");
            }
        }
    }
}
