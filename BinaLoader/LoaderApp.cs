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
            foreach (var dir in CandidateDirs())
            {
                try
                {
                    _inner = Instantiate(dir);
                }
                catch (Exception ex)
                {
                    Log($"load failed from '{dir}': {ex}");
                    continue; // half-staged or corrupt build — try the next-newest
                }

                Log($"loaded {_inner.GetType().Assembly.GetName().Version} from '{dir}'");
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

            Log("no loadable version found");
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
        /// version folders newest-first.</summary>
        private static IEnumerable<string> CandidateDirs()
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
                yield return dir;
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

            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path))
                   ?? new PluginManifest { Assembly = DefaultAssembly, EntryType = DefaultEntryType };
        }

        private static Version? ParseVersion(string name) =>
            Version.TryParse(name, out var v) ? v : null;

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
        }
    }
}
