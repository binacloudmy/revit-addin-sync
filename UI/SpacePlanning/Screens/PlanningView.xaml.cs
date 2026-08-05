using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot;                    // CopilotTheme (shared chrome)
using RevitWebAppSync.UI.Copilot.Controls;            // ToolHeader (shared chrome)
using RevitWebAppSync.UI.SpacePlanning.Model;
using CpWindows = RevitWebAppSync.UI.SpacePlanning.Windows;

namespace RevitWebAppSync.UI.SpacePlanning.Screens
{
    /// <summary>
    /// Massing / space-planning screen: the Schedule of Accommodation (with the
    /// Malaysian-standard citation behind every number), the candidate block
    /// schemes, the rejected ones, and a draw-only floor-plan preview.
    ///
    /// Rows are built in code-behind (the same approach as ResultView/ChatView)
    /// because the content is fully dynamic; the XAML owns the shell, the section
    /// labels and the action bar.
    ///
    /// Nothing on this screen writes to the Revit document except the Build button,
    /// which the view-model dispatches to Revit's main thread via the MCP job pump.
    /// </summary>
    public partial class PlanningView : UserControl
    {
        private SpacePlanningViewModel Vm => DataContext as SpacePlanningViewModel;
        private SpacePlanningViewModel _hooked;

        public PlanningView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => { HookTheme(); Render(); };
            Unloaded += (_, __) => CopilotTheme.ThemeChanged -= OnThemeChanged;

            RejectedToggle.Checked += (_, __) => SetRejectedOpen(true);
            RejectedToggle.Unchecked += (_, __) => SetRejectedOpen(false);

            // Preview/Build are per-scheme now (see ActionRow) — the footer holds only
            // the standing caveat, so there is no global button to wire here.
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.PropertyChanged -= OnVm;
            _hooked = Vm;
            if (_hooked != null) _hooked.PropertyChanged += OnVm;
            Render();
        }

        private void HookTheme()
        {
            CopilotTheme.ThemeChanged -= OnThemeChanged;
            CopilotTheme.ThemeChanged += OnThemeChanged;
        }

        // Code-drawn rows capture their brushes at build time, so a theme flip has
        // to rebuild them (the XAML chrome re-themes on its own via DynamicResource).
        private void OnThemeChanged() => Dispatcher.Invoke(Render);

