using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.UI;

namespace BinaLoader
{
    /// <summary>
    /// Thin bootstrap shim — the only assembly the .addin manifest references,
    /// and the only one Revit file-locks in the Addins folder. It never changes
    /// after install. At startup it picks the newest staged build under
    /// %LocalAppData%\Bina\RevitSync\versions\&lt;semver&gt;\ and forwards
    /// IExternalApplication calls to it. The in-plugin UpdateService stages new
    /// version folders while Revit runs; they take effect on the next start.
    ///
    /// Assembly.LoadFrom (default ALC) is deliberate: WPF resolves
    /// "pack://...;component" resource URIs by assembly NAME in the default
    /// context, so an isolated AssemblyLoadContext would break every XAML
    /// resource in the plugin. Dependencies probe from the version folder via
    /// the LoadFrom context. Old version folders are never overwritten, so the
    /// file locks are harmless.
    /// </summary>
    public class LoaderApp : IExternalApplication
    {
        /// <summary>Dev override: point at a build output dir to bypass versions\.</summary>
        private const string DevDirEnvVar = "BINA_SYNC_PLUGIN_DIR";

        private const string DefaultAssembly = "RevitWebAppSync.dll";
        private const string DefaultEntryType = "RevitWebAppSync.App";

        /// <summary>Marker written by the updater after a fully verified extract —
        /// folders without it are half-staged and must be ignored.</summary>
        private const string CompleteMarker = ".complete";

        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bina", "RevitSync");

        private static readonly string VersionsDir = Path.Combine(Root, "versions");
        private static readonly string LogPath = Path.Combine(Root, "loader.log");

        private IExternalApplication? _inner;

        public Result OnStartup(UIControlledApplication application)
        {
            var revitYear = application.ControlledApplication.VersionNumber;
            var attempted = 0;
            Exception? firstError = null;

            // A RevitWebAppSync already in the process before we've loaded
            // anything means ANOTHER manifest beat us to it — a leftover
            // pre-loader RevitWebAppSync.addin (ProgramData / ApplicationPlugins,
            // which the installer can't clean) or a second loader copy. Every
            // LoadFrom of a different version would now die with the opaque
            // "assembly with same name is already loaded" — name the culprit
            // and the fix instead.
            var preloaded = FindLoadedPlugin();
            if (preloaded != null)
            {
                var loc = SafeLocation(preloaded);
                Log($"RevitWebAppSync {preloaded.GetName().Version} was already loaded from '{loc}' before the loader ran — duplicate .addin manifest");
                TaskDialog.Show("BINA Sync",
                    "Two copies of BINA Sync are installed, so the newest version cannot load.\n\n" +
                    $"An older copy was already loaded from:\n{loc}\n\n" +
                    "Fix: remove the leftover .addin manifest that points at that file " +
                    "(check %ProgramData%\\Autodesk\\Revit\\Addins and " +
                    "%ProgramData%\\Autodesk\\ApplicationPlugins), then restart Revit.\n\n" +
                    $"Details: {LogPath}");
                return Result.Failed;
            }

            foreach (var dir in CandidateDirs(revitYear))
            {
                attempted++;
                try
                {
                    _inner = Instantiate(dir);
                }
                catch (Exception ex)
                {
                    // The FIRST failure is the root cause: once a LoadFrom has
                    // pulled RevitWebAppSync into the process, every OLDER
                    // candidate fails with "same name already loaded" noise.
                    firstError ??= ex;
                    Log($"load failed from '{dir}': {ex}");
                    continue; // half-staged or corrupt build — try the next-newest
                }

                Log($"loaded {_inner.GetType().Assembly.GetName().Version} from '{dir}' (Revit {revitYear})");
                CleanupOldVersions(keep: 2);

                try
                {
                    return _inner.OnStartup(application);
                }
                catch (Exception ex)
                {
                    // Plugin reached user code and blew up — do NOT fall back to an
                    // older build on top of a half-initialized one (double ribbon
                    // tabs, duplicate event handlers). Surface and stop.
                    Log($"OnStartup threw in '{dir}': {ex}");
                    TaskDialog.Show("BINA Sync", $"Add-in failed to start: {ex.Message}");
                    return Result.Failed;
                }
            }

            // Two distinct failures — do not conflate them (a "reinstall" dialog on
            // a load error hides the real cause; cost a morning on 2026-07-13):
            // builds existed but none loaded vs. nothing installed at all.
            if (attempted > 0)
            {
                Log($"{attempted} candidate build(s) found for Revit {revitYear}, none loadable");
                TaskDialog.Show("BINA Sync",
                    $"BINA Sync is installed but failed to load in Revit {revitYear}.\n\n" +
                    $"{firstError?.GetType().Name}: {firstError?.Message}\n\n" +
                    $"Details: {LogPath}");
                return Result.Failed;
            }

            Log($"no loadable version found for Revit {revitYear}");
            TaskDialog.Show("BINA Sync",
                "No installed version found. Please reinstall BINA Sync.\n\n" +
                $"Expected a build under:\n{VersionsDir}");
            return Result.Failed;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                return _inner?.OnShutdown(application) ?? Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log($"OnShutdown threw: {ex}");
                return Result.Failed;
            }
        }

