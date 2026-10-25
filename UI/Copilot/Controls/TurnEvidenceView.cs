#nullable enable
// What the turn measured, under the reply (lean-harness spec §7.11; W5 Task 3):
//   - the caution line whenever any write is not done (never decided from text);
//   - the receipt headline as fractions ("30/34 done · 4 failed (not owned)"),
//     with Undo (W3's UndoService, one entry per batch) and Save as command;
//   - the evidence strip of every read: count, what was searched, what was NOT
//     searched (in the warning colour), highlighted / visible counts;
//   - Export to Excel for tables a read returned.
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
        public Action? Undo;
        public Action<ResultTable>? Export;
        public Action<CommittedWrite>? SaveAsCommand;
    }

    public static class TurnEvidenceView
    {
        private const int MaxEvidenceRows = 6;
        private const int MaxExports = 3;

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
                else if (ev.AnyCommitted)
                    rs.Children.Add(CardKit.Text(ms
                            ? "Untuk membuat asal, guna senarai Undo Revit (entri bermula dengan \"BINA:\")."
                            : "To undo, use Revit's Undo list (entries start with \"BINA:\").",
                        10.5, "Cp.Muted", margin: new Thickness(0, 4, 0, 0)));
                receipt.Child = rs;
                sp.Children.Add(receipt);
            }

            foreach (var item in ev.Evidence.Take(MaxEvidenceRows))
            {
                var line = ReceiptTemplates.EvidenceLine(item, lang);
                if (line.Length > 0)
                    sp.Children.Add(CardKit.Text("⌕  " + line, 10.5, "Cp.Muted", margin: new Thickness(0, 1, 0, 1)));
                var missing = ReceiptTemplates.NotSearchedLine(item, lang);
                if (missing.Length > 0)
                    sp.Children.Add(CardKit.Text("   " + missing, 10.5, "Cp.AmberFg", FontWeights.Medium, margin: new Thickness(0, 0, 0, 2)));
            }

            if (actions.Export != null)
            {
                var tables = ev.Tables.Take(MaxExports).ToList();
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

        private static bool SafeCanUndo(TurnEvidenceActions a)
        {
            try { return a.CanUndo?.Invoke() ?? false; }
            catch { return false; }
        }

        private static string Short(string s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 28 ? s : s.Substring(0, 27) + "…";
    }
}
