#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BinaOta
{
    /// <summary>
    /// Crash guard + last-known-good for staged builds (OTA self-heal F1).
    ///
    /// Markers inside each versions\&lt;ver&gt;\ folder:
    ///   .launch   attempt count, bumped by the loader BEFORE it loads the build
    ///   .healthy  startup completed at least once (plugin, or the loader when
    ///             OnStartup returned Succeeded); clears .launch
    ///   .bad      reason + timestamp: two launches never became healthy, or
    ///             OnStartup failed. The loader skips it; the updater never
    ///             re-stages it; the gate stays open for it.
    /// &lt;root&gt;\bad-versions.json keeps a bad version blocked after its folder is
    /// pruned. A folder with .healthy is never treated as bad.
    ///
    /// SHARED SOURCE: compiled into BinaLoader (net48 + net8, System.Text.Json)
    /// and RevitWebAppSync (Newtonsoft) — so BCL only, no JSON library, no
    /// Revit. Every call is best-effort: a marker that cannot be written must
    /// never be what stops Revit starting.
    /// </summary>
    internal static class VersionHealth
    {
        public const string LaunchMarker = ".launch";
        public const string HealthyMarker = ".healthy";
        public const string BadMarker = ".bad";
        public const string BadListFile = "bad-versions.json";

        /// <summary>Unhealthy launches tolerated before a build is declared bad.</summary>
        public const int MaxUnhealthyLaunches = 2;

        private static readonly object ListSync = new object();

        public static bool IsHealthy(string versionDir) =>
            File.Exists(Path.Combine(versionDir, HealthyMarker));

        /// <summary>Launch attempts recorded and not yet cleared by a healthy
        /// start. A marker that exists but cannot be parsed still proves one
        /// attempt happened.</summary>
        public static int ReadLaunchCount(string versionDir)
        {
            var path = Path.Combine(versionDir, LaunchMarker);
            try
            {
                if (!File.Exists(path)) return 0;
                return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
            }
            catch { return 1; }
        }

        /// <summary>Loader: bump the attempt count right before loading.</summary>
        public static void RecordLaunch(string versionDir)
        {
            try
            {
                var next = ReadLaunchCount(versionDir) + 1;
                File.WriteAllText(Path.Combine(versionDir, LaunchMarker),
                    next.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        /// <summary>
        /// Loader, during the candidate walk: true when the build must be
        /// skipped. Marks it bad here when it has used up its unhealthy
        /// launches — the previous starts died before reaching healthy.
        /// </summary>
        public static bool Assess(string root, string versionDir, DateTime utcNow)
        {
            if (IsHealthy(versionDir)) return false;
            var version = Path.GetFileName(versionDir.TrimEnd('\\', '/'));
            if (IsBlocked(root, version)) return true;

            var launches = ReadLaunchCount(versionDir);
            if (launches < MaxUnhealthyLaunches) return false;

            MarkBad(root, versionDir,
                $"started {launches} launches without reaching healthy (crash during startup)", utcNow);
            return true;
        }

        /// <summary>True when this version must not be loaded, staged or gated
        /// on: a .bad marker on a never-healthy folder, or a block-list entry.</summary>
        public static bool IsBlocked(string root, string version)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(version)) return false;
            try
            {
                var dir = Path.Combine(root, "versions", version);
                if (IsHealthy(dir)) return false;
                if (File.Exists(Path.Combine(dir, BadMarker))) return true;
                return ReadBadList(root).Contains(version, StringComparer.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void MarkBad(string root, string versionDir, string reason, DateTime utcNow)
        {
            try
            {
                File.WriteAllText(Path.Combine(versionDir, BadMarker),
                    (reason ?? "unknown") + " @ " +
                    utcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }
            catch { }
            AddToBadList(root, Path.GetFileName(versionDir.TrimEnd('\\', '/')));
        }

        /// <summary>Startup completed: the build is good on this machine. Also
        /// lifts an earlier bad verdict — a last-resort load that works proves
        /// the earlier crashes were not the build's fault.</summary>
        public static void MarkHealthy(string root, string versionDir)
        {
            try
            {
                File.WriteAllText(Path.Combine(versionDir, HealthyMarker),
                    DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }
            catch { }
            try { File.Delete(Path.Combine(versionDir, LaunchMarker)); } catch { }
            try { File.Delete(Path.Combine(versionDir, BadMarker)); } catch { }
            RemoveFromBadList(root, Path.GetFileName(versionDir.TrimEnd('\\', '/')));
        }

        // --- bad-versions.json: a plain JSON array of version strings ----------

        private static readonly Regex ListEntry = new Regex("\"([0-9]+(?:\\.[0-9]+){1,3})\"");

        public static IReadOnlyList<string> ReadBadList(string root)
        {
            try
            {
                var path = Path.Combine(root, BadListFile);
                if (!File.Exists(path)) return Array.Empty<string>();
                var text = File.ReadAllText(path).Trim();
                if (!text.StartsWith("[", StringComparison.Ordinal) || !text.EndsWith("]", StringComparison.Ordinal))
                    return Array.Empty<string>();
                return ListEntry.Matches(text).Cast<Match>()
                    .Select(m => m.Groups[1].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { return Array.Empty<string>(); }
        }

        public static void AddToBadList(string root, string version)
        {
            if (string.IsNullOrWhiteSpace(version) || !ListEntry.IsMatch("\"" + version + "\"")) return;
            lock (ListSync)
            {
                var list = ReadBadList(root).ToList();
                if (list.Contains(version, StringComparer.OrdinalIgnoreCase)) return;
                list.Add(version);
                WriteBadList(root, list);
            }
        }

        private static void RemoveFromBadList(string root, string version)
        {
            lock (ListSync)
            {
                var list = ReadBadList(root).ToList();
                if (list.RemoveAll(v => string.Equals(v, version, StringComparison.OrdinalIgnoreCase)) == 0) return;
                WriteBadList(root, list);
            }
        }

        private static void WriteBadList(string root, IEnumerable<string> list)
        {
            try
            {
                Directory.CreateDirectory(root);
                var sb = new StringBuilder("[");
                sb.Append(string.Join(",", list.Select(v => "\"" + v + "\"")));
                sb.Append(']');
                File.WriteAllText(Path.Combine(root, BadListFile), sb.ToString());
            }
            catch { }
        }

        // --- loader candidate ordering + pruning -------------------------------

        /// <summary>Healthy-or-untried builds newest first, then bad builds newest
        /// first. Bad ones stay in the list as a LAST resort: a machine whose
        /// only build is bad must still run something, or its updater — the only
        /// thing that can fix it over the air — never runs again.</summary>
        public static IList<T> Order<T>(IEnumerable<T> items, Func<T, Version> version, Func<T, bool> bad)
        {
            var all = items.ToList();
            return all.Where(x => !bad(x)).OrderByDescending(version)
                .Concat(all.Where(bad).OrderByDescending(version))
                .ToList();
        }

        /// <summary>Versions to delete: everything beyond the newest
        /// <paramref name="keep"/>, never the one this session loaded (deleting
        /// a running build's folder half-fails on the locked DLL and leaves a
        /// corrupt tree behind).</summary>
        public static IList<Version> SelectPrunable(IEnumerable<Version> all, Version loaded, int keep)
        {
            return all.OrderByDescending(v => v)
                .Skip(keep)
                .Where(v => loaded == null || v != loaded)
                .ToList();
        }

        /// <summary>The versions\&lt;ver&gt;\ folder that contains
        /// <paramref name="dir"/> (the version root itself or a payload
        /// subfolder such as net8.0), or null when it is not under versions\.</summary>
        public static string VersionRootOf(string versionsDir, string dir)
        {
            var target = versionsDir.TrimEnd('\\', '/');
            for (var d = dir?.TrimEnd('\\', '/'); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            {
                var parent = Path.GetDirectoryName(d);
                if (parent != null && string.Equals(parent.TrimEnd('\\', '/'), target, StringComparison.OrdinalIgnoreCase)
                    && Version.TryParse(Path.GetFileName(d), out _))
                    return d;
            }
            return null;
        }
    }
}
