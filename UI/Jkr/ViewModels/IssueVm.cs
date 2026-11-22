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

    /// <summary>One validated proposal from the backend AI fixer (/jkr-autofix).</summary>
    public sealed class AiSuggestion
    {
        public string Action { get; set; } = "";
        public string ParameterName { get; set; } = "";
        public string Value { get; set; } = "";
        public string OldValue { get; set; } = "";
        public string Target { get; set; } = "instance";
        public string Reference { get; set; } = "";
        public int Priority { get; set; } = 10;
        /// <summary>The backend's reasoning for the value ("closest valid material code").</summary>
        public string Reason { get; set; } = "";
    }

    public class IssueVm : INotifyPropertyChanged, ICopilotRecheckRow, IRowElementIds
    {
        public string Id { get; set; } = "";

        /// <summary>Backend V2 check_id — keys the AI auto-fix round-trip.
        /// Distinct from Id (addin-computed audit hash).</summary>
        public string CheckId { get; set; } = "";

        /// <summary>AI-479: the spec check (of the 61) this row answers ("A2.1"), whichever source made it
        /// (backend rule, BIM010 checker, spec evaluator); "" = a family with no spec id.</summary>
        public string SpecCheckId { get; set; } = "";

        /// <summary>AI-479: a fixable row (a ready fix, a picker or an AI-fixer candidate) with no spec id —
        /// Grid, Level, JKR code and element-less rows (a "missing parameter" rule on an element maps to Db.1, so Kod_Jenis, Sistem, Sub_Sistem and Bidang are in the %). It stays on the board, labelled
        /// "extra", and is outside the %: fixing it never moves the score. The add-in's own spec rows and the
        /// BIM010 rows are never extra.</summary>
        public bool IsExtra =>
            string.IsNullOrEmpty(SpecCheckId) && !NeedsPerson
            && !CheckId.StartsWith("SPEC-", System.StringComparison.Ordinal)
            && !CheckId.StartsWith("BIM010-", System.StringComparison.Ordinal)
            && (AutoFixable || IsPick || !string.IsNullOrEmpty(FixAction));

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
        public List<string> ManualFixSteps => StepsIn();

        /// <summary><see cref="ManualFixSteps"/>. The backend's own steps are shown as the
        /// backend wrote them.</summary>
        public List<string> StepsIn()
        {
            if (Steps != null && Steps.Count > 0) return Steps;
            if (!string.IsNullOrWhiteSpace(HowToFix)) return new List<string> { HowToFix.Trim() };

            var select = "Select the element in Revit (Locate in 3D).";

            var steps = new List<string>();
            var target = !string.IsNullOrEmpty(FixValue) ? FixValue : Required;
            // FixError is the recorded (English) reason; matched here.
            var missing = Actual == "(element not found)" || (FixError ?? "").IndexOf("not found", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (missing && (FixError ?? "").IndexOf("not found on element", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                steps.Add("The element is no longer in the model. Re-scan to refresh this issue; if it comes back, check the element id with the BIM manager.");
                return steps;
            }

            var err = FixError ?? "";
            if (err.IndexOf("model group", System.StringComparison.OrdinalIgnoreCase) >= 0)
                steps.Add("The element is inside a model group: select the group and click Edit Group (or ungroup it) first.");

            bool isRename = string.Equals(FixAction, "rename_type", System.StringComparison.OrdinalIgnoreCase);
            bool isParam = !isRename && !string.IsNullOrEmpty(FixParameterName);
            bool onType = string.Equals(FixTarget, "type", System.StringComparison.OrdinalIgnoreCase);

            if (isRename && !string.IsNullOrEmpty(target))
            {
                if (string.Equals(Category, "Levels", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Category, "Grids", System.StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add("Select it in Revit (Locate in 3D).");
                    steps.Add($"In Properties, change Name to '{target}'.");
                }
                else
                {
                    steps.Add(select);
                    steps.Add($"Click Edit Type, then Rename…, and rename the type to '{target}'.");
                }
                if (err.IndexOf("already", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || err.IndexOf("in use", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    steps.Add($"Another type is already called '{target}'. Either switch these elements to that type, or rename the other type first.");
            }
            else if (isParam)
            {
                steps.Add(select);
                steps.Add(onType
                    ? $"Click Edit Type, find '{FixParameterName}' and set it to '{target}'."
                    : $"In Properties, find '{FixParameterName}' and set it to '{target}'.");
                if (err.IndexOf("read-only", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    steps.Add($"'{FixParameterName}' is read-only: Revit calculates it from the element. Change the element itself (for example its size or geometry), or check with the BIM manager whether this requirement applies.");
                if (err.IndexOf("not found on element", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    steps.Add($"'{FixParameterName}' is not on this element: add it from the JKR shared parameter file (Manage → Project Parameters), then set the value.");
            }
            else if (!string.IsNullOrEmpty(Required))
            {
                steps.Add(select);
                steps.Add($"Change it so it meets the requirement: {Required}.");
            }
            else
            {
                steps.Add("Check this item against the JKR requirement and correct it in Revit.");
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

        /// <summary>AI-491: every element id behind the row (a spec row can cover 46 doors), in Locate order;
        /// empty for a model-level row. When filled, Count == <see cref="LocateCount"/>.</summary>
        public List<long> ElementIds { get; set; } = new List<long>();
        IReadOnlyList<long> IRowElementIds.ElementIds => ElementIds;

        /// <summary>WP-6 (Dd.1): the type's current name, the new name the add-in derived (pure function, never the model) and the
        /// evidence that the NAME is the odd one out. Empty = no safe new name: no Copilot button.</summary>
        public string RenameFrom { get; set; } = "";
        public string RenameTo { get; set; } = "";
        public string RenameSource { get; set; } = "";

        /// <summary>AI-491: label per id in <see cref="ElementIds"/> ("Doors · PT1p_850x2100"); an id with no label is absent.</summary>
        public Dictionary<long, string> ElementLabels { get; set; } = new Dictionary<long, string>();

        /// <summary>AI-491: add element ids (positive, new ones only) with one label for them, and keep
        /// <see cref="LocateCount"/> equal to the id count. A label already set for an id is kept.</summary>
        public void AddElements(IEnumerable<long> ids, string label)
        {
            if (IssueElementIds.Add(ElementIds, ElementLabels, ids, label) > 0) LocateCount = ElementIds.Count;
        }

        /// <summary>The row can ask the backend AI fixer (/jkr-autofix) for this issue (never Fix all; AI-435):
        /// AI-judged, no ready-made fix, a real element and a backend check id. Not the
        /// local BIM010 rows, the add-in's own spec checks ("SPEC-") or test-fixture
        /// issues, which the backend doesn't know.</summary>
        public bool AiFixCandidate =>
            !NeedsPerson && !AutoFixable && RevitElementId > 0 && !string.IsNullOrEmpty(CheckId)
            && !CheckId.StartsWith("BIM010-", System.StringComparison.Ordinal)
            && !CheckId.StartsWith("SPEC-", System.StringComparison.Ordinal)
            && !CheckId.StartsWith("FIXTURE-", System.StringComparison.Ordinal);

        /// <summary>AI-435: the AI fixer was asked about this row (/jkr-autofix) and answered,
        /// with or without a value. False = "no suggestion yet".</summary>
        public bool AiAsked { get; set; }

        /// <summary>What the AI fixer proposed for this row, or null. Kept apart from the Fix*
        /// fields and AutoFixable on purpose: no bulk path (Fix all) can pick it up; it is
        /// written only when a person accepts it on the row (<see cref="UseAiSuggestion"/>).</summary>
        public AiSuggestion AiProposal { get; set; }

        /// <summary>The allowed values for this row's rule when a reference set is known
        /// (material codes, rules 7 and 14): the manual input becomes a dropdown of these.
        /// Empty = free text, validated against the rule before the write.</summary>
        public System.Collections.Generic.IReadOnlyList<string> ReferenceValues { get; set; } = System.Array.Empty<string>();

        // ── AI-434: the multi-valid-code picker ──

        /// <summary>The codes the rule accepts for this element (Kod_Jenis / Sistem / Sub_Sistem). Empty from an
        /// older backend or for a rule with one right answer.</summary>
        public System.Collections.Generic.IReadOnlyList<string> ValidOptions { get; private set; } = System.Array.Empty<string>();
        /// <summary>The AI's pick among <see cref="ValidOptions"/>, or "". Advisory: never written without a person.</summary>
        public string AiSuggestedCode { get; private set; } = "";
        public string AiSuggestionReason { get; private set; } = "";
        /// <summary>The backend says the person must choose (several valid codes).</summary>
        public bool RequiresUserChoice { get; private set; }

        /// <summary>The row is a picker: several valid codes and a person has to choose. Fix all never writes it.</summary>
        public bool IsPick => RequiresUserChoice && ValidOptions.Count > 0 && !NeedsPerson;

        /// <summary>AI-485: a pick row whose options are whole element names ("Naming convention" on a category with
        /// several subtypes: wall b/k/d, door k/g/b, window b/k), not codes for a parameter. The subtype is the person's
        /// call: no option is ever preselected, not by the AI and not by position.</summary>
        public bool IsNamePick => IsPick && string.IsNullOrEmpty(JkrManualFix.ParameterOf(RawRule));

        /// <summary>AI-492: the row offers "Fix via Copilot": its check is on the code-side list, no ready fix, no picker,
        /// at least one element id (model-level create-type checks need none, AI-498), and never a person-decides row (<see cref="JkrCopilotFix.Eligible(string, string, bool, bool, bool, int, string)"/>).
        /// Hidden while no Copilot launcher is set (no dead button). Not persisted: ids are not in the audit snapshot, so a restored row shows no button until a re-scan.</summary>
        public bool CopilotFixable => JkrCopilotFix.LauncherReady && JkrCopilotFix.Eligible(SpecCheckId, RevitCategory, NeedsPerson,
            AutoFixable || !string.IsNullOrEmpty(FixAction), IsPick || ValidOptions.Count > 0, ElementIds?.Count ?? 0, RenameTo);

        /// <summary>AI-492: what the Copilot launcher gets for this row.</summary>
        public CopilotFixRequest ToCopilotFixRequest() => new CopilotFixRequest(Id, SpecCheckId, ElementIds, Title);

        /// <summary>Fix all (and the checklist's Fix n) may write this row: a ready-made fix, never a pick row.</summary>
        public bool BulkFixable => AutoFixable && !IsPick;

        /// <summary>Take the backend's picker fields. Tolerates absent ones (older backends): no options, no pick.
        /// A suggestion that is not one of the options is dropped; a pick row is never AutoFixable.</summary>
        public void SetPick(System.Collections.Generic.IEnumerable<string> options, string suggestedCode, string reason, bool requiresUserChoice)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var o in options ?? System.Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(o) && !list.Contains(o.Trim())) list.Add(o.Trim());
            ValidOptions = list;
            RequiresUserChoice = requiresUserChoice;
            var code = (suggestedCode ?? "").Trim();
            var hit = list.Find(o => string.Equals(o, code, System.StringComparison.OrdinalIgnoreCase));
            AiSuggestedCode = hit ?? "";
            AiSuggestionReason = hit != null ? (reason ?? "").Trim() : "";
            if (IsPick) AutoFixable = false;
            // AI-485: no AI pre-selection for a name (a guessed subtype is what this ticket removes).
            if (IsNamePick) { AiSuggestedCode = ""; AiSuggestionReason = ""; }
        }

        /// <summary>Load the AI proposal into the Fix* fields for ONE accepted write (the
        /// confirm lists it as an AI proposal). AutoFixable stays false.</summary>
        public void UseAiSuggestion()
        {
            var a = AiProposal;
            if (a == null) return;
            FixAction = a.Action; FixParameterName = a.ParameterName; FixValue = a.Value;
            FixOldValue = a.OldValue; FixPriority = a.Priority;
            FixTarget = string.IsNullOrEmpty(a.Target) ? "instance" : a.Target;
            FixReference = a.Reference; Example = a.Value;
            FixProposedByAi = true;
        }

        /// <summary>Load a person's value (already validated, see JkrManualFix) into the Fix*
        /// fields for ONE write. AutoFixable stays false.</summary>
        public void UseManualValue(JkrManualFixPlan plan)
        {
            if (plan == null || !plan.Ok) return;
            FixAction = plan.Action; FixParameterName = plan.ParameterName; FixValue = plan.Value;
            FixOldValue = Actual ?? ""; FixTarget = plan.Target; FixPriority = 10;
            FixProposedByAi = false;
        }

        /// <summary>True when this issue targets a real Revit element (element_id > 0).
        /// Controls visibility of the "Locate in 3D" button.</summary>
        public bool Locatable { get; set; }

        /// <summary>AI-480: where Locate goes when there is no element id (Project Information, base point, links).</summary>
        public JkrLocateTarget LocateTarget { get; set; } = JkrLocateTarget.Element;

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

        /// <summary>AI-495: how a Fixed row was fixed when not by the ready-made Fix: "Copilot". Empty otherwise. Saved in the audit record.</summary>
        public string FixedVia { get; set; } = "";

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
        public bool CanMarkManual => JkrRowControls.CanMarkManual(JkrTierMap.CanMarkManual(Priority), JkrRowControls.HasFixControl(AutoFixable, JkrRowControls.AiStillOffers(AiFixCandidate, AiAsked, AiProposal != null && !string.IsNullOrEmpty(AiProposal.Value)), IsPick, CopilotFixable));
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
