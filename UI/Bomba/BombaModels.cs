using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace RevitWebAppSync.UI.Bomba
{
    // Mirrors the backend contract in bina-ai app/services/bomba/result.py.
    // When the HTTP client lands these are populated from Finding objects;
    // until then the view model supplies stub data.

    /// What the user may do about a finding. The distinction is the product's
    /// honesty: never render a Fix affordance where no automatic fix exists.
    public enum FindingAction
    {
        None,
        Fixable,        // one type/parameter swap the software can apply
        GuidanceOnly,   // needs a design decision, or cannot be verified
        NeedsModelling  // the thing is not in the model at all
    }

    public enum Severity { Pass, High, Medium, NotChecked }

    public enum PaneState { NeedsSetup, Ready, Stale, RulesUnavailable }

    public class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// One line of a finding's derivation. Rounding ORDER is load-bearing in
    /// these calculations, so intermediate values are shown, not just the answer.
    public class CalcStepVm
    {
        public string Label { get; set; }
        public string Expression { get; set; }
        public string ByLaw { get; set; }

        public bool HasByLaw { get { return !string.IsNullOrEmpty(ByLaw); } }
    }

    public class FindingVm : NotifyBase
    {
        private bool _expanded;

        /// Rule-derived values render as this until the tables are verified.
        public const string PlaceholderValue = "[X]";

        public string Subject { get; set; }      // "Dewan Serbaguna" or a system name
        public string RoomNumber { get; set; }   // "R-1-04", empty for building-scope
        public string Headline { get; set; }     // the one-line result

        /// THREE-VALUED. null means NOT CHECKED — neither pass nor fail.
        /// Treating null as false reports a false accusation.
        public bool? Passed { get; set; }

        public Severity Severity { get; set; }
        public string Metrics { get; set; }      // the mono block
        public string Guidance { get; set; }
        public string ClauseRef { get; set; }
        public string RulesVersion { get; set; }
        public string Jurisdiction { get; set; }
        public string SchedulePath { get; set; } // "II.1.d.iv" — which row fired
        public FindingAction Action { get; set; }
        public string FixLabel { get; set; }
        public IList<long> ElementIds { get; set; }        // long: ElementId.Value differs per TFM
        public IList<string> SearchedModels { get; set; }  // "missing" vs "cannot verify"
        public ObservableCollection<CalcStepVm> Steps { get; private set; }

        public FindingVm()
        {
            ElementIds = new List<long>();
            SearchedModels = new List<string>();
            Steps = new ObservableCollection<CalcStepVm>();
            Action = FindingAction.None;
        }

        public bool IsExpanded
        {
            get { return _expanded; }
            set { Set(ref _expanded, value); }
        }

        public bool ShowFix { get { return Action == FindingAction.Fixable; } }
        public bool ShowGuidance { get { return !string.IsNullOrEmpty(Guidance); } }
        public bool HasSteps { get { return Steps.Count > 0; } }

        /// Never collapse null into "FAIL" — that is the false accusation.
        public string StatusLabel
        {
            get
            {
                if (Passed == true) return "PASS";
                if (Passed == false) return "FAIL";
                return "NOT CHECKED";
            }
        }

        public string ActionLabel
        {
            get
            {
                switch (Action)
                {
                    case FindingAction.Fixable: return "FIXABLE";
                    case FindingAction.GuidanceOnly: return "DESIGN CALL";
                    case FindingAction.NeedsModelling: return "NOT MODELLED";
                    default: return "";
                }
            }
        }

        public string ElementIdList
        {
            get
            {
                if (ElementIds == null || ElementIds.Count == 0) return "";
                return string.Join(", ", ElementIds.Select(i => i.ToString()).ToArray());
            }
        }

        public string SearchedModelsLabel
        {
            get
            {
                if (SearchedModels == null || SearchedModels.Count == 0) return "";
                return "searched " + string.Join(" · ", SearchedModels.ToArray());
            }
        }
    }

    public class CheckVm : NotifyBase
    {
        /// Subject, never a schedule number — numbering differs by jurisdiction.
        public string Title { get; set; }
        public bool Available { get; set; }
        public string UnavailableReason { get; set; }
        public ObservableCollection<FindingVm> Findings { get; private set; }

        public CheckVm()
        {
            Available = true;
            Findings = new ObservableCollection<FindingVm>();
        }

        public int FailCount { get { return Findings.Count(f => f.Passed == false); } }
        public int NotCheckedCount { get { return Findings.Count(f => !f.Passed.HasValue); } }

        public string BadgeText
        {
            get { return Available ? FailCount.ToString() : "—"; }
        }
    }

    /// Coverage is deliberately separate from pass/fail. "All passed" while
    /// rooms went unchecked is the most dangerous output this product can show.
    public class CoverageVm
    {
        public int RoomsChecked { get; set; }
        public int RoomsTotal { get; set; }
        public IList<string> SkipReasons { get; set; }

        public CoverageVm() { SkipReasons = new List<string>(); }

        public int RoomsSkipped { get { return RoomsTotal - RoomsChecked; } }
        public bool IsComplete { get { return RoomsSkipped <= 0; } }
        public string Label { get { return RoomsChecked + "/" + RoomsTotal; } }

        public string Summary
        {
            get
            {
                if (IsComplete) return "every room checked, no skips";
                return RoomsSkipped + " rooms were not checked";
            }
        }
    }
}
