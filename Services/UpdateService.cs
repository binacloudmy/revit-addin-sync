using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
    /// After startup, checks the update feed in the background. A newer build
    /// is downloaded to staging, SHA256-verified, extracted, and atomically
    /// moved to %LocalAppData%\Bina\RevitSync\versions\&lt;ver&gt;\ with a
    /// .complete marker. BinaLoader picks it up on the next Revit start —
    /// nothing running is ever touched, so no reinstall and no admin rights.
    ///
    /// Feed JSON: { "version": "0.0.2", "url": "https://.../x.zip",
    ///              "sha256": "...", "notes": "..." }
    /// Feed URL comes from BinaConfig.ResolvedUpdateFeedUrl; empty = disabled.
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

        /// <summary>Set when a new build has been fully staged; the one-shot
        /// Idling handler turns it into a TaskDialog on the UI thread.</summary>
        private static volatile string _stagedVersion;
        private static bool _notified;
        private static UIControlledApplication _app;

        public static void Start(UIControlledApplication application)
        {
            _app = application;
            var feedUrl = BinaConfig.Load().ResolvedUpdateFeedUrl;
            if (string.IsNullOrWhiteSpace(feedUrl))
            {
                Log("no update feed configured — updater disabled");
                return;
            }

            // TaskDialog must run on the UI thread; Idling is the cheap way in.
            application.Idling += OnIdlingNotify;

            Task.Run(async () =>
            {
                try
                {
                    await CheckAndStageAsync(feedUrl);
                }
                catch (Exception ex)
                {
                    Log($"update check failed: {ex}");
                }
            });
        }

        private static void OnIdlingNotify(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_stagedVersion == null || _notified)
                return;

            _notified = true;
            try { _app.Idling -= OnIdlingNotify; } catch { }

            TaskDialog.Show("BINA Sync",
                $"Update {_stagedVersion} downloaded.\n\nIt will take effect the next time you start Revit.");
        }

        private static async Task CheckAndStageAsync(string feedUrl)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

            var feedJson = await http.GetStringAsync(feedUrl);
            var feed = JsonConvert.DeserializeObject<UpdateFeed>(feedJson);
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

            var current = CurrentVersion();
            if (remote <= current)
            {
                Log($"up to date (current {current}, feed {remote})");
                return;
            }

            var targetDir = Path.Combine(VersionsDir, remote.ToString());
            if (File.Exists(Path.Combine(targetDir, CompleteMarker)))
            {
                Log($"{remote} already staged");
                _stagedVersion = remote.ToString();
                return;
            }

            Log($"staging {remote} (current {current}) from {feed.Url}");
            Directory.CreateDirectory(StagingDir);
            var zipPath = Path.Combine(StagingDir, $"{remote}.zip");
            var extractDir = Path.Combine(StagingDir, remote.ToString());

            try
            {
                using (var zipStream = File.Create(zipPath))
                await using (var download = await http.GetStreamAsync(feed.Url))
                    await download.CopyToAsync(zipStream);

                if (!string.IsNullOrWhiteSpace(feed.Sha256))
                {
                    using var file = File.OpenRead(zipPath);
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
                    if (!actual.Equals(feed.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"SHA256 mismatch for {remote}: expected {feed.Sha256}, got {actual}");
                        return;
                    }
                }

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
                _stagedVersion = remote.ToString();
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }

        /// <summary>Effective running version. Prefer the versions\&lt;ver&gt;\ folder
        /// name we were loaded from (survives builds that forget to bump
        /// AssemblyVersion); fall back to the assembly version.</summary>
        private static Version CurrentVersion()
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

        private sealed class UpdateFeed
        {
            [JsonProperty("version")] public string Version { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("sha256")] public string Sha256 { get; set; }
            [JsonProperty("notes")] public string Notes { get; set; }
        }
    }
}
