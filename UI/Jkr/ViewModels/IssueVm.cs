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
        public bool AutoFixable { get; set; }
        public List<string> Steps { get; set; } = new List<string>();
        public string HowToFix { get; set; } = "";
        public SpecRef Spec { get; set; } = new SpecRef();

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
        public bool IsOpen => Status == IssueStatus.Open;
        public bool IsResolved => !IsOpen;
        public bool CanApprove => Priority != IssuePriority.High;
        public bool ShowAutoFixButton => IsOpen && AutoFixable;
        public bool ShowApproveButton => IsOpen && CanApprove;
        public System.Windows.TextDecorationCollection TitleDecoration
            => IsOpen ? null : System.Windows.TextDecorations.Strikethrough;
        public double TitleOpacity => IsOpen ? 1.0 : 0.55;

        public string PriorityLabel
        {
            get
            {
                switch (Priority)
                {
                    case IssuePriority.High: return "High";
                    case IssuePriority.Medium: return "Medium";
                    default: return "Low";
                }
            }
        }

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
