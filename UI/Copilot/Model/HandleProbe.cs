#nullable enable
// A diagnostic for the handle chase (overnight soak, 17 Nov): "handles <stage>: <count> (+delta)" in the clicks log at the stages of a turn, so
// the stage that gains handles shows by name. Off unless the file %LOCALAPPDATA%\Bina\RevitSync\handle-probe.txt exists (checked at most every
// 2 s); a line is written only when the count moved since the last mark, plus the turn's start and end. Never throws.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class HandleProbe
    {
        [DllImport("kernel32.dll")] private static extern bool GetProcessHandleCount(IntPtr process, out uint count);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

        private static long _checkedAt;
        private static bool _on;
        private static int _last = -1;

        /// <summary>Switch file; a test or a person creates it.</summary>
        internal static string SwitchPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "handle-probe.txt");

        public static bool On
        {
            get
            {
                var now = Stopwatch.GetTimestamp();
                var last = Interlocked.Read(ref _checkedAt);
                if (last != 0 && (now - last) < Stopwatch.Frequency * 2) return _on;
                Interlocked.Exchange(ref _checkedAt, now);
                try { _on = File.Exists(SwitchPath()); } catch (Exception) { _on = false; }
                return _on;
            }
        }

        /// <summary>The line for a stage, or null when the count did not move and the stage is not forced.</summary>
        public static string? Line(string stage, int count, int previous, bool force)
        {
            var delta = previous < 0 ? 0 : count - previous;
            if (delta == 0 && !force) return null;
            return "handles " + stage + ": " + count + " (" + (delta >= 0 ? "+" : "") + delta + ")";
        }

        public static void Mark(string stage, bool force = false)
        {
            try
            {
                if (!On) return;
                if (!GetProcessHandleCount(GetCurrentProcess(), out var n)) return;
                int previous;
                lock (typeof(HandleProbe)) { previous = _last; _last = (int)n; }
                var line = Line(stage, (int)n, previous, force);
                if (line != null) ClickTrace.Sink(line);
            }
            catch (Exception) { /* a diagnostic never breaks a turn */ }
        }
    }
}
