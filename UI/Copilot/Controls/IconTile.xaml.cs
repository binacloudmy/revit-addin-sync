using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// A rounded colored tile rendering a CopilotIcons glyph. Used on tool cards, headers,
    /// chat proposal/result headers, history rows. Reproduces the prototype's icon tiles.
    /// </summary>
    public partial class IconTile : UserControl
    {
        public IconTile()
        {
            InitializeComponent();
            Loaded += (_, __) => Apply();
        }

        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
            nameof(Glyph), typeof(string), typeof(IconTile), new PropertyMetadata(null, OnChanged));
        public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

        public static readonly DependencyProperty TileBgProperty = DependencyProperty.Register(
            nameof(TileBg), typeof(string), typeof(IconTile), new PropertyMetadata(null, OnChanged));
        public string TileBg { get => (string)GetValue(TileBgProperty); set => SetValue(TileBgProperty, value); }

        public static readonly DependencyProperty TileFgProperty = DependencyProperty.Register(
            nameof(TileFg), typeof(string), typeof(IconTile), new PropertyMetadata(null, OnChanged));
        public string TileFg { get => (string)GetValue(TileFgProperty); set => SetValue(TileFgProperty, value); }

        public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(
            nameof(TileSize), typeof(double), typeof(IconTile), new PropertyMetadata(30.0, OnChanged));
        public double TileSize { get => (double)GetValue(TileSizeProperty); set => SetValue(TileSizeProperty, value); }

        public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
            nameof(GlyphSize), typeof(double), typeof(IconTile), new PropertyMetadata(15.0, OnChanged));
        public double GlyphSize { get => (double)GetValue(GlyphSizeProperty); set => SetValue(GlyphSizeProperty, value); }

        public static readonly DependencyProperty CornerProperty = DependencyProperty.Register(
            nameof(Corner), typeof(double), typeof(IconTile), new PropertyMetadata(7.0, OnChanged));
        public double Corner { get => (double)GetValue(CornerProperty); set => SetValue(CornerProperty, value); }

        public static readonly DependencyProperty StrokePxProperty = DependencyProperty.Register(
            nameof(StrokePx), typeof(double), typeof(IconTile), new PropertyMetadata(1.8, OnChanged));
        public double StrokePx { get => (double)GetValue(StrokePxProperty); set => SetValue(StrokePxProperty, value); }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((IconTile)d).Apply();

        private void Apply()
        {
            if (Tile == null || GlyphPath == null) return;

            Tile.Width = TileSize;
            Tile.Height = TileSize;
            Tile.CornerRadius = new CornerRadius(Corner);
            Tile.Background = CopilotColors.From(TileBg);

            GlyphBox.Width = GlyphSize;
            GlyphBox.Height = GlyphSize;

            var fg = CopilotColors.From(TileFg);
            var geo = CopilotIcons.Get(Glyph);
            GlyphPath.Data = geo;

            if (CopilotIcons.IsFilled(Glyph))
            {
                GlyphPath.Fill = fg;
                GlyphPath.Stroke = null;
            }
            else
            {
                GlyphPath.Fill = null;
                GlyphPath.Stroke = fg;
                GlyphPath.StrokeThickness = StrokePx;
            }
        }
    }
}