        /// <summary>Plugin dirs to try, best first: dev override, then complete
        /// version folders newest-first — resolved to the payload matching the
        /// running Revit year. A version dir's root manifest.json may carry a
        /// "targets" map ({"2026": "net8.0", ...}); when it does, only the
        /// mapped subfolder is a candidate for this year (a version packaged
        /// for other years only is skipped, never load-attempted). No targets
        /// key = legacy flat layout: the version dir root IS the payload.</summary>
        private static IEnumerable<string> CandidateDirs(string revitYear)
        {
            var dev = Environment.GetEnvironmentVariable(DevDirEnvVar);
            if (!string.IsNullOrWhiteSpace(dev) && Directory.Exists(dev))
                yield return dev;

            if (!Directory.Exists(VersionsDir))
                yield break;

            var ranked = Directory.EnumerateDirectories(VersionsDir)
                .Select(d => (Dir: d, Ver: ParseVersion(Path.GetFileName(d))))
                .Where(x => x.Ver != null && File.Exists(Path.Combine(x.Dir, CompleteMarker)))
                .OrderByDescending(x => x.Ver)
                .Select(x => x.Dir);

            foreach (var dir in ranked)
            {
                var payload = ResolvePayloadDir(dir, revitYear);
                if (payload != null)
                    yield return payload;
            }
        }

        /// <summary>The payload dir inside a version dir for this Revit year,
        /// or null when the version deliberately has none for it.</summary>
        private static string? ResolvePayloadDir(string versionDir, string revitYear)
        {
            var targets = ReadManifest(versionDir).Targets;
            if (targets == null)
                return versionDir; // legacy flat layout

            if (!targets.TryGetValue(revitYear, out var sub) || string.IsNullOrWhiteSpace(sub))
            {
                Log($"'{versionDir}' has no payload for Revit {revitYear} (targets: {string.Join(",", targets.Keys)})");
                return null;
            }

            var payload = Path.Combine(versionDir, sub);
            if (!Directory.Exists(payload))
            {
                Log($"'{versionDir}' targets map names '{sub}' but the folder is missing");
                return null;
            }

            return payload;
        }

        private static IExternalApplication Instantiate(string dir)
        {
            var manifest = ReadManifest(dir);
            var asmPath = Path.Combine(dir, manifest.Assembly ?? DefaultAssembly);
            var assembly = Assembly.LoadFrom(asmPath);

            var type = manifest.EntryType != null
                ? assembly.GetType(manifest.EntryType, throwOnError: true)
                : assembly.GetExportedTypes().First(t =>
                    typeof(IExternalApplication).IsAssignableFrom(t) && !t.IsAbstract);

            return (IExternalApplication)Activator.CreateInstance(type!)!;
        }

        private static PluginManifest ReadManifest(string dir)
        {
            var path = Path.Combine(dir, "manifest.json");
            if (!File.Exists(path))
                return new PluginManifest { Assembly = DefaultAssembly, EntryType = DefaultEntryType };

            try
            {
                return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path))
                       ?? new PluginManifest { Assembly = DefaultAssembly, EntryType = DefaultEntryType };
            }
            catch (Exception ex)
            {
                // Called during candidate ENUMERATION now (targets map), where a
                // throw would escape the per-candidate try/catch — degrade to the
                // defaults instead (flat-layout semantics; a truly corrupt payload
                // still fails cleanly in Instantiate and falls to the next-newest).
                Log($"manifest.json unreadable in '{dir}': {ex.Message}");
                return new PluginManifest { Assembly = DefaultAssembly, EntryType = DefaultEntryType };
            }
        }

        private static Version? ParseVersion(string name) =>
            Version.TryParse(name, out var v) ? v : null;

        /// <summary>The plugin assembly if some other manifest already loaded it
        /// into this process, else null. Simple-name match — the loader itself
        /// has loaded nothing yet when this runs.</summary>
        private static Assembly? FindLoadedPlugin()
        {
            var name = Path.GetFileNameWithoutExtension(DefaultAssembly);
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        private static string SafeLocation(Assembly asm)
        {
            try { return string.IsNullOrEmpty(asm.Location) ? "(in-memory)" : asm.Location; }
            catch { return "(unknown)"; }
        }

        /// <summary>Best-effort: drop all but the newest <paramref name="keep"/>
        /// complete versions. A folder still locked by another running Revit
        /// session just fails its delete and is retried next start.</summary>
        private static void CleanupOldVersions(int keep)
        {
            try
            {
                var stale = Directory.EnumerateDirectories(VersionsDir)
                    .Select(d => (Dir: d, Ver: ParseVersion(Path.GetFileName(d))))
                    .Where(x => x.Ver != null)
                    .OrderByDescending(x => x.Ver)
                    .Skip(keep);

                foreach (var x in stale)
                {
                    try { Directory.Delete(x.Dir, recursive: true); }
                    catch { /* locked by a running session — next time */ }
                }
            }
            catch { /* cleanup must never break startup */ }
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [loader {Assembly.GetExecutingAssembly().GetName().Version}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private sealed class PluginManifest
        {
            [System.Text.Json.Serialization.JsonPropertyName("assembly")]
            public string? Assembly { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("entryType")]
            public string? EntryType { get; set; }

            /// <summary>Revit year -> payload subfolder ("2026": "net8.0").
            /// Present only in the multi-year layout; null = flat legacy dir.</summary>
            [System.Text.Json.Serialization.JsonPropertyName("targets")]
            public Dictionary<string, string>? Targets { get; set; }
        }
    }
}
