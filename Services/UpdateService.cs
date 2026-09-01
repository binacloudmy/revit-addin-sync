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
        private static readonly string GateStatePath = Path.Combine(Root, "gate.json");

        private static UIControlledApplication _app;
        private static volatile UpdateFeed _pending;   // newer build available
        private static volatile bool _staged;          // it is on disk, restart applies it
        private static bool _notified;

        // Hard floor: builds below this are refused outright (see UpdateGate).
        // Null = no floor. Set from the feed's minAddinVersion, from a 426
        // response (ApplyServerFloor), or from the persisted last-seen floor.
        private static volatile Version _floor;
        private static volatile Version _feedVersion;  // newest published build, for the floor sanity check
        private static string _feedUrl;

        /// <summary>Newer build waiting (for UI: version, notes…). Null = up to date.</summary>
        public static UpdateFeed Pending => _pending;

        /// <summary>True once the pending build is fully staged on disk.</summary>
        public static bool IsStaged => _staged;

        public static Version CurrentVersion => GetCurrentVersion();

        /// <summary>Version floor in force, or null when unrestricted.</summary>
        public static Version RequiredVersion => _floor;

        /// <summary>Raised whenever the gate state changes — the feed check is
        /// async and 426s arrive mid-session, so surfaces that render the gate
        /// (the Copilot wall) cannot just read it once at construction.</summary>
        public static event Action GateChanged;

        /// <summary>Current gate state. Cheap — safe to call per render. The rules
        /// themselves live in <see cref="UpdateGateRules"/> so they stay testable.</summary>
        public static UpdateGate Gate =>
            UpdateGateRules.Evaluate(GetCurrentVersion(), _floor, _feedVersion, _pending != null,
                                     RunningFromVersionsStore, _staged);

        /// <summary>Raise the floor from a backend 426 (Upgrade Required). Lets
        /// the server lock a build mid-session without waiting for the next feed
        /// poll — and reaches clients whose feed is unreachable.</summary>
        public static void ApplyServerFloor(Version floor)
        {
            if (floor == null || (_floor != null && floor <= _floor)) return;
            _floor = floor;
            Log($"floor raised to {floor} by backend (426)");
            PersistFloor(floor);
            TelemetryService.Track("update", "gate_blocked",
                new { required = floor.ToString(), source = "server_426" });
            RaiseGateChanged();
        }

        /// <summary>Ask every gate surface to re-read the state. For hosts that
        /// change it out of band — e.g. UpdateWindow closing after a stage.</summary>
        public static void NotifyGateChanged() => RaiseGateChanged();

        private static void RaiseGateChanged()
        {
            try { GateChanged?.Invoke(); }
            catch (Exception ex) { Log($"GateChanged handler threw: {ex.GetType().Name}"); }
        }

        /// <summary>Adopt the feed's minAddinVersion. Every rejection path here
        /// leaves the gate open — see Evaluate's fail-open note.</summary>
        private static void ApplyFeedFloor(UpdateFeed feed, Version remote, Version current)
        {
            if (string.IsNullOrWhiteSpace(feed?.MinAddinVersion))
                return;

            if (!Version.TryParse(feed.MinAddinVersion, out var floor))
            {
                Log($"unparseable minAddinVersion '{feed.MinAddinVersion}' — ignored");
                TelemetryService.Track("update", "floor_invalid",
                    new { floor = feed.MinAddinVersion, reason = "unparseable" });
                return;
            }

            if (floor > remote)
            {
                // The demanded build was never published. Honouring this would
                // lock out every client with no route back.
                Log($"minAddinVersion {floor} exceeds feed version {remote} — ignored");
                TelemetryService.Track("update", "floor_invalid",
                    new { floor = floor.ToString(), feed_version = remote.ToString(), reason = "above_latest" });
                return;
            }

            _floor = floor;
            PersistFloor(floor);

            if (current < floor)
            {
                Log($"GATE: running {current} is below required floor {floor}");
                TelemetryService.Track("update", "gate_blocked",
                    new { required = floor.ToString(), current = current.ToString(), source = "feed" });
            }
        }

        /// <summary>Reload the last floor we were told about. Without this,
        /// starting Revit with the feed unreachable would silently unlock a
        /// build the server has already retired.</summary>
        private static void LoadPersistedFloor()
        {
            try
            {
                if (!File.Exists(GateStatePath)) return;
                var state = JsonConvert.DeserializeObject<GateState>(File.ReadAllText(GateStatePath));
                if (state == null || !Version.TryParse(state.MinAddinVersion, out var floor)) return;

                var current = GetCurrentVersion();
                if (current >= floor)
                {
                    // Recovered — drop the stale record so a later downgrade of
                    // the floor is not shadowed by this file.
                    Log($"gate cleared: running {current} satisfies stored floor {floor}");
                    TelemetryService.Track("update", "gate_cleared",
                        new { required = floor.ToString(), current = current.ToString() });
                    try { File.Delete(GateStatePath); } catch { }
                    return;
                }

                _floor = floor;
                Log($"restored floor {floor} from gate.json (running {current})");
            }
            catch (Exception ex)
            {
                Log($"gate.json read failed (non-blocking): {ex.Message}");
            }
        }

        private static void PersistFloor(Version floor)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(GateStatePath,
                    JsonConvert.SerializeObject(new GateState { MinAddinVersion = floor.ToString() }));
            }
            catch (Exception ex)
            {
                Log($"gate.json write failed (non-blocking): {ex.Message}");
            }
        }

        private sealed class GateState
        {
            [JsonProperty("min_addin_version")] public string MinAddinVersion { get; set; }
        }

        public static void Start(UIControlledApplication application)
        {
            _app = application;
            var feedUrl = BinaConfig.Load().ResolvedUpdateFeedUrl;
            _feedUrl = feedUrl;
            if (string.IsNullOrWhiteSpace(feedUrl))
            {
                // No feed = dev box (.env.local ships a blank URL). Never gate:
                // a floor left in gate.json by a previous prod install must not
                // follow a developer into a local build.
                Log("no update feed configured — updater disabled");
                return;
            }

            // Load the last floor we were told about BEFORE the async check, so
            // "start Revit with the network off" is not a way around the gate.
            LoadPersistedFloor();

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
                    {
                        // Own catch: StageCoreAsync already Tracks stage_failed;
                        // letting it bubble would double-report as check_failed.
                        try { await StageAsync(null); }
                        catch (Exception stageEx)
                        {
                            Log($"silent stage failed: {stageEx.GetType().Name}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"update check failed: {ex}");
                    TelemetryService.Track("update", "check_failed",
                        new { error_class = ex.GetType().Name });
                }
                finally
                {
                    // Always — a surface rendered before the check finished has to
                    // re-read the gate whether the check found a floor, found
                    // nothing, or failed outright.
                    RaiseGateChanged();
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
            var gate = Gate;
            var pending = _pending;

            // A floor blocks regardless of the feed's mandatory flag — that flag
            // is chosen at release time and cannot be applied retroactively to a
            // build already in the field; the floor can.
            if (!gate.Blocked && (pending == null || !pending.Mandatory))
                return true;

            if (gate.Reason == GateReason.NoPayload)
            {
                TaskDialog.Show("BINA Sync",
                    $"BINA Sync {gate.Current} is no longer supported — version {gate.Required} or newer is required.\n\n" +
                    "The update could not be reached. Check your connection and restart Revit.");
                return false;
            }

            // A build that did NOT come from versions\ can never be replaced by
            // the updater: BinaLoader only ever boots versions\<ver>\, so the
            // staged folder sits there unread and "restart Revit" is a promise
            // we cannot keep. The old code looped that nag forever with every
            // command dead behind it. Say what is actually wrong instead.
            if (!RunningFromVersionsStore)
            {
                TaskDialog.Show("BINA Sync",
                    $"BINA Sync {CurrentVersion} is running from a manual install, " +
                    $"so update {TargetVersionLabel} cannot be applied automatically.\n\n" +
                    $"Running from:\n{RunningLocation}\n\n" +
                    "Fix: reinstall BINA Sync (this removes the manual copy), then restart Revit.");
                return false;
            }

            if (_staged)
            {
                TaskDialog.Show("BINA Sync",
                    $"Update {TargetVersionLabel} is installed.\n\nPlease restart Revit to continue using BINA Sync.");
                return false;
            }

            ShowUpdateWindow();
            return false;
        }

        /// <summary>Best label for the build the user is being sent to: the
        /// pending payload's version, else the floor, else a neutral phrase. A
        /// 426 can raise the floor before (or without) any feed payload landing,
        /// so <see cref="Pending"/> is not guaranteed non-null while blocked.</summary>
        private static string TargetVersionLabel =>
            _pending?.Version ?? _floor?.ToString() ?? "a newer version";

        /// <summary>Download + verify + stage the pending build, reporting
        /// (0..1, status) progress. Used by UpdateWindow's Update button.</summary>
        public static Task StageAsync(IProgress<(double Fraction, string Status)> progress) =>
            StageCoreAsync(_pending ?? throw new InvalidOperationException("no pending update"), progress);

        /// <summary>Stage the pending build, re-checking the feed first when no
        /// payload is known yet (GateReason.NoPayload — a 426 raised the floor
        /// while the feed was unreachable). Used by the Copilot update wall so
        /// its one CTA works in both states.</summary>
        public static async Task StageOrRefreshAsync(IProgress<(double Fraction, string Status)> progress)
        {
            if (_pending == null)
            {
                progress?.Report((0, "Checking for updates…"));
                if (string.IsNullOrWhiteSpace(_feedUrl))
                    throw new InvalidOperationException("no update feed configured");
                await CheckAsync(_feedUrl);
                RaiseGateChanged();
            }

            await StageAsync(progress);
        }

        private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            var blocked = Gate.Blocked;
            if ((_pending == null && !blocked) || _notified)
                return;

            _notified = true;
            try { _app.Idling -= OnIdling; } catch { }

            if (blocked || (_pending != null && _pending.Mandatory))
                ShowUpdateWindow();
            else if (_staged)
                TaskDialog.Show("BINA Sync",
                    $"Update {_pending.Version} downloaded.\n\nIt will take effect the next time you start Revit.");
        }

        private static void ShowUpdateWindow()
        {
            // Same reasoning as the EnsureUpToDate gate: offering "Update now"
            // on a manual install downloads a payload into versions\ that this
            // machine will never boot. Report the real problem once instead.
            if (!RunningFromVersionsStore)
            {
                Log($"manual install ({RunningLocation}) — update {TargetVersionLabel} cannot be applied");
                TelemetryService.Track("update", "blocked_manual_install",
                    new { to_version = TargetVersionLabel });
                TaskDialog.Show("BINA Sync",
                    $"BINA Sync {CurrentVersion} is running from a manual install, " +
                    $"so update {TargetVersionLabel} cannot be applied automatically.\n\n" +
                    $"Running from:\n{RunningLocation}\n\n" +
                    "Fix: reinstall BINA Sync (this removes the manual copy), then restart Revit.");
                return;
            }

            // The window renders Pending's version/notes; with a floor from a 426
            // and no feed payload there is nothing for it to show.
            if (_pending == null)
            {
                TaskDialog.Show("BINA Sync",
                    $"BINA Sync {CurrentVersion} is no longer supported — version {_floor} or newer is required.\n\n" +
                    "The update could not be reached. Check your connection and restart Revit.");
                return;
            }

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
                TelemetryService.Track("update", "feed_malformed");
                return;
            }

            if (!Version.TryParse(feed.Version, out var remote))
            {
                Log($"unparseable feed version '{feed.Version}'");
                return;
            }

            _feedVersion = remote;

            // Stage the engine payload independently of the add-in version — the
            // engine can update on its own cadence. Best-effort, never blocks.
            await CheckEngineAsync(feed);

            var current = GetCurrentVersion();

            // Floor BEFORE the up-to-date short-circuit: a floor is meaningful
            // even when this client is already on the newest published build
            // (the gate then resolves to "not blocked" via Evaluate, but the
            // value still has to be recorded and persisted).
            ApplyFeedFloor(feed, remote, current);

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
            TelemetryService.Track("update", "available",
                new { to_version = remote.ToString() });
        }

        private static async Task StageCoreAsync(UpdateFeed feed,
            IProgress<(double, string)> progress)
        {
            var remote = Version.Parse(feed.Version);
            var targetDir = Path.Combine(VersionsDir, remote.ToString());
            if (File.Exists(Path.Combine(targetDir, CompleteMarker)))
            {
                _staged = true;
                RaiseGateChanged();
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
                    using var download = await response.Content.ReadAsStreamAsync();
                    using var zipStream = File.Create(zipPath);

                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await download.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await zipStream.WriteAsync(buffer, 0, read);
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
                    var actual = RuntimeCompat.ToHexString(await RuntimeCompat.Sha256Async(file));
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
                TelemetryService.Track("update", "staged",
                    new { to_version = remote.ToString() });
                // Flips a blocked Copilot wall from "Update now" to "restart Revit".
                RaiseGateChanged();
            }
            catch (Exception ex)
            {
                Log($"stage {remote} failed: {ex}");
                TelemetryService.Track("update", "stage_failed",
                    new { to_version = remote.ToString(), error_class = ex.GetType().Name });
                throw;   // UpdateWindow still surfaces the failure to the user
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }

        private static HttpClient NewHttp() =>
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        /// <summary>Where the running plugin assembly actually sits. Blank when
        /// the location is unavailable (single-file / in-memory host).</summary>
        private static string RunningLocation
        {
            get
            {
                try { return Assembly.GetExecutingAssembly().Location ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>True when BinaLoader booted us out of versions\&lt;ver&gt;\ —
        /// the only arrangement in which a staged update can ever take effect.
        /// False for a direct-load install (a leftover RevitWebAppSync.addin in
        /// Addins\&lt;year&gt;\, or the dev PostBuild deploy): the loader is not in
        /// the chain, so nothing will ever read versions\. Mirrors the same test
        /// <see cref="LegacyInstallCleaner"/> uses to decide whether purging is
        /// safe, so the two agree on what "properly installed" means.</summary>
        private static bool RunningFromVersionsStore =>
            RunningLocation.StartsWith(VersionsDir, StringComparison.OrdinalIgnoreCase);

        /// <summary>Effective running version. Prefer the versions\&lt;ver&gt;\ folder
        /// name we were loaded from (survives builds that forget to bump
        /// AssemblyVersion); fall back to the assembly version. Handles both
        /// layouts: flat legacy (versions\&lt;ver&gt;\*.dll) and multi-year
        /// (versions\&lt;ver&gt;\net8.0\*.dll) — walk up until the parent is
        /// versions\ and the folder name parses as a version.</summary>
        private static Version GetCurrentVersion()
        {
            for (var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                 !string.IsNullOrEmpty(dir);
                 dir = Path.GetDirectoryName(dir))
            {
                if (string.Equals(Path.GetDirectoryName(dir), VersionsDir, StringComparison.OrdinalIgnoreCase)
                    && Version.TryParse(Path.GetFileName(dir), out var fromDir))
                    return fromDir;
            }

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

            // HARD floor, distinct from Mandatory: builds below this are refused
            // outright (the Copilot is walled, every command bails), and unlike
            // Mandatory it can be raised for builds ALREADY in the field. Absent
            // = no floor. Never set it above Version — see ApplyFeedFloor.
            [JsonProperty("minAddinVersion")] public string MinAddinVersion { get; set; }

            // Optional Copilot Engine bundle channel, shipped as flat fields in
            // the SAME version.json as the addin payload above. All three are
            // OPTIONAL and independent of the addin version fields — old feeds
            // that omit any of them leave this channel entirely inert (see
            // CheckEngineAsync). Staged into engine\<EngineVersion>\, hot-safe,
            // same restart-to-apply UX as the addin's own update.
            [JsonProperty("engineVersion")] public string EngineVersion { get; set; }
            [JsonProperty("engineUrl")] public string EngineUrl { get; set; }
            [JsonProperty("engineSha256")] public string EngineSha256 { get; set; }
        }

        private static readonly string EngineDir = Path.Combine(Root, "engine");

        /// <summary>Stage the engine bundle if the feed carries one and it is
        /// newer than the newest installed engine\&lt;ver&gt;\ dir. Self-contained
        /// (does not touch the add-in staging path); never overwrites a
        /// running engine — the new version is only picked up at the next
        /// <c>EnsureRunningAsync()</c> (next Revit start). Best-effort — a
        /// failed engine stage never blocks the add-in update. Skips entirely
        /// when any of the three feed fields is missing (old feeds — channel
        /// inert).</summary>
        /// <summary>Last engine-stage failure, for the turn preflight to put in
        /// front of the drafter instead of a socket error. Null after success.</summary>
        internal static volatile string LastEngineStageError;

        /// <summary>Mid-session entry for the turn preflight: re-read the feed
        /// and stage the engine it names. Startup calls CheckEngineAsync with
        /// the feed it already fetched; a turn that finds no bundle on disk has
        /// no feed in hand, so it re-fetches. NOT UpdateService.Pending — that
        /// is the ADD-IN update, a different channel. True when a bundle is on
        /// disk afterwards (staged now, or was already there).</summary>
        internal static async Task<bool> EnsureEngineBundleAsync()
        {
            try
            {
                var feedUrl = BinaConfig.Load().ResolvedUpdateFeedUrl;
                if (string.IsNullOrWhiteSpace(feedUrl))
                {
                    LastEngineStageError = "no update feed configured";
                    return NewestInstalledEngineVersion() > new Version(0, 0, 0, 0);
                }
                using var http = NewHttp();
                var feed = JsonConvert.DeserializeObject<UpdateFeed>(await http.GetStringAsync(feedUrl));
                if (feed == null)
                {
                    LastEngineStageError = "malformed feed";
                    return NewestInstalledEngineVersion() > new Version(0, 0, 0, 0);
                }
                return await CheckEngineAsync(feed);
            }
            catch (Exception ex)
            {
                LastEngineStageError = ex.Message;
                Log("engine: on-demand stage failed: " + ex.Message);
                return NewestInstalledEngineVersion() > new Version(0, 0, 0, 0);
            }
        }

        internal static async Task<bool> CheckEngineAsync(UpdateFeed feed)
        {
            if (string.IsNullOrWhiteSpace(feed?.EngineVersion)
                || string.IsNullOrWhiteSpace(feed.EngineUrl)
                || string.IsNullOrWhiteSpace(feed.EngineSha256))
            {
                LastEngineStageError = "feed carries no engine channel";
                return NewestInstalledEngineVersion() > new Version(0, 0, 0, 0);
            }

            if (!Version.TryParse(feed.EngineVersion, out var remote))
            {
                Log($"engine: unparseable version '{feed.EngineVersion}'");
                LastEngineStageError = "unparseable engine version in feed";
                return NewestInstalledEngineVersion() > new Version(0, 0, 0, 0);
            }

            var newestInstalled = NewestInstalledEngineVersion();
            if (remote <= newestInstalled)
            {
                Log($"engine up to date (installed {newestInstalled}, feed {remote})");
                LastEngineStageError = null;
                return true;
            }

            Log($"staging engine {remote} from {feed.EngineUrl}");
            Directory.CreateDirectory(StagingDir);
            var zipPath = Path.Combine(StagingDir, $"engine-{remote}.zip");
            var extractDir = Path.Combine(StagingDir, $"engine-{remote}");
            var targetDir = Path.Combine(EngineDir, remote.ToString());

            try
            {
                using (var http = NewHttp())
                using (var response = await http.GetAsync(feed.EngineUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using var download = await response.Content.ReadAsStreamAsync();
                    using (var zipStream = File.Create(zipPath))
                    {
                        await download.CopyToAsync(zipStream);
                    }
                }

                using (var file = File.OpenRead(zipPath))
                {
                    var actual = RuntimeCompat.ToHexString(await RuntimeCompat.Sha256Async(file));
                    if (!actual.Equals(feed.EngineSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"engine {remote} sha256 mismatch — refused, keeping current");
                        LastEngineStageError = "sha256 mismatch";
                        return newestInstalled > new Version(0, 0, 0, 0);
                    }
                }

                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, recursive: true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                Directory.CreateDirectory(EngineDir);
                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, recursive: true); // stale incomplete leftover
                Directory.Move(extractDir, targetDir);            // never extract into the live dir

                Log($"engine {remote} staged → {targetDir} — EngineManager picks it up on its next EnsureRunningAsync");
                LastEngineStageError = null;
                return true;
            }
            catch (Exception ex)
            {
                Log($"engine {remote} stage failed (non-blocking): {ex.Message}");
                LastEngineStageError = ex.Message;
                TelemetryService.Track("update", "engine_stage_failed",
                    new { to_version = remote.ToString(), error_class = ex.GetType().Name });
                return newestInstalled > new Version(0, 0, 0, 0);
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }

        /// <summary>Newest engine\&lt;ver&gt;\ dir currently on disk, by folder
        /// name (mirrors EngineManager's own scan); 0.0.0.0 when none.</summary>
        private static Version NewestInstalledEngineVersion()
        {
            var newest = new Version(0, 0, 0, 0);
            if (!Directory.Exists(EngineDir)) return newest;
            foreach (var dir in Directory.GetDirectories(EngineDir))
            {
                if (Version.TryParse(Path.GetFileName(dir), out var v) && v > newest)
                    newest = v;
            }
            return newest;
        }
    }
}
