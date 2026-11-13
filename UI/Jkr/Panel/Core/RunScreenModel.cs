using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    public enum RunRowState { Waiting, Checking, Done, Failed }

    /// <summary>One Borang section on the Running screen (prototype .step).</summary>
    public sealed class RunRow : INotifyPropertyChanged
    {
        private RunRowState _state = RunRowState.Waiting;
        private int? _pct;
        private int _pend;
        private string _name = "";
        private string _manualShort = "manual";

        public string Key { get; }
        public RunRow(string key) { Key = key; }

        public string Name { get => _name; internal set { _name = value; Raise(); } }
        public RunRowState State { get => _state; internal set { _state = value; Raise(); } }
        /// <summary>Section % (AI-verified cells); null when the section has no AI cells.</summary>
        public int? Pct { get => _pct; internal set { _pct = value; Raise(); } }
        /// <summary>Manual-review cells in the section (never in the %).</summary>
        public int Pend { get => _pend; internal set { _pend = value; Raise(); } }
        internal string ManualShort { set { _manualShort = value; Raise(); } }

        public bool IsWaiting => _state == RunRowState.Waiting;
        public bool IsChecking => _state == RunRowState.Checking;
        public bool IsDone => _state == RunRowState.Done;
        public bool IsFailed => _state == RunRowState.Failed;
        public string PctLabel => _pct.HasValue ? _pct.Value + "%" : "—";
        public string PendLabel => "+" + _pend + " " + _manualShort;

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    /// <summary>
    /// The Running screen (prototype isRunning; frames r-a, r-mid, r-done, r-fail).
    ///
    /// The real scan reads and checks the model in one pass, so it runs in two
    /// honest phases: while the scan runs every section shows "Checking…" and no %
    /// is shown; when the results arrive the sections complete A→E with the
    /// prototype's timing (750 ms each, then 800 ms) using each section's real %
    /// and manual count, the live manual counter adds up, and <see cref="Finished"/>
    /// fires. A failed scan marks the unfinished sections "Couldn't read" and
    /// offers Retry / Back to start (one pass: a single section can't be retried).
    /// WPF-free; timers through <see cref="IPanelClock"/>.
    /// </summary>
    public sealed class RunScreenModel : INotifyPropertyChanged
    {
        public static readonly TimeSpan SectionTime = PanelCore.SectionTime;
        public static readonly TimeSpan ResultsDelay = PanelCore.ResultsDelay;

        private static readonly Dictionary<string, (string Ms, string En)> Extra = new Dictionary<string, (string, string)>
        {
            ["failRead"] = ("Tidak dapat dibaca", "Couldn’t read"),
            ["failTitle"] = ("Audit tidak dapat diselesaikan.", "The audit couldn’t finish."),
            ["retry"] = ("Cuba semula", "Retry"),
            ["back"] = ("Kembali ke mula", "Back to start"),
        };

        private readonly IPanelClock _clock;
        private IDisposable _timer;
        private string _lang = PanelStrings.Ms;
        private string _lod = "", _disc = "";
        private int _runDone;
        private int _pendLive;
        private string _error;

        public RunScreenModel(IPanelClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Rows = PanelCore.Secs.Select(s => new RunRow(s)).ToList();
            Relabel();
        }

        public IReadOnlyList<RunRow> Rows { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>Every section has been shown and the 800 ms pause is over: go to Results.</summary>
        public event Action Finished;
        public event Action RetryRequested;
        public event Action BackRequested;

        public void SetLang(string lang)
        {
            _lang = string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase) ? PanelStrings.En : PanelStrings.Ms;
            Relabel();
            Raise();
        }

        private string T(string key) =>
            Extra.TryGetValue(key, out var p) ? (_lang == PanelStrings.En ? p.En : p.Ms) : PanelStrings.Get(_lang, key);

        private void Relabel()
        {
            foreach (var r in Rows)
            {
                var names = Bim010Form.SectionNames[r.Key];
                r.Name = _lang == PanelStrings.En ? names.En : names.Ms;
                r.ManualShort = T("manualShort");
            }
        }

        // ── phase 1: the scan is running ──
        /// <summary>A run started: every section is being checked, no % yet.</summary>
        public void Begin(string lod, string disc)
        {
            _timer?.Dispose();
            _timer = null;
            _lod = lod ?? "";
            _disc = disc ?? "";
            _runDone = 0;
            _pendLive = 0;
            _error = null;
            foreach (var r in Rows) { r.State = RunRowState.Checking; r.Pct = null; r.Pend = 0; }
            Raise();
        }

        // ── phase 2: results arrived ──
        /// <summary>Show the sections A→E with their real numbers, one every 750 ms,
        /// then <see cref="Finished"/> 800 ms after the last.</summary>
        public void Complete(IReadOnlyDictionary<string, (int? Pct, int Pend)> sections)
        {
            _timer?.Dispose();
            _error = null;
            _runDone = 0;
            _pendLive = 0;
            for (int i = 0; i < Rows.Count; i++)
                Rows[i].State = i == 0 ? RunRowState.Checking : RunRowState.Waiting;
            Raise();
            Reveal(0, sections ?? new Dictionary<string, (int?, int)>());
        }

        /// <summary>Numbers per section from the BIM010 summary of the scan.</summary>
        public static Dictionary<string, (int? Pct, int Pend)> FromSummary(Bim010Summary summary) =>
            PanelCore.Secs.ToDictionary(s => s, s =>
            {
                var sc = summary?.Section(s);
                return sc == null ? ((int?)null, 0) : (sc.Pct, sc.Pending);
            });

        private void Reveal(int i, IReadOnlyDictionary<string, (int? Pct, int Pend)> sections)
        {
            _timer = _clock.Schedule(SectionTime, () =>
            {
                var row = Rows[i];
                sections.TryGetValue(row.Key, out var n);
                row.Pct = n.Pct;
                row.Pend = n.Pend;
                row.State = RunRowState.Done;
                _runDone = i + 1;
                _pendLive += n.Pend;
                if (i + 1 < Rows.Count)
                {
                    Rows[i + 1].State = RunRowState.Checking;
                    Raise();
                    Reveal(i + 1, sections);
                }
                else
                {
                    Raise();
                    _timer = _clock.Schedule(ResultsDelay, () => Finished?.Invoke());
                }
            });
        }

        // ── failure ──
        /// <summary>The scan failed: unfinished sections can't be read; say why.</summary>
        public void Fail(string message)
        {
            _timer?.Dispose();
            _timer = null;
            _error = string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
            foreach (var r in Rows.Where(r => r.State != RunRowState.Done)) r.State = RunRowState.Failed;
            Raise();
        }

        public void Retry() => RetryRequested?.Invoke();
        public void Back() { _timer?.Dispose(); _timer = null; BackRequested?.Invoke(); }

        // ── bindable ──
        public bool IsFailed => _error != null;
        public string ErrorText => _error ?? "";
        public bool HasErrorText => !string.IsNullOrEmpty(_error);
        public int RunDone => _runDone;
        /// <summary>Bar fill, 0..1 (prototype runPct).</summary>
        public double Progress => _runDone / (double)Rows.Count;
        public int PendLive => _pendLive;

        public string Title => T("running");
        public string LodDisc => "LOD " + _lod + " · " + _disc;
        public string CheckingLabel => T("checking");
        public string WaitingLabel => T("waiting");
        public string FailReadLabel => T("failRead");
        public string FailTitle => T("failTitle");
        public string RetryLabel => T("retry");
        public string BackLabel => T("back");
        /// <summary>"Semak manual dikumpul · Keputusan manusia · tiada dalam %".</summary>
        public string LiveText => T("pendingLive") + " · " + T("heroPendSub");
        public string NoOverall => T("noOverall");

        private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
