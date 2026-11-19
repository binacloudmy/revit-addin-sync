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

        /// <summary>The old value as shown: <see cref="Actual"/>, or "(missing)" in the
        /// dialog's language when the model has none. Set by <see cref="JkrConfirmModel"/>.</summary>
        public string Old { get; internal set; }

        /// <summary>"element [id]", or just the element, or just [id].</summary>
        public string Scope =>
            string.Join(" ", new[] { Item, ElementId > 0 ? $"[{ElementId}]" : "" }.Where(p => p.Length > 0));

        /// <summary>"title · scope" (Clear Sheet .ci .r).</summary>
        public string Caption => Scope.Length == 0 ? Title : $"{Title} · {Scope}";

        public JkrConfirmRow(string item, string title, string actual, string proposed,
                             long elementId = 0, bool isAiProposal = false)
        {
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
            return new JkrConfirmRow(where, issue.Title, actual, issue.FixValue,
                                     issue.RevitElementId, issue.FixProposedByAi);
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
            foreach (var r in Rows) r.Old = r.Actual.Length > 0 ? r.Actual : MissingIn(Lang);
            ReadyRows = Rows.Where(r => !r.IsAiProposal).ToList();
            AiRows = Rows.Where(r => r.IsAiProposal).ToList();
        }

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

        public string Title => S("confirmFixT");
        public string Sub => S("confirmFixS");

        /// <summary>"Write n changes": every row, both groups. All proposals are back
        /// before the dialog opens (one /jkr-autofix call), so there is no "on the way".</summary>
        public string ConfirmLabel => Rows.Count == 1 ? S("write1") : string.Format(S("writeN"), Rows.Count);

        public string CancelLabel => S("cancel");

        public bool ShowPauseWarning => JkrFixBatch.MayPauseRevit(AffectedElements);
        public string PauseWarning => ShowPauseWarning ? string.Format(S("pause"), JkrFixBatch.PauseWarningElementCount) : "";

        /// <summary>The confirm button is enabled: there is something to confirm.</summary>
        public bool CanConfirm => Rows.Count > 0;

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
}
