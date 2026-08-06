using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>Shared header for the tool form / review / running screens.</summary>
    public partial class ToolHeader : UserControl
    {
        public ToolHeader()
        {
            InitializeComponent();
            BackBtn.Click += (_, __) => { if (BackCommand != null && BackCommand.CanExecute(null)) BackCommand.Execute(null); };
            Loaded += (_, __) => Apply();
        }

        public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
            nameof(Tool), typeof(ToolDef), typeof(ToolHeader), new PropertyMetadata(null, OnChanged));
        public ToolDef Tool { get => (ToolDef)GetValue(ToolProperty); set => SetValue(ToolProperty, value); }

        public static readonly DependencyProperty HideBackProperty = DependencyProperty.Register(
            nameof(HideBack), typeof(bool), typeof(ToolHeader), new PropertyMetadata(false, OnChanged));
        public bool HideBack { get => (bool)GetValue(HideBackProperty); set => SetValue(HideBackProperty, value); }

        public static readonly DependencyProperty SubtitleTextProperty = DependencyProperty.Register(
            nameof(SubtitleText), typeof(string), typeof(ToolHeader), new PropertyMetadata(null, OnChanged));
        public string SubtitleText { get => (string)GetValue(SubtitleTextProperty); set => SetValue(SubtitleTextProperty, value); }

        public static readonly DependencyProperty SubtitleColorProperty = DependencyProperty.Register(
            nameof(SubtitleColor), typeof(string), typeof(ToolHeader), new PropertyMetadata(null, OnChanged));
        public string SubtitleColor { get => (string)GetValue(SubtitleColorProperty); set => SetValue(SubtitleColorProperty, value); }

        public static readonly DependencyProperty BackCommandProperty = DependencyProperty.Register(
            nameof(BackCommand), typeof(ICommand), typeof(ToolHeader), new PropertyMetadata(null));
        public ICommand BackCommand { get => (ICommand)GetValue(BackCommandProperty); set => SetValue(BackCommandProperty, value); }

        // ── Tool-less header (flows with no CopilotCatalog ToolDef, e.g. the
        // massing/planning flow, which is driven by a SlashTool instead). When
        // Tool is null these supply the title + tile and the tier badge is hidden.
        public static readonly DependencyProperty TitleTextProperty = DependencyProperty.Register(
            nameof(TitleText), typeof(string), typeof(ToolHeader), new PropertyMetadata(null, OnChanged));
        public string TitleText { get => (string)GetValue(TitleTextProperty); set => SetValue(TitleTextProperty, value); }

        public static readonly DependencyProperty GlyphKeyProperty = DependencyProperty.Register(
            nameof(GlyphKey), typeof(string), typeof(ToolHeader), new PropertyMetadata(null, OnChanged));
        public string GlyphKey { get => (string)GetValue(GlyphKeyProperty); set => SetValue(GlyphKeyProperty, value); }

        public static readonly DependencyProperty TileBgHexProperty = DependencyProperty.Register(
            nameof(TileBgHex), typeof(string), typeof(ToolHeader), new PropertyMetadata(null, OnChanged));
        public string TileBgHex { get => (string)GetValue(TileBgHexProperty); set => SetValue(TileBgHexProperty, value); }

        public static readonly DependencyProperty TileFgHexProperty = DependencyProperty.Register(
            nameof(TileFgHex), typeof(string), typeof(ToolHeader), new PropertyMetadata(null, OnChanged));
        public string TileFgHex { get => (string)GetValue(TileFgHexProperty); set => SetValue(TileFgHexProperty, value); }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((ToolHeader)d).Apply();

        private void Apply()
        {
            if (Tile == null) return;
            BackBtn.Visibility = HideBack ? Visibility.Collapsed : Visibility.Visible;

            var t = Tool;
            if (t != null)
            {
                Tile.Glyph = t.Icon; Tile.TileBg = t.TileBg; Tile.TileFg = t.TileFg;
                Title.Text = t.Title;
                Badge.Tier = t.Tier;
                Badge.Visibility = Visibility.Visible;
                Subtitle.Text = string.IsNullOrEmpty(SubtitleText) ? t.Desc : SubtitleText;
            }
            else
            {
                Tile.Glyph = GlyphKey;
                Tile.TileBg = TileBgHex; Tile.TileFg = TileFgHex;
                Tile.Visibility = string.IsNullOrEmpty(GlyphKey) ? Visibility.Collapsed : Visibility.Visible;
                Title.Text = TitleText ?? "";
                Badge.Visibility = Visibility.Collapsed;
                Subtitle.Text = SubtitleText;
            }

            Subtitle.Foreground = string.IsNullOrEmpty(SubtitleColor)
                ? CopilotColors.From("#6b7280")
                : CopilotColors.From(SubtitleColor);
        }
    }
}
