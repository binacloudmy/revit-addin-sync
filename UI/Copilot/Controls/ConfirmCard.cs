#nullable enable
// The add-in's confirm card (lean-harness spec §7.5; W5 Task 3). Renders a
// ConfirmCardModel, which holds only what the add-in measured in the preview:
// deletions first and in red, additions, before -> after, side effects,
// warnings, the code in full. The assistant's words sit on their own labelled
// line, never in the headline. Ya is bound to the batch the add-in holds
// (W3 checks the plan hash); this control only reports the decision.

using System;
using System.Windows;
using System.Windows.Controls;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    public static class ConfirmCard
    {
        /// <param name="live">Buttons render only while the card is unresolved and newest.</param>
        public static FrameworkElement Build(ConfirmCardModel m, bool live, Action<ConfirmDecision> decide)
        {
            var ms = m.Language == ReceiptLanguage.Ms;
            var outer = CardKit.Card(m.HardWarning ? "Cp.IssueLn" : "Cp.AmberLn", "Cp.Bg");
            // Ctrl+Enter only approves while focus is inside THIS card (review M3):
            // the card is tagged with its model and takes focus when clicked.
            outer.Tag = m;
            outer.Focusable = true;
            outer.FocusVisualStyle = null;
            outer.MouseLeftButtonDown += (_, __) => outer.Focus();
            var sp = new StackPanel();

            // Header: who is asking (Revit, not the assistant) and what kind of change.
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = CardKit.Text((ms ? "Sahkan dalam Revit · " : "Confirm in Revit · ") + m.ToolLabel, 12.5,
                m.HardWarning ? "Cp.IssueFg" : "Cp.AmberFg", FontWeights.SemiBold);
            head.Children.Add(title);
            if (!string.IsNullOrEmpty(m.PlanHash))
            {
                var hash = CardKit.Text(ShortHash(m.PlanHash), 9.5, "Cp.Faint", wrap: false, mono: true);
                hash.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(hash, 1);
                head.Children.Add(hash);
            }
            sp.Children.Add(CardKit.Header(m.HardWarning ? "Cp.IssueBg" : "Cp.AmberBg", head));

            var body = new StackPanel { Margin = new Thickness(13, 10, 13, 12) };
            body.Children.Add(CardKit.Text(m.Headline, 13.5, m.Deletions > 0 ? "Cp.Red" : "Cp.Ink", FontWeights.SemiBold,
                margin: new Thickness(0, 0, 0, 6)));
            if (m.EstimateOnly)
                body.Children.Add(CardKit.Text(ms
                        ? "Revit tidak dapat membuat pratonton penuh untuk perubahan ini; kiraan di bawah ialah anggaran."
                        : "Revit could not fully preview this change; the counts below are estimates.",
                    11, "Cp.AmberFg", margin: new Thickness(0, 0, 0, 6)));

            foreach (var row in m.Rows)
                body.Children.Add(CardKit.Text(Glyph(row.Kind) + "  " + row.Text, 12,
                    row.Danger ? "Cp.Red" : row.Kind == ConfirmRowKind.Warning ? "Cp.AmberFg" : "Cp.Ink2",
                    row.Kind == ConfirmRowKind.Delete ? FontWeights.SemiBold : FontWeights.Normal,
                    margin: new Thickness(0, 1, 0, 1)));

            // What the preview drew: a thumbnail or markers of created elements.
            foreach (var img in m.Images)
            {
                var picture = Thumbnail(img);
                if (picture != null) body.Children.Add(picture);
            }

            if (!string.IsNullOrEmpty(m.Code))
            {
                body.Children.Add(CardKit.Text(ms ? "Kod yang akan dijalankan (penuh)" : "Code that will run (in full)", 10.5, "Cp.Muted",
                    FontWeights.SemiBold, margin: new Thickness(0, 8, 0, 0)));
                body.Children.Add(CardKit.Code(m.Code!));
            }

            // Raw failure text (often from the model's own code) only under its label.
            if (!string.IsNullOrWhiteSpace(m.CodeOutput))
            {
                body.Children.Add(CardKit.Text(m.CodeOutputLabel, 10, "Cp.Faint", FontWeights.SemiBold, margin: new Thickness(0, 8, 0, 1)));
                body.Children.Add(CardKit.Code(m.CodeOutput!, maxHeight: 140));
            }

            if (!string.IsNullOrWhiteSpace(m.AssistantText))
            {
                body.Children.Add(CardKit.Text(m.AssistantLabel, 10, "Cp.Faint", FontWeights.SemiBold, margin: new Thickness(0, 8, 0, 1)));
                body.Children.Add(CardKit.Text(m.AssistantText!, 11.5, "Cp.Muted", italic: true));
            }

            if (live && !m.Resolved)
            {
                var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                actions.Children.Add(CardKit.Primary(ms ? "Ya, laksanakan" : "Yes, apply",
                    () => decide(new ConfirmDecision { Approved = true })));
                actions.Children.Add(CardKit.Secondary(ms ? "Tidak" : "No",
                    () => decide(new ConfirmDecision { Approved = false, Reason = "declined" })));
                if (m.OfferApproveThisBuild)
                    actions.Children.Add(CardKit.Secondary(ms ? "Luluskan binaan ini" : "Approve this build",
                        () => decide(new ConfirmDecision { Approved = true, ApproveThisBuild = true })));
                body.Children.Add(actions);
                if (m.OfferApproveThisBuild)
                    body.Children.Add(CardKit.Text(ms
                            ? "\"Luluskan binaan ini\" meluluskan baki kumpulan yang hanya MENAMBAH elemen baharu dalam giliran ini (maks. 500). Sebarang padam atau suntingan tetap ditanya."
                            : "\"Approve this build\" approves the rest of this turn's batches that only ADD new elements (up to 500). Any delete or edit still asks.",
                        10.5, "Cp.Faint", margin: new Thickness(0, 6, 0, 0)));
                body.Children.Add(CardKit.Text(m.HardWarning
                        ? (ms ? "Esc = Tidak · Boleh dibuat asal · Klik Ya untuk meluluskan padam atau kesan sampingan" : "Esc = No · Undoable · Click Yes to approve deletions or side effects")
                        : (ms ? "Ctrl+Enter (dalam kad ini) = Ya · Esc = Tidak · Boleh dibuat asal" : "Ctrl+Enter (in this card) = Yes · Esc = No · Undoable"),
                    10, "Cp.Faint", margin: new Thickness(0, 6, 0, 0)));
            }
            else if (m.Outcome is ConfirmDecision d)
            {
                var text = !d.Approved
                    ? (d.Reason == "cancelled" ? (ms ? "Dibatalkan" : "Cancelled") : (ms ? "Ditolak" : "Declined"))
                    : d.ApproveThisBuild ? (ms ? "Diluluskan untuk binaan ini" : "Approved for this build") : (ms ? "Diluluskan" : "Approved");
                body.Children.Add(CardKit.Text(text, 11.5, d.Approved ? "Cp.Reasoning.Success" : "Cp.Faint", FontWeights.Medium,
                    margin: new Thickness(0, 10, 0, 0)));
            }

            sp.Children.Add(body);
            outer.Child = sp;
            return outer;
        }

        /// <summary>The add-in's own label for a write tool (English; the card
        /// itself uses the drafter's language).</summary>
        public static string ToolLabel(string tool) => ConfirmCardModel.ToolLabelOf(tool, ms: false);

        private static FrameworkElement? Thumbnail(BinaVibe.Copilot.Contracts.ImageDto img)
        {
            try
            {
                var bytes = Convert.FromBase64String(img.Base64);
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.StreamSource = new System.IO.MemoryStream(bytes);
                bi.EndInit();
                bi.Freeze();
                var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 2) };
                var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
                frame.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                frame.Child = new Image
                {
                    Source = bi,
                    MaxWidth = Math.Min(360, img.Width > 0 ? img.Width : 360),
                    MaxHeight = 240,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                };
                stack.Children.Add(frame);
                if (!string.IsNullOrWhiteSpace(img.Caption))
                    stack.Children.Add(CardKit.Text(img.Caption, 10.5, "Cp.Muted", margin: new Thickness(0, 2, 0, 0)));
                return stack;
            }
            catch
            {
                return null;   // a broken thumbnail never breaks the card
            }
        }

        private static string Glyph(ConfirmRowKind kind) => kind switch
        {
            ConfirmRowKind.Delete => "−",
            ConfirmRowKind.Add => "+",
            ConfirmRowKind.Modify => "~",
            ConfirmRowKind.SideEffect => "↻",
            ConfirmRowKind.Warning => "⚠",
            ConfirmRowKind.Failure => "✕",
            _ => "·",
        };

        private static string ShortHash(string planHash)
        {
            var hex = planHash.StartsWith("sha256:", StringComparison.Ordinal) ? planHash.Substring(7) : planHash;
            return "plan " + (hex.Length > 10 ? hex.Substring(0, 10) : hex);
        }
    }
}
