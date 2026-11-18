using System;
using System.Globalization;

namespace RevitWebAppSync.Services
{
    public enum SyncPhase { Hashing, Registering, Uploading, Finalizing }

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

        // Share of the bar each step owns. Upload is the long one; the others get
        // a slice so the bar never sits still between steps. shortcut: fixed
        // weights, not measured — retune if hashing or saving dominate.
        private static readonly int[] Start = { 0, 10, 15, 95 };
        private static readonly int[] End = { 10, 15, 95, 100 };

        /// <summary>Overall 0-100. Can step back once if an upload retry restarts.</summary>
        public int Percent
        {
            get
            {
                int i = (int)Phase;
                bool moves = Phase == SyncPhase.Hashing || Phase == SyncPhase.Uploading;
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
                    default: return "Saving the new version…";
                }
            }
        }

        private string Bytes(string verb, bool showPercent)
        {
            if (BytesTotal <= 0) return verb + "…";
            string s = $"{verb} {Mb(BytesDone)} of {Mb(BytesTotal)}";
            if (showPercent)
                s += " · " + ((int)(100.0 * BytesDone / BytesTotal)).ToString(CultureInfo.InvariantCulture) + "%";
            return s;
        }

        private static string Mb(long bytes)
            => (bytes / 1048576.0).ToString(bytes >= 10485760 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB";
    }
}
