#nullable enable
// Where the time goes inside one add-in operation (a write preview or commit, a read, an Idling refresh). Revit-free.
//
// A StageTimer marks named stages and, when it is disposed, writes ONE line to %LOCALAPPDATA%\Bina\RevitSync\logs\copilot-clicks.log
// (the same file as the click log): "stages preview batch: plan 3 ms, run call 0 12040 ms, shape 220 ms, rollback 15 ms, total 12300 ms".
// A timer shorter than MinLogMs is not written (Idling handlers run constantly). Added for the 40 s isolate turn (add_in_s 39.9) and
// the 4 s queue after a turn: they could not be told apart without it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace BinaVibe.Copilot.Guard
{
    public sealed class StageTimer : IDisposable
    {
        /// <summary>Where a finished timer's line goes; a test swaps it.</summary>
        public static Action<string> Sink = WriteToFile;

        private readonly string _label;
        private readonly long _minLogMs;
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly List<(string Stage, long Ms)> _stages = new List<(string, long)>();
        private long _lastMs;
        private bool _done;

        private StageTimer(string label, long minLogMs) { _label = label ?? ""; _minLogMs = minLogMs; }

        public static StageTimer Begin(string label, long minLogMs = 0) => new StageTimer(label, minLogMs);

        /// <summary>The time since the previous mark (or the start) belongs to <paramref name="stage"/>.</summary>
        public void Mark(string stage)
        {
            var now = _total.ElapsedMilliseconds;
            _stages.Add((stage ?? "", now - _lastMs));
            _lastMs = now;
        }

        public string Line()
        {
            var sb = new StringBuilder("stages ").Append(_label).Append(": ");
            foreach (var (stage, ms) in _stages) sb.Append(stage).Append(' ').Append(ms.ToString(CultureInfo.InvariantCulture)).Append(" ms, ");
            sb.Append("total ").Append(_total.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)).Append(" ms");
            return sb.ToString();
        }

        public long TotalMs => _total.ElapsedMilliseconds;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (_total.ElapsedMilliseconds < _minLogMs) return;
            try { Sink(Line()); } catch (Exception) { /* a log must never break the work */ }
        }

        private static readonly object Gate = new object();

        private static void WriteToFile(string line)
        {
            Debug.WriteLine("[BINA] " + line);
            lock (Gate)
            {
                try
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "logs", "copilot-clicks.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var w = new StreamWriter(fs);
                    w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line);
                }
                catch (IOException) { /* the other writer holds it: drop the line */ }
            }
        }
    }
}
