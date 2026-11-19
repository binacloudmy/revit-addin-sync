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

        private bool _stopped;

        public string Key { get; }
        /// <summary>The Borang letter (the chip on the row).</summary>
        public string Letter => Key;
        /// <summary>Column of the section in the A-E strip.</summary>
        public int Index => System.Array.IndexOf(PanelCore.Secs.ToArray(), Key);
        /// <summary>Checks in this section, counted from the JKR spec catalog.</summary>
        public int Checks { get; }
        /// <summary>One tick per check (Clear Sheet draws them under the row).</summary>
        public IReadOnlyList<int> Ticks { get; }
        public RunRow(string key)
        {
            Key = key;
            Checks = SpecCatalog.Checks.Count(c => c.Section == key);
            Ticks = Enumerable.Range(0, Checks).ToList();
        }

        public string Name { get => _name; internal set { _name = value; Raise(); } }
        public RunRowState State { get => _state; internal set { _state = value; Raise(); } }
        /// <summary>Section % (AI-verified cells); null when the section has no AI cells.</summary>
        public int? Pct { get => _pct; internal set { _pct = value; Raise(); } }
        /// <summary>Manual-review cells in the section (never in the %).</summary>
        public int Pend { get => _pend; internal set { _pend = value; Raise(); } }
        internal string ManualShort { set { _manualShort = value; Raise(); } }
        /// <summary>The first section the scan did not finish (failed run only).</summary>
        public bool IsStopped { get => _stopped; internal set { _stopped = value; Raise(); } }
        /// <summary>A section after the one that stopped: never started.</summary>
        public bool IsNotRun => _state == RunRowState.Failed && !_stopped;

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
            // Clear Sheet copy
            ["checksDoneOf"] = ("{0} daripada {1} semakan selesai", "{0} of {1} checks done"),
            ["headingSection"] = ("Menyemak seksyen {0}", "Checking section {0}"),
            ["headingDone"] = ("Audit selesai", "Audit finished"),
            ["subScan"] = ("Menyemak lima seksyen terhadap LOD {0} untuk {1}.", "Checking the five sections against LOD {0} for {1}."),
            ["subSection"] = ("Seksyen {0} daripada 5, terhadap LOD {1} untuk {2}.", "Section {0} of 5, against LOD {1} for {2}."),
            ["subDone"] = ("Kelima-lima seksyen telah selesai.", "All five sections are done."),
            ["backendNote"] = ("Semakan peraturan bahagian belakang BINA berjalan bersama lima seksyen.",
                               "BINA backend rule checks run alongside the five sections."),
            ["noOverall"] = ("Keputusan keseluruhan dipaparkan apabila kelima-lima seksyen selesai.",
                             "The overall result appears when all five sections finish."),
            ["failHeading"] = ("Audit terhenti di seksyen {0}", "The audit stopped in section {0}"),
            ["failDetail"] = ("Revit memulangkan: {0}", "Revit returned: {0}"),
            ["stoppedIn"] = ("Terhenti di {0}", "Stopped in {0}"),
            ["rowStopped"] = ("Terhenti", "Stopped"),
            ["rowNotRun"] = ("Tidak dijalankan", "Not run"),
            ["discAR"] = ("AR · Arkitek", "AR · Architect"),
            ["discNameAR"] = ("Arkitek", "Architect"),
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
            foreach (var r in Rows) { r.IsStopped = false; r.State = RunRowState.Checking; r.Pct = null; r.Pend = 0; }
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
        /// <summary>The scan failed: unfinished sections can't be read; say why. The scan
        /// reads the whole model in one pass, so an error is normally not tied to a section
        /// (<paramref name="stoppedIn"/> null): the screen then says the audit couldn't
        /// finish and no section is marked stopped. Pass a section letter only when the
        /// scan really reports where it stopped.</summary>
        public void Fail(string message, string stoppedIn = null)
        {
            _timer?.Dispose();
            _timer = null;
            _error = string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
            foreach (var r in Rows) r.IsStopped = false;
            foreach (var r in Rows.Where(r => r.State != RunRowState.Done)) r.State = RunRowState.Failed;
            var at = Rows.FirstOrDefault(r => r.IsFailed && r.Key == stoppedIn);
            if (at != null) at.IsStopped = true;
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

        // ── Clear Sheet: context strip, heading, check counts ──
        /// <summary>The scan or the reveal is under way (the heading spinner).</summary>
        public bool IsRunning => _error == null && !AllDone;
        /// <summary>The run has not failed (the progress bar and tick rows show).</summary>
        public bool IsOk => _error == null;
        public string LodValue => _lod;
        /// <summary>"AR · Architect" for AR, the code for the others.</summary>
        public string DiscName => _disc == "AR" ? T("discAR") : _disc;
        private string DiscWord => _disc == "AR" ? T("discNameAR") : _disc;
        public string LodLabel => T("lod");
        public string RescanLabel => T("rescan");
        public string BackendNote => T("backendNote");

        /// <summary>Every check in the JKR spec catalog.</summary>
        public int ChecksTotal => SpecCatalog.Checks.Count;
        /// <summary>The scan has no per-check progress: this is the checks of the
        /// sections that are done, so it moves a section at a time.</summary>
        public int ChecksDone => Rows.Where(r => r.IsDone).Sum(r => r.Checks);
        public string ChecksDoneLabel => string.Format(T("checksDoneOf"), ChecksDone, ChecksTotal);

        private RunRow Current => Rows.FirstOrDefault(r => r.IsChecking);
        private bool AllDone => Rows.All(r => r.IsDone);
        public string Heading =>
            AllDone ? T("headingDone")
            : RunDone > 0 && Current != null ? string.Format(T("headingSection"), Current.Key)
            : Title;
        public string Sub =>
            AllDone ? T("subDone")
            : RunDone > 0 && Current != null ? string.Format(T("subSection"), Current.Key, _lod, DiscWord)
            : string.Format(T("subScan"), _lod, DiscWord);

        // ── failure, Clear Sheet ──
        /// <summary>The section the run stopped in ("" when it has not failed).</summary>
        public string StoppedLetter => Rows.FirstOrDefault(r => r.IsStopped)?.Key ?? "";
        /// <summary>"The audit stopped in section C" when a section is known, else "The audit couldn't finish."</summary>
        public string FailHeading => StoppedLetter == "" ? T("failTitle") : string.Format(T("failHeading"), StoppedLetter);
        public string FailDetail => HasErrorText ? string.Format(T("failDetail"), _error) : "";
        /// <summary>"Stopped in C"; empty when no section is known.</summary>
        public string StoppedIn => StoppedLetter == "" ? "" : string.Format(T("stoppedIn"), StoppedLetter);
        public string RowStoppedLabel => T("rowStopped");
        public string RowNotRunLabel => T("rowNotRun");

        private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
