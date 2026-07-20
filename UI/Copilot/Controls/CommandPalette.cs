using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Slash-command tool palette (design file's binacmd-list). A self-contained
    /// popover surface: search header · Quick access + category sections · footer
    /// legend. Filters live on the query, supports ↑/↓/⏎/Esc, and pin toggling.
    /// UI-only for now — picking a tool raises <see cref="ToolPicked"/>.
    /// </summary>
    public sealed class CommandPalette : Border
    {
        public event Action<SlashTool> ToolPicked;

        private readonly TextBlock _countText;
        private readonly StackPanel _listHost;
        private readonly ScrollViewer _scroller;

        private readonly List<RowInfo> _flat = new List<RowInfo>();  // keyboard order
        private int _active;
        private string _query = "";
        private CopilotPrefs _prefs = CopilotPrefs.Load();

        private sealed class RowInfo { public SlashTool Tool; public Border Row; }

        public CommandPalette()
        {
            // ── chrome ──
            // Small text (11–13px) renders sharp only with Display formatting +
            // pixel snapping; this is inherited by everything built below.
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            var root = new StackPanel();

            // ── search header ──
            var header = new Border { Padding = new Thickness(10, 10, 12, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
            header.SetResourceReference(BorderBrushProperty, "Cp.Line");
            var hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cmdTile = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
            cmdTile.SetResourceReference(BackgroundProperty, "Cp.Sunken");
            var cmdIcon = IconEl("ti-command", 12, "Cp.Muted");   // design var(--text2)
            cmdTile.Child = cmdIcon;
            Grid.SetColumn(cmdTile, 0);
            hg.Children.Add(cmdTile);

            var q = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 12 };
            var slash = new Run("/") { FontWeight = FontWeights.Bold };
            slash.SetResourceReference(Run.ForegroundProperty, "Cp.Accent");
            var qText = new Run("");
            q.Inlines.Add(slash);
            q.Inlines.Add(qText);
            q.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Text");
            _queryText = q; _queryRunText = qText; _slashRun = slash;
            Grid.SetColumn(q, 1);
            hg.Children.Add(q);

            _countText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 10.5 };
            _countText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            Grid.SetColumn(_countText, 2);
            hg.Children.Add(_countText);
            header.Child = hg;
            root.Children.Add(header);

            // ── list ──
            _listHost = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            _scroller = new ScrollViewer
            {
                MaxHeight = 268,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _listHost,
            };
            ApplySlimScrollbar(_scroller);
            root.Children.Add(_scroller);

            // ── footer legend ──
            var footer = new Border { Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
            footer.SetResourceReference(BorderBrushProperty, "Cp.Line");
            var fg = new StackPanel { Orientation = Orientation.Horizontal };
            fg.Children.Add(LegendDot("Cp.Tool.Det", "deterministic"));
            fg.Children.Add(LegendDot("Cp.Tool.Ai", "ai-assisted"));
            fg.Children.Add(LegendDot("Cp.Tool.Rep", "report"));
            var spacer = new Border { Width = 1 };  // pushed apart via DockPanel instead
            var footDock = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(fg, Dock.Left);
            var kbd = new TextBlock { Text = "↑↓ ↵ esc", FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center };
            kbd.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            DockPanel.SetDock(kbd, Dock.Right);
            footDock.Children.Add(kbd);
            footDock.Children.Add(fg);
            footer.Child = footDock;
            root.Children.Add(footer);

            // The card holds ALL the text and carries NO Effect — so WPF never
            // rasterizes the text subtree (that's what softens/blurs it).
            var card = new Border
            {
                CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1),
                ClipToBounds = true, SnapsToDevicePixels = true, Child = root,
            };
            card.SetResourceReference(BackgroundProperty, "Cp.Menu");
            card.SetResourceReference(BorderBrushProperty, "Cp.Line");

            // The drop shadow lives on a SIBLING behind the card (no text inside it),
            // stretched to the same size by the shared Grid cell. Text stays crisp.
            var shadow = new Border { CornerRadius = new CornerRadius(13) };
            shadow.SetResourceReference(BackgroundProperty, "Cp.Menu");
            shadow.Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 7, Opacity = 0.16, Color = Colors.Black };

            var layered = new Grid();
            layered.Children.Add(shadow);
            layered.Children.Add(card);
            Child = layered;
        }

        private TextBlock _queryText;
        private Run _queryRunText;
        private Run _slashRun;

        // ── public API ──────────────────────────────────────────────────────
        public bool HasResults => _flat.Count > 0;
        public string Query => _query;

        /// <summary>Cap the scrolling tool-list region so the whole palette (header
        /// + list + footer) fits inside the host overlay. The host recomputes this
        /// from the panel height so the palette never overruns the pane.</summary>
        public void SetListMaxHeight(double h)
        {
            if (h > 0) _scroller.MaxHeight = System.Math.Max(90, h);
        }

        public void Open(string query)
        {
            _prefs = CopilotPrefs.Load();
            SetQuery(query ?? "");
        }

        public void SetQuery(string query)
        {
            _query = query ?? "";
            _active = 0;
            Rebuild();
        }

        public void MoveActive(int delta)
        {
            if (_flat.Count == 0) return;
            _active = ((_active + delta) % _flat.Count + _flat.Count) % _flat.Count;
            Highlight();
        }

        public SlashTool PickActive()
        {
            if (_active < 0 || _active >= _flat.Count) return null;
            return Pick(_flat[_active].Tool);
        }

        // ── rebuild ─────────────────────────────────────────────────────────
        private void Rebuild()
        {
            _listHost.Children.Clear();
            _flat.Clear();

            string q = _query.Trim().ToLowerInvariant();
            var matches = ToolCatalog.All
                .Where(t => q.Length == 0 || (t.Name + " " + t.Subtitle + " " + t.Keywords).ToLowerInvariant().Contains(q))
                .ToList();

            // header query + count
            if (_slashRun != null)
            {
                _queryRunText.Text = q.Length > 0 ? _query : "";
                _queryRunText.SetResourceReference(Run.ForegroundProperty, q.Length > 0 ? "Cp.Text" : "Cp.Faint");
                if (q.Length == 0) _queryRunText.Text = "search tools…";
            }
            _countText.Text = matches.Count + (matches.Count == 1 ? " result" : " results");

            var groups = new List<KeyValuePair<string, List<SlashTool>>>();
            var matchSet = new HashSet<SlashTool>(matches);

            // Quick access is VIRTUAL — computed from the pin flags, not a real
            // category. It lists the pinned (starred) tools (filtered by the query),
            // in pin order. Pinned tools ALSO stay in their real category below, so a
            // pinned tool appears twice. Hidden entirely when nothing pinned matches.
            var quick = (_prefs.PinnedTools ?? new List<string>())
                .Select(ToolCatalog.ById).Where(t => t != null && matchSet.Contains(t))
                .ToList();
            if (quick.Count > 0)
                groups.Add(new KeyValuePair<string, List<SlashTool>>("Quick access", quick));

            // Real categories, always in this fixed order (ToolCatalog.Categories =
            // General → Architecture → Structure → MEP). Hide any with no matches.
            foreach (var cat in ToolCatalog.Categories)
            {
                var g = matches.Where(t => t.Category == cat).ToList();
                if (g.Count > 0) groups.Add(new KeyValuePair<string, List<SlashTool>>(cat, g));
            }

            if (groups.Count == 0)
            {
                _listHost.Children.Add(EmptyState(_query));
                return;
            }

            foreach (var grp in groups)
            {
                _listHost.Children.Add(SectionHeader(grp.Key));
                foreach (var tool in grp.Value)
                {
                    var row = ToolRow(tool);
                    _flat.Add(new RowInfo { Tool = tool, Row = row });
                    _listHost.Children.Add(row);
                }
            }
            Highlight();
        }

        private void Highlight()
        {
            for (int i = 0; i < _flat.Count; i++)
            {
                var r = _flat[i].Row;
                if (i == _active) r.SetResourceReference(BackgroundProperty, "Cp.Hover");
                else r.Background = Brushes.Transparent;
            }
            if (_active >= 0 && _active < _flat.Count)
                _flat[_active].Row.BringIntoView();
        }

        // ── row + section builders ──────────────────────────────────────────
        // Design section header (line 1484): flex row — muted icon(13) · uppercase
        // category label · a hairline rule filling the rest. gap 7px, pad 9/12/5.
        private Border SectionHeader(string cat)
        {
            var b = new Border { Padding = new Thickness(12, 9, 12, 5) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // icon
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // label
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // rule

            var icon = IconEl(ToolCatalog.SectionIconKey(cat), 13, "Cp.Faint");
            Grid.SetColumn(icon, 0); grid.Children.Add(icon);

            var t = new TextBlock
            {
                Text = cat.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold,
                Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            Grid.SetColumn(t, 1); grid.Children.Add(t);

            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
            line.SetResourceReference(BackgroundProperty, "Cp.Line");
            Grid.SetColumn(line, 2); grid.Children.Add(line);

            b.Child = grid;
            return b;
        }

        private Border ToolRow(SlashTool tool)
        {
            var row = new Border
            {
                Margin = new Thickness(5, 0, 5, 0), Padding = new Thickness(12, 8, 10, 8),
                CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            };
            int myIndex = _flat.Count;
            row.MouseMove += (_, __) => { if (_active != myIndex) { _active = myIndex; Highlight(); } };
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Pick(tool); };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // tile
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // text
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // star

            // icon tile (badge colour, white icon)
            var tile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
            tile.SetResourceReference(BackgroundProperty, ToolCatalog.BadgeKey(tool.Badge));
            tile.Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 1, Opacity = 0.18, Color = Colors.Black };
            tile.Child = IconEl(tool.IconKey, 16, null, Brushes.White);
            Grid.SetColumn(tile, 0); grid.Children.Add(tile);

            // middle (name + badge tag · subtitle)
            var mid = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var line1 = new StackPanel { Orientation = Orientation.Horizontal };
            var name = new TextBlock { Text = tool.Name, FontSize = 12.5, FontWeight = FontWeight.FromOpenTypeWeight(620), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Text");
            line1.Children.Add(name);
            var tag = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            tag.SetResourceReference(BackgroundProperty, ToolCatalog.BadgeBgKey(tool.Badge));
            var tagText = new TextBlock { Text = ToolCatalog.BadgeLabel(tool.Badge).ToUpperInvariant(), FontSize = 8.5, FontWeight = FontWeights.Bold };
            tagText.SetResourceReference(TextBlock.ForegroundProperty, ToolCatalog.BadgeKey(tool.Badge));
            tag.Child = tagText;
            line1.Children.Add(tag);
            mid.Children.Add(line1);
            var sub = new TextBlock { Text = tool.Subtitle, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            mid.Children.Add(sub);
            Grid.SetColumn(mid, 1); grid.Children.Add(mid);

            // star (pin)
            var star = new Button { Width = 26, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, FocusVisualStyle = null };
            star.Template = StarTemplate();
            bool pinned = _prefs.IsPinned(tool.Id);
            star.Content = IconEl(pinned ? "ti-star-filled" : "ti-star", 14, pinned ? null : "Cp.Faint", pinned ? (Brush)ResBrush("Cp.Pin") : null);
            star.ToolTip = pinned ? "Unpin" : "Pin to top";
            star.Click += (_, e) => { e.Handled = true; _prefs.TogglePin(tool.Id); Rebuild(); };
            Grid.SetColumn(star, 2); grid.Children.Add(star);

            row.Child = grid;
            return row;
        }

        private FrameworkElement EmptyState(string query)
        {
            var sp = new StackPanel { Margin = new Thickness(14, 22, 14, 22), HorizontalAlignment = HorizontalAlignment.Center };
            var ic = IconEl("ti-search-off", 20, "Cp.Faint");
            ic.HorizontalAlignment = HorizontalAlignment.Center;
            ic.Opacity = 0.7;
            sp.Children.Add(ic);
            var t = new TextBlock { Text = "No tools match “" + query + "”", FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            sp.Children.Add(t);
            return sp;
        }

        private SlashTool Pick(SlashTool tool)
        {
            if (tool == null) return null;
            _prefs.PushRecent(tool.Id);
            ToolPicked?.Invoke(tool);
            return tool;
        }

        // ── helpers ─────────────────────────────────────────────────────────
        private StackPanel LegendDot(string colorKey, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(BackgroundProperty, colorKey);
            sp.Children.Add(dot);
            var t = new TextBlock { Text = label, FontSize = 9.5, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            sp.Children.Add(t);
            return sp;
        }

        // Icon rendered in a 24×24 canvas inside a Viewbox → consistent framing.
        // strokeKey/strokeBrush colours a stroked icon; fillBrush fills (star-filled).
        private FrameworkElement IconEl(string key, double size, string strokeKey, Brush fillBrush = null)
        {
            bool filled = ToolCatalog.IconFilled(key) || fillBrush != null && strokeKey == null && key == "ti-star-filled";
            var path = new Path
            {
                Data = ToolCatalog.Icon(key),
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            if (ToolCatalog.IconFilled(key)) { path.Fill = fillBrush ?? Brushes.White; }
            else
            {
                if (fillBrush != null) path.Stroke = fillBrush;
                else if (strokeKey != null) path.SetResourceReference(Shape.StrokeProperty, strokeKey);
                else path.Stroke = Brushes.White;
            }
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(path);
            return new Viewbox { Width = size, Height = size, Child = canvas, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        private static Brush ResBrush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

        private static ControlTemplate StarTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            return t;
        }

        private static void ApplySlimScrollbar(ScrollViewer sv)
        {
            var baseStyle = Application.Current?.TryFindResource("Cp.SlimScrollBar") as Style;
            if (baseStyle == null) return;
            sv.Resources[typeof(ScrollBar)] = new Style(typeof(ScrollBar), baseStyle);
        }
    }
}
