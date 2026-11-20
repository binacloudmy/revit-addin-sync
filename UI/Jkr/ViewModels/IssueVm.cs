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

    public class IssueVm : INotifyPropertyChanged, IRecheckRow
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

        /// <summary>The element's Revit category ("Doors"); "" for project-level checks.</summary>
        public string RevitCategory { get; set; } = "";

        /// <summary>BIM010 row this item belongs to ("A5", "C1.1", "D10"); "" when
        /// it maps to no Borang row (listed as unmapped in the summary).</summary>
        public string BorangKey { get; set; } = "";

        /// <summary>BIM010 section letter (A–E) of <see cref="BorangKey"/>, or "".</summary>
        public string BorangSection => string.IsNullOrEmpty(BorangKey) ? "" : BorangKey.Substring(0, 1);
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";

        public ElementRef Element { get; set; } = new ElementRef();
        public long RevitElementId { get; set; }
        public string Required { get; set; } = "";
        public string Actual { get; set; } = "";
        /// <summary>English of <see cref="Actual"/> for the add-in's own spec results ("" = same).</summary>
        public string ActualEn { get; set; } = "";
        public string Example { get; set; } = "";
        private bool _autoFixable;
        public bool AutoFixable
        {
            get => _autoFixable;
            set { if (_autoFixable != value) { _autoFixable = value; OnAll(); } }
        }
        public List<string> Steps { get; set; } = new List<string>();

        /// <summary>
        /// What "How to fix manually" lists. The backend's steps, else its fix
        /// suggestion, else steps built from the fix data (rename / parameter,
        /// type or instance), with advice for the known auto-fix failures. Never
        /// empty, so the card is never blank.
        /// </summary>
        public List<string> ManualFixSteps => StepsIn(RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.Current);

        /// <summary><see cref="ManualFixSteps"/> in BM ("ms") or EN ("en"). The
        /// backend's own steps come in the language the backend wrote them.</summary>
        public List<string> StepsIn(string lang)
        {
            if (Steps != null && Steps.Count > 0) return Steps;
            if (!string.IsNullOrWhiteSpace(HowToFix)) return new List<string> { HowToFix.Trim() };

            var en = string.Equals(lang, "en", System.StringComparison.OrdinalIgnoreCase);
            string L(string ms, string e) => en ? e : ms;
            var select = L("Pilih elemen dalam Revit (Cari dalam 3D).", "Select the element in Revit (Locate in 3D).");

            var steps = new List<string>();
            var target = !string.IsNullOrEmpty(FixValue) ? FixValue : Required;
            // FixError is the recorded (English) reason; matched here, translated when shown.
            var missing = Actual == "(element not found)" || (FixError ?? "").IndexOf("not found", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (missing && (FixError ?? "").IndexOf("not found on element", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                steps.Add(L("Elemen tiada lagi dalam model. Imbas semula untuk mengemas kini isu ini; jika ia muncul lagi, semak id elemen dengan pengurus BIM.",
                            "The element is no longer in the model. Re-scan to refresh this issue; if it comes back, check the element id with the BIM manager."));
                return steps;
            }

            var err = FixError ?? "";
            if (err.IndexOf("model group", System.StringComparison.OrdinalIgnoreCase) >= 0)
                steps.Add(L("Elemen berada dalam kumpulan model (model group): pilih kumpulan itu dan klik Edit Group (atau leraikan) dahulu.",
                            "The element is inside a model group: select the group and click Edit Group (or ungroup it) first."));

            bool isRename = string.Equals(FixAction, "rename_type", System.StringComparison.OrdinalIgnoreCase);
            bool isParam = !isRename && !string.IsNullOrEmpty(FixParameterName);
            bool onType = string.Equals(FixTarget, "type", System.StringComparison.OrdinalIgnoreCase);

            if (isRename && !string.IsNullOrEmpty(target))
            {
                if (string.Equals(Category, "Levels", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Category, "Grids", System.StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(L("Pilih dalam Revit (Cari dalam 3D).", "Select it in Revit (Locate in 3D)."));
                    steps.Add(L($"Dalam Properties, tukar Name kepada '{target}'.", $"In Properties, change Name to '{target}'."));
                }
                else
                {
                    steps.Add(select);
                    steps.Add(L($"Klik Edit Type, kemudian Rename…, dan tukar nama jenis kepada '{target}'.",
                                $"Click Edit Type, then Rename…, and rename the type to '{target}'."));
                }
                if (err.IndexOf("already", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || err.IndexOf("in use", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    steps.Add(L($"Jenis lain sudah bernama '{target}'. Tukar elemen ini kepada jenis itu, atau tukar nama jenis yang satu lagi dahulu.",
                                $"Another type is already called '{target}'. Either switch these elements to that type, or rename the other type first."));
            }
            else if (isParam)
            {
                steps.Add(select);
                steps.Add(onType
                    ? L($"Klik Edit Type, cari '{FixParameterName}' dan tetapkan kepada '{target}'.",
                        $"Click Edit Type, find '{FixParameterName}' and set it to '{target}'.")
                    : L($"Dalam Properties, cari '{FixParameterName}' dan tetapkan kepada '{target}'.",
                        $"In Properties, find '{FixParameterName}' and set it to '{target}'."));
                if (err.IndexOf("read-only", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    steps.Add(L($"'{FixParameterName}' hanya-baca (read-only): Revit mengiranya daripada elemen. Ubah elemen itu sendiri (contohnya saiz atau geometrinya), atau semak dengan pengurus BIM sama ada keperluan ini terpakai.",
                                $"'{FixParameterName}' is read-only: Revit calculates it from the element. Change the element itself (for example its size or geometry), or check with the BIM manager whether this requirement applies."));
                if (err.IndexOf("not found on element", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    steps.Add(L($"'{FixParameterName}' tiada pada elemen ini: tambah daripada fail shared parameter JKR (Manage → Project Parameters), kemudian tetapkan nilainya.",
                                $"'{FixParameterName}' is not on this element: add it from the JKR shared parameter file (Manage → Project Parameters), then set the value."));
            }
            else if (!string.IsNullOrEmpty(Required))
            {
                steps.Add(select);
                steps.Add(L($"Ubah supaya memenuhi keperluan: {Required}.", $"Change it so it meets the requirement: {Required}."));
            }
            else
            {
                steps.Add(L("Semak item ini berbanding keperluan JKR dan betulkan dalam Revit.",
                            "Check this item against the JKR requirement and correct it in Revit."));
            }
            return steps;
        }

        /// <summary>The issue cites a spec document or passage to show.</summary>
        public bool HasSpecRef => Spec != null && (!string.IsNullOrEmpty(Spec.Doc) || !string.IsNullOrEmpty(Spec.Quote));
        public string HowToFix { get; set; } = "";
        public SpecRef Spec { get; set; } = new SpecRef();

        // Backend-supplied fix metadata — used by the Auto-fix handler to queue
        // real Revit edits into App.JkrRenameHandler. Never shown in the UI.
        public string FixAction { get; set; } = "";          // rename_type | set_parameter | set_jkr_code
        public string FixParameterName { get; set; } = "";
        public string FixValue { get; set; } = "";
        public string FixOldValue { get; set; } = "";
        /// <summary>Backend confidence in the fix ("high"/"medium"/"low"); "" when none. Display only.</summary>
        public string Confidence { get; set; } = "";

        /// <summary>The fix (FixAction / FixValue) came from the backend AI fixer during
        /// Fix all, not from the scan: the confirm lists it under "Proposed by the AI fixer".</summary>
        public bool FixProposedByAi { get; set; }
        public int FixPriority { get; set; } = 10;

        /// <summary>"instance" or "type" — passed through to JkrFixApplicator.
        /// "type" routes the write to ElementType for type-bound JKR classification
        /// params (Kod_Jenis, Sistem, etc.) so the instance-side read-only handle
        /// doesn't trip the fix.</summary>
        public string FixTarget { get; set; } = "instance";

        /// <summary>How many elements the issue lists (BIM010 rows can list many;
        /// Locate goes to the first). 1 for a single-element issue.</summary>
        public int LocateCount { get; set; } = 1;

        /// <summary>Fix all can ask the backend AI fixer (/jkr-autofix) for this issue:
        /// AI-judged, no ready-made fix, a real element and a backend check id. Not the
        /// local BIM010 rows, the add-in's own spec checks ("SPEC-") or test-fixture
        /// issues, which the backend doesn't know.</summary>
        public bool AiFixCandidate =>
            !NeedsPerson && !AutoFixable && RevitElementId > 0 && !string.IsNullOrEmpty(CheckId)
            && !CheckId.StartsWith("BIM010-", System.StringComparison.Ordinal)
            && !CheckId.StartsWith("SPEC-", System.StringComparison.Ordinal)
            && !CheckId.StartsWith("FIXTURE-", System.StringComparison.Ordinal);

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

        /// <summary>Status Fixed and the latest scan confirmed it (the check no longer
        /// fails): counts as passed. A fix not yet re-scanned is "fixed, re-scan
        /// pending" (prototype: fixed ≠ passed until re-scan).</summary>
        public bool FixVerified { get; set; }

        /// <summary>How the targeted re-check (JkrRecheck) treats this row.</summary>
        public RecheckState RecheckState =>
            Status == IssueStatus.Fixed ? RecheckState.Fixed
            : Status == IssueStatus.Open && !NeedsPerson ? RecheckState.Open
            : RecheckState.Other;

        /// <summary>Items that need a person: "Mark as reviewed" was clicked. Kept in
        /// the audit file next to the model; never a verdict and never in the %.</summary>
        public bool Reviewed { get; set; }

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
