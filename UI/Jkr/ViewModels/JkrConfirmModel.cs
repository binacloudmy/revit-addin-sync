using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using RevitWebAppSync.Handlers;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>What the confirm dialog is asking about.</summary>
    public enum JkrConfirmKind
    {
        /// <summary>Write fixes to the model (Fix all, or one auto-fix).</summary>
        Fix,
    }

    /// <summary>One change listed in the confirm dialog (Clear Sheet "confirm changes"):
    /// "title · scope" over "old → new". <see cref="IsAiProposal"/> puts it in the
    /// "Proposed by the AI fixer" group instead of "Ready-made fixes".</summary>
    public sealed class JkrConfirmRow
    {
        /// <summary>Where the change lands: the category (or Borang item).</summary>
        public string Item { get; }
        public string Title { get; }
        public long ElementId { get; }
        /// <summary>The value in the model now; "" when it has none.</summary>
        public string Actual { get; }
        public string Proposed { get; }
        /// <summary>The backend AI fixer proposed this value during Fix all (not a
        /// ready-made fix from the scan).</summary>
        public bool IsAiProposal { get; }

        /// <summary>The parameter the fix writes ("" when none, e.g. a type rename).</summary>
        public string ParameterName { get; }
        /// <summary>Elements the issue covers (a batch row lists many); 1 = one element.</summary>
        public int AffectedCount { get; }
        /// <summary>The backend's confidence in the fix ("high"/"medium"/"low"), shown as
        /// given; "" when it sent none. Display only.</summary>
        public string Confidence { get; }

        /// <summary>"Parameter: X · affects N elements · Confidence: high" in the dialog's
        /// language; only the parts the row has. Set by <see cref="JkrConfirmModel"/>.</summary>
        public string Detail { get; internal set; } = "";
        public bool HasDetail => Detail.Length > 0;

        internal static string BuildDetail(JkrConfirmRow r, string lang)
        {
            bool ms = string.Equals(lang, "ms", StringComparison.OrdinalIgnoreCase);
            var parts = new List<string>();
            if (r.ParameterName.Length > 0) parts.Add("Parameter: " + r.ParameterName);
            if (r.AffectedCount > 1)
                parts.Add(ms ? $"melibatkan {r.AffectedCount} elemen" : $"affects {r.AffectedCount} elements");
            if (r.Confidence.Length > 0) parts.Add((ms ? "Keyakinan: " : "Confidence: ") + r.Confidence);
            return string.Join(" · ", parts);
        }
        /// <summary>The issue this row writes (its pane id); "" for a row built by hand.</summary>
        public string IssueId { get; }

        /// <summary>AI-434: the row needs a person's choice among several valid codes. It is listed in the
        /// "Needs your choice" bucket under a picker, never as a ready-made or AI-proposed value.</summary>
        public bool IsPick { get; }
        public IReadOnlyList<string> PickOptions { get; } = Array.Empty<string>();
        /// <summary>The parameter the code is written to ("Kod_Jenis_jkr_stt"), or "".</summary>
        public string PickParameter { get; } = "";
        /// <summary>The AI's code for this element among <see cref="PickOptions"/>, or "".</summary>
        public string PickCode { get; } = "";
        public string PickReason { get; } = "";

        /// <summary>The old value as shown: <see cref="Actual"/>, or "(missing)" in the
        /// dialog's language when the model has none. Set by <see cref="JkrConfirmModel"/>.</summary>
        public string Old { get; internal set; }

        /// <summary>"element [id]", or just the element, or just [id].</summary>
        public string Scope =>
            string.Join(" ", new[] { Item, ElementId > 0 ? $"[{ElementId}]" : "" }.Where(p => p.Length > 0));

        /// <summary>"title · scope" (Clear Sheet .ci .r).</summary>
        public string Caption => Scope.Length == 0 ? Title : $"{Title} · {Scope}";

        public JkrConfirmRow(string item, string title, string actual, string proposed,
                             long elementId = 0, bool isAiProposal = false,
                             string parameterName = "", int affectedCount = 1, string confidence = "",
                             string issueId = "",
                             IReadOnlyList<string> pickOptions = null, string pickParameter = "",
                             string pickCode = "", string pickReason = "")
        {
            ParameterName = parameterName ?? "";
            AffectedCount = Math.Max(1, affectedCount);
            Confidence = confidence ?? "";
            Detail = BuildDetail(this, "en");
            IssueId = issueId ?? "";
            if (pickOptions != null && pickOptions.Count > 0)
            {
                IsPick = true; PickOptions = pickOptions; PickParameter = pickParameter ?? "";
                PickCode = pickCode ?? ""; PickReason = pickReason ?? "";
            }
            Item = item ?? "";
            Title = title ?? "";
            ElementId = elementId;
            Actual = actual ?? "";
            Proposed = string.IsNullOrEmpty(proposed) ? "—" : proposed;
            IsAiProposal = isAiProposal;
            Old = Actual.Length > 0 ? Actual : JkrConfirmModel.MissingIn("en");
        }

        /// <summary>Row for one fixable issue: its Revit category (else the issue's
        /// category) and element, the value before and after, and whether the AI
        /// fixer proposed it.</summary>
        public static JkrConfirmRow FromIssue(IssueVm issue)
        {
            if (issue == null) throw new ArgumentNullException(nameof(issue));
            var actual = !string.IsNullOrEmpty(issue.FixOldValue) ? issue.FixOldValue : issue.Actual;
            if (actual == "(none)" || actual == "(empty)" || actual == "—") actual = "";
            var where = !string.IsNullOrWhiteSpace(issue.RevitCategory) ? issue.RevitCategory : issue.Category;
            return issue.IsPick
                ? new JkrConfirmRow(where, issue.Title, actual, "", issue.RevitElementId, false,
                                    issue.FixParameterName, issue.LocateCount, issue.Confidence, issue.Id,
                                    issue.ValidOptions, JkrManualFix.ParameterOf(issue.RawRule),
                                    issue.AiSuggestedCode, issue.AiSuggestionReason)
                : new JkrConfirmRow(where, issue.Title, actual, issue.FixValue,
                                    issue.RevitElementId, issue.FixProposedByAi,
                                    issue.FixParameterName, issue.LocateCount, issue.Confidence, issue.Id);
        }
    }

    /// <summary>
    /// The confirm dialog shown before every model write (Clear Sheet "confirm
    /// changes"). Revit- and WPF-free so its rules are unit-tested:
    ///  - nothing is written unless the user confirms;
    ///  - from JkrFixBatch.PauseWarningElementCount affected elements (instances of
    ///    renamed types included) it warns that Revit may pause.
    /// Rows are grouped into ready-made fixes and AI fixer proposals.
    /// </summary>
    public sealed class JkrConfirmModel : INotifyPropertyChanged
    {
        private static readonly Dictionary<string, Dictionary<string, string>> T =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ms"] = new Dictionary<string, string>
                {
                    ["confirmFixT"] = "Tulis perubahan ini ke model?",
                    ["confirmFixS"] = "Tiada apa-apa ditulis sehingga anda sahkan. Revit boleh undo kesemuanya sebagai satu langkah.",
                    ["write1"] = "Tulis 1 perubahan",
                    ["writeN"] = "Tulis {0} perubahan",
                    ["ready"] = "Pembaikan sedia",
                    ["ai"] = "Dicadangkan oleh pembaiki AI",
                    ["pick"] = "Perlu pilihan anda",
                    ["aiBadge"] = "Dicadangkan AI",
                    ["pickN"] = "{0} elemen",
                    ["missing"] = "(tiada)",
                    ["cancel"] = "Batal",
                    ["pause"] = "Ini melibatkan {0} elemen atau lebih. Revit mungkin terhenti seketika semasa perubahan ditulis.",
                },
                ["en"] = new Dictionary<string, string>
                {
                    ["confirmFixT"] = "Write these changes to the model?",
                    ["confirmFixS"] = "Nothing is written until you confirm. Revit can undo all of them as one step.",
                    ["write1"] = "Write 1 change",
                    ["writeN"] = "Write {0} changes",
                    ["ready"] = "Ready-made fixes",
                    ["ai"] = "Proposed by the AI fixer",
                    ["pick"] = "Needs your choice",
                    ["aiBadge"] = "AI suggested",
                    ["pickN"] = "{0} elements",
                    ["missing"] = "(missing)",
                    ["cancel"] = "Cancel",
                    ["pause"] = "This touches {0} or more elements. Revit may pause for a moment while the changes are written.",
                },
            };

        public JkrConfirmKind Kind { get; }
        public IReadOnlyList<JkrConfirmRow> Rows { get; }
        public string Lang { get; }

        /// <summary>Elements the write changes, instances of renamed types included
        /// (capped at the warning threshold by JkrAffectedElements).</summary>
        public int AffectedElements { get; }

        public JkrConfirmModel(JkrConfirmKind kind, IEnumerable<JkrConfirmRow> rows, int affectedElements, string lang = "en")
        {
            Kind = kind;
            Rows = (rows ?? Enumerable.Empty<JkrConfirmRow>()).ToList();
            AffectedElements = Math.Max(0, affectedElements);
            Lang = T.ContainsKey(lang ?? "") ? lang.ToLowerInvariant() : "en";
            foreach (var r in Rows) { r.Old = r.Actual.Length > 0 ? r.Actual : MissingIn(Lang); r.Detail = JkrConfirmRow.BuildDetail(r, Lang); }
            ReadyRows = Rows.Where(r => !r.IsAiProposal && !r.IsPick).ToList();
            AiRows = Rows.Where(r => r.IsAiProposal && !r.IsPick).ToList();
            PickRows = Rows.Where(r => r.IsPick).ToList();
            // One picker per category + parameter + option set; every element in it is still its own write.
            PickGroups = PickRows
                .GroupBy(r => r.Item + "|" + r.PickParameter + "|" + string.Join(",", r.PickOptions))
                .Select(g => new JkrPickGroup(g.ToList(), Lang, S("pickN")))
                .ToList();
        }

        /// <summary>"Needs your choice" (AI-434): rows with several valid codes. Fix all never adds these.</summary>
        public IReadOnlyList<JkrConfirmRow> PickRows { get; }
        public IReadOnlyList<JkrPickGroup> PickGroups { get; }
        public int PickCount => PickRows.Count;
        public bool HasPick => PickCount > 0;
        public string PickHeader => S("pick");

        /// <summary>The code the person chose for this pick row (its group's picker).</summary>
        public string SelectedFor(JkrConfirmRow row) =>
            PickGroups.FirstOrDefault(g => g.Rows.Contains(row))?.Selected ?? "";

        /// <summary>"(missing)" for an empty old value, in <paramref name="lang"/>.</summary>
        internal static string MissingIn(string lang) =>
            T[T.ContainsKey(lang ?? "") ? lang : "en"]["missing"];

        /// <summary>"Ready-made fixes": rows from the scan's own fixes.</summary>
        public IReadOnlyList<JkrConfirmRow> ReadyRows { get; }
        /// <summary>"Proposed by the AI fixer": rows whose value the AI fixer returned.</summary>
        public IReadOnlyList<JkrConfirmRow> AiRows { get; }
        public int ReadyCount => ReadyRows.Count;
        public int AiCount => AiRows.Count;
        public bool HasReady => ReadyCount > 0;
        public bool HasAi => AiCount > 0;
        public string ReadyHeader => S("ready");
        public string AiHeader => S("ai");

        private string S(string key) => T[Lang][key];
        internal static string Phrase(string lang, string key) => T[T.ContainsKey(lang ?? "") ? lang : "en"][key];

        public string Title => S("confirmFixT");
        public string Sub => S("confirmFixS");

        /// <summary>"Write n changes": every row, both groups. All proposals are back
        /// before the dialog opens (one /jkr-autofix call), so there is no "on the way".</summary>
        public string ConfirmLabel => Rows.Count == 1 ? S("write1") : string.Format(S("writeN"), Rows.Count);

        public string CancelLabel => S("cancel");

        public bool ShowPauseWarning => JkrFixBatch.MayPauseRevit(AffectedElements);
        public string PauseWarning => ShowPauseWarning ? string.Format(S("pause"), JkrFixBatch.PauseWarningElementCount) : "";

        /// <summary>The confirm button is enabled: there is something to confirm.</summary>
        public bool CanConfirm => Rows.Count > 0 && PickGroups.All(g => g.IsValid);

        /// <summary>Set when the user confirmed; the caller writes only when true.</summary>
        public bool Confirmed { get; private set; }

        /// <summary>Confirm button / Enter. Ignored while <see cref="CanConfirm"/> is false.</summary>
        public bool TryConfirm()
        {
            if (!CanConfirm) return false;
            Confirmed = true;
            return true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// One picker in the confirm dialog (AI-434): the valid codes for one category + parameter, shared by
    /// every element listed under it. Starts on the AI's code (badged "AI suggested") when it gave one,
    /// else on the first option. Choosing a code does not write anything: each element is still its own
    /// write and re-check.
    /// </summary>
    public sealed class JkrPickGroup : INotifyPropertyChanged
    {
        private string _selected;

        public IReadOnlyList<JkrConfirmRow> Rows { get; }
        public IReadOnlyList<string> Options { get; }
        public string Parameter { get; }
        public string Category { get; }
        public string PickCode { get; }
        public string Tooltip { get; }
        public string Badge { get; }
        public string CountLabel { get; }

        public JkrPickGroup(IReadOnlyList<JkrConfirmRow> rows, string lang, string countFormat)
        {
            Rows = rows;
            var first = rows[0];
            Options = first.PickOptions;
            Parameter = first.PickParameter;
            Category = first.Item;
            // The AI's code, when it picked the same one for every element in the group.
            var codes = rows.Select(r => r.PickCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            PickCode = codes.Count == 1 ? codes[0] : "";
            Tooltip = PickCode.Length > 0 ? rows.Select(r => r.PickReason).FirstOrDefault(x => x.Length > 0) ?? "" : "";
            _selected = PickCode.Length > 0 ? PickCode : Options[0];
            Badge = JkrConfirmModel.Phrase(lang, "aiBadge");
            CountLabel = string.Format(countFormat, rows.Count);
        }

        /// <summary>The chosen code; a value not in <see cref="Options"/> is kept but makes the dialog unconfirmable.</summary>
        public string Selected
        {
            get => _selected;
            set
            {
                var v = value ?? "";
                if (_selected == v) return;
                _selected = v;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAiBadge)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsValid)));
            }
        }

        public bool IsValid => Options.Contains(_selected);
        public bool ShowAiBadge => PickCode.Length > 0 && string.Equals(_selected, PickCode, StringComparison.OrdinalIgnoreCase);

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
