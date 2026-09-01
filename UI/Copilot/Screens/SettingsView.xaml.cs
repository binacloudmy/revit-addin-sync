using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>
    /// v6-panel "Settings" view. Each card is one REAL persisted preference
    /// (CopilotPrefs) — the toggle writes through immediately, same lifecycle
    /// as the composer's Reasoning toggle. The theme row reuses
    /// CopilotTheme.Toggle (the header moon button's path).
    /// </summary>
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
            Loaded += (_, __) => Render();
            CopilotTheme.ThemeChanged += OnTheme;
            Unloaded += (_, __) => CopilotTheme.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Render();

        private void Render()
        {
            if (Cards == null) return;
            Cards.Children.Clear();
            var p = CopilotPrefs.Load();

            Cards.Children.Add(ToggleCard(
                "Show agent activity",
                "Stream the reasoning timeline while Copilot works",
                p.ReasoningEnabled,
                on => { var q = CopilotPrefs.Load(); q.ReasoningEnabled = on; q.Save(); }));

            Cards.Children.Add(ToggleCard(
                "Auto-approve writes",
                "Run pre-approved write batches without the confirm card",
                p.AutoApproveWrites,
                on => { var q = CopilotPrefs.Load(); q.AutoApproveWrites = on; q.Save(); }));

            Cards.Children.Add(TagCard("Language", "Answers and agent activity",
                "Auto", accent: true, onClick: null));

            Cards.Children.Add(TagCard("Theme", "Interface appearance",
                CopilotTheme.IsDark ? "Slate" : "Light", accent: false,
                onClick: () => CopilotTheme.Toggle()));
        }

        // ── v6 card shells ──────────────────────────────────────────────────

        private FrameworkElement Card(out Grid content)
        {
            content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var card = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 11, 13, 11), Margin = new Thickness(0, 0, 0, 8),
                Child = content,
            };
            card.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            card.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            return card;
        }

        private static StackPanel Labels(string name, string desc)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var n = new TextBlock { Text = name, FontSize = 13 };
            n.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            var d = new TextBlock { Text = desc, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            d.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            sp.Children.Add(n);
            sp.Children.Add(d);
            return sp;
        }

        private FrameworkElement ToggleCard(string name, string desc, bool on, Action<bool> apply)
        {
            var card = Card(out var g);
            var labels = Labels(name, desc);
            Grid.SetColumn(labels, 0);
            g.Children.Add(labels);

            // 34×19 pill + 15px knob (design switchStyles) — no Storyboard;
            // the flip is a re-render, not an animation (Revit pane rule).
            var knob = new Border
            {
                Width = 15, Height = 15, CornerRadius = new CornerRadius(99),
                Background = Brushes.White,
                HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, Opacity = 0.25, BlurRadius = 2, ShadowDepth = 1, Direction = 270 },
            };
            var pill = new Border
            {
                Width = 34, Height = 19, CornerRadius = new CornerRadius(99),
                Padding = new Thickness(2), Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Child = knob,
            };
            pill.SetResourceReference(Border.BackgroundProperty, on ? "Cp.Green" : "Cp.Hair2");
            bool state = on;
            pill.MouseLeftButtonUp += (_, __) =>
            {
                state = !state;
                try { apply(state); } catch { /* prefs write is best-effort */ }
                pill.SetResourceReference(Border.BackgroundProperty, state ? "Cp.Green" : "Cp.Hair2");
                knob.HorizontalAlignment = state ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            };
            Grid.SetColumn(pill, 1);
            g.Children.Add(pill);
            return card;
        }

        private FrameworkElement TagCard(string name, string desc, string tag, bool accent, Action onClick)
        {
            var card = Card(out var g);
            var labels = Labels(name, desc);
            Grid.SetColumn(labels, 0);
            g.Children.Add(labels);

            var text = new TextBlock { Text = tag, FontSize = 11 };
            text.SetResourceReference(TextBlock.ForegroundProperty, accent ? "Cp.Tier2Fg" : "Cp.Ink");
            var pill = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center, Child = text,
                Cursor = onClick != null ? System.Windows.Input.Cursors.Hand : null,
            };
            pill.SetResourceReference(Border.BackgroundProperty, accent ? "Cp.Tier2Bg" : "Cp.TabBadgeBg");
            if (onClick != null) pill.MouseLeftButtonUp += (_, __) => onClick();
            Grid.SetColumn(pill, 1);
            g.Children.Add(pill);
            return card;
        }
    }
}
