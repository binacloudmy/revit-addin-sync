using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Global ⌘K / Ctrl+K command palette (design file
    /// docs/design/bina-copilot-v5.dc.html lines 408+). Modal overlay that
    /// surfaces quick actions: jump to nav, run a slash tool, toggle theme,
    /// open menu. Renders as a single floating card with scrim. No animation
    /// on show/hide (Revit pane constraint).
    /// </summary>
    public sealed class CmdKPalette : Border
    {
        public event Action<string> ActionInvoked;   // payload: action id

        private readonly TextBox _input;
        private readonly StackPanel _list;
        private readonly ScrollViewer _scroller;
        private readonly List<RowInfo> _flat = new List<RowInfo>();
        private readonly TextBlock _hint;
        private int _active;
        private string _query = "";

        // Populated in the constructor, not here: the action lambdas capture
        // ActionInvoked (an instance event), and a field initializer cannot
        // reference instance members (CS0236).
        private readonly List<Cmd> Commands;

        private sealed class RowInfo { public Cmd Cmd; public Border Row; }

        private sealed class Cmd
        {
            public string Id; public string Title; public string Desc;
            public string Icon; public string ActionKey; public Action Run;
            public Cmd(string id, string t, string d, string i, string a, Action r) { Id = id; Title = t; Desc = d; Icon = i; ActionKey = a; Run = r; }
        }

        public CmdKPalette()
        {
            Commands = new List<Cmd>
            {
                new Cmd("new",        "New chat",                "Clear the thread and start a new conversation",   "ph-chat-circle", null,                () => ActionInvoked?.Invoke("new")),
                new Cmd("nav-history","Jump to History",         "Browse past runs",                                "ph-clock-counter-clockwise", "Nav:History", null),
                new Cmd("nav-library","Jump to Library",         "Open the tool library",                            "ph-bookmarks",     "Nav:Library", null),
                new Cmd("nav-model",  "Jump to Model",           "Open model inspector",                             "ph-cube",          "Nav:Model",   null),
                new Cmd("nav-settings","Jump to Settings",        "Copilot preferences",                              "ph-gear",          "Nav:Settings",null),
                new Cmd("theme",      "Toggle theme",            "Light / dark",                                     "ph-moon",          null,           () => ActionInvoked?.Invoke("theme")),
                new Cmd("resync",     "Resync model",            "Re-index the active document",                     "ph-arrows-clockwise", null,         () => ActionInvoked?.Invoke("resync")),
                new Cmd("undo",       "Undo the last change",    "Roll back the most recent model edit",             "ph-arrow-counter-clockwise", null, () => ActionInvoked?.Invoke("undo")),
                new Cmd("doors",      "List all doors in this model", "Quick query preset",                           "ph-door-open",     "Preset:Doors", null),
                new Cmd("walls",      "List all walls in this model", "Quick query preset",                           "ph-ruler",         "Preset:Walls", null),
                new Cmd("rooms",      "Tag all untagged rooms",       "Quick query preset",                           "ph-house-line",    "Preset:Rooms", null),
                new Cmd("rate",       "Rate Copilot",            "Leave a thumbs-up / thumbs-down",                  "ph-star",          null,           () => ActionInvoked?.Invoke("rate")),
                new Cmd("bug",        "Report a bug",            "Open the bug-report sheet",                        "ph-bug",           null,           () => ActionInvoked?.Invoke("bug")),
                new Cmd("help",       "Get help on WhatsApp",    "Open the support channel",                         "ph-whatsapp-logo", null,           () => ActionInvoked?.Invoke("help")),
            };

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            // outer card chrome (sharp on top, soft shadow underneath)
            CornerRadius = new CornerRadius(14);
            Background = (Brush)Application.Current.Resources["Cp.Menu"];
            BorderBrush = (Brush)Application.Current.Resources["Cp.Line"];
            BorderThickness = new Thickness(1);
            MinWidth = 540;
            MaxWidth = 640;
            MinHeight = 80;

            var outer = new StackPanel();

            // ── search header ──
            var header = new Border
            {
                Padding = new Thickness(14, 12, 14, 12),
                BorderBrush = (Brush)Application.Current.Resources["Cp.Line"],
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            var hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchIcon = new Path
            {
                Width = 15, Height = 15, Stretch = Stretch.Uniform,
                Stroke = (Brush)Application.Current.Resources["Cp.Muted"],
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Data = Geometry.Parse("M11,4 a7,7 0 1 0 0,14 a7,7 0 1 0 0,-14 M11,4 a7,7 0 1 1 0,14 a7,7 0 1 1 0,-14 M16,16 l5,5"),
            };
            Grid.SetColumn(searchIcon, 0);
            hg.Children.Add(searchIcon);

            _input = new TextBox
            {
                Margin = new Thickness(10, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontFamily = new FontFamily("Geist, Segoe UI, system-ui, sans-serif"),
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources["Cp.Text"],
                CaretBrush = (Brush)Application.Current.Resources["Cp.Accent"],
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 4, 0, 4),
            };
            _input.TextChanged += (_, __) => { _query = _input.Text ?? ""; Rebuild(); };
            _input.PreviewKeyDown += OnInputKey;
            Grid.SetColumn(_input, 1);
            hg.Children.Add(_input);

            var escHint = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderBrush = (Brush)Application.Current.Resources["Cp.Line"],
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            escHint.Child = new TextBlock
            {
                Text = "esc",
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["Cp.Muted"],
            };
            Grid.SetColumn(escHint, 2);
            hg.Children.Add(escHint);
            header.Child = hg;
            outer.Children.Add(header);

            // ── list ──
            _scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 360,
                Margin = new Thickness(0, 4, 0, 4),
            };
            _scroller.Resources.Add(typeof(ScrollBar), Application.Current.Resources["Cp.SlimScrollBar"]);
            _list = new StackPanel { Margin = new Thickness(6, 4, 6, 6) };
            _scroller.Content = _list;
            outer.Children.Add(_scroller);

            // ── footer hint ──
            _hint = new TextBlock
            {
                Margin = new Thickness(14, 0, 14, 9),
                FontSize = 10.5,
                Foreground = (Brush)Application.Current.Resources["Cp.Faint"],
            };
            outer.Children.Add(_hint);

            Child = outer;

            // Card shadow
            Effect = new DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 6, Opacity = 0.16, Color = Color.FromRgb(0, 0, 0),
            };

            Rebuild();
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            FocusInput();
        }

        public new void Focus()
        {
            base.Focus();
            FocusInput();
        }

        private void FocusInput()
        {
            _input?.Focus();
            _input?.Select(_input.Text.Length, 0);
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            _flat.Clear();
            _active = 0;

            // Score by query: title contains q (weight 2), id starts with q (weight 3)
            string q = _query.Trim().ToLowerInvariant();
            var matches = new List<Cmd>();
            foreach (var c in Commands)
            {
                if (q.Length == 0) { matches.Add(c); continue; }
                int score = 0;
                if (c.Title.ToLowerInvariant().Contains(q)) score += 2;
                if (c.Id.ToLowerInvariant().StartsWith(q)) score += 3;
                if (c.Id.ToLowerInvariant().Contains(q)) score += 1;
                if (score > 0) matches.Add(c);
            }

            foreach (var c in matches)
            {
                var row = MakeRow(c);
                _list.Children.Add(row);
                _flat.Add(new RowInfo { Cmd = c, Row = row });
            }

            if (_flat.Count == 0)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = "No matches.",
                    Margin = new Thickness(14, 18, 14, 18),
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["Cp.Muted"],
                });
            }
            else
            {
                Highlight(_active);
            }

            _hint.Text = _flat.Count == 0
                ? ""
                : $"{_flat.Count} command{(_flat.Count == 1 ? "" : "s")} · ↑↓ to move · ↵ to run";
        }

        private Border MakeRow(Cmd c)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 2),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            row.MouseEnter += (_, __) => { /* visual handled in Highlight */ };
            row.MouseLeftButtonUp += (_, __) => Invoke(c);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // left icon
            var iconTile = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.Resources["Cp.Sunken"],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            iconTile.Child = new Path
            {
                Width = 13, Height = 13, Stretch = Stretch.Uniform,
                Stroke = (Brush)Application.Current.Resources["Cp.Muted"],
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                Data = Geometry.Parse(FallbackIconFor(c.Icon)),
            };
            Grid.SetColumn(iconTile, 0);
            g.Children.Add(iconTile);

            // title + desc
            var ts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            ts.Children.Add(new TextBlock
            {
                Text = c.Title,
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["Cp.Text"],
                FontWeight = FontWeights.Medium,
            });
            ts.Children.Add(new TextBlock
            {
                Text = c.Desc,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["Cp.Faint"],
                Margin = new Thickness(0, 1, 0, 0),
            });
            Grid.SetColumn(ts, 1);
            g.Children.Add(ts);

            // action key hint
            if (!string.IsNullOrEmpty(c.ActionKey))
            {
                var kb = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    BorderBrush = (Brush)Application.Current.Resources["Cp.Line"],
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 1, 6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                kb.Child = new TextBlock
                {
                    Text = c.ActionKey,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    FontSize = 10,
                    Foreground = (Brush)Application.Current.Resources["Cp.Muted"],
                };
                Grid.SetColumn(kb, 2);
                g.Children.Add(kb);
            }
            else
            {
                // chevron indicator
                var chev = new Path
                {
                    Width = 11, Height = 11, Stretch = Stretch.Uniform,
                    Stroke = (Brush)Application.Current.Resources["Cp.Faint"],
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Data = Geometry.Parse("M9,6 l6,6 -6,6"),
                };
                Grid.SetColumn(chev, 2);
                g.Children.Add(chev);
            }

            row.Child = g;
            return row;
        }

        private void Highlight(int idx)
        {
            for (int i = 0; i < _flat.Count; i++)
            {
                var row = _flat[i].Row;
                row.Background = i == idx
                    ? (Brush)Application.Current.Resources["Cp.Hover"]
                    : Brushes.Transparent;
            }
        }

        private void Invoke(Cmd c)
        {
            // Local closures (theme/resync/etc.) plus action keys for nav/presets
            c.Run?.Invoke();
            if (!string.IsNullOrEmpty(c.ActionKey))
                ActionInvoked?.Invoke(c.ActionKey);
            else if (c.Id == "new" || c.Id == "theme" || c.Id == "resync" || c.Id == "undo"
                  || c.Id == "rate" || c.Id == "bug" || c.Id == "help")
                ActionInvoked?.Invoke(c.Id);
        }

        private void OnInputKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (_flat.Count > 0) { _active = (_active + 1) % _flat.Count; Highlight(_active); }
                    e.Handled = true; break;
                case Key.Up:
                    if (_flat.Count > 0) { _active = (_active - 1 + _flat.Count) % _flat.Count; Highlight(_active); }
                    e.Handled = true; break;
                case Key.Enter:
                    if (_flat.Count > 0) Invoke(_flat[_active].Cmd);
                    e.Handled = true; break;
                case Key.Escape:
                    ActionInvoked?.Invoke("__close__");
                    e.Handled = true; break;
            }
        }

        private static string FallbackIconFor(string phosphorName) => phosphorName switch
        {
            "ph-chat-circle" => "M12,4 a8,8 0 1 0 0,16 a8,8 0 1 0 0,-16 M12,8 v6 M12,16 v0.01",
            "ph-clock-counter-clockwise" => "M12,4 a8,8 0 1 0 0,16 a8,8 0 1 0 0,-16 M12,8 v4 l3,2 M4,4 l-2,2 M4,4 l2,-2",
            "ph-bookmarks" => "M6,4 h12 v16 l-6,-4 -6,4 z",
            "ph-cube" => "M12,3 l9,5 v8 l-9,5 -9,-5 v-8 z M12,3 l0,18 M3,8 l9,5 M21,8 l-9,5",
            "ph-gear" => "M12,8 a4,4 0 1 0 0,8 a4,4 0 1 0 0,-8 M19,12 l2,1 -2,3 -2,-1 M5,12 l-2,1 2,3 2,-1 M12,3 l1,2 2,0 M12,21 l1,-2 2,0 M3,12 l2,-1 0,-2 M21,12 l-2,-1 0,-2",
            "ph-moon" => "M21,13 a9,9 0 1 1 -10,-10 a7,7 0 0 0 10,10 z",
            "ph-arrows-clockwise" => "M4,12 a8,8 0 0 1 14,-5 M20,4 v5 h-5 M20,12 a8,8 0 0 1 -14,5 M4,20 v-5 h5",
            "ph-arrow-counter-clockwise" => "M20,12 a8,8 0 0 0 -14,-5 M4,4 v5 h5 M4,12 a8,8 0 0 0 14,5 M20,20 v-5 h-5",
            "ph-door-open" => "M4,4 h12 v16 h-4 v-6 a4,4 0 0 0 -4,-4 h-4 z M4,4 v16 M16,12 v0.01",
            "ph-ruler" => "M3,17 l14,-14 4,4 -14,14 z M7,13 l2,2 M10,10 l2,2 M13,7 l2,2 M16,4 l2,2",
            "ph-house-line" => "M3,12 l9,-8 9,8 M5,10 v10 h14 v-10 M9,20 v-6 h6 v6",
            "ph-star" => "M12,3 l3,6 6,1 -4.5,4 1,6 -5.5,-3 -5.5,3 1,-6 -4.5,-4 6,-1 z",
            "ph-bug" => "M8,7 a4,4 0 0 1 8,0 v8 a4,4 0 0 1 -8,0 z M5,11 h3 M16,11 h3 M3,8 l3,1 M18,8 l3,1 M9,5 l-1,-2 M15,5 l1,-2",
            "ph-whatsapp-logo" => "M12,3 a9,9 0 0 1 7.5,14 l0.5,5 -5,-1.5 a9,9 0 1 1 -3,-17.5 z",
            _ => "M12,4 a8,8 0 1 0 0,16 a8,8 0 1 0 0,-16",
        };
    }
}
