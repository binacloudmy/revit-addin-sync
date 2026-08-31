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
        /// <summary>User message row — Slate design: right-aligned dark bubble
        /// (no avatar), 14/14/4/14 radius, image/file chips + selectable text,
        /// hover copy button on the left.</summary>
        public static FrameworkElement User(string text, string userInitial,
            IEnumerable<string> imagesBase64, IEnumerable<Model.HistoryFile> files,
            double maxWidth, string time = null, Model.SlashTool slashCommand = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            // Command bubbles use the design's tighter padding (8px 9px 9px); plain
            // text bubbles keep the wider 9px 13px. (Both radius 14 14 4 14.)
            var padding = slashCommand != null ? new Thickness(9, 8, 9, 9) : new Thickness(13, 9, 13, 9);
            // v6: the user bubble is a white surface card with the design's
            // shadow-sm ring instead of a tinted fill (radius stays 14/14/4/14).
            var bubble = new Border
            {
                Background = CopilotColors.From("#eef1f5"),   // → surface white in v6 light, slate bubble in dark
                CornerRadius = new CornerRadius(14, 14, 4, 14),
                Padding = padding, MaxWidth = maxWidth * 0.82,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = System.Windows.Media.Colors.Black, Opacity = 0.10, BlurRadius = 5, ShadowDepth = 1, Direction = 270 },
            };
            var bubbleStack = new StackPanel();

            // Slash command runs as a chip at the top of the bubble.
            if (slashCommand != null)
            {
                var chip = CommandChip.Build(slashCommand);
                chip.HorizontalAlignment = HorizontalAlignment.Left;
                chip.Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(text) ? 0 : 7);
                bubbleStack.Children.Add(chip);
            }

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
                    // Binary kinds have no line count — the addin read them, the
                    // pane never held their contents.
                    var chip = f.ResolvedKind == "dwg" ? AttachmentChip.ForDrawing(f.Name)
                             : f.ResolvedKind == "pdf" ? AttachmentChip.ForDocument(f.Name, f.Pages)
                             : AttachmentChip.ForFile(f.Name, f.Lines);
                    chip.Margin = new Thickness(0, 0, 6, 0);
                    fileStrip.Children.Add(chip);
                }
                if (fileStrip.Children.Count > 0) bubbleStack.Children.Add(fileStrip);
            }

            // Selectable read-only TextBox (a WPF TextBlock cannot be selected/
            // copied) — styled to look identical to a plain TextBlock. User text
            // stays plain (no markdown).
            var textBox = new TextBox
            {
                Text = text, FontSize = 13.5, Foreground = CopilotColors.From("#131c2b"),
                CaretBrush = CopilotColors.From("#131c2b"),
                TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
                BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(0), IsTabStop = false,
            };
            // Skip the (empty) text row for a bare slash command — the chip stands alone.
            if (!string.IsNullOrEmpty(text) || slashCommand == null)
                bubbleStack.Children.Add(ClampableText(textBox));
            bubble.Child = bubbleStack;
            AttachCopyMenu(bubble, text);
            // Copy affordance sits on the LEFT of the right-aligned bubble.
            if (!string.IsNullOrEmpty(text))
                row.Children.Add(HoverReveal(row, CopyButton(text)));
            row.Children.Add(bubble);

            // Column: bubble row + small right-aligned timestamp under it (design).
            var colWrap = new StackPanel { Margin = new Thickness(0, 0, 0, 15), HorizontalAlignment = HorizontalAlignment.Right };
            colWrap.Children.Add(row);
            if (!string.IsNullOrWhiteSpace(time))
            {
                var t = new TextBlock
                {
                    Text = time, FontSize = 10, Foreground = CopilotColors.From("#99a3b3"),
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0),
                };
                colWrap.Children.Add(t);
            }
            return colWrap;
        }

        // Long messages clamp to 80px with a bottom fade and a Show more/Show
        // less toggle (design's textWrapStyle + needsToggle). Measured after
        // layout; short messages render untouched.
        private const double ClampPx = 80;

        private static FrameworkElement ClampableText(TextBox textBox)
        {
            var host = new StackPanel();
            var clip = new Border { Child = textBox, ClipToBounds = true };
            host.Children.Add(clip);

            var fade = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.58),
                    new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0),
                },
            };

            textBox.Loaded += (_, __) =>
            {
                textBox.UpdateLayout();
                if (textBox.ExtentHeight <= ClampPx + 8) return;
                if (host.Children.Count > 1) return;   // already toggled

                bool expanded = false;
                clip.MaxHeight = ClampPx;
                clip.OpacityMask = fade;

                var label = new TextBlock { Text = "Show more", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                label.Foreground = CopilotColors.From("#131c2b");
                var chevron = new System.Windows.Shapes.Path
                {
                    Width = 11, Height = 11, Stretch = Stretch.Uniform, StrokeThickness = 2.2,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse("M6,9 L12,15 L18,9"), Margin = new Thickness(5, 1, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center, Stroke = CopilotColors.From("#131c2b"),
                };
                var toggle = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent,
                };
                toggle.Children.Add(label);
                toggle.Children.Add(chevron);
                toggle.MouseLeftButtonDown += (s, e) =>
                {
                    expanded = !expanded;
                    clip.MaxHeight = expanded ? double.PositiveInfinity : ClampPx;
                    clip.OpacityMask = expanded ? null : fade;
                    label.Text = expanded ? "Show less" : "Show more";
                    chevron.Data = Geometry.Parse(expanded ? "M6,15 L12,9 L18,15" : "M6,9 L12,15 L18,9");
                    e.Handled = true;
                };
                host.Children.Add(toggle);
            };
            return host;
        }

        /// <summary>AI message row — Slate design: full-width plain text (no
        /// avatar, no bubble surface) with right-click/hover copy.</summary>
        public static FrameworkElement Ai(string markdown, double maxWidth)
        {
            var aiRow = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            var col = new StackPanel();
            col.MaxWidth = maxWidth;
            col.HorizontalAlignment = HorizontalAlignment.Left;
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

        // Design gstarLogo path: four-point star, 24×24 viewbox.
        private static readonly Geometry StarGeom = Geometry.Parse(
            "M12,1.1 C12.45,7.05 16.95,11.55 22.9,12 C16.95,12.45 12.45,16.95 12,22.9 C11.55,16.95 7.05,12.45 1.1,12 C7.05,11.55 11.55,7.05 12,1.1 Z");

        /// <summary>Gradient four-point BINA star (transparent tile) — the Slate
        /// design's logo mark, shared by header, greeting and thinking line.</summary>
        public static FrameworkElement BotAvatar(double size = 22)
        {
            var b = new Border { Width = size, Height = size, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top };
            b.Child = new System.Windows.Shapes.Path
            {
                Width = size * 0.82, Height = size * 0.82, Stretch = Stretch.Uniform,
                Fill = StarGradient(), Data = StarGeom,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            return b;
        }

        /// <summary>The design's gstarLogo gradient (#7dd6ff → #3b8ef7 → #1d5fe0).</summary>
        public static LinearGradientBrush StarGradient()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#7dd6ff"), 0));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#3b8ef7"), 0.45));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#1d5fe0"), 1));
            g.Freeze();
            return g;
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
            var label = new TextBlock { Text = "⧉", FontSize = 12, Foreground = CopilotColors.From("#99a3b3") };
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
                label.Text = "✓"; label.Foreground = CopilotColors.From("#10b981");
                var t = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1.2) };
                t.Tick += (s, e2) =>
                {
                    label.Text = "⧉"; label.Foreground = CopilotColors.From("#99a3b3");
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

        /// <summary>Saved Commands J1 — the "Save as command" footer action on a
        /// completed tool reply (design: bookmark icon + label, secondary
        /// button styling on the answer's action row).</summary>
        public static FrameworkElement SaveCommandButton(System.Action onClick)
        {
            var bd = new Border
            {
                Height = 26, CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 0, 9, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Turn this into a command you can re-run",
            };
            bd.SetResourceReference(Border.BackgroundProperty, "Cp.BlueSoft");
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(CommandPalette.IconEl("ti-device-floppy", 12, "Cp.BlueText"));
            var t = new TextBlock
            {
                Text = "Save as command", FontSize = 11.5,
                FontWeight = FontWeight.FromOpenTypeWeight(600),
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
            sp.Children.Add(t);
            bd.Child = sp;
            bd.MouseLeftButtonUp += (_, __) => onClick();
            return bd;
        }
    }
}
