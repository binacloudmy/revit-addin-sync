using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using RevitWebAppSync.Handlers;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// The Fix all result window (design "JKR Fix all result"): how many changes
    /// were written as one undo step, and every fix that was not, grouped by
    /// reason with a how-to-fix tip and a Locate button per element. WPF-free; the
    /// window is UI\Jkr\Modals\FixResultWindow.xaml.
    /// </summary>
    public sealed class FixResultModel
    {
        /// <summary>Rows shown per group before "Show n more".</summary>
        public const int RowsShown = 5;

        private readonly string _lang;
        private readonly RenameResult _result;

        public FixResultModel(RenameResult result, int applied, bool fixAll,
                              Func<long, string> elementLabel = null, string lang = null)
        {
            _result = result ?? throw new ArgumentNullException(nameof(result));
            _lang = lang ?? PaneLanguage.Current;
            Applied = Math.Max(0, applied);
            Failed = result.Failures.Count > 0 ? result.Failures.Count : result.Failed + result.Skipped;

            Kicker = (fixAll ? T("Baiki semua", "Fix all") : T("Baiki", "Fix")).ToUpperInvariant();
            Title = AnyWritten ? T("Sebahagian perubahan tidak ditulis", "Some changes weren't written")
                               : T("Tiada apa-apa ditulis", "Nothing was written");

            Groups = result.Failures
                .GroupBy(f => KindOf(f.Why))
                .Select(g => new FixResultGroup(g.Key, g.ToList(), elementLabel, _lang, this))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => (int)g.Kind)
                .ToList();
            if (Groups.Count > 0) Groups[0].IsOpen = true;
        }

        private string T(string ms, string en) => PaneText.IsEn(_lang) ? en : ms;

        public int Applied { get; }
        public int Failed { get; }
        public bool AnyWritten => Applied > 0;
        public bool NothingWritten => !AnyWritten;

        public string Kicker { get; }
        public string Title { get; }

        // ── summary cards ──
        public string WrittenLabel => T("ditulis", "written");
        public string WrittenSub => T("Satu langkah undo · ", "One undo step · ");
        public string UndoKey => "Ctrl+Z";
        public string WrittenSubEnd => T(" batalkan semua", " undoes them all");
        public string FailedLabel => T("tidak ditulis", "not written");
        public string FailedSub => T("Kekal Terbuka · sebab dipapar pada setiap isu", "Stay Open · the reason is on each issue");

        /// <summary>The single card when nothing was written.</summary>
        public string NothingTitle => PaneText.IsEn(_lang)
            ? $"{Failed} fix{(Failed == 1 ? " was" : "es were")} not written"
            : $"{Failed} pembaikan tidak ditulis";
        public string NothingSub => T("Model tidak berubah. Isu ini kekal Terbuka, dengan sebab pada setiap baris.",
                                      "The model is unchanged. These issues stay Open, with the reason on each row.");

        /// <summary>Share of the batch that was written (0..1), for the bar.</summary>
        public double WrittenShare => Applied + Failed == 0 ? 0 : (double)Applied / (Applied + Failed);

        // ── reasons ──
        public string ReasonsHeader => T("Sebab tidak ditulis", "Why they weren't written");
        public string ReasonsCount => PaneText.IsEn(_lang)
            ? $"{Groups.Count} reason{(Groups.Count == 1 ? "" : "s")} · {Failed} issue{(Failed == 1 ? "" : "s")}"
            : $"{Groups.Count} sebab · {Failed} isu";
        public IReadOnlyList<FixResultGroup> Groups { get; }

        public string HowToLabel => T("Cara baiki: ", "How to fix: ");
        public string LocateLabel => T("Cari", "Locate");
        public string LocateHint => T("Cari elemen dalam model", "Locate the element in the model");

        // ── footer ──
        public string CopyLabel => T("Salin senarai", "Copy list");
        public string CopiedLabel => T("Disalin", "Copied");
        public string CopyHint => T("Senarai penuh untuk pemodel atau pengurus BIM", "The full list, for the modeller or BIM manager");
        public string CloseLabel => T("Tutup", "Close");

        /// <summary>Only one group is open at a time; opening one closes the others.</summary>
        public void Toggle(FixResultGroup group)
        {
            if (group == null) return;
            var open = !group.IsOpen;
            foreach (var g in Groups) g.IsOpen = false;
            group.IsOpen = open;
        }

        /// <summary>The whole list as text, every failure (no "…and n more").</summary>
        public string CopyText() =>
            PaneText.FailedFixes(Applied, Failed, _result.BuildFailDetails(0, _lang), _lang);

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

    /// <summary>One reason and the fixes that failed for it.</summary>
    public sealed class FixResultGroup : INotifyPropertyChanged
    {
        private readonly string _lang;
        private readonly FixResultModel _owner;
        private bool _open;
        private bool _all;

        internal FixResultGroup(FixResultModel.ReasonKind kind, List<FixFailure> failures,
                                Func<long, string> elementLabel, string lang, FixResultModel owner)
        {
            Kind = kind;
            _lang = lang;
            _owner = owner;
            var en = PaneText.IsEn(lang);
            Items = failures.Select(f => new FixResultItem(f, elementLabel, lang, kind == FixResultModel.ReasonKind.Other)).ToList();
            var elements = failures.Select(f => f.ElementId).Distinct().Count();
            Scope = en ? $"{elements} element{(elements == 1 ? "" : "s")}" : $"{elements} elemen";
            (Title, HowTo) = Text(kind, en);
        }

        public FixResultModel.ReasonKind Kind { get; }
        public string Title { get; }
        public string HowTo { get; }
        public string Scope { get; }
        public int Count => Items.Count;
        public IReadOnlyList<FixResultItem> Items { get; }

        public bool IsOpen
        {
            get => _open;
            set { if (_open == value) return; _open = value; Changed(nameof(IsOpen)); }
        }

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
        public string MoreText => PaneText.IsEn(_lang) ? $"Show {Hidden} more" : $"Tunjuk {Hidden} lagi";

        public void Toggle() => _owner.Toggle(this);

        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static (string Title, string HowTo) Text(FixResultModel.ReasonKind kind, bool en)
        {
            switch (kind)
            {
                case FixResultModel.ReasonKind.Category:
                    return en
                        ? ("Category doesn't take JKR parameters",
                           "This element's category has no JKR shared parameters. Add them to the category (Manage › Project Parameters), or move the element to the right category, then run Fix all again.")
                        : ("Kategori elemen tidak terima parameter JKR",
                           "Kategori elemen ini tiada parameter kongsi JKR. Tambah parameter pada kategori (Manage › Project Parameters), atau tukar elemen ke kategori yang betul, kemudian Baiki semua sekali lagi.");
                case FixResultModel.ReasonKind.ModelGroup:
                    return en
                        ? ("Element is inside a model group",
                           "Revit doesn't allow instance parameters inside a group to be changed from outside. Edit the group (or ungroup it), then run Fix all again.")
                        : ("Elemen dalam model group",
                           "Revit tidak benarkan ubah parameter elemen dalam group dari luar. Edit group (atau ungroup), kemudian Baiki semua sekali lagi.");
                case FixResultModel.ReasonKind.ReadOnly:
                    return en
                        ? ("Parameter is read-only",
                           "This value is set by the type, a formula or a group. Change it in Type Properties or at its source.")
                        : ("Parameter baca sahaja",
                           "Nilai ini dikawal oleh jenis, formula atau group. Ubah pada Type Properties atau pada sumber nilainya.");
                case FixResultModel.ReasonKind.ValueType:
                    return en
                        ? ("Value type not supported",
                           "This parameter isn't text, so it can't be filled automatically. Enter its value yourself in Properties.")
                        : ("Jenis nilai tidak disokong",
                           "Parameter ini bukan teks, jadi tidak boleh diisi secara automatik. Isi nilainya sendiri dalam Properties.");
                case FixResultModel.ReasonKind.NameInUse:
                    return en
                        ? ("Name already in use",
                           "Another element already has this name. Rename or delete the other one first, then run Fix all again.")
                        : ("Nama sudah digunakan",
                           "Elemen lain sudah menggunakan nama ini. Tukar nama atau padam elemen itu dahulu, kemudian Baiki semua sekali lagi.");
                case FixResultModel.ReasonKind.Missing:
                    return en
                        ? ("Element no longer in the model",
                           "The element was deleted or replaced after the scan. Re-scan to refresh the list.")
                        : ("Elemen tiada lagi dalam model",
                           "Elemen telah dipadam atau diganti selepas imbasan. Imbas semula untuk mengemas kini senarai.");
                default:
                    return en
                        ? ("Revit refused the change",
                           "The reason Revit gave is under each item. Fix it in Revit, then run Fix all again.")
                        : ("Revit tidak menerima perubahan",
                           "Sebab daripada Revit dipapar pada setiap item. Betulkan dalam Revit, kemudian Baiki semua sekali lagi.");
            }
        }
    }

    /// <summary>One failed fix: what it would have written, and on which element.</summary>
    public sealed class FixResultItem
    {
        internal FixResultItem(FixFailure f, Func<long, string> elementLabel, string lang, bool withReason)
        {
            ElementId = f.ElementId;
            var en = PaneText.IsEn(lang);
            Param = f.IsRename
                ? (string.IsNullOrEmpty(f.NewName) ? (en ? "Rename" : "Tukar nama") : "→ " + f.NewName)
                : (f.Parameter ?? "");
            string label = null;
            try { label = elementLabel?.Invoke(f.ElementId); } catch { }
            Where = (en ? "Element " : "Elemen ") + f.ElementId + (string.IsNullOrWhiteSpace(label) ? "" : " · " + label);
            Reason = withReason ? PaneText.FixReason(f.Why, lang) : "";
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
