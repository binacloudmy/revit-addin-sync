using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    public enum IssuePriority { High, Medium, Low }
    /// <summary>
    /// ManualFixNeeded: the user chose to handle this issue by hand ("Mark as
    /// manual"). Behaves like Ignored/Approved for filtering — out of Open, hidden
    /// Fix button — in its own tab, persisted via audit. A FAILED auto-fix does not
    /// land here: it stays Open with IssueVm.FixError set. (Step 6 of the Binazone
    /// rebuild redefines Manual as the checks the AI cannot judge.)
    /// </summary>
    public enum IssueStatus { Open, Fixed, Ignored, Approved, ManualFixNeeded }

    public class ElementRef
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = "—";
    }

    public class SpecRef
    {
        public string Doc { get; set; } = "doc09";
        public string Clause { get; set; } = "";
        public int Page { get; set; }
        public string Quote { get; set; } = "";
    }

    public class IssueVm : INotifyPropertyChanged
    {
        public string Id { get; set; } = "";

        /// <summary>Backend V2 check_id — keys the AI auto-fix round-trip.
        /// Distinct from Id (addin-computed audit hash).</summary>
        public string CheckId { get; set; } = "";

        /// <summary>Backend V2 domain (element, project_setup, ...). Sent back
        /// with AI auto-fix requests.</summary>
        public string Domain { get; set; } = "";

        /// <summary>Verbatim backend rule string (Title is the humanized copy).
        /// Round-tripped with AI auto-fix requests so the backend can match the
        /// rule exactly.</summary>
        public string RawRule { get; set; } = "";

        public string Category { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";

        public ElementRef Element { get; set; } = new ElementRef();
        public long RevitElementId { get; set; }
        public string Required { get; set; } = "";
        public string Actual { get; set; } = "";
        public string Example { get; set; } = "";
        private bool _autoFixable;
        public bool AutoFixable
        {
            get => _autoFixable;
            set { if (_autoFixable != value) { _autoFixable = value; OnAll(); } }
        }
        public List<string> Steps { get; set; } = new List<string>();
        public string HowToFix { get; set; } = "";
        public SpecRef Spec { get; set; } = new SpecRef();

        // Backend-supplied fix metadata — used by the Auto-fix handler to queue
        // real Revit edits into App.JkrRenameHandler. Never shown in the UI.
        public string FixAction { get; set; } = "";          // rename_type | set_parameter | set_jkr_code
        public string FixParameterName { get; set; } = "";
        public string FixValue { get; set; } = "";
        public string FixOldValue { get; set; } = "";
        public int FixPriority { get; set; } = 10;

        /// <summary>"instance" or "type" — passed through to JkrFixApplicator.
        /// "type" routes the write to ElementType for type-bound JKR classification
        /// params (Kod_Jenis, Sistem, etc.) so the instance-side read-only handle
        /// doesn't trip the fix.</summary>
        public string FixTarget { get; set; } = "instance";

        /// <summary>True when this issue targets a real Revit element (element_id > 0).
        /// Controls visibility of the "Locate in 3D" button.</summary>
        public bool Locatable { get; set; }

        /// <summary>JKR spec reference for the fix (e.g. "Doc 09 — BIM Spesifikasi Parameter JKR").
        /// Shown in the fix tab so users know which spec mandates the change.</summary>
        public string FixReference { get; set; } = "";

        private IssueVerification _verification = IssueVerification.Ai;
        /// <summary>Ai: the backend judged it. Human: the backend could not
        /// (cannot_verify) — needs a person; never a failure, never in the %.</summary>
        public IssueVerification Verification
        {
            get => _verification;
            set { if (_verification != value) { _verification = value; OnAll(); } }
        }
        public bool NeedsPerson => Verification == IssueVerification.Human;

        /// <summary>Open and judged by the AI: counts as "not compliant".</summary>
        public bool IsAiOpen => Status == IssueStatus.Open && !NeedsPerson;

        /// <summary>Shown in the Manual tab: needs a person, or the user chose to
        /// handle it by hand.</summary>
        public bool InManualTab => NeedsPerson || Status == IssueStatus.ManualFixNeeded;

        /// <summary>Row tag for items that need a person (prototype manualRow).</summary>
        public string ManualReviewLabel => NeedsPerson ? "Manual review" : "";

        private string _fixError = "";
        /// <summary>Why the last auto-fix of this issue failed (empty when it has not
        /// failed). A failed fix leaves the issue Open and not compliant: the reason
        /// shows on the row and in the detail view. It never moves the issue to
        /// Manual, which is only for checks the AI cannot judge.</summary>
        public string FixError
        {
            get => _fixError;
            set { var v = value ?? ""; if (_fixError != v) { _fixError = v; OnAll(); } }
        }
        public bool HasFixError => !string.IsNullOrEmpty(FixError);
        public string FixErrorLabel => HasFixError ? "Fix failed: " + FixError : "";

        private IssuePriority _priority = IssuePriority.Medium;
        public IssuePriority Priority
        {
            get => _priority;
            set { if (_priority != value) { _priority = value; OnAll(); } }
        }

        private IssueStatus _status = IssueStatus.Open;
        public IssueStatus Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnAll(); } }
        }

        // ─── Derived (bindable) ───
        // Action gating flows from the tier hierarchy in JkrTierMap:
        //   High   → auto-fix only.
        //   Medium → auto-fix + ignore.
        //   Low    → auto-fix + ignore + approve.
        public bool IsOpen => Status == IssueStatus.Open;
        public bool IsIgnored => Status == IssueStatus.Ignored;
        /// <summary>Fix / ignore / approve / mark-manual apply. Never to an item that
        /// needs a person: the AI made no verdict to fix or ignore.</summary>
        public bool IsActionable => !NeedsPerson && (Status == IssueStatus.Open || Status == IssueStatus.Ignored);
        public bool IsResolved => Status == IssueStatus.Fixed || Status == IssueStatus.Approved;
        public bool CanIgnore  => JkrTierMap.CanIgnore(Priority);
        public bool CanApprove => JkrTierMap.CanApprove(Priority);
        public bool CanMarkManual => JkrTierMap.CanMarkManual(Priority);
        public bool ShowAutoFixButton => IsActionable && AutoFixable;
        public bool ShowIgnoreButton  => IsAiOpen && CanIgnore;  // only from Open → Ignored
        public bool ShowApproveButton => IsActionable && CanApprove;
        /// <summary>"Mark as Manual" — user signals they'll handle this in Revit by hand,
        /// stop the addin from re-prompting. Hidden on High-tier ("Must Fix") issues:
        /// those have to be resolved, not deferred. A failed auto-fix stays Open with
        /// its error shown; it is not moved to Manual.</summary>
        public bool ShowMarkManualButton => IsActionable && CanMarkManual;
        public string TierLabel    => JkrTierMap.Label(Priority);
        public string TierSubtitle => JkrTierMap.Subtitle(Priority);
        public System.Windows.TextDecorationCollection TitleDecoration
            => IsOpen ? null : System.Windows.TextDecorations.Strikethrough;
        public double TitleOpacity => IsOpen ? 1.0 : 0.55;

        public string PriorityLabel => JkrTierMap.Label(Priority);

        public Brush PriorityColor
        {
            get
            {
                switch (Priority)
                {
                    case IssuePriority.High:   return JkrTheme.Brush("Hi");
                    case IssuePriority.Medium: return JkrTheme.Brush("Md");
                    default:                   return JkrTheme.Brush("Lo");
                }
            }
        }
        public Brush PriorityBg
        {
            get
            {
                switch (Priority)
                {
                    case IssuePriority.High:   return JkrTheme.Brush("HiBg");
                    case IssuePriority.Medium: return JkrTheme.Brush("MdBg");
                    default:                   return JkrTheme.Brush("LoBg");
                }
            }
        }

        public Brush PriorityBarBrush
            => NeedsPerson ? JkrTheme.Brush("Info")
             : IsOpen ? PriorityColor : JkrTheme.Brush("Surface.Line");

        public string StatusLabel
        {
            get
            {
                switch (Status)
                {
                    case IssueStatus.Fixed: return "Fixed";
                    case IssueStatus.Ignored: return "Ignored";
                    case IssueStatus.Approved: return "Approved";
                    case IssueStatus.ManualFixNeeded: return "Manual fix";
                    default: return "Open";
                }
            }
        }

        public string StatusIconName
        {
            get
            {
                switch (Status)
                {
                    case IssueStatus.Approved: return "approve";
                    case IssueStatus.Fixed:
                    case IssueStatus.Ignored: return "check";
                    case IssueStatus.ManualFixNeeded: return "warning";
                    default: return "dot";
                }
            }
        }

        public Brush StatusColor
            => Status == IssueStatus.Approved ? JkrTheme.Brush("Info") : JkrTheme.Brush("Ok");

        public Brush StatusBg
            => Status == IssueStatus.Approved ? JkrTheme.Brush("InfoBg") : JkrTheme.Brush("OkBg");

        // ─── Row display helpers ───
        public bool HasElementName => Element != null && !string.IsNullOrEmpty(Element.Name) && Element.Name != "—";
        /// <summary>The red actual → green required line. Hidden for items that need
        /// a person: no ✓/✗ is implied for them.</summary>
        public bool HasActualOrRequired => !NeedsPerson
                                        && !string.IsNullOrEmpty(Actual) && Actual != "(none)" && Actual != "(empty)"
                                        && !string.IsNullOrEmpty(Required);
        public string RequiredOrExample => string.IsNullOrEmpty(Example) ? Required : Example;
        public bool HasSpec => Spec != null && !string.IsNullOrEmpty(Spec.Doc);
        public string SpecLabel
        {
            get
            {
                if (Spec == null || string.IsNullOrEmpty(Spec.Doc)) return "";
                var doc = SpecDoc.Get(Spec.Doc);
                var page = Spec.Page > 0 ? $" p{Spec.Page}" : "";
                return $"{doc.Short}{page}";
            }
        }

        // ─── INotifyPropertyChanged ───
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnAll()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
        protected void Raise([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
