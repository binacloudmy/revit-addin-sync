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

    /// <summary>One change listed in the confirm dialog: "item · title" over
    /// "actual → proposed" (prototype .cf-r).</summary>
    public sealed class JkrConfirmRow
    {
        public string Item { get; }
        public string Title { get; }
        public string Actual { get; }
        public string Proposed { get; }
        public string Caption => string.IsNullOrEmpty(Item) ? Title : $"{Item} · {Title}";

        public JkrConfirmRow(string item, string title, string actual, string proposed)
        {
            Item = item ?? "";
            Title = title ?? "";
            Actual = string.IsNullOrEmpty(actual) ? "—" : actual;
            Proposed = string.IsNullOrEmpty(proposed) ? "—" : proposed;
        }

        /// <summary>Row for one auto-fixable issue. Item is the issue's category until
        /// step 5 keys rows by Borang item.</summary>
        public static JkrConfirmRow FromIssue(IssueVm issue)
        {
            if (issue == null) throw new ArgumentNullException(nameof(issue));
            var actual = !string.IsNullOrEmpty(issue.FixOldValue) ? issue.FixOldValue : issue.Actual;
            if (actual == "(none)" || actual == "(empty)") actual = "";
            return new JkrConfirmRow(issue.Category, issue.Title, actual, issue.FixValue);
        }
    }

    /// <summary>
    /// The confirm dialog shown before every model write (prototype askFix /
    /// doConfirm). Revit- and WPF-free so its rules are unit-tested:
    ///  - nothing is written unless the user confirms;
    ///  - from JkrFixBatch.PauseWarningElementCount affected elements (instances of
    ///    renamed types included) it warns that Revit may pause.
    /// Strings are the prototype's T.ms / T.en; the pause line is new.
    /// </summary>
    public sealed class JkrConfirmModel : INotifyPropertyChanged
    {
        private static readonly Dictionary<string, Dictionary<string, string>> T =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ms"] = new Dictionary<string, string>
                {
                    ["confirmFixT"] = "Ubah model?",
                    ["confirmFixS"] = "Perubahan berikut akan ditulis ke model Revit. Anda boleh undo dalam Revit.",
                    ["confirmFixB"] = "Sahkan & ubah",
                    ["cancel"] = "Batal",
                    ["pause"] = "Ini melibatkan {0} elemen atau lebih. Revit mungkin terhenti seketika semasa perubahan ditulis.",
                },
                ["en"] = new Dictionary<string, string>
                {
                    ["confirmFixT"] = "Modify the model?",
                    ["confirmFixS"] = "These changes will be written to the Revit model. You can undo in Revit.",
                    ["confirmFixB"] = "Confirm & modify",
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
        }

        private string S(string key) => T[Lang][key];

        public string Title => S("confirmFixT");
        public string Sub => S("confirmFixS");

        /// <summary>"Confirm & modify (n)".</summary>
        public string ConfirmLabel => $"{S("confirmFixB")} ({Rows.Count})";

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
