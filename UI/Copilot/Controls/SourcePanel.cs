#nullable enable
// Board 4: the source of one value, under its row in the result card. For a document source it
// draws the brochure page's own words around the quote (no rasteriser in the add-in: the page's
// text layer, placed where the words sit, the quote's words marked), a MATCH / CHECK badge from
// comparing the written value with the quote, Written against Brochure, "Open full page" (the
// whole page the same way), "Open PDF" and "Edit value". Other sources get their one-line reason.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Mcp.Tools;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    public static class SourcePanel
    {
        private const double CropWidth = 300;

        public static FrameworkElement Build(ChangeRow row, ReceiptLanguage lang, TurnEvidenceActions actions, Action? startEdit)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var info = row.SourceInfo;
            var quote = info?.Quote;
            // A changed-back row holds its old value now; what the source supports is the value that was written.
            var written = row.ChangedBack ? row.Intended : row.After;
            var verdict = row.ChangedBack && row.Intended == null ? SourceVerdict.NoQuote : SourceCrop.Verdict(written, quote);
            var outer = new Border { Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6) };
            outer.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            var sp = new StackPanel();
            outer.Child = sp;

            // Title line: where it came from, and the badge.
            var head = new DockPanel { LastChildFill = true };
            var badgeText = SourceText.Badge(verdict, lang);
            if (badgeText.Length > 0)
            {
                var ok = verdict == SourceVerdict.Match;
                var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                badge.SetResourceReference(Border.BackgroundProperty, ok ? "Cp.OkBg" : "Cp.AmberBg");
                badge.Child = CardKit.Text(badgeText, 10.5, ok ? "Cp.Ink" : "Cp.AmberFg", FontWeights.SemiBold);
                DockPanel.SetDock(badge, Dock.Right); head.Children.Add(badge);
            }
            var title = string.IsNullOrEmpty(row.Source) ? (ms ? "Prompt anda" : "Your prompt") : row.Source;
            head.Children.Add(CardKit.Text((ms ? "Sumber · " : "Source · ") + title, 11, "Cp.Ink2", FontWeights.Medium));
            sp.Children.Add(head);

            var isDoc = info != null && info.Kind == ResultSourceKinds.Document && !string.IsNullOrEmpty(info.Attachment) && info.Page is int;
            if (isDoc)
            {
                var host = new ContentControl { Margin = new Thickness(0, 6, 0, 4) };
                host.Content = CardKit.Text(ms ? "Membaca halaman…" : "Reading the page…", 10.5, "Cp.Faint");
                sp.Children.Add(host);
                var full = false;
                void Load() => LoadCrop(host, info!, written, lang, actions, full);
                // Read the page when the panel is first opened, not when the card is built: a card has one panel per row.
                var loaded = false;
                outer.IsVisibleChanged += (_, e) => { if (!loaded && outer.IsVisible) { loaded = true; Load(); } };
                if (verdict != SourceVerdict.NoQuote) sp.Children.Add(CardKit.Text(SourceText.VerdictLine(verdict, lang), 10.5, verdict == SourceVerdict.Match ? "Cp.Muted" : "Cp.AmberFg", margin: new Thickness(0, 0, 0, 4)));
                sp.Children.Add(Compare(written, row.ChangedBack ? row.IntendedLabel : null, quote, ms));
                var acts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                Button? fullBtn = null;
                fullBtn = CardKit.Chip(ms ? "Buka halaman penuh" : "Open full page", () =>
                {
                    full = !full;
                    if (fullBtn != null) fullBtn.Content = full ? (ms ? "Tunjuk petikan sahaja" : "Show the crop only") : (ms ? "Buka halaman penuh" : "Open full page");
                    Load();
                });
                acts.Children.Add(fullBtn);
                if (actions.OpenSource != null)
                {
                    var att = info!.Attachment!; var page = info.Page;
                    acts.Children.Add(CardKit.Chip(ms ? "Buka PDF" : "Open PDF", () => actions.OpenSource(att, page)));
                }
                if (startEdit != null) acts.Children.Add(CardKit.Chip(ms ? "Ubah nilai" : "Edit value", startEdit));
                sp.Children.Add(acts);
                return outer;
            }

            // Not a document: say what it is, and what was written.
            var kind = SourceText.Kind(info?.Kind, lang);
            if (kind.Length > 0) sp.Children.Add(CardKit.Text(kind, 11, "Cp.Muted", margin: new Thickness(0, 3, 0, 0)));
            string Or(string? s) => string.IsNullOrEmpty(s) ? (ms ? "kosong" : "empty") : s!;
            sp.Children.Add(CardKit.Text((ms ? "Sebelum · " : "Before · ") + Or(row.Before) + "   " + (ms ? "Selepas · " : "After · ") + Or(row.After), 11, "Cp.Muted", mono: true, margin: new Thickness(0, 3, 0, 0)));
            if (!string.IsNullOrEmpty(quote)) sp.Children.Add(Compare(written, row.ChangedBack ? row.IntendedLabel : null, quote, ms));
            if (startEdit != null)
            {
                var acts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                acts.Children.Add(CardKit.Chip(ms ? "Ubah nilai" : "Edit value", startEdit));
                sp.Children.Add(acts);
            }
            return outer;
        }

        /// <summary>Written | Brochure (the quote), side by side.</summary>
        private static FrameworkElement Compare(string? written, string? writtenLabel, string? quote, bool ms)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var w = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            w.Children.Add(CardKit.Text(ms ? "Ditulis" : "Written", 10, "Cp.Faint", FontWeights.Medium));
            w.Children.Add(CardKit.Text(string.IsNullOrEmpty(written) ? (writtenLabel ?? (ms ? "kosong" : "empty")) : written!, 11.5, "Cp.Ink", FontWeights.SemiBold, mono: true));
            var q = new StackPanel();
            q.Children.Add(CardKit.Text(ms ? "Sumber" : "Brochure", 10, "Cp.Faint", FontWeights.Medium));
            q.Children.Add(CardKit.Text(string.IsNullOrEmpty(quote) ? "—" : quote!, 11.5, "Cp.Ink2", mono: true));
            Grid.SetColumn(w, 0); Grid.SetColumn(q, 1);
            g.Children.Add(w); g.Children.Add(q);
            return g;
        }

        private static async void LoadCrop(ContentControl host, ResultSourceDto info, string? written, ReceiptLanguage lang, TurnEvidenceActions actions, bool full)
        {
            try
            {
                var page = info.Page ?? 1;
                IReadOnlyList<PdfWord>? words = null;
                try { if (actions.SourcePageWords != null) words = await actions.SourcePageWords(info.Attachment!, page); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] source page: " + ex.Message); }
                if (words == null) { host.Content = CardKit.Text(SourceText.NotAvailable(lang), 10.5, "Cp.Faint"); return; }
                var layout = full ? SourceCrop.FullPage(words, info.Quote, written) : SourceCrop.Crop(words, info.Quote, written);
                if (layout.Words.Count == 0) { host.Content = CardKit.Text(SourceText.NotFoundOnPage(page, lang), 10.5, "Cp.Faint"); return; }
                host.Content = Draw(layout, full);
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the dispatcher.
                System.Diagnostics.Debug.WriteLine("[BINA] source crop: " + ex.Message);
                try { host.Content = CardKit.Text(SourceText.NotAvailable(lang), 10.5, "Cp.Faint"); } catch { /* the card is gone */ }
            }
        }

        /// <summary>The words as a small page: a canvas scaled to the pane's width, the marked words tinted and bold.</summary>
        private static FrameworkElement Draw(CropLayout layout, bool full)
        {
            var height = Math.Min(CropWidth / Math.Max(layout.Aspect, 0.2), full ? 420 : 180);
            var width = height * layout.Aspect > CropWidth ? CropWidth : height * layout.Aspect;
            if (width < CropWidth / 4) width = CropWidth / 4;
            var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };
            foreach (var w in layout.Words)
            {
                var fs = Math.Max(5, Math.Min(15, w.H * height * 0.78));
                var tb = new TextBlock { Text = w.Text, FontSize = fs, FontWeight = w.Highlight ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.NoWrap };
                tb.SetResourceReference(TextBlock.ForegroundProperty, w.Highlight ? "Cp.Ink" : "Cp.Muted");
                var cell = new Border { Width = Math.Max(w.W * width, 2), Height = Math.Max(w.H * height, 4), Child = tb, ClipToBounds = false };
                if (w.Highlight) cell.Background = CopilotColors.Tint("Cp.Brand", 0x40);
                Canvas.SetLeft(cell, w.X * width); Canvas.SetTop(cell, w.Y * height);
                canvas.Children.Add(cell);
            }
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Child = canvas };
            frame.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            frame.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = full ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = full ? 440 : 200, Content = frame };
            return scroll;
        }
    }
}
