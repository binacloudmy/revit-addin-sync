#nullable enable
// A stopwatch on every result-button click: how long from the click until the work was handed to Revit, how long it
// waited for Revit's turn (the queue), how long it ran, and the whole round trip. One line per click in
// %LOCALAPPDATA%\Bina\RevitSync\logs\copilot-clicks.log, so "the button is slow" can be told apart as "Revit was busy
// and the click waited" or "the work itself is slow". Pure apart from the file sink (replaceable in a test).
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class ClickOp
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private long _handedOff = -1, _started = -1, _finished = -1;

        public string Label { get; }
        /// <summary>How many items the click acts on (element ids, rows), 0 when it is not a count.</summary>
        public int Count { get; set; }
        public string Note { get; set; } = "";

        internal ClickOp(string label, int count) { Label = label ?? ""; Count = count; }

        /// <summary>The work left the pane for Revit's queue.</summary>
        public void HandedOff() { if (_handedOff < 0) _handedOff = _watch.ElapsedMilliseconds; }
        /// <summary>Revit's thread picked the work up.</summary>
        public void Started() { if (_started < 0) _started = _watch.ElapsedMilliseconds; }
        /// <summary>The work is done (on whatever thread it ended).</summary>
        public void Finished() { if (_finished < 0) _finished = _watch.ElapsedMilliseconds; }

        public long QueueMs => _handedOff < 0 || _started < 0 ? -1 : Math.Max(0, _started - _handedOff);
        public long RunMs => _started < 0 || _finished < 0 ? -1 : Math.Max(0, _finished - _started);
        public long TotalMs => _finished < 0 ? _watch.ElapsedMilliseconds : _finished;
        public long ToHandOffMs => _handedOff;

        /// <summary>"click copy-table n=12: to-revit 3 ms, queued 140 ms, ran 2300 ms, total 2450 ms".</summary>
        public string Line()
        {
            string Ms(long v) => v < 0 ? "-" : v.ToString(CultureInfo.InvariantCulture) + " ms";
            var n = Count > 0 ? " n=" + Count.ToString(CultureInfo.InvariantCulture) : "";
            return "click " + Label + n + ": to-revit " + Ms(_handedOff) + ", queued " + Ms(QueueMs) + ", ran " + Ms(RunMs)
                + ", total " + Ms(TotalMs) + (Note.Length > 0 ? " (" + Note + ")" : "");
        }
    }

    public static class ClickTrace
    {
        /// <summary>Where a finished click's line goes; a test swaps it.</summary>
        public static Action<string> Sink = WriteToFile;

        /// <summary>Raised (on the thread that finished it) when a click's work ended: the button that started it stops spinning.</summary>
        public static event Action<ClickOp>? Ended;

        private static int _opened;
        /// <summary>How many ops have begun (a button compares it before and after its handler to see whether the click went to Revit).</summary>
        public static int Opened => System.Threading.Volatile.Read(ref _opened);

        public static ClickOp Begin(string label, int count = 0)
        {
            System.Threading.Interlocked.Increment(ref _opened);
            return new ClickOp(label, count);
        }

        public static void End(ClickOp? op, string note = "")
        {
            if (op == null) return;
            op.Finished();
            if (note.Length > 0) op.Note = note;
            try { Sink(op.Line()); } catch (Exception) { /* a log must never break a click */ }
            try { Ended?.Invoke(op); } catch (Exception) { /* nor a listener */ }
        }

        private const long MaxBytes = 1_000_000;

        internal static string LogPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "logs", "copilot-clicks.log");

        private static readonly object Gate = new object();

        private static void WriteToFile(string line)
        {
            Debug.WriteLine("[BINA] " + line);
            lock (Gate)
            {
                var path = LogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) { try { File.Copy(path, path + ".1", true); File.Delete(path); } catch (Exception) { /* keep appending */ } }
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
            }
        }
    }
}
