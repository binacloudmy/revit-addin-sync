using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using RevitWebAppSync.Handlers;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// The fix outcome window (Clear Sheet "fix outcome"): "n changes written, m not
    /// written", the one-undo-step line, every fix that was not written grouped by
    /// reason with a how-to-fix tip and a Locate button per element, and "The m
    /// changes not written stay in Open." The counts are the batch's own result:
    /// <c>applied</c> from the handler, not written from its failure records.
    /// WPF-free; the window is UI\Jkr\Modals\FixResultWindow.xaml.
    /// </summary>
    public sealed class FixResultModel
    {
        /// <summary>Rows shown per group before "Show n more".</summary>
        public const int RowsShown = 5;

        private readonly RenameResult _result;

        public FixResultModel(RenameResult result, int applied, bool fixAll, Func<long, string> elementLabel = null)
        {
            _result = result ?? throw new ArgumentNullException(nameof(result));
            Applied = Math.Max(0, applied);
            Failed = result.Failures.Count > 0 ? result.Failures.Count : result.Failed + result.Skipped;
            FixAll = fixAll;

            Groups = result.Failures
                .GroupBy(f => KindOf(f.Why))
                .Select(g => new FixResultGroup(g.Key, g.ToList(), elementLabel))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => (int)g.Kind)
                .ToList();
        }

        private string Changes(int n) => $"{n} change{(n == 1 ? "" : "s")}";

        public int Applied { get; }
        public int Failed { get; }
        /// <summary>Fix all (several fixes) rather than one fix.</summary>
        public bool FixAll { get; }
        public bool AnyWritten => Applied > 0;
        public bool NothingWritten => !AnyWritten;

        /// <summary>"5 changes written, 2 not written"; "5 changes written" when all were;
        /// "2 changes not written" when none were.</summary>
        public string Title =>
            NothingWritten ? ($"{Changes(Failed)} not written")
            : Failed == 0 ? ($"{Changes(Applied)} written")
            : $"{Changes(Applied)} written, {Failed} not written";

        /// <summary>The one-undo-step line, or "The model is unchanged." when nothing was written.</summary>
        public string UndoLine => AnyWritten
            ? "The written changes are one undo step. Ctrl+Z in Revit undoes them together."
            : "The model is unchanged.";

        /// <summary>"The 2 changes not written stay in Open." (failed fixes stay Open
        /// with their reason on the row).</summary>
        public string StayOpenLine => !HasStayOpen ? ""
            : $"The {Changes(Failed)} not written {(Failed == 1 ? "stays" : "stay")} in Open.";
        public bool HasStayOpen => Failed > 0;

        public IReadOnlyList<FixResultGroup> Groups { get; }

        public string LocateLabel => "Locate";
        public string LocateHint => "Locate the element in the model";

        // ── footer ──
        public string CopyLabel => "Copy list for BIM manager";
        public string CopiedLabel => "Copied";
        public string DoneLabel => "Done";
        public bool CanCopy => Failed > 0;

        /// <summary>The whole list as text, every failure (no "…and n more").</summary>
        public string CopyText() =>
            PaneText.FailedFixes(Applied, Failed, _result.BuildFailDetails(0));

        // ── grouping ──
        public enum ReasonKind { Category, ModelGroup, ReadOnly, ValueType, NameInUse, Missing, Other }

        private static readonly (Regex Re, ReasonKind Kind)[] Kinds =
        {
            (new Regex(@"^Parameter '.*' (not found on element|was bound but still not visible)", RegexOptions.Singleline), ReasonKind.Category),
            (new Regex(@"inside a model group", RegexOptions.IgnoreCase), ReasonKind.ModelGroup),
            (new Regex(@"is read-only", RegexOptions.IgnoreCase), ReasonKind.ReadOnly),
            (new Regex(@"^Unsupported storage type", RegexOptions.IgnoreCase), ReasonKind.ValueType),
            (new Regex(@"already (in use|exists|used)|is not unique|must be unique", RegexOptions.IgnoreCase), ReasonKind.NameInUse),
            (new Regex(@"not found in the model|^Element \d+ not found$|^Type not found", RegexOptions.IgnoreCase), ReasonKind.Missing),
        };

        public static ReasonKind KindOf(string why)
        {
            foreach (var (re, kind) in Kinds)
                if (re.IsMatch(why ?? "")) return kind;
            return ReasonKind.Other;
        }
    }

    /// <summary>One reason and the fixes that failed for it (a Clear Sheet .rs card).</summary>
    public sealed class FixResultGroup : INotifyPropertyChanged
    {
        private bool _all;

        internal FixResultGroup(FixResultModel.ReasonKind kind, List<FixFailure> failures, Func<long, string> elementLabel)
        {
            Kind = kind;
            Items = failures.Select(f => new FixResultItem(f, elementLabel, kind == FixResultModel.ReasonKind.Other)).ToList();
            (Title, HowTo) = Text(kind);
        }

        public FixResultModel.ReasonKind Kind { get; }
        public string Title { get; }
        public string HowTo { get; }
        public int Count => Items.Count;
        public IReadOnlyList<FixResultItem> Items { get; }

        public bool ShowingAll
        {
            get => _all;
            set
            {
                if (_all == value) return;
                _all = value;
                Changed(nameof(ShowingAll)); Changed(nameof(Visible)); Changed(nameof(Hidden)); Changed(nameof(HasMore)); Changed(nameof(MoreText));
            }
        }

        public IReadOnlyList<FixResultItem> Visible => _all ? Items : Items.Take(FixResultModel.RowsShown).ToList();
        public int Hidden => Count - Visible.Count;
        public bool HasMore => Hidden > 0;
        public string MoreText => $"Show {Hidden} more";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static (string Title, string HowTo) Text(FixResultModel.ReasonKind kind)
        {
            switch (kind)
            {
                case FixResultModel.ReasonKind.Category:
                    return ("Category doesn't take JKR parameters",
                           "This element's category has no JKR shared parameters. Add them to the category (Manage › Project Parameters), or move the element to the right category, then run Fix all again.");
                case FixResultModel.ReasonKind.ModelGroup:
                    return ("Element is inside a model group",
                           "Revit doesn't allow instance parameters inside a group to be changed from outside. Edit the group (or ungroup it), then run Fix all again.");
                case FixResultModel.ReasonKind.ReadOnly:
                    return ("Parameter is read-only",
                           "This value is set by the type, a formula or a group. Change it in Type Properties or at its source.");
                case FixResultModel.ReasonKind.ValueType:
                    return ("Value type not supported",
                           "This parameter isn't text, so it can't be filled automatically. Enter its value yourself in Properties.");
                case FixResultModel.ReasonKind.NameInUse:
                    return ("Name already in use",
                           "Another element already has this name. Rename or delete the other one first, then run Fix all again.");
                case FixResultModel.ReasonKind.Missing:
                    return ("Element no longer in the model",
                           "The element was deleted or replaced after the scan. Re-scan to refresh the list.");
                default:
                    return ("Revit refused the change",
                           "The reason Revit gave is under each item. Fix it in Revit, then run Fix all again.");
            }
        }
    }

    /// <summary>One failed fix: what it would have written, and on which element.</summary>
    public sealed class FixResultItem
    {
        internal FixResultItem(FixFailure f, Func<long, string> elementLabel, bool withReason)
        {
            ElementId = f.ElementId;
            Param = f.IsRename
                ? (string.IsNullOrEmpty(f.NewName) ? ("Rename") : "→ " + f.NewName)
                : (f.Parameter ?? "");
            string label = null;
            try { label = elementLabel?.Invoke(f.ElementId); } catch { }
            Where = "[" + f.ElementId + "]" + (string.IsNullOrWhiteSpace(label) ? "" : " " + label);
            Reason = withReason ? f.Why ?? "" : "";
        }

        public long ElementId { get; }
        public string Param { get; }
        public string Where { get; }
        /// <summary>Revit's own reason, shown only in the "Revit refused" group.</summary>
        public string Reason { get; }
        public bool HasReason => !string.IsNullOrEmpty(Reason);
        public bool CanLocate => ElementId > 0;
    }
}
