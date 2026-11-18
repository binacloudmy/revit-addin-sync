using System;
using System.Globalization;

namespace RevitWebAppSync.Services
{
    public enum SyncPhase { Hashing, Registering, Uploading, PreparingViewer, Finalizing }

    /// <summary>
    /// One progress report from a sync run: which step it is on and, for the two
    /// steps that move bytes, how many. Revit-free so the numbers can be tested.
    /// </summary>
    public sealed class SyncProgress
    {
        public SyncPhase Phase { get; }
        public long BytesDone { get; }
        public long BytesTotal { get; }

        public SyncProgress(SyncPhase phase, long bytesDone = 0, long bytesTotal = 0)
        {
            Phase = phase;
            BytesTotal = Math.Max(0, bytesTotal);
            BytesDone = Math.Min(Math.Max(0, bytesDone), BytesTotal);
        }

        // Share of the bar each step owns. The two uploads (BINA, then the same
        // file again to Autodesk for the viewer) are the long ones; the others
        // get a slice so the bar never sits still between steps. shortcut: fixed
        // weights, not measured — retune if hashing or the viewer step dominate.
        private static readonly int[] Start = { 0, 5, 8, 50, 95 };
        private static readonly int[] End = { 5, 8, 50, 95, 100 };

        /// <summary>Overall 0-100. Can step back once if an upload retry restarts.</summary>
        public int Percent
        {
            get
            {
                int i = (int)Phase;
                bool moves = Phase == SyncPhase.Hashing || Phase == SyncPhase.Uploading || Phase == SyncPhase.PreparingViewer;
                double within = moves && BytesTotal > 0 ? (double)BytesDone / BytesTotal : 0;
                return (int)Math.Round(Start[i] + (End[i] - Start[i]) * within);
            }
        }

        /// <summary>Text for the dialog, e.g. "Uploading 42 MB of 130 MB · 32%".</summary>
        public string Text
        {
            get
            {
                switch (Phase)
                {
                    case SyncPhase.Hashing: return Bytes("Checking the model", false);
                    case SyncPhase.Registering: return "Contacting BINA…";
                    case SyncPhase.Uploading: return Bytes("Uploading", true);
                    case SyncPhase.PreparingViewer: return Bytes("Preparing the viewer:", true);
                    default: return "Saving the new version…";
                }
            }
        }

        private string Bytes(string verb, bool showPercent)
        {
            if (BytesTotal <= 0) return verb.TrimEnd(':') + "…";
            string s = $"{verb} {Mb(BytesDone)} of {Mb(BytesTotal)}";
            if (showPercent)
                s += " · " + ((int)(100.0 * BytesDone / BytesTotal)).ToString(CultureInfo.InvariantCulture) + "%";
            return s;
        }

        private static string Mb(long bytes)
            => (bytes / 1048576.0).ToString(bytes >= 10485760 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// Thins a per-8-KB byte callback to about one report per step (default 1 MB),
    /// always letting the final byte through so the bar can reach its end.
    /// </summary>
    public sealed class ByteThrottle
    {
        private readonly long _step;
        private long _last;
        public ByteThrottle(long step = 1048576) { _step = step; }

        public bool ShouldReport(long done, long total)
        {
            if (done < _last) _last = 0;   // a retry restarted the count
            if (done - _last < _step && !(total > 0 && done >= total)) return false;
            _last = done;
            return true;
        }
    }
}
