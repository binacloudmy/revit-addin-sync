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
        /// <summary>The positioned words of one page (1-based) of an attached PDF, or null when the file is not available (Source panel).</summary>
        public Func<string, int, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<BinaVibe.Mcp.Tools.PdfWord>?>>? SourcePageWords;
        /// <summary>Open an attached PDF in the default viewer (attachment name, page for the label).</summary>
        public Action<string, int?>? OpenSource;
        /// <summary>Reset the active view's Temporary Hide/Isolate; null when done, "none" when the view was not isolated, else why not.</summary>
        public Func<System.Threading.Tasks.Task<string?>>? ResetIsolate;
        /// <summary>Open a sheet or view in Revit from its id; null when done, else why not.</summary>
        public Func<long, bool, System.Threading.Tasks.Task<string?>>? OpenView;
        /// <summary>What these element ids are in the model that is open: used to show a row's button only when the id is what
        /// the row says it is. Null when Revit cannot say.</summary>
        public Func<System.Collections.Generic.IReadOnlyCollection<long>, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyDictionary<long, ItemClass>?>>? ClassifyIds;
        /// <summary>Ask the pane to draw this card again (its state changed).</summary>
        public Action? Redraw;
        /// <summary>Run a check's button (set_parameters or prompt); null when done, else why not.</summary>
        public Func<CheckAction, System.Threading.Tasks.Task<string?>>? RunCheckAction;
        /// <summary>Send a next-step chip as the next message.</summary>
        public Action<string>? SendPrompt;
        /// <summary>What the "Steps" toggle opens under the headline (the activity card and the evidence lines); null when there is nothing.</summary>
        public FrameworkElement? StepsPanel;
        /// <summary>"Save as command" on the card's action row; null when this turn does not qualify.</summary>
        public Action? SaveAsCommand;
        /// <summary>The "Rules checked" block of a rule-set result, drawn inside the card (RulesBlock); null when the turn ran no rule set.</summary>
        public FrameworkElement? RulesBlock;
        /// <summary>The ids of the rules the Rules block lists: a check that only restates one is not drawn again.</summary>
        public System.Collections.Generic.IReadOnlyList<string>? RuleIds;
        /// <summary>The tables the one "Export" button offers (a menu when there are several); null or empty when none.</summary>
        public System.Collections.Generic.IReadOnlyList<ResultTable>? ExportTables;
    }

    public static class TurnEvidenceView
    {
        private const int MaxEvidenceRows = 6;
        private const int MaxExports = 3;
        private const int MaxCheckRows = 12;

        public static FrameworkElement? Build(TurnEvidence? ev, ReceiptLanguage lang, TurnEvidenceActions actions, ResultCardModel? card = null,
            Func<ResultCardModel, TurnEvidenceActions, bool, string?, FrameworkElement?>? cardBuilder = null)
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
            var tablesOnce = EvidenceDedupe.DistinctTables(ev.Tables);
            // A rule-set result is one "Rules checked" block (inside the card when there is one), never a title plus a line per rule.
            actions.RuleIds = RulesBlockText.Rows(tablesOnce.Where(t => t.Kind == ResultTable.CheckKind)).Select(r => r.Id).Where(i => i.Length > 0).ToList();
            actions.RulesBlock = RulesBlock.Build(tablesOnce.Where(t => t.Kind == ResultTable.CheckKind), lang, actions);
            actions.ExportTables = actions.Export == null ? null : EvidenceDedupe.ExportTables(ev.Tables).Take(MaxExports).ToList();
            if (hasCard)
            {
                var canUndo = ev.AnyCommitted && actions.Undo != null && SafeCanUndo(actions);
                var undoOff = ev.AnyCommitted && actions.Undo != null && !canUndo ? ReceiptTemplates.UndoStatusLine(SafeWhyNot(actions), lang) : null;
                var details = new StackPanel();
                foreach (var item in EvidenceDedupe.DistinctBy(ev.Evidence, e => ReceiptTemplates.EvidenceLine(e, lang) + "\u0001" + ReceiptTemplates.NotSearchedLine(e, lang)).Take(MaxEvidenceRows))
                {
                    var line = ReceiptTemplates.EvidenceLine(item, lang);
                    if (line.Length > 0) details.Children.Add(CardKit.Text("⌕  " + line, 10.5, item.FailedReason != null ? "Cp.AmberFg" : "Cp.Muted"));
                    var missing = ReceiptTemplates.NotSearchedLine(item, lang);
                    if (missing.Length > 0) details.Children.Add(CardKit.Text("   " + missing, 10.5, "Cp.Muted"));
                }
                // The turn's steps and what it searched sit behind the card's "Steps" toggle (not a stock Expander).
                if (actions.StepsPanel != null || details.Children.Count > 0)
                {
                    var steps = new StackPanel();
                    if (actions.StepsPanel != null) steps.Children.Add(actions.StepsPanel);
                    if (details.Children.Count > 0) { details.Margin = new Thickness(0, 8, 0, 0); steps.Children.Add(details); }
                    actions.StepsPanel = steps;
                }
                var cardView = cardBuilder != null ? cardBuilder(card!, actions, canUndo, undoOff) : ResultCardView.Build(card, lang, actions, canUndo, undoOff);
                if (cardView != null) sp.Children.Add(cardView);
            }

            if (ev.HasWrites && !hasCard)
            {
                var receipt = CardKit.Card(ev.Caution ? "Cp.AmberLn" : "Cp.SuccessLn", "Cp.OkBg", new Thickness(0, 0, 0, 6), 10);
                receipt.Padding = new Thickness(12, 8, 12, 8);
                var rs = new StackPanel();
                rs.Children.Add(CardKit.Text(ReceiptTemplates.Headline(ev.Receipt, lang), 12.5, "Cp.Ink", FontWeights.SemiBold));
                var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                if (ev.AnyCommitted && actions.Undo != null && SafeCanUndo(actions))
                    buttons.Children.Add(CardKit.Secondary(ms ? "Buat asal" : "Undo", actions.Undo));
                if (buttons.Children.Count > 0) rs.Children.Add(buttons);
                // L19: Undo is off (an edit in Revit after this turn, Revit's own Undo,
                // already undone): say why in one line instead of a button that vanished.
                var undoOff = ev.AnyCommitted && actions.Undo != null && !SafeCanUndo(actions)
                    ? ReceiptTemplates.UndoStatusLine(SafeWhyNot(actions), lang)
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

            // What the turn read and searched (no card): behind the same "Steps" toggle, not loose lines under the reply.
            if (!hasCard)
            {
                var lines = new StackPanel();
                foreach (var item in EvidenceDedupe.DistinctBy(ev.Evidence, e => ReceiptTemplates.EvidenceLine(e, lang) + "\u0001" + ReceiptTemplates.NotSearchedLine(e, lang)).Take(MaxEvidenceRows))
                {
                    var line = ReceiptTemplates.EvidenceLine(item, lang);
                    if (line.Length > 0)
                        lines.Children.Add(CardKit.Text("⌕  " + line, 11, item.FailedReason != null ? "Cp.AmberFg" : "Cp.Muted", margin: new Thickness(0, 1, 0, 1)));
                    var missing = ReceiptTemplates.NotSearchedLine(item, lang);
                    if (missing.Length > 0)
                        lines.Children.Add(CardKit.Text("   " + missing, 11, "Cp.Muted", margin: new Thickness(0, 0, 0, 2)));
                }
                if (lines.Children.Count > 0)
                {
                    lines.Visibility = Visibility.Collapsed;
                    lines.Margin = new Thickness(0, 4, 0, 4);
                    var toggle = ResultCardView.StepsToggle(lines, ms);
                    toggle.Margin = new Thickness(0, 4, 0, 0);
                    sp.Children.Add(toggle);
                    sp.Children.Add(lines);
                }
            }

            // Every image the model saw, under the reply (look).
            foreach (var img in EvidenceDedupe.DistinctImages(ev.Images))
            {
                var picture = ConfirmCard.Thumbnail(img, expandable: true);
                if (picture != null) sp.Children.Add(picture);
            }

            // No card: the rules block stands as its own card, and the export is one ghost button under it.
            if (!hasCard)
            {
                if (actions.RulesBlock != null)
                {
                    var frame = CardKit.Card("Cp.Line", "Cp.Bg", new Thickness(0, 6, 0, 6), 12);
                    frame.Child = actions.RulesBlock;
                    sp.Children.Add(frame);
                }
                var exportButton = ResultCardView.ExportButton(actions, ms);
                if (exportButton != null) sp.Children.Add(new WrapPanel { Margin = new Thickness(0, 4, 0, 0), Children = { exportButton } });
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

    }
}
