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
        /// <summary>Paid "Generate room schedule": needs the consent checkbox.</summary>
        Generate,
        /// <summary>Reset: undo the add-in's own fixes in the model.</summary>
        Revert,
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
    ///  - the paid Generate action stays disabled until the consent box is ticked,
    ///    and the box starts unticked every time the dialog opens;
    ///  - from JkrFixBatch.PauseWarningElementCount affected elements (instances of
    ///    renamed types included) it warns that Revit may pause.
    /// Strings are the prototype's T.ms / T.en; the pause and revert lines are new.
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
                    ["confirmGenT"] = "Tindakan berbayar",
                    ["confirmGenS"] = "Jadual ruang akan dijana dan ditambah ke model. Setiap klik jana dicaj.",
                    ["confirmGenB"] = "Jana",
                    ["confirmRevT"] = "Pulihkan model?",
                    ["confirmRevS"] = "Auto-fix berikut akan dibatalkan dalam model Revit, sebagai satu langkah yang boleh di-undo dalam Revit.",
                    ["confirmRevB"] = "Sahkan & pulihkan",
                    ["consent"] = "Saya faham tindakan ini dicaj ke akaun saya.",
                    ["cancel"] = "Batal",
                    ["pause"] = "Ini melibatkan {0} elemen atau lebih. Revit mungkin terhenti seketika semasa perubahan ditulis.",
                },
                ["en"] = new Dictionary<string, string>
                {
                    ["confirmFixT"] = "Modify the model?",
                    ["confirmFixS"] = "These changes will be written to the Revit model. You can undo in Revit.",
                    ["confirmFixB"] = "Confirm & modify",
                    ["confirmGenT"] = "Paid action",
                    ["confirmGenS"] = "A room schedule will be generated and added to the model. Each generate click is charged.",
                    ["confirmGenB"] = "Generate",
                    ["confirmRevT"] = "Revert the model?",
                    ["confirmRevS"] = "These auto-fixes will be undone in the Revit model, as one step you can undo in Revit.",
                    ["confirmRevB"] = "Confirm & revert",
                    ["consent"] = "I understand this action is charged to my account.",
                    ["cancel"] = "Cancel",
                    ["pause"] = "This touches {0} or more elements. Revit may pause for a moment while the changes are written.",
                },
            };

        private bool _consent;

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

        public string Title => S(Kind == JkrConfirmKind.Generate ? "confirmGenT" : Kind == JkrConfirmKind.Revert ? "confirmRevT" : "confirmFixT");
        public string Sub => S(Kind == JkrConfirmKind.Generate ? "confirmGenS" : Kind == JkrConfirmKind.Revert ? "confirmRevS" : "confirmFixS");

        /// <summary>"Confirm & modify (n)"; Generate has no count (prototype).</summary>
        public string ConfirmLabel => Kind == JkrConfirmKind.Generate
            ? S("confirmGenB")
            : $"{S(Kind == JkrConfirmKind.Revert ? "confirmRevB" : "confirmFixB")} ({Rows.Count})";

        public string CancelLabel => S("cancel");
        public string ConsentText => S("consent");
        public bool NeedsConsent => Kind == JkrConfirmKind.Generate;

        public bool ShowPauseWarning => Kind != JkrConfirmKind.Generate && JkrFixBatch.MayPauseRevit(AffectedElements);
        public string PauseWarning => ShowPauseWarning ? string.Format(S("pause"), JkrFixBatch.PauseWarningElementCount) : "";

        /// <summary>Starts unticked every time the dialog opens.</summary>
        public bool Consent
        {
            get => _consent;
            set
            {
                if (_consent == value) return;
                _consent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Consent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConfirm)));
            }
        }

        /// <summary>The confirm button is enabled: there is something to confirm, and
        /// for the paid action the consent box is ticked.</summary>
        public bool CanConfirm => Rows.Count > 0 && (!NeedsConsent || Consent);

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
