using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using BinaOta;
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
    /// plus the OPTIONAL self-heal fields (missing = feature off):
    /// installer_url / installer_sha256 / min_loader_version (SelfReinstall)
    /// and feed_urls (persisted to feed.json, tried first next time).
    ///
    /// Self-heal (OTA-SELF-HEAL F1-F5): a version the loader proved bad
    /// (VersionHealth) is never staged and never gates; the feed is tried at
    /// every known URL in turn (FeedUrls); every feed GET carries the install
    /// id + versions so the server can see the fleet.
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

        /// <summary>The last forced-update answer (UpdateGateMemory), so the next
        /// Revit start is locked before the feed has answered.</summary>
        private static readonly string GateMemoryPath = Path.Combine(Root, "update-gate.json");

        /// <summary>Server-supplied feed URLs (feed_urls), tried before the baked ones.</summary>
        private static readonly string FeedJsonPath = Path.Combine(Root, "feed.json");

        private static readonly Lazy<string> _installId =
            new Lazy<string>(() => InstallIdentity.ReadOrCreate(Path.Combine(Root, "telemetry.id")));

        private static string _hostVersion = "";

        /// <summary>%LocalAppData%\Bina\RevitSync — for diagnostics.</summary>
        internal static string RootDir => Root;

        /// <summary>Anonymous per-install GUID (&lt;root&gt;\telemetry.id).</summary>
        internal static string InstallId => _installId.Value;

        /// <summary>The BinaLoader shim's version; "0.0.0" for a loader that
        /// predates publishing one (every such loader needs a reinstall).</summary>
        internal static string LoaderVersion =>
            ReinstallPolicy.NormalizeLoaderVersion(SafeAppDomainData("Bina.LoaderVersion"));

        /// <summary>Revit build ("2026.2"), for feed headers and diagnostics.</summary>
        internal static string HostVersion => _hostVersion;

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
            try
            {
                var ca = application.ControlledApplication;
                _hostVersion = string.IsNullOrWhiteSpace(ca.SubVersionNumber) ? ca.VersionNumber : ca.SubVersionNumber;
            }
            catch { }

            ReportLoaderVerdicts();

            if (ResolveFeedUrls().Count == 0)
            {
                Log("no update feed configured — updater disabled");
                UpdateGate.Set(UpdateGateState.Open);
                return;
            }

            // Lock from the remembered answer until the feed replies, so the
            // first seconds after Revit opens are not a way around the gate.
            GateFromMemory();

            application.Idling += OnIdling;

            Task.Run(async () =>
            {
                try
                {
                    await CheckAsync();

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
                    // Offline / feed down: never lock the machine out — it could
                    // not download the update anyway.
                    UpdateGate.Set(UpdatePolicyWhenUnreachable());
                }
            });
        }

        private static UpdateGateState UpdatePolicyWhenUnreachable() => UpdateGatePolicy.WhenFeedUnreachable();

        private static bool StagedOnDisk(string version) =>
            !string.IsNullOrEmpty(version) && File.Exists(Path.Combine(VersionsDir, version, CompleteMarker));

        /// <summary>This machine proved <paramref name="version"/> crashes at
        /// startup (loader .bad marker or bad-versions.json).</summary>
        private static bool MarkedBad(string version) => VersionHealth.IsBlocked(Root, version);

        private static void GateFromMemory()
        {
            try
            {
                var memory = File.Exists(GateMemoryPath) ? UpdateGateMemory.Parse(File.ReadAllText(GateMemoryPath)) : null;
                var state = UpdateGatePolicy.FromMemory(GetCurrentVersion(), memory, StagedOnDisk(memory?.Version),
                    markedBadLocally: MarkedBad(memory?.Version));
                if (state != UpdateGateState.Open)
                    Log($"gate from memory: {state} (waiting for {memory?.Version})");
                UpdateGate.Set(state, memory?.Version);
            }
            catch (Exception ex)
            {
                Log($"gate memory unreadable: {ex.Message}");
            }
        }

        private static void RememberGate(UpdateGateMemory memory)
        {
            try
            {
                if (memory == null) { if (File.Exists(GateMemoryPath)) File.Delete(GateMemoryPath); return; }
                Directory.CreateDirectory(Root);
                File.WriteAllText(GateMemoryPath, memory.ToJson());
            }
            catch (Exception ex) { Log($"gate memory not saved: {ex.Message}"); }
        }

        /// <summary>
        /// Command gate. Call first in every IExternalCommand.Execute:
        /// returns true when the running build is usable; otherwise shows the
        /// update UI (or the restart nag once staged) and returns false.
        /// </summary>
        public static bool EnsureUpToDate()
        {
            switch (UpdateGate.State)
            {
                case UpdateGateState.Open:
                    return true;
                case UpdateGateState.RestartRequired:
                    TaskDialog.Show("BINA Sync", UpdateGate.RefusalMessage);
                    return false;
                default:
                    if (_pending != null) ShowUpdateWindow();
                    else TaskDialog.Show("BINA Sync", UpdateGate.RefusalMessage);   // still checking the feed
                    return false;
            }
        }

        /// <summary>Download + verify + stage the pending build, reporting
        /// (0..1, status) progress. Used by UpdateWindow's Update button.</summary>
        public static Task StageAsync(IProgress<(double Fraction, string Status)> progress)
        {
            // Single-flight: the update window and every pane's "Update now"
            // share one download instead of racing on the same staging folder.
            lock (StageLock)
            {
                if (_stageTask != null && !_stageTask.IsCompleted) return _stageTask;
                _stageTask = StageCoreAsync(_pending ?? throw new InvalidOperationException("no pending update"), progress);
                return _stageTask;
            }
        }

        private static readonly object StageLock = new object();
        private static Task _stageTask;

        /// <summary>Lock from the remembered forced-update answer. Call as early in
        /// App.OnStartup as possible — before the tunnel, engine and panes start —
        /// so nothing runs in the seconds before the feed answers. Idempotent.</summary>
        public static void PrimeGateFromMemory() => GateFromMemory();

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

        private static async Task CheckAsync()
        {
            var feed = await FetchFeedAsync();

            if (!Version.TryParse(feed.Version, out var remote))
            {
                Log($"unparseable feed version '{feed.Version}'");
                UpdateGate.Set(UpdatePolicyWhenUnreachable());
                return;
            }

            // F1: the feed still offers a build this machine proved bad. Never
            // stage it, never gate on it (that was the endless "restart Revit"
            // loop: restart -> loader skips it -> still restart-required). The
            // engine and loader channels below still run.
            if (MarkedBad(remote.ToString()))
            {
                Log($"feed version {remote} is marked bad on this machine — not staging it, gate open");
                TelemetryService.Track("update", "bad_build",
                    new { to_version = remote.ToString(), source = "feed" });
                UpdateGate.Set(UpdateGateState.Open);
                RememberGate(null);
                await CheckEngineAsync(feed);
                await ReinstallIfLoaderTooOldAsync(feed);
                return;
            }

            // Decide the gate BEFORE the engine download below (~60 MB): the
            // gate must follow the feed's answer, not wait on an unrelated bundle.
            {
                var installed = GetCurrentVersion();
                var gate = UpdateGatePolicy.FromFeed(installed, remote, feed.Mandatory, StagedOnDisk(remote.ToString()));
                if (remote > installed) _pending = feed;   // "Update now" needs it the moment the gate blocks
                UpdateGate.Set(gate, remote.ToString());
                RememberGate(feed.Mandatory && remote > installed ? new UpdateGateMemory(remote.ToString(), true) : null);
            }

            // Stage the engine payload independently of the add-in version — the
            // engine can update on its own cadence. Best-effort, never blocks.
            await CheckEngineAsync(feed);

            // F2: the loader shim / manifests / cert / logon task can only be
            // replaced by the installer. Best-effort, independent of the payload.
            await ReinstallIfLoaderTooOldAsync(feed);

            var current = GetCurrentVersion();
            if (remote <= current)
            {
                Log($"up to date (current {current}, feed {remote})");
                UpdateGate.Set(UpdateGateState.Open);
                RememberGate(null);
                return;
            }

            if (File.Exists(Path.Combine(VersionsDir, remote.ToString(), CompleteMarker)))
            {
                Log($"{remote} already staged");
                _staged = true;
            }

            UpdateGate.Set(UpdateGatePolicy.FromFeed(current, remote, feed.Mandatory, _staged), remote.ToString());
            RememberGate(feed.Mandatory ? new UpdateGateMemory(remote.ToString(), true) : null);

            Log($"update available: {remote} (current {current}, mandatory {feed.Mandatory})");
            _pending = feed;
            TelemetryService.Track("update", "available",
                new { to_version = remote.ToString() });
        }

        private static async Task StageCoreAsync(UpdateFeed feed,
            IProgress<(double, string)> progress)
        {
            var remote = Version.Parse(feed.Version);
            if (MarkedBad(remote.ToString()))
                throw new InvalidOperationException(
                    $"BINA Sync {remote} failed to start on this PC before, so it will not be installed again. Wait for the next version.");

            var targetDir = Path.Combine(VersionsDir, remote.ToString());
            if (File.Exists(Path.Combine(targetDir, CompleteMarker)))
            {
                _staged = true;
                if (feed.Mandatory)
                    UpdateGate.Set(UpdateGateState.RestartRequired, remote.ToString());
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
                if (feed.Mandatory)
                    UpdateGate.Set(UpdateGateState.RestartRequired, remote.ToString());
                progress?.Report((1.0, "Done"));
                TelemetryService.Track("update", "staged",
                    new { to_version = remote.ToString() });
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

        // --- F3: feed URL fallback + server-driven feed move --------------------

        /// <summary>Feed URLs in the order to try: custom config.json pin,
        /// server-persisted feed_urls, baked UPDATE_FEED_URL, then
        /// UPDATE_FEED_URL_FALLBACK. Empty = updater disabled.</summary>
        private static IReadOnlyList<string> ResolveFeedUrls()
        {
            try
            {
                var cfg = BinaConfig.Load();
                var resolved = cfg.ResolvedUpdateFeedUrl;
                var baked = BinaConfig.DEFAULT_UPDATE_FEED_URL;
                // UrlResolution already sent a pin at one of OUR hosts back to the
                // env default; whatever still differs is a deliberate custom feed.
                var custom = string.Equals(resolved?.Trim(), baked?.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? null : resolved;
                IReadOnlyList<string> persisted = Array.Empty<string>();
                try { if (File.Exists(FeedJsonPath)) persisted = FeedUrls.Parse(File.ReadAllText(FeedJsonPath)); }
                catch { }
                return FeedUrls.Order(custom, persisted, baked, BinaConfig.DEFAULT_UPDATE_FEED_URL_FALLBACK);
            }
            catch (Exception ex)
            {
                Log($"feed urls unresolvable: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>First well-formed feed from <see cref="ResolveFeedUrls"/>;
        /// throws the last failure when none answers.</summary>
        private static async Task<UpdateFeed> FetchFeedAsync()
        {
            Exception last = null;
            foreach (var url in ResolveFeedUrls())
            {
                try
                {
                    using var http = NewHttp();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    AddFleetHeaders(req.Headers);
                    using var resp = await http.SendAsync(req, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    var feed = JsonConvert.DeserializeObject<UpdateFeed>(await resp.Content.ReadAsStringAsync());
                    if (feed?.Version == null || feed.Url == null)
                    {
                        Log($"malformed feed at {url}");
                        TelemetryService.Track("update", "feed_malformed");
                        last = new InvalidDataException($"malformed feed at {url}");
                        continue;
                    }

                    PersistFeedUrls(feed.FeedUrls);
                    return feed;
                }
                catch (Exception ex)
                {
                    Log($"feed {url} failed: {ex.GetType().Name}: {ex.Message}");
                    last = ex;
                }
            }
            throw last ?? new InvalidOperationException("no update feed configured");
        }

        /// <summary>Remember the server's feed_urls (https only) for next start.
        /// A feed without the field leaves feed.json untouched.</summary>
        private static void PersistFeedUrls(IEnumerable<string> urls)
        {
            try
            {
                var clean = FeedUrls.Sanitize(urls);
                if (clean.Count == 0) return;
                var current = File.Exists(FeedJsonPath) ? FeedUrls.Parse(File.ReadAllText(FeedJsonPath)) : Array.Empty<string>();
                if (current.SequenceEqual(clean, StringComparer.OrdinalIgnoreCase)) return;
                Directory.CreateDirectory(Root);
                File.WriteAllText(FeedJsonPath, FeedUrls.ToJson(clean));
                Log($"feed urls updated: {string.Join(", ", clean)}");
            }
            catch (Exception ex) { Log($"feed.json not saved: {ex.Message}"); }
        }

        /// <summary>F5: who is asking, on every feed / diagnostics request.</summary>
        internal static void AddFleetHeaders(HttpRequestHeaders headers)
        {
            try
            {
                headers.TryAddWithoutValidation("X-Bina-Install-Id", InstallId);
                headers.TryAddWithoutValidation("X-Bina-Addin-Version", GetCurrentVersion().ToString());
                headers.TryAddWithoutValidation("X-Bina-Loader-Version", LoaderVersion);
                headers.TryAddWithoutValidation("X-Bina-Host-Version", _hostVersion ?? "");
            }
            catch { }
        }

        // --- F1: crash guard, plugin side ----------------------------------------

        /// <summary>The loader passed over a newer build as bad this start: keep
        /// it blocked (bad-versions.json survives pruning) and tell the fleet.</summary>
        private static void ReportLoaderVerdicts()
        {
            try
            {
                var skipped = SafeAppDomainData("Bina.SkippedBadVersion") as string;
                if (string.IsNullOrWhiteSpace(skipped)) return;
                VersionHealth.AddToBadList(Root, skipped);
                Log($"loader skipped bad build {skipped}; running {GetCurrentVersion()}");
                TelemetryService.Track("update", "bad_build",
                    new { to_version = skipped, source = "loader" });
            }
            catch { }
        }

        /// <summary>End of a successful App.OnStartup: this build is good here.
        /// Writes versions\&lt;ver&gt;\.healthy and clears .launch, so the loader
        /// never marks it bad. No-op for dev builds outside versions\.</summary>
        public static void MarkRunningBuildHealthy()
        {
            try
            {
                var dir = VersionHealth.VersionRootOf(VersionsDir,
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                if (dir != null) VersionHealth.MarkHealthy(Root, dir);
            }
            catch (Exception ex) { Log($"healthy marker not written: {ex.Message}"); }
        }

        private static object SafeAppDomainData(string name)
        {
            try { return AppDomain.CurrentDomain.GetData(name); }
            catch { return null; }
        }

        // --- F2: silent self-reinstall ---------------------------------------------

        private static async Task ReinstallIfLoaderTooOldAsync(UpdateFeed feed)
        {
            try { await SelfReinstall.MaybeScheduleAsync(feed, Root, Log); }
            catch (Exception ex)
            {
                Log($"reinstall: {ex.GetType().Name}: {ex.Message}");
                TelemetryService.Track("reinstall", "failed",
                    new { to_version = feed?.Version, error_class = ex.GetType().Name });
            }
        }

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

            // Optional Copilot Engine bundle channel, shipped as flat fields in
            // the SAME version.json as the addin payload above. All three are
            // OPTIONAL and independent of the addin version fields — old feeds
            // that omit any of them leave this channel entirely inert (see
            // CheckEngineAsync). Staged into engine\<EngineVersion>\, hot-safe,
            // same restart-to-apply UX as the addin's own update.
            [JsonProperty("engineVersion")] public string EngineVersion { get; set; }
            [JsonProperty("engineUrl")] public string EngineUrl { get; set; }
            [JsonProperty("engineSha256")] public string EngineSha256 { get; set; }

            // OTA self-heal, all OPTIONAL (an old feed leaves both features off).
            // The setup EXE for THIS version (presigned) + its SHA-256, and the
            // oldest BinaLoader shim that may keep running without a reinstall.
            [JsonProperty("installer_url")] public string InstallerUrl { get; set; }
            [JsonProperty("installer_sha256")] public string InstallerSha256 { get; set; }
            [JsonProperty("min_loader_version")] public string MinLoaderVersion { get; set; }
            // Where the feed lives now; persisted to feed.json and tried first.
            [JsonProperty("feed_urls")] public List<string> FeedUrls { get; set; }
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
                if (ResolveFeedUrls().Count == 0)
                {
                    LastEngineStageError = "no update feed configured";
                    return NewestInstalledEngineVersion() > new Version(0, 0, 0, 0);
                }
                var feed = await FetchFeedAsync();
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
