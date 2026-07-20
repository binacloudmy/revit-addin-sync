using System;
using System.IO;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>Crash-breadcrumb logger for the Copilot pane.
    ///
    /// The round-32 CIDB crash (fresh chat, empty buffer, "@" click → 10s
    /// Not Responding → second click → Revit process dies) reproduces on a
    /// build that already has the Idling-snapshot fix, and no Event Viewer
    /// stack could be captured. This logger writes timestamped breadcrumbs
    /// around every suspect step so the LAST line before a crash names the
    /// site. File: %APPDATA%\RevitWebAppSync\panel-debug.log (safe to send).
    /// Never throws; disabled silently if the folder is unwritable.</summary>
    public static class PanelDebugLog
    {
        private static readonly object _lock = new object();
        private static string _path;
        private static bool _dead;

        private static string PathSafe()
        {
            if (_path != null) return _path;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitWebAppSync");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "panel-debug.log");
            }
            catch { _dead = true; }
            return _path;
        }

        public static void Write(string site, string detail = null)
        {
            if (_dead) return;
            try
            {
                var p = PathSafe();
                if (p == null) return;
                var line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + site + "]"
                           + (string.IsNullOrEmpty(detail) ? "" : " " + detail) + Environment.NewLine;
                lock (_lock)
                {
                    // Keep the file from growing without bound across sessions.
                    try
                    {
                        var fi = new FileInfo(p);
                        if (fi.Exists && fi.Length > 2_000_000)
                            File.WriteAllText(p, "(truncated " + DateTime.Now.ToString("u") + ")" + Environment.NewLine);
                    }
                    catch { }
                    File.AppendAllText(p, line);
                }
            }
            catch { _dead = true; }
        }

        /// <summary>Stamp assembly version + DLL write time at startup — closes
        /// the "which build was I actually testing?" gap from rounds 31-32.</summary>
        public static void WriteBuildStamp()
        {
            try
            {
                var asm = typeof(PanelDebugLog).Assembly;
                var ver = asm.GetName().Version?.ToString() ?? "?";
                string built = "?";
                try { built = File.GetLastWriteTime(asm.Location).ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
                Write("startup", "assembly=" + ver + " dll-built=" + built + " location=" + asm.Location);
            }
            catch { }
        }
    }
}