        private void OnVm(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SpacePlanningViewModel.Planning):
                    Render();
                    break;
                case nameof(SpacePlanningViewModel.SelectedScheme):
                    RenderSchemes();
                    RenderLevelToggle();
                    RepointPreview();
                    break;
                case nameof(SpacePlanningViewModel.SelectedLevel):
                    RenderLevelToggle();
                    RepointPreview();
                    break;
                case nameof(SpacePlanningViewModel.IsBuildingMassing):
                    // Row buttons carry the label now, so re-render them (they also
                    // disable while a build is in flight) and show the footer status.
                    BuildStatus.Visibility = Vm != null && Vm.IsBuildingMassing
                        ? Visibility.Visible : Visibility.Collapsed;
                    RenderSchemes();
                    break;
            }
        }

        // ── Floating Scheme Preview window ───────────────────────────────────
        // Draw-only, non-modal, owned by the Revit main window. Singleton: a second
        // Preview re-points the open window instead of stacking another one over the
        // viewport (same reuse-or-Activate contract as the JKR dashboard's windows).

        private CpWindows.SchemePreviewWindow _preview;

        private void OpenPreviewWindow()
        {
            var vm = Vm;
            if (vm?.SelectedScheme == null) return;

            if (_preview != null && _preview.IsLoaded)
            {
                _preview.SetScheme(vm.SelectedScheme, vm.SelectedLevel);
                _preview.Activate();
                return;
            }

            try
            {
                _preview = new CpWindows.SchemePreviewWindow();
                _preview.Closed += (_, __) => _preview = null;
                // Keep the pane's level indicator in step when the user switches
                // storey from inside the preview window.
                _preview.LevelPicked += n =>
                {
                    if (Vm != null && Vm.SelectedLevel != n) Vm.SelectedLevel = n;
                };
                try { _preview.Owner = Window.GetWindow(this); } catch { /* pane not rooted yet */ }
                _preview.SetScheme(vm.SelectedScheme, vm.SelectedLevel);
                _preview.Show();
                _preview.SnapToOwner();
            }
            catch
            {
                // A preview that fails to open must never take the pane down with
                // it — the inline plan on this screen is still there.
                _preview = null;
            }
        }

        /// <summary>Keep an already-open preview in step with the pane's selection.</summary>
        private void RepointPreview()
        {
            var vm = Vm;
            if (_preview == null || !_preview.IsLoaded || vm?.SelectedScheme == null) return;
            _preview.SetScheme(vm.SelectedScheme, vm.SelectedLevel);
        }

        private void Render()
        {
            if (SoaHost == null) return;
            var vm = Vm;
            // The full brief stays in the chat thread; here it is only an "this is
            // what I read" echo, so cap it at ~2 lines instead of pushing the whole
            // screen down on a long one.
            Header.SubtitleText = string.IsNullOrWhiteSpace(vm?.PlanningBrief)
                ? "Schedule of accommodation + block schemes"
                : Clip(vm.PlanningBrief, 110);

            RenderStats();
            RenderSoa();
            RenderSchemes();
            RenderRejected();
            RenderLevelToggle();
        }

        // ══════════ (top) orienting numbers ══════════

        private void RenderStats()
        {
            StatsHost.Children.Clear();
            var vm = Vm;
            var stats = vm?.Planning?.Stats;
            var soa = vm?.PlanningSoa;
            if (soa == null) { StatsCard.Visibility = Visibility.Collapsed; return; }
            StatsCard.Visibility = Visibility.Visible;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(Caption("TARGET GFA"));
            left.Children.Add(new TextBlock
            {
                Text = $"{soa.TotalGfaM2:N0} m²",
                FontSize = 18, FontWeight = FontWeights.SemiBold,
                Foreground = Ink(), Margin = new Thickness(0, 1, 0, 0),
            });
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            if (stats != null)
            {
                var right = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
                right.Children.Add(new TextBlock
                {
                    Text = $"{stats.PassingCount} of {stats.SchemeCount} schemes meet it",
                    FontSize = 11.5, Foreground = Muted(),
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
                if (stats.BestMarginM2.HasValue)
                    right.Children.Add(new TextBlock
                    {
                        Text = $"best headroom +{stats.BestMarginM2.Value:N0} m²",
                        FontSize = 11, Foreground = Faint(),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 1, 0, 0),
                    });
                Grid.SetColumn(right, 1);
                row.Children.Add(right);
            }
            StatsHost.Children.Add(row);

            // ── PROGRAM READ chips ───────────────────────────────────────────
            // Site area / setback / building type, per the design. Each chip is
            // rendered ONLY when the backend actually supplied it: site and setback
            // are null unless the brief stated them or the caller sent them, and a
            // placeholder figure on a screen whose whole credibility rests on cited
            // standards would poison every real number next to it.
            var result = Vm?.Planning;
            if (result == null) return;

            var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            if (result.SiteAreaM2.HasValue) chips.Children.Add(ReadChip("SITE", $"{result.SiteAreaM2.Value:N0} m²"));
            if (result.SetbackM.HasValue) chips.Children.Add(ReadChip("SETBACK", $"{result.SetbackM.Value:N0} m"));
            var typeLabel = result.BuildingTypeLabel;
            if (!string.IsNullOrEmpty(typeLabel)) chips.Children.Add(ReadChip("TYPE", typeLabel));

            if (chips.Children.Count == 0) return;
            StatsHost.Children.Add(new Border
            {
                BorderBrush = Line(), BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 9, 0, 0), Child = chips,
            });
        }

        /// <summary>One PROGRAM READ chip: small caption over its figure.</summary>
        private UIElement ReadChip(string caption, string value)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
            box.Children.Add(Caption(caption));
            box.Children.Add(new TextBlock
            {
                Text = value, FontSize = 12.5, FontWeight = FontWeights.Medium,
                Foreground = Ink(), Margin = new Thickness(0, 1, 0, 0),
            });
            return box;
        }

        // ══════════ (a) Schedule of Accommodation ══════════

        private void RenderSoa()
        {
            SoaHost.Children.Clear();
            NotesHost.Children.Clear();
            var soa = Vm?.PlanningSoa;
            if (soa == null) return;

            foreach (var item in soa.Items)
            {
                SoaHost.Children.Add(SoaRow(item));
                // The auto-derived sanitary breakdown belongs to the tandas row —
                // that adjacency IS the point being demonstrated (count → UBBL).
                if (string.Equals(item.Key, "tandas", StringComparison.OrdinalIgnoreCase) && soa.Sanitary.Count > 0)
                    SoaHost.Children.Add(SanitaryBlock(soa.Sanitary));
            }

            // Sanitary data with no tandas line to hang it off — show it standalone.
            if (soa.Sanitary.Count > 0 &&
                !soa.Items.Any(i => string.Equals(i.Key, "tandas", StringComparison.OrdinalIgnoreCase)))
                SoaHost.Children.Add(SanitaryBlock(soa.Sanitary));

            // Total, separated by a hairline so it reads as a sum and not a row.
            SoaHost.Children.Add(new Border
            {
                BorderBrush = Line(), BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(2, 6, 2, 0), Padding = new Thickness(0, 8, 0, 0),
                Child = TwoColumn(
                    new TextBlock { Text = "Total GFA", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ink() },
                    Number($"{soa.TotalGfaM2:N0} m²", 12.5, Ink(), FontWeights.SemiBold)),
            });

            if (!string.IsNullOrWhiteSpace(soa.Notes))
                NotesHost.Children.Add(Disclaimer(soa.Notes));
        }

        /// <summary>"7.2 × 9.0 m" for an SOA key, read off the drawn scheme so it is
        /// the rectangle actually placed. Null when the scheme has no room of that
        /// type (the SOA and the layout can legitimately differ — the padang is in
        /// the schedule and is never built).
        ///
        /// SOA keys and room types differ by design: the schedule is named for the
        /// PROGRAM ("bilik_darjah") and the layout for the DRAWN type ("kelas").</summary>
        private string DimensionsFor(string soaKey)
        {
            var scheme = Vm?.SelectedScheme;
            if (scheme?.Rooms == null || string.IsNullOrWhiteSpace(soaKey)) return null;

            string type;
            switch (soaKey.ToLowerInvariant())
            {
                case "bilik_darjah": type = "kelas"; break;
                case "bilik_sokongan": type = "sokongan"; break;
                case "tandas": type = "tandas"; break;
                case "perhimpunan": type = "perhimpunan"; break;
                case "kantin": type = "kantin"; break;
                case "padang": type = "padang"; break;
                default: type = soaKey; break;
            }

            var room = scheme.Rooms.FirstOrDefault(
                r => r != null && string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase));
            return room == null ? null : $"{room.W:N1} × {room.H:N1} m";
        }

        private FrameworkElement SoaRow(SoaItem item)
        {
            var outer = new StackPanel { Margin = new Thickness(2, 0, 2, 10) };

            var title = new TextBlock
            {
                Text = item.LabelMs ?? item.Key ?? "",
                FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = Ink(),
                TextWrapping = TextWrapping.Wrap,
            };
            // A site-only line (the padang) has no floor area. Printing "0 m²"
            // beside it reads as a bug; say what it actually is.
            var figure = item.TotalAreaM2 > 0
                ? Number($"{item.TotalAreaM2:N0} m²", 12.5, Ink())
                : Number("site only", 11.5, Faint());
            outer.Children.Add(TwoColumn(title, figure));

            // count × unit area, and which storey(s) it occupies. Uses LevelLabel so a
            // space spanning storeys reads "Tingkat 1–2" rather than dropping the
            // level entirely (the single `level` field is null for spanning spaces).
            // Dimensions, not just an area. "64.8 m²" tells a drafter the size;
            // "7.2 × 9.0 m" tells them the SHAPE, which is what they actually need
            // to judge whether a bay works. Taken from the drawn scheme, so it is
            // the real rectangle rather than a nominal figure.
            // The STANDARD's bay first, the drawn scheme only as a fallback. Reading
            // it off the layout meant the dimensions disappeared whenever no scheme
            // passed — precisely when the drafter most needs to see what is being
            // asked for (reported 2026-08-05 on a site 1.4 m too shallow).
            var dims = item.BayLabel ?? DimensionsFor(item.Key);
            var sub = item.Count > 0
                ? $"{item.Count} × {item.UnitAreaM2:N1} m²"
                : $"{item.UnitAreaM2:N1} m²";
            if (dims != null) sub += $"  ({dims})";
            var lvl = item.LevelLabel;
            if (!string.IsNullOrEmpty(lvl)) sub += " · " + lvl;
            outer.Children.Add(new TextBlock
            {
                Text = sub, FontSize = 11, Foreground = Muted(), Margin = new Thickness(0, 1, 0, 0),
            });

            // Citation + advisory. Both carry TEXT — never colour alone. A WrapPanel
            // (not a StackPanel) because long clauses like the sokongan list wrap to
            // two lines; the short advisory pill goes FIRST so it can't get orphaned
            // half-way down a wrapped chip.
            var chips = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            if (item.Advisory)
                chips.Children.Add(AdvisoryBadge());
            if (!string.IsNullOrWhiteSpace(item.Citation))
                chips.Children.Add(CitationChip(item.Citation));
            if (chips.Children.Count > 0) outer.Children.Add(chips);

            return outer;
        }

        private FrameworkElement SanitaryBlock(List<FixtureReq> fixtures)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "Auto-derived sanitary provision",
                FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Accent(),
                Margin = new Thickness(0, 0, 0, 5),
            });

            var wrap = new WrapPanel();
            foreach (var f in fixtures)
                wrap.Children.Add(FixtureChip(f));
            stack.Children.Add(wrap);

            var cite = fixtures.Select(f => Cite(f)).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            if (!string.IsNullOrWhiteSpace(cite))
                stack.Children.Add(new TextBlock
                {
                    Text = cite, FontSize = 10.5, Foreground = Faint(),
                    Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
                });

            // Inset + accent edge so it reads as belonging to the row above it.
            return new Border
            {
                Background = BlueSoftBg(),
                BorderBrush = Accent(), BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 8, 8, 0),
                Padding = new Thickness(10, 8, 10, 9),
                Margin = new Thickness(10, 0, 2, 12),
                Child = stack,
            };
        }

        private static string Cite(FixtureReq f) =>
            string.IsNullOrWhiteSpace(f.Clause) ? f.Source
            : string.IsNullOrWhiteSpace(f.Source) ? f.Clause
            : f.Source + " · " + f.Clause;

        private FrameworkElement FixtureChip(FixtureReq f)
        {
            var text = new TextBlock { FontSize = 11, Foreground = Ink() };
            text.Inlines.Add(new System.Windows.Documents.Run(FixtureLabel(f))
            { Foreground = Muted() });
            text.Inlines.Add(new System.Windows.Documents.Bold(
                new System.Windows.Documents.Run(" " + f.Count.ToString())));
            return new Border
            {
                Background = ChipBg(), CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 3, 9, 4), Margin = new Thickness(0, 0, 6, 6),
                Child = text,
            };
        }

        // Spelled out rather than ♂/♀ glyphs: symbol-only labels don't read for
        // screen readers and render inconsistently across Windows font fallbacks.
        private static string FixtureLabel(FixtureReq f)
        {
            string kind = (f.Fixture ?? "").ToLowerInvariant() switch
            {
                "wc" => "WC",
                "urinal" => "Urinal",
                "wash_basin" => "Wash basin",
                _ => f.Fixture ?? "Fixture",
            };
            string who = (f.Gender ?? "").ToLowerInvariant() switch
            {
                "male" => " (lelaki)",
                "female" => " (perempuan)",
                "all" => "",
                _ => "",
            };
            return kind + who;
        }

        // ══════════ (b) Scheme cards ══════════

        private void RenderSchemes()
        {
            if (SchemesHost == null) return;
            SchemesHost.Children.Clear();
            var vm = Vm;
            var schemes = vm?.PlanningSchemes ?? new List<MassingScheme>();

            SchemesLabel.Text = schemes.Count == 1 ? "SCHEME" : $"SCHEMES ({schemes.Count})";

            if (schemes.Count == 0)
            {
                // Say what actually happened, with the numbers, and name the RIGHT
                // cause. This used to blame the GFA target unconditionally ("the
                // generator lays out two storeys — reduce the brief"), copy written
                // before the site fit check existed. Once a real boundary was read
                // out of the model the usual reason became exceeds_site, and the
                // pane was confidently telling drafters to shrink a brief that fitted
                // perfectly well — the land was simply too small (2026-08-05).
                var rejected = vm?.PlanningRejected ?? new List<RejectedScheme>();
                bool siteBound = rejected.Count > 0
                    && rejected.Count(r => r?.Reason == "exceeds_site") * 2 >= rejected.Count;

                var why = new System.Text.StringBuilder();
                string title;

                if (siteBound)
                {
                    title = "The site is too small for this brief";
                    var detail = rejected.FirstOrDefault(r => r?.Reason == "exceeds_site"
                                                             && !string.IsNullOrWhiteSpace(r.Detail));
                    if (detail != null) why.Append("The smallest candidate ").Append(detail.Detail).Append(". ");
                    why.Append("Draw a larger property line, reduce the class count, "
                             + "or untick \u201cFit to the site boundary\u201d on the brief to plan "
                             + "without it.");
                }
                else
                {
                    title = "No scheme met the target GFA";
                    double target = vm?.PlanningSoa?.TotalGfaM2 ?? 0;
                    double best = 0;
                    foreach (var r in rejected)
                        if (r != null && r.TotalGfaM2 > best) best = r.TotalGfaM2;
                    why.Append(rejected.Count > 0
                        ? $"All {rejected.Count} candidates fell short"
                        : "No candidate met the brief");
                    if (target > 0) why.Append($" of the {target:N0} m² target");
                    if (best > 0) why.Append($"; the largest reached {best:N0} m²");
                    why.Append(". Reduce the brief, or split it across blocks.");
                }

                SchemesHost.Children.Add(EmptyState(title, why.ToString()));
                return;
            }

            // A warning carried by EVERY scheme is a property of the generator, not
            // of any one candidate — the circulation notice was printing verbatim on
            // all three cards, which is a third of the screen saying one thing three
            // times. Hoist those above the list and show each card only what is
            // actually particular to it.
            _sharedWarnings = schemes.Count > 1
                ? new HashSet<string>(
                    schemes[0].Warnings ?? new List<string>(), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var scheme in schemes.Skip(1))
                _sharedWarnings.IntersectWith(scheme.Warnings ?? new List<string>());

            foreach (var shared in _sharedWarnings)
                SchemesHost.Children.Add(WarningLine(shared, allSchemes: true));

            foreach (var scheme in schemes)
                SchemesHost.Children.Add(SchemeCard(scheme, ReferenceEquals(scheme, vm.SelectedScheme)));
        }

        /// <summary>Warnings every scheme shares — rendered once above the list
        /// instead of repeated on each card. Rebuilt on every RenderSchemes.</summary>
        private HashSet<string> _sharedWarnings = new HashSet<string>(StringComparer.Ordinal);

        private FrameworkElement SchemeCard(MassingScheme scheme, bool selected)
        {
            var body = new StackPanel();

            // Title row: id pill · title · selected check
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pill = new Border
            {
                Background = selected ? Accent() : ChipBg(),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = scheme.Id ?? "?", FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = selected ? OnAccent() : Muted(),
                },
            };
            Grid.SetColumn(pill, 0);
            head.Children.Add(pill);

            var title = new TextBlock
            {
                Text = scheme.Title ?? "Scheme", FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = Ink(), Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(title, 1);
            head.Children.Add(title);

            // "In plan" chip — which scheme the preview is currently drawing. Text,
            // not colour alone, so it survives greyscale and screen readers.
            var headRight = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (selected)
            {
                headRight.Children.Add(new Border
                {
                    Background = BlueSoftBg(),
                    BorderBrush = Accent(),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "In plan", FontSize = 10, FontWeight = FontWeights.SemiBold,
                        Foreground = Accent(),
                    },
                });
            }
            headRight.Children.Add(Chevron(scheme));
            Grid.SetColumn(headRight, 2);
            head.Children.Add(headRight);
            body.Children.Add(head);

            // GFA + margin badge
            var figures = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            figures.Children.Add(Number($"{scheme.TotalGfaM2:N0} m² GFA", 11.5, Muted()));
            figures.Children.Add(MarginBadge(scheme));
            body.Children.Add(figures);

            // What the GFA is MADE OF. The margin compares program against the
            // target, so the circulation has to be visible somewhere or the GFA
            // headline and the margin look like they contradict each other.
            if (scheme.CirculationM2 > 0)
                body.Children.Add(new TextBlock
                {
                    Text = $"program {scheme.ProgramAreaM2:N0} m² + circulation "
                         + $"{scheme.CirculationM2:N0} m² ({scheme.CirculationPct:P0})",
                    FontSize = 11, Foreground = Faint(), Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });

            // Per-level breakdown — what the L1/L2 toggle will show.
            var levels = scheme.Levels();
            if (levels.Count > 0)
                body.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", levels.Select(l => $"T{l} {scheme.LevelArea(l):N0} m²")),
                    FontSize = 11, Foreground = Faint(), Margin = new Thickness(0, 4, 0, 0),
                });

            foreach (var warning in scheme.Warnings ?? new List<string>())
                if (!_sharedWarnings.Contains(warning))       // hoisted above the list
                    body.Children.Add(WarningLine(warning));

            // Expanded per-storey detail (chevron). Collapsed by default so three
            // cards stay scannable.
            if (IsExpanded(scheme))
                body.Children.Add(LevelDetail(scheme));

            // Per-scheme actions, per the design: Preview draws this scheme, Build
            // commits it. Both act on THIS row rather than on a separate selection,
            // so there is no "which one am I building?" ambiguity.
            body.Children.Add(ActionRow(scheme));

            var card = new Button
            {
                Style = (Style)FindResource("PlanCard"),
                Content = body,
                Background = selected ? BlueSoftBg() : Brush("Cp.Menu"),
                BorderBrush = selected ? Accent() : Line(),
                BorderThickness = new Thickness(selected ? 1.6 : 1),
                Command = Vm?.SelectSchemeCommand,
                CommandParameter = scheme,
            };
            // Hovering a scheme redraws the preview WITHOUT changing the selection —
            // the design's "hovering a scheme redraws the preview only". Moving the
            // mouse must never commit a state change the user didn't ask for, so this
            // only re-points an already-open preview window.
            card.MouseEnter += (_, __) => PeekPreview(scheme);
            card.MouseLeave += (_, __) => RepointPreview();

            // Screen readers announce the whole card, not the individual runs.
            card.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                $"Scheme {scheme.Id}, {scheme.Title}, {scheme.TotalGfaM2:N0} square metres GFA, " +
                (scheme.MeetsGfa ? "meets target" : "below target") + (selected ? ", shown in plan" : ""));
            return card;
        }

        // ── Per-scheme expand / actions ──────────────────────────────────────

        // Expanded rows survive a re-render (RenderSchemes rebuilds every card), so
        // the open set is keyed by scheme id rather than held on the control.
        private readonly HashSet<string> _expanded = new HashSet<string>();

        private static string KeyOf(MassingScheme s) => s?.Id ?? s?.Title ?? "";
        private bool IsExpanded(MassingScheme s) => _expanded.Contains(KeyOf(s));

        private FrameworkElement Chevron(MassingScheme scheme)
        {
            bool open = IsExpanded(scheme);
            var path = new Path
            {
                Width = 10, Height = 10, Stretch = Stretch.Uniform,
                Stroke = Muted(), StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M9,6 l6,6 -6,6"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(open ? 90 : 0),
            };
            var btn = new Button
            {
                Content = path,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(7, 4, 4, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = BareTemplate(),
            };
            System.Windows.Automation.AutomationProperties.SetName(
                btn, (open ? "Hide" : "Show") + $" storey breakdown for scheme {scheme.Id}");
            btn.Click += (_, e) =>
            {
                // Stop the click reaching the card, or expanding would also re-select.
                e.Handled = true;
                var k = KeyOf(scheme);
                if (!_expanded.Remove(k)) _expanded.Add(k);
                RenderSchemes();
            };
            return btn;
        }

        private FrameworkElement LevelDetail(MassingScheme scheme)
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var level in scheme.Levels())
            {
                var rooms = (scheme.Rooms ?? new List<MassingRoom>()).Where(r => r != null && r.Level == level).ToList();
                var box = new StackPanel();
                box.Children.Add(TwoColumn(
                    new TextBlock
                    {
                        Text = "Tingkat " + level, FontSize = 11.5, FontWeight = FontWeights.Bold,
                        Foreground = Accent(),
                    },
                    Number($"{scheme.LevelArea(level):N0} m²", 11.5, Muted())));

                // Room mix for this storey, by type, using the palette's own labels so
                // the wording matches the plan legend exactly.
                var mix = rooms.GroupBy(r => MassingPalette.For(r.Type))
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} × {g.Key.Label}");
                box.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", mix), FontSize = 10.5, Foreground = Faint(),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                });

                wrap.Children.Add(new Border
                {
                    Background = Brush("Cp.Sunken"),
                    BorderBrush = Line(), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(9, 7, 9, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = box,
                });
            }
            return wrap;
        }

        private FrameworkElement ActionRow(MassingScheme scheme)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 9, 0, 0),
            };
            row.Children.Add(RowButton(scheme, "Preview", accent: false, onClick: () =>
            {
                Vm?.SelectSchemeCommand?.Execute(scheme);
                OpenPreviewWindow();
            }));
            bool building = Vm != null && Vm.IsBuildingMassing;
            row.Children.Add(RowButton(scheme, building ? "Building…" : "Build", accent: true, enabled: !building, onClick: () =>
            {
                // Select first: BuildMassingAsync reads SelectedScheme, so a row's
                // Build must never commit whatever happened to be selected before.
                Vm?.SelectSchemeCommand?.Execute(scheme);
                var cmd = Vm?.BuildMassingCommand;
                if (cmd != null && cmd.CanExecute(null)) cmd.Execute(null);
            }));
            return row;
        }

        private Button RowButton(
            MassingScheme scheme, string label, bool accent, Action onClick, bool enabled = true)
        {
            var btn = new Button
            {
                Content = new TextBlock
                {
                    Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = accent ? OnAccent() : Ink(),
                },
                Background = accent ? Accent() : Brush("Cp.Menu"),
                BorderBrush = accent ? Accent() : Brush("Cp.Hair2"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 5, 12, 6),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = ChipTemplate(),
                IsEnabled = enabled,
                Opacity = enabled ? 1.0 : 0.55,
            };
            System.Windows.Automation.AutomationProperties.SetName(
                btn, $"{label} scheme {scheme.Id}, {scheme.Title}");
            btn.Click += (_, e) => { e.Handled = true; onClick(); };
            return btn;
        }

        private static ControlTemplate _bare, _chip;

        /// <summary>Chromeless button (icon hit-target only).</summary>
        private static ControlTemplate BareTemplate()
        {
            if (_bare != null) return _bare;
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, TemplateBind("Background"));
            b.SetBinding(Border.PaddingProperty, TemplateBind("Padding"));
            b.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            _bare = new ControlTemplate(typeof(Button)) { VisualTree = b };
            _bare.Seal();
            return _bare;
        }

        /// <summary>Rounded action button. The default Button chrome would paint a
        /// grey box over the accent fill.</summary>
        private static ControlTemplate ChipTemplate()
        {
            if (_chip != null) return _chip;
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            b.SetBinding(Border.BackgroundProperty, TemplateBind("Background"));
            b.SetBinding(Border.BorderBrushProperty, TemplateBind("BorderBrush"));
            b.SetBinding(Border.BorderThicknessProperty, TemplateBind("BorderThickness"));
            b.SetBinding(Border.PaddingProperty, TemplateBind("Padding"));
            b.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            b.AppendChild(cp);
            _chip = new ControlTemplate(typeof(Button)) { VisualTree = b };
            _chip.Seal();
            return _chip;
        }

        private static System.Windows.Data.Binding TemplateBind(string path) =>
            new System.Windows.Data.Binding(path)
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent };

        /// <summary>Hover-preview: re-point an OPEN preview window at the hovered
        /// scheme. Deliberately does nothing when the window is closed — hovering a
        /// card should not spawn a window over the Revit viewport.</summary>
        private void PeekPreview(MassingScheme scheme)
        {
            if (_preview == null || !_preview.IsLoaded || scheme == null) return;
            _preview.SetScheme(scheme, Vm?.SelectedLevel ?? 1);
        }

        private FrameworkElement MarginBadge(MassingScheme scheme)
        {
            bool ok = scheme.MeetsGfa;
            string sign = scheme.MarginM2 >= 0 ? "+" : "−";
            // The margin is PROGRAM vs target. With no explicit target the target
            // IS the SOA total, so a scheme that placed the schedule exactly lands
            // on zero — say that, rather than showing a bare "+0 m²" that reads
            // like a bug. A real number only appears when the drafter typed a
            // target of their own, which is the case where it means something.
            string text = Math.Abs(scheme.MarginM2) < 0.5
                ? "program matches target"
                : $"{sign}{Math.Abs(scheme.MarginM2):N0} m² vs target";
            return new Border
            {
                Background = ok ? Brush("Cp.OkBg") : Brush("Cp.Tool.RepBg"),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(7, 2, 8, 3),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text, FontSize = 10.5, FontWeight = FontWeights.Medium,
                    Foreground = ok ? Brush("Cp.Green") : Brush("Cp.Amber"),
                },
            };
        }

        /// <summary><paramref name="allSchemes"/> marks a warning hoisted above the
        /// list because every candidate carries it — it is prefixed so the reader
        /// knows it is not about the card underneath it.</summary>
        private FrameworkElement WarningLine(string message, bool allSchemes = false)
        {
            if (allSchemes) message = "All schemes: " + message;
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, allSchemes ? 10 : 0),
            };
            row.Children.Add(new Path
            {
                Width = 11, Height = 11, Stretch = Stretch.Uniform,
                Stroke = Brush("Cp.Amber"), StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                Data = CopilotIcons.Get("warning"),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = message, FontSize = 11, Foreground = Brush("Cp.Amber"),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 320,
            });
            return row;
        }

        // ══════════ (c) Rejected ══════════

        private void RenderRejected()
        {
            RejectedHost.Children.Clear();
            var rejected = Vm?.PlanningRejected ?? new List<RejectedScheme>();
            RejectedBlock.Visibility = rejected.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (rejected.Count == 0) return;

            RejectedSummary.Text = rejected.Count == 1
                ? "1 scheme rejected"
                : $"{rejected.Count} schemes rejected";

            foreach (var r in rejected)
            {
                var row = new StackPanel { Margin = new Thickness(17, 0, 2, 8) };
                row.Children.Add(TwoColumn(
                    new TextBlock
                    {
                        Text = $"{r.Id} · {r.Title}", FontSize = 11.5, Foreground = Muted(),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    Number($"{r.TotalGfaM2:N0} m²", 11.5, Muted())));
                row.Children.Add(new TextBlock
                {
                    Text = $"{r.ReasonLabel} — short by {Math.Abs(r.GapM2):N0} m²",
                    FontSize = 10.5, Foreground = Faint(), Margin = new Thickness(0, 1, 0, 0),
                });
                RejectedHost.Children.Add(row);
            }
        }

        private void SetRejectedOpen(bool open)
        {
            RejectedHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            // Chevron: right when closed, down when open.
            RejectedCaret.RenderTransformOrigin = new Point(0.5, 0.5);
            RejectedCaret.RenderTransform = new RotateTransform(open ? 90 : 0);
        }

        // ══════════ (e) Level toggle ══════════

        private void RenderLevelToggle()
        {
            if (LevelToggleHost == null) return;
            LevelToggleHost.Children.Clear();
            var vm = Vm;
            var levels = vm?.SelectedSchemeLevels ?? new List<int>();
            // One storey → no choice to offer; hide the control rather than show a
            // single dead segment.
            LevelToggleWrap.Visibility = levels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (levels.Count <= 1) return;

            foreach (var level in levels)
            {
                bool active = level == vm.SelectedLevel;
                var btn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = "T" + level,
                        FontSize = 11.5,
                        FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium,
                        Foreground = active ? OnAccent() : Muted(),
                    },
                    Background = active ? Accent() : Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    // 44px-ish hit area in a 440px pane: 34×26 is the pane's
                    // established control height (see the tab bar) — keep parity.
                    MinWidth = 34, Height = 26,
                    Padding = new Thickness(9, 0, 9, 0),
                    Command = vm.SelectLevelCommand,
                    CommandParameter = level,
                    Template = SegmentTemplate(),
                };
                btn.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                    $"Show Tingkat {level}" + (active ? " (showing)" : ""));
                LevelToggleHost.Children.Add(btn);
            }
        }

        private static ControlTemplate _segment;
        private static ControlTemplate SegmentTemplate()
        {
            if (_segment != null) return _segment;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            _segment = new ControlTemplate(typeof(Button)) { VisualTree = border };
            _segment.Seal();
            return _segment;
        }

        // ══════════ shared bits ══════════

        private FrameworkElement TwoColumn(FrameworkElement left, FrameworkElement right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(left, 0);
            right.Margin = new Thickness(10, 0, 0, 0);
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            grid.Children.Add(left);
            grid.Children.Add(right);
            return grid;
        }

        /// <summary>Right-aligned figure with tabular digits so the m² column
        /// doesn't wobble between rows.</summary>
        private TextBlock Number(string text, double size, Brush brush, FontWeight? weight = null)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = size, Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            if (weight.HasValue) tb.FontWeight = weight.Value;
            System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
            return tb;
        }

        /// <summary>Trim at a word boundary so the echo never cuts mid-word.</summary>
        private static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            int cut = text.LastIndexOf(' ', Math.Min(max, text.Length - 1));
            return (cut > max / 2 ? text.Substring(0, cut) : text.Substring(0, max)).TrimEnd(',', ' ') + "…";
        }

        private TextBlock Caption(string text) => new TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = Faint(),
        };

        private FrameworkElement CitationChip(string text) => new Border
        {
            Background = ChipBg(),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(7, 2, 8, 3),
            Margin = new Thickness(0, 0, 6, 4),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = text, FontSize = 10.5, Foreground = Muted(),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 290,
            },
        };

        /// <summary>"advisory" pill. Amber + the word itself — the colour is never
        /// the only carrier of the meaning.</summary>
        private FrameworkElement AdvisoryBadge() => new Border
        {
            Background = Brush("Cp.Tool.RepBg"),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(7, 2, 8, 3),
            Margin = new Thickness(0, 0, 6, 4),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "advisory", FontSize = 10.5, FontWeight = FontWeights.Medium,
                Foreground = Brush("Cp.Amber"),
            },
        };

        private FrameworkElement Disclaimer(string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 2, 0) };
            row.Children.Add(new Path
            {
                Width = 11, Height = 11, Stretch = Stretch.Uniform,
                Stroke = Faint(), StrokeThickness = 1.6,
                Data = Geometry.Parse("M12,16 v-4 M12,8 v0.01 M12,3 a9,9 0 1 0 0.01,0 z"),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = text, FontSize = 11, Foreground = Faint(),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
            });
            return row;
        }

        private FrameworkElement EmptyState(string title, string body)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ink(),
                TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(new TextBlock
            {
                Text = body, FontSize = 11.5, Foreground = Muted(), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
            return new Border
            {
                Background = Brush("Cp.Sunken"), BorderBrush = Line(), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 11, 12, 12),
                Child = stack,
            };
        }

        // Theme brushes. Resolved per render (not cached) so a theme flip repaints.
        private Brush Brush(string key) => CopilotTheme.Brush(key);
        private Brush Ink() => Brush("Cp.Ink");
        private Brush Muted() => Brush("Cp.Muted");
        private Brush Faint() => Brush("Cp.Faint");
        private Brush Line() => Brush("Cp.Line");
        private Brush Accent() => Brush("Cp.Accent");
        private Brush OnAccent() => Brush("Cp.AccentContrast");
        private Brush ChipBg() => Brush("Cp.Sunken");
        private Brush BlueSoftBg() => Brush("Cp.PurpleSoft");
    }
}
