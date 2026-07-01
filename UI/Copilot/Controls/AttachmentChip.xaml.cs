using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    public partial class AttachmentChip : UserControl
    {
        // ── DependencyProperties ──────────────────────────────────────────────
        public static readonly DependencyProperty IsImageProperty =
            DependencyProperty.Register(nameof(IsImage), typeof(bool), typeof(AttachmentChip),
                new PropertyMetadata(false, (d, _) => ((AttachmentChip)d).Apply()));

        public static readonly DependencyProperty ImageSourceProperty =
            DependencyProperty.Register(nameof(ImageSource), typeof(BitmapSource), typeof(AttachmentChip),
                new PropertyMetadata(null, (d, _) => ((AttachmentChip)d).Apply()));

        public static readonly DependencyProperty FileNameProperty =
            DependencyProperty.Register(nameof(FileName), typeof(string), typeof(AttachmentChip),
                new PropertyMetadata(string.Empty, (d, _) => ((AttachmentChip)d).Apply()));

        public static readonly DependencyProperty FileTypeProperty =
            DependencyProperty.Register(nameof(FileType), typeof(string), typeof(AttachmentChip),
                new PropertyMetadata(string.Empty, (d, _) => ((AttachmentChip)d).Apply()));

        public static readonly DependencyProperty LineInfoProperty =
            DependencyProperty.Register(nameof(LineInfo), typeof(string), typeof(AttachmentChip),
                new PropertyMetadata(string.Empty, (d, _) => ((AttachmentChip)d).Apply()));

        public static readonly DependencyProperty ShowRemoveProperty =
            DependencyProperty.Register(nameof(ShowRemove), typeof(bool), typeof(AttachmentChip),
                new PropertyMetadata(false, (d, _) => ((AttachmentChip)d).Apply()));

        public bool IsImage { get => (bool)GetValue(IsImageProperty); set => SetValue(IsImageProperty, value); }
        public BitmapSource ImageSource { get => (BitmapSource)GetValue(ImageSourceProperty); set => SetValue(ImageSourceProperty, value); }
        public string FileName { get => (string)GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
        public string FileType { get => (string)GetValue(FileTypeProperty); set => SetValue(FileTypeProperty, value); }
        public string LineInfo { get => (string)GetValue(LineInfoProperty); set => SetValue(LineInfoProperty, value); }
        public bool ShowRemove { get => (bool)GetValue(ShowRemoveProperty); set => SetValue(ShowRemoveProperty, value); }

        // Not a DependencyProperty — Actions aren't bindable; set directly by factory methods.
        public Action OnRemove { get; set; }

        // ── Static factory methods ────────────────────────────────────────────

        public static AttachmentChip ForImage(BitmapSource src, Action onRemove = null) =>
            new AttachmentChip
            {
                IsImage    = true,
                ImageSource = src,
                ShowRemove = onRemove != null,
                OnRemove   = onRemove,
            };

        public static AttachmentChip ForFile(string name, string content, Action onRemove = null)
        {
            int lines = string.IsNullOrEmpty(content) ? 0 : content.Count(c => c == '\n') + 1;
            return ForFile(name, lines, onRemove);
        }

        /// <summary>Build a file chip from a precomputed line count — used when the
        /// content isn't available (e.g. redrawing from persisted run history).</summary>
        public static AttachmentChip ForFile(string name, int lines, Action onRemove = null)
        {
            string ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "FILE";
            return new AttachmentChip
            {
                IsImage    = false,
                FileName   = Path.GetFileNameWithoutExtension(name),
                FileType   = ext,
                LineInfo   = $"{lines} ln",
                ShowRemove = onRemove != null,
                OnRemove   = onRemove,
            };
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public AttachmentChip()
        {
            InitializeComponent();
            Loaded += (_, __) => Apply();
        }

        private void Apply()
        {
            if (Root == null) return;
            Root.Children.Clear();
            if (IsImage) BuildImageChip();
            else         BuildFileChip();
            if (ShowRemove) BuildRemoveOverlay();
        }

        // ── Image chip ────────────────────────────────────────────────────────

        private void BuildImageChip()
        {
            var border = new Border
            {
                CornerRadius    = new CornerRadius(8),
                ClipToBounds    = true,
                BorderThickness = new Thickness(1),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            border.Child = new Image { Source = ImageSource, Stretch = Stretch.UniformToFill };
            Root.Children.Add(border);
        }

        // ── File chip ─────────────────────────────────────────────────────────

        private void BuildFileChip()
        {
            var border = new Border
            {
                CornerRadius    = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background      = Brushes.White,
                Padding         = new Thickness(4, 6, 4, 6),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");

            var stack = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };

            // Extension badge — "TXT", "CSV", etc.
            var badgeText = new TextBlock
            {
                Text                = FileType,
                FontSize            = 7,
                FontWeight          = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Purple");

            var badge = new Border
            {
                CornerRadius        = new CornerRadius(2),
                Padding             = new Thickness(3, 1, 3, 1),
                Margin              = new Thickness(0, 0, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child               = badgeText,
            };
            badge.SetResourceReference(Border.BackgroundProperty, "Cp.PurpleSoft");
            stack.Children.Add(badge);

            // Filename (extension stripped, truncated)
            stack.Children.Add(new TextBlock
            {
                Text                = FileName,
                FontSize            = 8,
                MaxWidth            = 48,
                TextTrimming        = TextTrimming.CharacterEllipsis,
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 2),
            });

            // Line count
            var lineInfo = new TextBlock
            {
                Text                = LineInfo,
                FontSize            = 7,
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            lineInfo.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            stack.Children.Add(lineInfo);

            border.Child = stack;
            Root.Children.Add(border);
        }

        // ── Remove overlay ────────────────────────────────────────────────────

        private void BuildRemoveOverlay()
        {
            var btn = new Button
            {
                Content             = "✕",
                FontSize            = 7,
                Width               = 14,
                Height              = 14,
                Cursor              = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                Margin              = new Thickness(0, -3, -3, 0),
                Padding             = new Thickness(0),
                Background          = Brushes.White,
                BorderThickness     = new Thickness(1),
                BorderBrush         = CopilotColors.From("#e5e7eb"),
                IsTabStop           = false,
            };
            btn.Click += (_, __) => OnRemove?.Invoke();
            Root.Children.Add(btn);
        }
    }
}
