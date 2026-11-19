using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// A small, permanent trace of the pane's click → detail path (and the scroll the pane lets
    /// through), appended to %LOCALAPPDATA%\Bina\RevitSync\jkr-pane.log with a timestamp.
    /// Revit shows no Debug output, so this is how a live session says what happened.
    /// Off until the pane shell turns it on (unit tests write nothing); IO errors are swallowed.
    /// </summary>
    public static class JkrPaneLog
    {
        /// <summary>Set by the pane shell; nothing is written while false.</summary>
        public static bool Enabled { get; set; }

        private const long MaxBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new object();

        public static string Path
        {
            get
            {
                try
                {
                    return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                  "Bina", "RevitSync", "jkr-pane.log");
                }
                catch { return null; }
            }
        }

        public static void Write(string message)
        {
            if (!Enabled) return;
            try
            {
                var path = Path;
                if (path == null) return;
                lock (Gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes) File.Delete(path);   // keep it small: start over
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Environment.CurrentManagedThreadId + "] " + message + Environment.NewLine);
                }
            }
            catch { /* never let the trace break the pane */ }
        }

        /// <summary>The callers, short ("PanelCore.CloseDetail ← DetailScreenModel.Close ← …").</summary>
        public static string Callers(int skip = 1, int take = 6)
        {
            try
            {
                var frames = new StackTrace(skip + 1, false).GetFrames() ?? new StackFrame[0];
                return string.Join(" ← ", frames.Select(f => f.GetMethod()).Where(m => m != null)
                    .Take(take).Select(m => (m.DeclaringType?.Name ?? "?") + "." + m.Name));
            }
            catch { return "?"; }
        }
    }
}
