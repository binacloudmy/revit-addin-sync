using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>One choice in a Start-screen dropdown.</summary>
    public sealed class StartOption
    {
        public string Value { get; }
        public string Label { get; }
        public StartOption(string value, string label) { Value = value; Label = label; }
        public override string ToString() => Label;
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
        public static readonly string[] DiscValues = { "AR", "CD", "EL", "ME", "ST" };

        private readonly PanelCore _core;
        private string _fileLabel = "—";

        public StartScreenModel(PanelCore core)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _core.Changed += RaiseAll;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised by Run audit with the chosen LOD (int) and discipline code.</summary>
        public event Action<int, string> RunRequested;

        public PanelCore Core => _core;

        /// <summary>The project file (from the open document).</summary>
        public string FileLabel
        {
            get => _fileLabel;
            set { if (_fileLabel == value) return; _fileLabel = value ?? "—"; RaiseAll(); }
        }

        private string T(string key) => _core.T(key);

        // ── dropdowns ──
        /// <summary>"— Pilih —" / "— Select —" first (value ""), then LOD 100–500.</summary>
        public IReadOnlyList<StartOption> LodOptions { get { EnsureOptions(); return _lodOptions; } }

        /// <summary>AR shows "AR · Arkitek" / "AR · Architecture"; the others their code.</summary>
        public IReadOnlyList<StartOption> DiscOptions { get { EnsureOptions(); return _discOptions; } }

        // Rebuilt only when the language changes: a new list on every change would
        // make the ComboBox drop and re-find its selection each time.
        private IReadOnlyList<StartOption> _lodOptions, _discOptions;
        private string _optionsLang;

        private void EnsureOptions()
        {
            if (_optionsLang == _core.Lang && _lodOptions != null) return;
            _optionsLang = _core.Lang;
            _lodOptions = new[] { new StartOption("", T("lodPh")) }
                .Concat(LodValues.Select(v => new StartOption(v, "LOD " + v))).ToList();
            _discOptions = DiscValues.Select(v => new StartOption(v, v == "AR" ? T("discAR") : v)).ToList();
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
            set { if (!string.IsNullOrEmpty(value)) _core.SetDisc(value); }
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

        /// <summary>Run audit: nothing without an LOD (the button is disabled too).</summary>
        public bool Run()
        {
            if (!CanRun || !int.TryParse(_core.Lod, out var lod)) return false;
            RunRequested?.Invoke(lod, _core.Disc);
            return true;
        }

        private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
