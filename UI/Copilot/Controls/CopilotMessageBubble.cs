using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Shared chat-bubble renderer used by both the live chat (ChatView) and the
    /// History detail view. Owns the two common shapes — the user bubble and the
    /// AI markdown-text bubble — plus the bot avatar and copy affordances, so the
    /// two screens stay visually identical without duplicating layout. ChatView
    /// wraps <see cref="MarkdownText"/> with its kind-specific cards (proposal,
    /// result, steps, verdict); History calls <see cref="User"/>/<see cref="Ai"/>
    /// directly.
    /// </summary>
    public static class CopilotMessageBubble
    {
        /// <summary>User message row: initial avatar + grey bubble (image chips,
        /// file chips, selectable plain text) + hover copy button.</summary>
        public static FrameworkElement User(string text, string userInitial,
            IEnumerable<string> imagesBase64, IEnumerable<(string Name, int Lines)> files,
            double maxWidth)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var av = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = CopilotColors.From("#e5e7eb"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0) };
            string initial = !string.IsNullOrEmpty(userInitial) ? userInitial.Substring(0, 1).ToUpperInvariant() : "?";
            av.Child = new TextBlock { Text = initial, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#374151"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var bubble = new Border { Background = CopilotColors.From("#f1f3f5"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), MaxWidth = maxWidth };
            var bubbleStack = new StackPanel();

            // Screenshots pasted with this prompt render above the text.
            if (imagesBase64 != null)
                foreach (var b64 in imagesBase64)
                {
                    var src = ImageFromBase64(b64);
                    if (src == null) continue;
                    var chip = AttachmentChip.ForImage(src);
                    chip.Margin = new Thickness(0, 0, 0, 4);
                    bubbleStack.Children.Add(chip);
                }

            // Attached files render as chips (their contents go to the backend,
            // never as raw text in the bubble).
            if (files != null)
            {
                var fileStrip = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                foreach (var f in files)
                {
                    var chip = AttachmentChip.ForFile(f.Name, f.Lines);
                    chip.Margin = new Thickness(0, 0, 6, 0);
                    fileStrip.Children.Add(chip);
                }
                if (fileStrip.Children.Count > 0) bubbleStack.Children.Add(fileStrip);
            }

            // Selectable read-only TextBox (a WPF TextBlock cannot be selected/
            // copied) — styled to look identical to a plain TextBlock. User text
            // stays plain (no markdown).
            bubbleStack.Children.Add(new TextBox
            {
                Text = text, FontSize = 13, Foreground = CopilotColors.From("#0b0d12"),
                TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
                BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(0), IsTabStop = false,
            });
            bubble.Child = bubbleStack;
            AttachCopyMenu(bubble, text);
            row.Children.Add(av); row.Children.Add(bubble);
            if (!string.IsNullOrEmpty(text))
                row.Children.Add(HoverReveal(row, CopyButton(text)));
            return row;
        }

        /// <summary>AI message row: bot avatar + markdown-rendered text with
        /// right-click/hover copy. Used directly where no cards are needed.</summary>
        public static FrameworkElement Ai(string markdown, double maxWidth)
        {
            var aiRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            aiRow.Children.Add(BotAvatar());
            var col = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            col.MaxWidth = maxWidth;
            if (!string.IsNullOrEmpty(markdown))
            {
                col.Children.Add(MarkdownText(markdown, maxWidth));
                AttachCopyMenu(col, markdown);
                col.Children.Add(HoverReveal(aiRow, CopyButton(markdown)));
            }
            aiRow.Children.Add(col);
            return aiRow;
        }

        /// <summary>Just the markdown element (AI replies are markdown — headers,
        /// **bold**, tables, lists). ChatView stacks its cards/Steps/Verdict
        /// around this inside its own AI column.</summary>
        public static FrameworkElement MarkdownText(string markdown, double maxWidth)
        {
            var md = RevitWebAppSync.Helpers.MarkdownRenderer.Render(markdown, maxWidth);
            md.Margin = new Thickness(0, 0, 0, 8);
            return md;
        }

        public static FrameworkElement BotAvatar(double size = 22)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Top };
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#2563eb"), 0));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#7c3aed"), 1));
            b.Background = g;
            b.Child = new System.Windows.Shapes.Path { Width = size * 0.55, Height = size * 0.55, Stretch = Stretch.Uniform, Fill = Brushes.White, Data = CopilotIcons.Get("sparkleSolid"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return b;
        }

        // ─── Copy-to-clipboard affordances ─────────────────────────────────
        // WPF TextBlocks are not selectable, so chat text could be pasted INTO
        // the input but never copied OUT. Every bubble gets a right-click "Copy
        // message" menu + a hover ⧉ button; user bubbles are additionally
        // text-selectable (read-only TextBox).

        public static void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // Revit add-ins share the clipboard with the host; SetDataObject can
            // throw CLIPBRD_E_CANT_OPEN when another app holds it — retry once.
            try { Clipboard.SetDataObject(text, true); }
            catch { try { Clipboard.SetDataObject(text, false); } catch { /* clipboard busy */ } }
        }

        public static void AttachCopyMenu(FrameworkElement el, string text)
        {
            if (el == null || string.IsNullOrEmpty(text)) return;
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "Copy message" };
            item.Click += (_, __) => CopyText(text);
            menu.Items.Add(item);
            el.ContextMenu = menu;
        }

        // Small ⧉ button that copies the message and flashes ✓ as feedback.
        public static Button CopyButton(string text)
        {
            var label = new TextBlock { Text = "⧉", FontSize = 12, Foreground = CopilotColors.From("#9ca3af") };
            var btn = new Button
            {
                Content = label, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Copy",
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 2, 0, 0), IsTabStop = false,
            };
            btn.Click += (_, __) =>
            {
                CopyText(text);
                label.Text = "✓"; label.Foreground = CopilotColors.From("#16a34a");
                var t = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1.2) };
                t.Tick += (s, e2) =>
                {
                    label.Text = "⧉"; label.Foreground = CopilotColors.From("#9ca3af");
                    ((System.Windows.Threading.DispatcherTimer)s).Stop();
                };
                t.Start();
            };
            return btn;
        }

        // Keeps the chat quiet: the affordance only appears while the pointer is
        // over its message row.
        public static FrameworkElement HoverReveal(FrameworkElement row, FrameworkElement affordance)
        {
            affordance.Visibility = Visibility.Hidden;
            row.MouseEnter += (_, __) => affordance.Visibility = Visibility.Visible;
            row.MouseLeave += (_, __) => affordance.Visibility = Visibility.Hidden;
            return affordance;
        }

        /// <summary>Decode a base64 PNG (a pasted screenshot) into a frozen
        /// BitmapImage for the chat thumbnail. Null on bad input.</summary>
        public static System.Windows.Media.Imaging.BitmapImage ImageFromBase64(string b64)
        {
            try
            {
                var bytes = System.Convert.FromBase64String(b64);
                var img = new System.Windows.Media.Imaging.BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    img.BeginInit();
                    img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                }
                img.Freeze();
                return img;
            }
            catch { return null; }
        }
    }
}
