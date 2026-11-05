#nullable enable
// What the turn measured, under the reply (lean-harness spec §7.11; W5 Task 3):
//   - the caution line whenever any write is not done (never decided from text);
//   - the receipt headline as fractions ("30/34 done · 4 failed (not owned)"),
//     with Undo (W3's UndoService, one entry per batch) and Save as command;
//   - the evidence strip of every read in ONE line (count, what was searched,
//     highlighted / visible), one "not searched" line for the turn, the names
//     behind Details (AI-269);
//   - a thumbnail of every image a read returned (look; click to enlarge), so the
//     drafter sees what the model saw (§7.7);
//   - a check's rules, one line each, with Highlight on a failing rule (§21);
//   - Export to Excel for tables a read returned, check results included; one per
//     table, none for a table the reply already exports.
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
        /// <summary>Why Undo is off (W3's reason), or null when it is available (L19).</summary>
        public Func<string?>? WhyNotUndo;
        public Action? Undo;
        public Action<ResultTable>? Export;
        public Action<CommittedWrite>? SaveAsCommand;
        /// <summary>Select a handle's elements in Revit (find_elements highlight).</summary>
        public Action<string>? Highlight;
        /// <summary>The markdown tables in the reply (each has its own Download CSV).</summary>
        public System.Collections.Generic.IReadOnlyCollection<EvidenceSummary.ReplyTable>? ReplyTables;
    }

    public static class TurnEvidenceView
    {
        private const int MaxExports = 3;
        private const int MaxCheckRows = 12;

        public static FrameworkElement? Build(TurnEvidence? ev, ReceiptLanguage lang, TurnEvidenceActions actions)
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

            if (ev.HasWrites)
            {
                var receipt = CardKit.Card(ev.Caution ? "Cp.AmberLn" : "Cp.SuccessLn", "Cp.OkBg", new Thickness(0, 0, 0, 6), 10);
                receipt.Padding = new Thickness(12, 8, 12, 8);
                var rs = new StackPanel();
                rs.Children.Add(CardKit.Text(ReceiptTemplates.Headline(ev.Receipt, lang), 12.5, "Cp.Ink", FontWeights.SemiBold));
                var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                if (ev.AnyCommitted && actions.Undo != null && SafeCanUndo(actions))
                    buttons.Children.Add(CardKit.Chip(ms ? "Buat asal" : "Undo", actions.Undo));
                var save = ev.Committed.FirstOrDefault();
                if (save != null && actions.SaveAsCommand != null)
                    buttons.Children.Add(CardKit.Chip(ms ? "Simpan sebagai arahan" : "Save as command", () => actions.SaveAsCommand(save)));
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

            // What the turn read, in ONE line; the names behind Details (AI-269). Several reads that
            // repeat the same unloaded links say it once.
            var reads = ev.Evidence.ToList();
            var summary = EvidenceSummary.Line(reads, lang);
            var notSearched = EvidenceSummary.NotSearchedLine(reads, lang);
            var details = EvidenceSummary.Details(reads, lang);
            // A check rule's cite and reason in full, only where the short line cut something.
            foreach (var ct in ev.Tables.Where(t => t.Kind == ResultTable.CheckKind))
                for (var i = 0; i < ct.Rows.Count && i < MaxCheckRows; i++)
                {
                    var full = ReceiptTemplates.CheckRuleFullLine(ct, i, lang);
                    if (full != ReceiptTemplates.CheckRuleLine(ct, i, lang) && !details.Contains(full)) details.Add(full);
                }
            var anyFailed = reads.Any(r => r.FailedReason != null);
            if (summary.Length > 0)
                sp.Children.Add(CardKit.Text("⌕  " + summary, 10.5, anyFailed ? "Cp.AmberFg" : "Cp.Muted",
                    margin: new Thickness(0, 1, 0, 1)));
            if (notSearched.Length > 0)
                sp.Children.Add(CardKit.Text("   " + notSearched, 10.5, "Cp.AmberFg", FontWeights.Medium, margin: new Thickness(0, 0, 0, 2)));
            if (details.Count > 0)
            {
                var detailPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
                foreach (var d in details)
                    detailPanel.Children.Add(CardKit.Text("   " + d, 10.5, "Cp.Muted", margin: new Thickness(0, 1, 0, 1)));
                var showLabel = ms ? "Butiran" : "Details";
                var hideLabel = ms ? "Sembunyikan butiran" : "Hide details";
                Button? toggle = null;
                toggle = CardKit.Chip(showLabel, () =>
                {
                    var open = detailPanel.Visibility != Visibility.Visible;
                    detailPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                    if (toggle != null) toggle.Content = open ? hideLabel : showLabel;
                });
                sp.Children.Add(toggle);
                sp.Children.Add(detailPanel);
            }

            // Every image the model saw, under the reply (look).
            foreach (var img in ev.Images)
            {
                var picture = ConfirmCard.Thumbnail(img, expandable: true);
                if (picture != null) sp.Children.Add(picture);
            }

            // Check results: one line per rule; a failing rule's elements can be selected.
            foreach (var t in ev.Tables.Where(t => t.Kind == ResultTable.CheckKind))
            {
                sp.Children.Add(CardKit.Text(t.Title, 11.5, "Cp.Ink", FontWeights.SemiBold, margin: new Thickness(0, 6, 0, 2)));
                for (var i = 0; i < t.Rows.Count && i < MaxCheckRows; i++)
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
                if (t.Rows.Count > MaxCheckRows)
                    sp.Children.Add(CardKit.Text(ms
                            ? "… dan " + (t.Rows.Count - MaxCheckRows) + " peraturan lagi (dalam eksport Excel)"
                            : "… and " + (t.Rows.Count - MaxCheckRows) + " more rules (in the Excel export)",
                        10.5, "Cp.Muted", margin: new Thickness(0, 1, 0, 2)));
            }

            if (actions.Export != null)
            {
                // One Export per table: the same table twice, or one the reply's own table already
                // exports ("Download CSV": same rows and first header or cell), is not offered again.
                var tables = EvidenceSummary.ExportTables(ev.Tables, actions.ReplyTables, MaxExports);
                if (tables.Count > 0)
                {
                    var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                    foreach (var t in tables)
                    {
                        var table = t;
                        var label = (ms ? "Eksport ke Excel" : "Export to Excel") + (tables.Count > 1 ? " · " + EvidenceSummary.ExportName(table) : "");
                        row.Children.Add(CardKit.Chip(label, () => actions.Export(table)));
                    }
                    sp.Children.Add(row);
                }
            }

            return sp.Children.Count == 0 ? null : sp;
        }

        private static string? SafeWhyNot(TurnEvidenceActions a)
        {
            try { return a.WhyNotUndo?.Invoke() ?? "unavailable"; }
            catch { return "unavailable"; }
        }

        private static bool SafeCanUndo(TurnEvidenceActions a)
        {
            try { return a.CanUndo?.Invoke() ?? false; }
            catch { return false; }
        }

    }
}
