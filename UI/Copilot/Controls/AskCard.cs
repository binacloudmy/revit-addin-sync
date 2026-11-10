#nullable enable
// The assistant's question (ask_user) as a card (lean-harness spec §7.11;
// W5 Task 3). Labelled as coming from the assistant and styled unlike the
// confirm card (blue, not amber/red). AskCardModel has already dropped
// approval-like options and credential fields. A project note shows the fact
// and the pane's own "Save note" / "Don't save"; nothing is saved otherwise.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    public static class AskCard
    {
        /// <param name="submit">The answer plus a short summary for the collapsed record.</param>
        public static FrameworkElement Build(AskCardModel m, bool live, Action<AnswerDto, string> submit)
        {
            var ms = m.Language == ReceiptLanguage.Ms;
            var outer = CardKit.Card("Cp.Line", "Cp.Bg");
            var sp = new StackPanel();
            sp.Children.Add(CardKit.Header("Cp.BlueSoft",
                CardKit.Text(m.FromLabel, 11.5, "Cp.BlueText", FontWeights.SemiBold)));

            var body = new StackPanel { Margin = new Thickness(13, 10, 13, 12) };
            var q = CopilotMessageBubble.MarkdownText(m.Question, 460);
            q.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(q);

            if (!live || m.AnsweredSummary != null)
            {
                body.Children.Add(CardKit.Text("✓ " + (m.AnsweredSummary ?? (ms ? "dijawab" : "answered")), 12, "Cp.BlueText", FontWeights.Medium));
                sp.Children.Add(body);
                outer.Child = sp;
                return outer;
            }

            if (m.AsksForCredential)
            {
                // Never an answer box for a password or token (review M2).
                body.Children.Add(CardKit.Text(m.CredentialNote, 11.5, "Cp.AmberFg", margin: new Thickness(0, 0, 0, 8)));
                body.Children.Add(CardKit.Secondary(m.DeclineLabel, () => submit(m.AnswerDeclined(), m.DeclineLabel)));
            }
            else if (m.IsProjectNote)
            {
                var note = new Border { CornerRadius = new System.Windows.CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1) };
                note.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
                note.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                var ns = new StackPanel();
                ns.Children.Add(CardKit.Text(ms ? "Nota projek yang akan disimpan" : "Project note to save", 10, "Cp.Faint", FontWeights.SemiBold));
                ns.Children.Add(CardKit.Text(m.NoteText ?? "", 12, "Cp.Ink"));
                note.Child = ns;
                body.Children.Add(note);
                var row = new WrapPanel();
                row.Children.Add(CardKit.Primary(m.SaveLabel, () => submit(m.AnswerProjectNote(true), m.SaveLabel)));
                row.Children.Add(CardKit.Secondary(m.DontSaveLabel, () => submit(m.AnswerProjectNote(false), m.DontSaveLabel)));
                body.Children.Add(row);
            }
            else if (m.Options.Count > 0)
            {
                var picked = new HashSet<string>();
                Button? send = null;
                foreach (var opt in m.Options)
                {
                    var o = opt;
                    if (m.Multi)
                    {
                        var cb = new CheckBox { Content = o.Label, FontSize = 12.5, Margin = new Thickness(0, 3, 0, 3), Cursor = System.Windows.Input.Cursors.Hand };
                        cb.SetResourceReference(Control.ForegroundProperty, "Cp.Ink");
                        cb.Checked += (_, __) => { picked.Add(o.Id); if (send != null) send.IsEnabled = picked.Count > 0; };
                        cb.Unchecked += (_, __) => { picked.Remove(o.Id); if (send != null) send.IsEnabled = picked.Count > 0; };
                        body.Children.Add(cb);
                    }
                    else
                    {
                        var b = CardKit.Secondary(o.Label, () => submit(m.AnswerForOptions(new[] { o.Id }), o.Label));
                        b.HorizontalAlignment = HorizontalAlignment.Stretch;
                        b.HorizontalContentAlignment = HorizontalAlignment.Left;
                        b.Margin = new Thickness(0, 0, 0, 6);
                        body.Children.Add(b);
                    }
                }
                if (m.Multi)
                {
                    send = CardKit.Primary(ms ? "Hantar" : "Submit", () =>
                        submit(m.AnswerForOptions(picked), string.Join(", ", m.Options.Where(o => picked.Contains(o.Id)).Select(o => o.Label))));
                    send.IsEnabled = false;
                    send.Margin = new Thickness(0, 6, 0, 0);
                    send.HorizontalAlignment = HorizontalAlignment.Left;
                    body.Children.Add(send);
                }
                body.Children.Add(CardKit.Text(ms ? "Atau taip jawapan anda di ruang mesej." : "Or type your own answer in the message box.",
                    10.5, "Cp.Muted", margin: new Thickness(0, 6, 0, 0)));
            }
            else if (m.Fields.Count > 0)
            {
                var inputs = new Dictionary<string, Func<string>>();
                foreach (var f in m.Fields)
                {
                    body.Children.Add(CardKit.Text(f.Label + (string.IsNullOrEmpty(f.Unit) ? "" : " (" + f.Unit + ")"), 11.5, "Cp.Ink2",
                        FontWeights.Medium, margin: new Thickness(0, 4, 0, 2)));
                    if (f.Type == AskFieldTypes.Choice)
                    {
                        var combo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
                        foreach (var c in f.Choices) combo.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Id });
                        inputs[f.Id] = () => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                        body.Children.Add(combo);
                    }
                    else
                    {
                        var box = new TextBox { FontSize = 12, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 4) };
                        inputs[f.Id] = () => box.Text;
                        body.Children.Add(box);
                    }
                }
                var go = CardKit.Primary(ms ? "Hantar" : "Submit", () =>
                {
                    var values = inputs.ToDictionary(kv => kv.Key, kv => kv.Value());
                    var summary = string.Join(" · ", m.Fields.Where(f => !string.IsNullOrWhiteSpace(values[f.Id]))
                        .Select(f => f.Label + ": " + (f.Type == AskFieldTypes.Choice
                            ? f.Choices.FirstOrDefault(c => c.Id == values[f.Id])?.Label ?? values[f.Id]
                            : values[f.Id] + (string.IsNullOrEmpty(f.Unit) ? "" : " " + f.Unit))));
                    submit(m.AnswerForFields(values), summary);
                });
                go.Margin = new Thickness(0, 8, 0, 0);
                go.HorizontalAlignment = HorizontalAlignment.Left;
                body.Children.Add(go);
            }
            else
            {
                var box = new TextBox { FontSize = 12, Padding = new Thickness(6, 4, 6, 4), AcceptsReturn = false };
                body.Children.Add(box);
                var go = CardKit.Primary(ms ? "Hantar" : "Submit", () =>
                {
                    if (!string.IsNullOrWhiteSpace(box.Text)) submit(m.AnswerForText(box.Text), box.Text.Trim());
                });
                go.Margin = new Thickness(0, 8, 0, 0);
                go.HorizontalAlignment = HorizontalAlignment.Left;
                body.Children.Add(go);
            }

            if (m.SomethingWasDropped)
                body.Children.Add(CardKit.Text(ms
                        ? "Sebahagian pilihan disembunyikan: hanya kad pengesahan Revit boleh meluluskan perubahan, dan BINA tidak pernah meminta kata laluan atau token di sini."
                        : "Some choices were hidden: only Revit's confirm card can approve changes, and BINA never asks for passwords or tokens here.",
                    10, "Cp.Faint", margin: new Thickness(0, 8, 0, 0)));

            sp.Children.Add(body);
            outer.Child = sp;
            return outer;
        }
    }
}
