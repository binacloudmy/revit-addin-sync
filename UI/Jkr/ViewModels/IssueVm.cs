using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    public enum IssuePriority { High, Medium, Low }
    public enum IssueStatus { Open, Fixed, Accepted, Approved }

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
        public string Category { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";

        public ElementRef Element { get; set; } = new ElementRef();
        public int RevitElementId { get; set; }
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

        /// <summary>True when this issue targets a real Revit element (element_id > 0).
        /// Controls visibility of the "Locate in 3D" button.</summary>
        public bool Locatable { get; set; }

        /// <summary>JKR spec reference for the fix (e.g. "Doc 09 — BIM Spesifikasi Parameter JKR").
        /// Shown in the fix tab so users know which spec mandates the change.</summary>
        public string FixReference { get; set; } = "";

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
        //   Medium → auto-fix + accept.
        //   Low    → auto-fix + accept + approve.
        public bool IsOpen => Status == IssueStatus.Open;
        public bool IsAccepted => Status == IssueStatus.Accepted;
        public bool IsActionable => Status == IssueStatus.Open || Status == IssueStatus.Accepted;
        public bool IsResolved => Status == IssueStatus.Fixed || Status == IssueStatus.Approved;
        public bool CanAccept  => JkrTierMap.CanAccept(Priority);
        public bool CanApprove => JkrTierMap.CanApprove(Priority);
        public bool ShowAutoFixButton => IsActionable && AutoFixable;
        public bool ShowAcceptButton  => IsOpen && CanAccept;  // only from Open → Accepted
        public bool ShowApproveButton => IsActionable && CanApprove;
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
            => IsOpen ? PriorityColor : JkrTheme.Brush("Surface.Line");

        public string StatusLabel
        {
            get
            {
                switch (Status)
                {
                    case IssueStatus.Fixed: return "Fixed";
                    case IssueStatus.Accepted: return "Accepted";
                    case IssueStatus.Approved: return "Approved";
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
                    case IssueStatus.Accepted: return "check";
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
        public bool HasActualOrRequired => !string.IsNullOrEmpty(Actual) && Actual != "(none)" && Actual != "(empty)"
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
