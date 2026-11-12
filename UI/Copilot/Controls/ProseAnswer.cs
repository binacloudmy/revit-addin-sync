#nullable enable
// A long prose answer with no result card: its first sentence as the headline (the 22px check disc and 17px/600, like a result card),
// and the rest under the headline, or behind a "More detail" toggle when it is longer than 60 words. The wording is the model's, untouched.
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class ProseAnswer
    {
        /// <summary>The server's reply as the headline and the whole pane text behind "More detail" (always collapsed), or, with no
        /// server reply, the first sentence as the headline and the rest under it.</summary>
        public static FrameworkElement Build(string text, ReceiptLanguage lang, double maxWidth, bool tableDownloads, string? serverHeadline = null)
        {
            var col = new StackPanel();
            if (!string.IsNullOrWhiteSpace(serverHeadline))
            {
                col.Children.Add(ResultCardView.HeadlineText(serverHeadline!.Trim(), warn: false));
                var more = MoreDetail(text, serverHeadline, lang, maxWidth, tableDownloads);
                if (more != null) col.Children.Add(more);
                return col;
            }
            var (headline, rest) = ProseSplit.Split(text);
            col.Children.Add(ResultCardView.HeadlineText(headline, warn: false));
            if (rest.Length == 0) return col;
            return AddRest(col, rest, ProseSplit.CollapseRest(text), lang, maxWidth, tableDownloads);
        }

        /// <summary>The "More detail" toggle and the collapsed pane text, for a card that already has its own headline; null when the
        /// text only repeats the headline.</summary>
        public static FrameworkElement? MoreDetail(string text, string headline, ReceiptLanguage lang, double maxWidth, bool tableDownloads)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0 || ProseSplit.Plain(t) == ProseSplit.Plain(headline.Trim())) return null;
            return AddRest(new StackPanel(), t, true, lang, maxWidth, tableDownloads);
        }

        private static FrameworkElement AddRest(StackPanel col, string rest, bool collapse, ReceiptLanguage lang, double maxWidth, bool tableDownloads)
        {
            var detail = CopilotMessageBubble.MarkdownText(rest, maxWidth, tableDownloads: tableDownloads);
            if (!collapse)
            {
                detail.Margin = new Thickness(30, 8, 0, 0);
                col.Children.Add(detail);
                return col;
            }
            detail.Visibility = Visibility.Collapsed;
            detail.Margin = new Thickness(30, 4, 0, 0);
            Button toggle = null!;
            toggle = CardKit.Ghost(StepsSummary.MoreDetailLabel(false, lang), () =>
            {
                var open = detail.Visibility != Visibility.Visible;
                detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = StepsSummary.MoreDetailLabel(open, lang);
                if (open) CardKit.Reveal(detail);
            }, compact: true);
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.MinHeight = 26;
            toggle.Margin = new Thickness(24, 6, 0, 0);
            col.Children.Add(toggle);
            col.Children.Add(detail);
            return col;
        }
    }
}
