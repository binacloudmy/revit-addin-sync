using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// The "Send diagnostics" zip (OTA self-heal F5): loader.log, updater.log,
    /// the other logs under &lt;root&gt;, the OTA state files (update-gate.json,
    /// reinstall.json, feed.json, bad-versions.json), setup logs from silent
    /// reinstalls, and a listing of versions\ — folder names and marker files
    /// only, never payload file names. Always &lt;= <see cref="MaxBytes"/>: logs
    /// are cut to their TAIL (the newest lines are the ones that matter).
    /// Revit-free and network-free; DiagnosticsUploader posts the bytes.
    /// </summary>
    public static class DiagnosticsBundle
    {
        public const int MaxBytes = 2 * 1024 * 1024;

        private const int StateFileCap = 64 * 1024;
        private static readonly string[] StateFiles =
            { "update-gate.json", "reinstall.json", "feed.json", "bad-versions.json" };
        private static readonly string[] Markers = { ".complete", ".launch", ".healthy", ".bad" };

        public static byte[] Build(string root, IEnumerable<string> extraLogs)
        {
            var extras = (extraLogs ?? Enumerable.Empty<string>()).ToList();
            // Content budget below the ceiling leaves room for zip headers even
            // if the logs do not compress at all; shrink until it fits.
            for (var budget = MaxBytes - 128 * 1024; budget > 16 * 1024; budget /= 2)
            {
                var zip = BuildWithBudget(root, extras, budget);
                if (zip.Length <= MaxBytes) return zip;
            }
            return BuildWithBudget(root, new List<string>(), 0);
        }

        private static byte[] BuildWithBudget(string root, List<string> extraLogs, int budget)
        {
            var entries = new List<KeyValuePair<string, byte[]>>();
            var listing = Encoding.UTF8.GetBytes(ListVersions(root));
            entries.Add(new KeyValuePair<string, byte[]>("versions.txt", listing));
            var remaining = budget - listing.Length;

            foreach (var name in StateFiles)
            {
                var bytes = Tail(Path.Combine(root, name), Math.Min(StateFileCap, Math.Max(0, remaining)));
                if (bytes == null) continue;
                entries.Add(new KeyValuePair<string, byte[]>(name, bytes));
                remaining -= bytes.Length;
            }

            // Logs share what is left, smallest first so a short log is never
            // truncated to make room it did not need.
            var logs = new List<KeyValuePair<string, string>>();
            foreach (var path in SafeFiles(root, "*.log"))
                logs.Add(new KeyValuePair<string, string>(Path.GetFileName(path), path));
            foreach (var path in SafeFiles(Path.Combine(root, "installer"), "*.log"))
                logs.Add(new KeyValuePair<string, string>("installer/" + Path.GetFileName(path), path));
            foreach (var path in extraLogs.Where(p => !string.IsNullOrWhiteSpace(p) && SafeExists(p)))
                logs.Add(new KeyValuePair<string, string>("logs/" + Path.GetFileName(path), path));

            logs = logs.GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                       .OrderBy(l => SafeLength(l.Value)).ToList();
            for (var i = 0; i < logs.Count; i++)
            {
                var share = Math.Max(0, remaining) / (logs.Count - i);
                var bytes = Tail(logs[i].Value, share);
                if (bytes == null) continue;
                entries.Add(new KeyValuePair<string, byte[]>(logs[i].Key, bytes));
                remaining -= bytes.Length;
            }

            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var e in entries)
                    {
                        var entry = zip.CreateEntry(e.Key, CompressionLevel.Optimal);
                        using (var s = entry.Open()) s.Write(e.Value, 0, e.Value.Length);
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>versions\ as "&lt;ver&gt;: .complete .healthy" lines, with the
        /// small marker contents (.launch count, .bad reason) inline.</summary>
        public static string ListVersions(string root)
        {
            var sb = new StringBuilder();
            try
            {
                var versions = Path.Combine(root, "versions");
                if (!Directory.Exists(versions)) return "no versions folder\n";
                foreach (var dir in Directory.GetDirectories(versions).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append(Path.GetFileName(dir)).Append('\n');
                    foreach (var m in Markers)
                    {
                        var p = Path.Combine(dir, m);
                        if (!File.Exists(p)) continue;
                        sb.Append("  ").Append(m);
                        if (m == ".launch" || m == ".bad")
                        {
                            var text = Encoding.UTF8.GetString(Tail(p, 512) ?? new byte[0]).Trim();
                            sb.Append(": ").Append(text.Replace('\n', ' ').Replace('\r', ' '));
                        }
                        sb.Append('\n');
                    }
                }
                if (sb.Length == 0) sb.Append("no versions\n");
            }
            catch (Exception ex)
            {
                sb.Append("listing failed: ").Append(ex.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The last <paramref name="max"/> bytes of a file (opened
        /// share-read-write: the logs are usually still open), cut forward to a
        /// line start when truncated. Null when the file cannot be read.</summary>
        private static byte[] Tail(string path, int max)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    var len = fs.Length;
                    if (len <= max)
                    {
                        var all = new byte[len];
                        ReadFully(fs, all);
                        return all;
                    }

                    var header = Encoding.UTF8.GetBytes($"[truncated: last {max} of {len} bytes]\n");
                    var take = Math.Max(0, max - header.Length);
                    fs.Seek(len - take, SeekOrigin.Begin);
                    var tail = new byte[take];
                    ReadFully(fs, tail);
                    var nl = Array.IndexOf(tail, (byte)'\n');
                    var start = nl >= 0 && nl < tail.Length - 1 ? nl + 1 : 0;
                    var result = new byte[header.Length + tail.Length - start];
                    Buffer.BlockCopy(header, 0, result, 0, header.Length);
                    Buffer.BlockCopy(tail, start, result, header.Length, tail.Length - start);
                    return result;
                }
            }
            catch { return null; }
        }

        private static void ReadFully(Stream s, byte[] buffer)
        {
            var off = 0;
            int n;
            while (off < buffer.Length && (n = s.Read(buffer, off, buffer.Length - off)) > 0) off += n;
        }

        private static IEnumerable<string> SafeFiles(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : new string[0]; }
            catch { return new string[0]; }
        }

        private static bool SafeExists(string p)
        {
            try { return File.Exists(p); } catch { return false; }
        }

        private static long SafeLength(string p)
        {
            try { return new FileInfo(p).Length; } catch { return 0; }
        }
    }
}
