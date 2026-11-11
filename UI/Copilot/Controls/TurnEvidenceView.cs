#nullable enable
// What the turn measured, under the reply (lean-harness spec §7.11; W5 Task 3):
//   - the caution line whenever any write is not done (never decided from text);
//   - the receipt headline as fractions ("30/34 done · 4 failed (not owned)"),
//     with Undo (W3's UndoService, one entry per batch);
//   - the evidence strip of every read: count, what was searched, what was NOT
//     searched (in the warning colour), highlighted / visible counts;
//   - a thumbnail of every image a read returned (look; click to enlarge), so the
//     drafter sees what the model saw (§7.7);
//   - a check's rules, one line each, with Highlight on a failing rule (§21);
//   - Export to Excel for tables a read returned, check results included.
// All text comes from ReceiptTemplates over TurnEvidence.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    public sealed class TurnEvidenceActions
    {
        public Func<bool>? CanUndo;
        /// <summary>Why Undo is off (W3's UndoModel kind); None when it is available (L19).</summary>
        public Func<BinaVibe.Copilot.Undo.UndoBlock>? WhyNotUndo;
        public Action? Undo;
        /// <summary>Undo only the newest batch (offered when the turn has several).</summary>
        public Func<bool>? CanUndoLast;
        public Action? UndoLast;
        /// <summary>Redo what Undo took back.</summary>
        public Func<bool>? CanRedo;
        public Action? Redo;
        public Action<ResultTable>? Export;
        /// <summary>Select a handle's elements in Revit (find_elements highlight).</summary>
        public Action<string>? Highlight;
        /// <summary>Result card (result pattern): redraw the rings and callouts in the model.</summary>
        public Action? ShowInModel;
        /// <summary>Select the elements of a result group (instances of a type group).</summary>
        public Action<TypeGroup>? SelectGroup;
        public Action<TypeGroup>? ZoomGroup;
        public Action<TypeGroup>? OpenTypeProperties;
        /// <summary>Select and zoom to element ids (created items, a finding's elements).</summary>
        public Action<long[]>? ShowElements;
        /// <summary>Write a value the drafter typed into a result row (one more batch of the same turn); returns an error line, or null when it was written.</summary>
        public Func<TypeGroup, ChangeRow, string, System.Threading.Tasks.Task<string?>>? EditValue;
    }

    public static class TurnEvidenceView
    {
        private const int MaxEvidenceRows = 6;
        private const int MaxExports = 3;
        private const int MaxCheckRows = 12;

        public static FrameworkElement? Build(TurnEvidence? ev, ReceiptLanguage lang, TurnEvidenceActions actions, ResultCardModel? card = null)
        {
            if (ev == null) return null;
            var ms = lang == ReceiptLanguage.Ms;
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };

            if (ev.Caution)
            {
                var caution = CardKit.Card("Cp.AmberLn", "Cp.AmberBg", new Thickness(0, 0, 0, 6), 9);
                caution.Padding = new Thickness(10, 6, 10, 6);
                caution.Child = CardKit.Text("⚠  " + ReceiptTemplates.Caution(ev.Receipt, lang), 11.5, "Cp.AmberFg", FontWeights.Medium);
                sp.Children.Add(caution);
            }

            // Pane-owned facts that qualify the receipt (a cut table, M5).
            foreach (var note in ev.Notes)
                sp.Children.Add(CardKit.Text("⚠  " + note, 11, "Cp.AmberFg", margin: new Thickness(0, 0, 0, 4)));

            // The result card (result pattern) replaces the receipt headline and the
            // evidence strip; what it does not show moves under a Details toggle.
            var hasCard = card != null && card.Kind != ResultKind.None;
            if (hasCard)
            {
                var canUndo = ev.AnyCommitted && actions.Undo != null && SafeCanUndo(actions);
                var undoOff = ev.AnyCommitted && actions.Undo != null && !canUndo ? ReceiptTemplates.UndoOffLine(SafeWhyNot(actions), lang) : null;
                var cardView = ResultCardView.Build(card, lang, actions, canUndo, undoOff);
                if (cardView != null) sp.Children.Add(cardView);
                var details = new StackPanel();
                foreach (var item in EvidenceDedupe.DistinctBy(ev.Evidence, e => ReceiptTemplates.EvidenceLine(e, lang) + "\u0001" + ReceiptTemplates.NotSearchedLine(e, lang)).Take(MaxEvidenceRows))
                {
                    var line = ReceiptTemplates.EvidenceLine(item, lang);
                    if (line.Length > 0) details.Children.Add(CardKit.Text("⌕  " + line, 10.5, item.FailedReason != null ? "Cp.AmberFg" : "Cp.Muted"));
                    var missing = ReceiptTemplates.NotSearchedLine(item, lang);
                    if (missing.Length > 0) details.Children.Add(CardKit.Text("   " + missing, 10.5, "Cp.Muted"));
                }
                if (details.Children.Count > 0)
                {
                    var exp = new Expander { Header = CardKit.Text(ms ? "Butiran" : "Details", 11, "Cp.Muted"), Content = details, IsExpanded = false, Margin = new Thickness(0, 2, 0, 2) };
                    sp.Children.Add(exp);
                }
            }

            if (ev.HasWrites && !hasCard)
            {
                var receipt = CardKit.Card(ev.Caution ? "Cp.AmberLn" : "Cp.SuccessLn", "Cp.OkBg", new Thickness(0, 0, 0, 6), 10);
                receipt.Padding = new Thickness(12, 8, 12, 8);
                var rs = new StackPanel();
                rs.Children.Add(CardKit.Text(ReceiptTemplates.Headline(ev.Receipt, lang), 12.5, "Cp.Ink", FontWeights.SemiBold));
                var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                if (ev.AnyCommitted && actions.Undo != null && SafeCanUndo(actions))
                    buttons.Children.Add(CardKit.Chip(ms ? "Buat asal" : "Undo", actions.Undo));
                if (buttons.Children.Count > 0) rs.Children.Add(buttons);
                // L19: Undo is off (an edit in Revit after this turn, Revit's own Undo,
                // already undone): say why in one line instead of a button that vanished.
                var undoOff = ev.AnyCommitted && actions.Undo != null && !SafeCanUndo(actions)
                    ? ReceiptTemplates.UndoOffLine(SafeWhyNot(actions), lang)
                    : null;
                if (undoOff != null)
                    rs.Children.Add(CardKit.Text(undoOff, 10.5, "Cp.AmberFg", margin: new Thickness(0, 4, 0, 0)));
                if (ev.AnyCommitted && (undoOff != null || buttons.Children.Count == 0 || actions.Undo == null))
                    rs.Children.Add(CardKit.Text(ms
                            ? "Untuk membuat asal, guna senarai Undo Revit (entri bermula dengan \"BINA:\")."
                            : "To undo, use Revit's Undo list (entries start with \"BINA:\").",
                        10.5, "Cp.Muted", margin: new Thickness(0, 2, 0, 0)));
                receipt.Child = rs;
                sp.Children.Add(receipt);
            }

            // A repeated read (same tool, same answer) is one line, not two.
            var items = hasCard
                ? Enumerable.Empty<EvidenceItem>()
                : EvidenceDedupe.DistinctBy(ev.Evidence, e => ReceiptTemplates.EvidenceLine(e, lang) + "\u0001" + ReceiptTemplates.NotSearchedLine(e, lang));
            foreach (var item in items.Take(MaxEvidenceRows))
            {
                var line = ReceiptTemplates.EvidenceLine(item, lang);
                if (line.Length > 0)
                    sp.Children.Add(CardKit.Text("⌕  " + line, 10.5, item.FailedReason != null ? "Cp.AmberFg" : "Cp.Muted",
                        margin: new Thickness(0, 1, 0, 1)));
                var missing = ReceiptTemplates.NotSearchedLine(item, lang);
                if (missing.Length > 0)
                    sp.Children.Add(CardKit.Text("   " + missing, 10.5, "Cp.Muted", margin: new Thickness(0, 0, 0, 2)));
            }

            // Every image the model saw, under the reply (look).
            foreach (var img in EvidenceDedupe.DistinctImages(ev.Images))
            {
                var picture = ConfirmCard.Thumbnail(img, expandable: true);
                if (picture != null) sp.Children.Add(picture);
            }

            // Check results: only rules that failed or could not be verified, one line each (a
            // failing rule's elements can be selected); the passes are one count. All rows are in the export.
            var tablesOnce = EvidenceDedupe.DistinctTables(ev.Tables);
            foreach (var t in tablesOnce.Where(t => t.Kind == ResultTable.CheckKind))
            {
                sp.Children.Add(CardKit.Text(t.Title, 11.5, "Cp.Ink", FontWeights.SemiBold, margin: new Thickness(0, 6, 0, 2)));
                string Cell(int r, int c) => c < t.Rows[r].Count ? t.Rows[r][c] ?? "" : "";
                var open = Enumerable.Range(0, t.Rows.Count).Where(r => Cell(r, 4) is var f && f != "0" && f != ""
                                                                       || Cell(r, 5) is var nv && nv != "0" && nv != "").ToList();
                foreach (var i in open.Take(MaxCheckRows))
                {
                    var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = true };
                    var handle = t.HandleOf(i);
                    if (handle != null && actions.Highlight != null)
                    {
                        var h = handle;
                        var chip = CardKit.Chip(ReceiptTemplates.HighlightLabel(lang), () => actions.Highlight(h));
                        DockPanel.SetDock(chip, Dock.Right);
                        row.Children.Add(chip);
                    }
                    var failing = handle != null;
                    row.Children.Add(CardKit.Text(ReceiptTemplates.CheckRuleLine(t, i, lang), 10.5,
                        failing ? "Cp.Ink" : "Cp.Muted", failing ? FontWeights.Medium : (FontWeight?)null));
                    sp.Children.Add(row);
                }
                var rest = (open.Count > MaxCheckRows ? (open.Count - MaxCheckRows) + " more to fix, " : "")
                           + (t.Rows.Count - open.Count) + " rules passed (all rules in the Excel export)";
                sp.Children.Add(CardKit.Text(rest, 10.5, "Cp.Muted", margin: new Thickness(0, 1, 0, 2)));
            }

            if (actions.Export != null)
            {
                var tables = tablesOnce.Take(MaxExports).ToList();
                if (tables.Count > 0)
                {
                    var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                    foreach (var t in tables)
                    {
                        var table = t;
                        var label = (ms ? "Eksport ke Excel" : "Export to Excel") + (tables.Count > 1 ? " · " + Short(table.Title) : "");
                        row.Children.Add(CardKit.Chip(label, () => actions.Export(table)));
                    }
                    sp.Children.Add(row);
                }
            }

            return sp.Children.Count == 0 ? null : sp;
        }

        private static BinaVibe.Copilot.Undo.UndoBlock SafeWhyNot(TurnEvidenceActions a)
        {
            try { return a.WhyNotUndo?.Invoke() ?? BinaVibe.Copilot.Undo.UndoBlock.Other; }
            catch { return BinaVibe.Copilot.Undo.UndoBlock.Other; }
        }

        private static bool SafeCanUndo(TurnEvidenceActions a)
        {
            try { return a.CanUndo?.Invoke() ?? false; }
            catch { return false; }
        }

        private static string Short(string s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 28 ? s : s.Substring(0, 27) + "…";
    }
}
