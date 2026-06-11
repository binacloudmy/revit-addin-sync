using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// A Library/Saved tool row. Vetted (Tier 1) shows a 3px green left stripe + play glyph;
    /// AI (Tier 2) shows a chevron. Optional pinned bookmark + "Saved" badge.
    /// Forwards Click to the bound Command with the tool id as parameter.
    /// </summary>
    public partial class ToolCard : UserControl
    {
        private static readonly Geometry PlayGeo = CopilotIcons.Get("play");
        private static readonly Geometry ChevronGeo = CopilotIcons.Get("chevronRight");
        private static readonly Geometry BookmarkGeo = CopilotIcons.Get("bookmark");

        public ToolCard()
        {
            InitializeComponent();
            Root.Click += (_, __) =>
            {
                var id = Tool?.Id;
                if (Command != null && Command.CanExecute(id)) Command.Execute(id);
            };
            Loaded += (_, __) => Apply();
        }

        public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
            nameof(Tool), typeof(ToolDef), typeof(ToolCard), new PropertyMetadata(null, OnChanged));
        public ToolDef Tool { get => (ToolDef)GetValue(ToolProperty); set => SetValue(ToolProperty, value); }

        public static readonly DependencyProperty IsPinnedProperty = DependencyProperty.Register(
            nameof(IsPinned), typeof(bool), typeof(ToolCard), new PropertyMetadata(false, OnChanged));
        public bool IsPinned { get => (bool)GetValue(IsPinnedProperty); set => SetValue(IsPinnedProperty, value); }

        public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
            nameof(Command), typeof(ICommand), typeof(ToolCard), new PropertyMetadata(null));
        public ICommand Command { get => (ICommand)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((ToolCard)d).Apply();

        private void Apply()
        {
            if (Root == null) return;
            var t = Tool;
            if (t == null) return;

            bool vetted = t.Tier == 1;
            Tile.Glyph = t.Icon;
            Tile.TileBg = t.TileBg;
            Tile.TileFg = t.TileFg;
            Title.Text = t.Title;
            Desc.Text = t.Desc;

            Stripe.Visibility = vetted ? Visibility.Visible : Visibility.Collapsed;

            PinGlyph.Data = BookmarkGeo;
            PinGlyph.Visibility = IsPinned ? Visibility.Visible : Visibility.Collapsed;

            SavedPill.Visibility = t.Saved ? Visibility.Visible : Visibility.Collapsed;

            if (vetted)
            {
                Trailing.Data = PlayGeo;
                Trailing.Fill = CopilotColors.From("#6b7280");
                Trailing.Stroke = null;
            }
            else
            {
                Trailing.Data = ChevronGeo;
                Trailing.Stroke = CopilotColors.From("#9ca3af");
                Trailing.StrokeThickness = 1.8;
                Trailing.Fill = null;
            }
        }
    }
}
