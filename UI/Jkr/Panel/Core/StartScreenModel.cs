using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>One choice in a Start-screen dropdown. The same objects live for
    /// the whole session; only the label changes with BM / EN, so the dropdown
    /// never loses its selection on a language switch.</summary>
    public sealed class StartOption : INotifyPropertyChanged
    {
        private string _label;
        public string Value { get; }
        public string Label
        {
            get => _label;
            internal set { if (_label == value) return; _label = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label))); }
        }
        public StartOption(string value, string label) { Value = value; _label = label; }
        public override string ToString() => Label;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>One Borang section row on the Start screen: letter, name, check count.</summary>
    public sealed class StartSection : INotifyPropertyChanged
    {
        private string _name = "", _countLabel = "";
        public string Letter { get; }
        public int Checks { get; }
        /// <summary>One tick per check (Clear Sheet draws them grey before a run).</summary>
        public IReadOnlyList<int> Ticks { get; }
        public string Name { get => _name; internal set { if (_name == value) return; _name = value; Raise(); } }
        public string CountLabel { get => _countLabel; internal set { if (_countLabel == value) return; _countLabel = value; Raise(); } }
        public StartSection(string letter, int checks) { Letter = letter; Checks = checks; Ticks = Enumerable.Range(0, checks).ToList(); }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    /// <summary>
    /// The Start screen (prototype isStart; frames s-start, s-ready, s-lod100):
    /// project (read-only), LOD (required, no default, Run disabled until set),
    /// discipline, the "required" and "LOD 100" notes, Run audit and its hint.
    /// Reads and writes the step 6 <see cref="PanelCore"/>; WPF-free.
    /// </summary>
    public sealed class StartScreenModel : INotifyPropertyChanged
    {
        public static readonly string[] LodValues = { "100", "200", "300", "400", "500" };
        public static readonly string[] DiscValues = Services.JkrDisciplineDetector.Selectable;

        private readonly PanelCore _core;
        private string _fileLabel = "—";
        private string _detectedDisc = "AR";
        /// <summary>The model's file name names a discipline other than AR (ST, ME…):
        /// the audit still runs as AR and the hint says so.</summary>
        private bool _otherDisc;

        public StartScreenModel(PanelCore core)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _core.Changed += RaiseAll;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised by Run audit with the chosen LOD (int) and discipline code.</summary>
        public event Action<int, string> RunRequested;

        public PanelCore Core => _core;

        /// <summary>The project file (from the open document). A new model pre-fills
        /// Discipline with the one detected from its file name.</summary>
        public string FileLabel
        {
            get => _fileLabel;
            set
            {
                value = value ?? "—";
                if (_fileLabel == value) return;
                _fileLabel = value;
                _otherDisc = value != "—" && Services.JkrDisciplineDetector.Detect(value) != "AR";
                _detectedDisc = value == "—" ? "AR" : Services.JkrDisciplineDetector.DetectSelectable(value);
                _core.SetDisc(_detectedDisc);
                RaiseAll();
            }
        }

        /// <summary>The discipline detected from the model's file name (AR when unknown).</summary>
        public string DetectedDisc => _detectedDisc;

        // Strings the prototype does not have: a clearer LOD placeholder.
        private static readonly Dictionary<string, (string Ms, string En)> Extra = new Dictionary<string, (string, string)>
        {
            ["lodPick"] = ("— Pilih tahap LOD —", "— Select LOD level —"),
            ["setupTitle"] = ("Sediakan audit", "Set up the audit"),
            ["setupLead"] = ("{0} semakan daripada spesifikasi JKR Model Audit, disusun mengikut lima seksyen Borang BIM010.",
                             "{0} checks from the JKR Model Audit spec, laid out as the five sections of Borang BIM010."),
            ["lodRequiredTag"] = ("Wajib", "Required"),
            ["lodHint"] = ("Menetapkan parameter yang mesti ada pada setiap family. Tiada nilai lalai.",
                           "Sets which parameters each family must carry. There is no default."),
            ["discHint"] = ("Pratetap daripada nama fail ({0}).", "Preset from the file name ({0})."),
            ["discOnlyAr"] = ("Hanya AR disokong buat masa ini.", "Only AR is supported for now."),
            ["sectionsLabel"] = ("Seksyen Borang", "Borang sections"),
            ["checksMany"] = ("{0} semakan", "{0} checks"),
            ["checksOne"] = ("{0} semakan", "{0} check"),
            ["readyHint"] = ("Pilih tahap LOD untuk menjalankan audit.", "Choose a level of development to run the audit."),
            ["discAR"] = ("AR · Arkitek", "AR · Architect"),
        };

        private string T(string key) =>
            Extra.TryGetValue(key, out var p) ? (_core.Lang == PanelStrings.En ? p.En : p.Ms) : _core.T(key);

        // ── dropdowns ──
        /// <summary>"— Pilih —" / "— Select —" first (value ""), then LOD 100–500.</summary>
        public IReadOnlyList<StartOption> LodOptions { get { EnsureOptions(); return _lodOptions; } }

        /// <summary>AR only for now: "AR · Arkitek" / "AR · Architect".</summary>
        public IReadOnlyList<StartOption> DiscOptions { get { EnsureOptions(); return _discOptions; } }

        // Built once; a language switch only renames them. Replacing the lists made
        // the dropdown lose its selection (the Discipline box went blank).
        private IReadOnlyList<StartOption> _lodOptions, _discOptions;

        /// <summary>Checks per Borang section (A–E), counted from the JKR spec catalog.</summary>
        private static readonly int[] SectionChecks =
            PanelCore.Secs.Select(l => SpecCatalog.Checks.Count(c => c.Section == l)).ToArray();
        private static int TotalChecks => SpecCatalog.Checks.Count;
        private readonly IReadOnlyList<StartSection> _sections =
            PanelCore.Secs.Select((l, i) => new StartSection(l, SectionChecks[i])).ToList();

        /// <summary>The five Borang sections with their check counts (names follow BM / EN).</summary>
        public IReadOnlyList<StartSection> Sections { get { RelabelSections(); return _sections; } }

        private void RelabelSections()
        {
            var en = _core.Lang == PanelStrings.En;
            foreach (var sec in _sections)
            {
                var n = Bim010Form.SectionNames[sec.Letter];
                sec.Name = en ? n.En : n.Ms;
                sec.CountLabel = string.Format(T(sec.Checks == 1 ? "checksOne" : "checksMany"), sec.Checks);
            }
        }

        private void EnsureOptions()
        {
            if (_lodOptions == null)
            {
                _lodOptions = new[] { new StartOption("", "") }
                    .Concat(LodValues.Select(v => new StartOption(v, "LOD " + v))).ToList();
                _discOptions = DiscValues.Select(v => new StartOption(v, v)).ToList();
            }
            _lodOptions[0].Label = T("lodPick");
            _discOptions[0].Label = T("discAR");
            RelabelSections();
        }

        /// <summary>The chosen LOD ("" = none yet). Bound by value, so the option
        /// list can be rebuilt (BM / EN) without losing the choice.</summary>
        public string LodValue
        {
            get => _core.Lod ?? "";
            // null = the ComboBox lost its item for a moment (list rebuilt): keep the choice.
            set { if (value != null) _core.SetLod(value); }
        }

        /// <summary>The chosen discipline code (default AR, as in the prototype).</summary>
        public string DiscValue
        {
            get => _core.Disc;
            set
            {
                if (string.IsNullOrEmpty(value) || value == _core.Disc) return;
                if (Array.IndexOf(DiscValues, value) < 0) return;   // only offered disciplines
                _core.SetDisc(value);
            }
        }

        // ── state ──
        public bool LodMissing => _core.LodMissing;
        /// <summary>"need" while LOD is empty: the select shows its yellow required state.</summary>
        public string LodTag => _core.LodMissing ? "need" : "";
        public bool Lod100 => _core.Lod100;
        public bool CanRun => !_core.RunDisabled;

        // ── strings ──
        public string ProjectLabel => T("project");
        public string LodLabel => T("lod");
        public string DiscLabel => T("disc");
        public string LodRequiredNote => T("lodReq");
        public string Lod100Note => T("lod100");
        public string RunLabel => T("run");
        public string RunHint => T("runHint");
        public string Title => T("setupTitle");
        public string Lead => string.Format(T("setupLead"), TotalChecks);
        public string LodRequiredTag => T("lodRequiredTag");
        public string LodHint => T("lodHint");
        public string DiscHint => _otherDisc ? T("discOnlyAr") : string.Format(T("discHint"), _detectedDisc);
        public string SectionsLabel => T("sectionsLabel");
        public string SectionsCount => string.Format(T("checksMany"), TotalChecks);
        /// <summary>"Choose a level of development…", shown above Run until a LOD is chosen.</summary>
        public string ReadyHint => T("readyHint");
        public bool ReadyHintVisible => _core.LodMissing;

        /// <summary>Run audit: nothing without an LOD (the button is disabled too).</summary>
        public bool Run()
        {
            if (!CanRun || !int.TryParse(_core.Lod, out var lod)) return false;
            RunRequested?.Invoke(lod, _core.Disc);
            return true;
        }

        private void RaiseAll()
        {
            EnsureOptions();   // relabel for the current language
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
