using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Events;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using Point = System.Windows.Point;
using Visibility = System.Windows.Visibility;

namespace RevitWebAppSync.UI
{
    public partial class CostDashboardPanel : Page
    {
        private UIApplication _uiApp;
        private List<CostItem> _allItems = new List<CostItem>();
        private CostSummary _summary;
        private PriceDatabase _priceDb;
        private bool _showByLevel = true;

        private double _previousTotal;
        private int _previousItemCount;
        private DispatcherTimer _bannerAutoHideTimer;
        private readonly List<string> _recentChanges = new List<string>();
        private const int MaxRecentChanges = 5;

        // UI references
        private TextBlock _subtitleText;
        private TextBlock _grandTotalText;
        private TextBlock _markupNoteText;
        private TextBlock _estimateRangeText;
        private double _appliedMarkupPct;
        private TextBlock _itemCountText;
        private TextBlock _levelCountText;
        private TextBlock _pricedPercentText;
        private Border _changeBanner;
        private TextBlock _changeText;
        private TextBlock _changeDeltaText;
        private RadioButton _byLevelRadio;
        private RadioButton _byCategoryRadio;
        private ComboBox _levelFilter;
        private StackPanel _contentPanel;
        private Border _totalCard;
        private ProgressBar _coverageBar;

        // Revit-style colors (light mode)
        private static readonly Color PrimaryBlue = Color.FromRgb(0, 120, 215);       // #0078D7
        private static readonly Color HeaderBg = Color.FromRgb(0, 99, 177);            // #0063B1
        private static readonly Color CardBg = Color.FromRgb(255, 255, 255);           // White
        private static readonly Color PageBg = Color.FromRgb(241, 241, 241);           // #F1F1F1
        private static readonly Color BorderColor = Color.FromRgb(217, 217, 217);      // #D9D9D9
        private static readonly Color TextPrimary = Color.FromRgb(51, 51, 51);         // #333333
        private static readonly Color TextSecondary = Color.FromRgb(102, 102, 102);    // #666666
        private static readonly Color TextMuted = Color.FromRgb(153, 153, 153);        // #999999
        private static readonly Color SuccessGreen = Color.FromRgb(16, 124, 16);       // #107C10
        private static readonly Color WarningAmber = Color.FromRgb(255, 140, 0);       // #FF8C00
        private static readonly Color RowHover = Color.FromRgb(235, 243, 252);         // #EBF3FC
        private static readonly Color RowAlt = Color.FromRgb(248, 248, 248);           // #F8F8F8

        // Level colors for fallback
        private static readonly Color[] LevelColors = {
            Color.FromRgb(0, 120, 215),
            Color.FromRgb(16, 124, 16),
            Color.FromRgb(255, 140, 0),
            Color.FromRgb(156, 39, 176),
            Color.FromRgb(233, 30, 99),
            Color.FromRgb(0, 150, 136),
            Color.FromRgb(255, 87, 34),
            Color.FromRgb(63, 81, 181),
        };

        public CostDashboardPanel()
        {
            InitializeComponent();
            BuildUI();
            _bannerAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _bannerAutoHideTimer.Tick += (s, e) => { _bannerAutoHideTimer.Stop(); _changeBanner.Visibility = Visibility.Collapsed; };
        }

        private void BuildUI()
        {
            var root = new Grid { Background = new SolidColorBrush(PageBg) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Banner
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Total
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Filter
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Actions

            // ── Row 0: Header ──
            var header = new Border
            {
                Background = new LinearGradientBrush(HeaderBg, Color.FromRgb(0, 78, 140), new Point(0, 0), new Point(1, 0)),
                Padding = new Thickness(16, 12, 16, 12)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerLeft = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = "BINA",
                FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 6, 0)
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = "Cost Tracker",
                FontSize = 16, FontWeight = FontWeights.Light, Foreground = new SolidColorBrush(Color.FromRgb(180, 210, 240))
            });
            headerLeft.Children.Add(titleRow);
            _subtitleText = new TextBlock
            {
                Text = "No model loaded",
                FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(150, 190, 230)),
                Margin = new Thickness(0, 2, 0, 0)
            };
            headerLeft.Children.Add(_subtitleText);
            Grid.SetColumn(headerLeft, 0);
            headerGrid.Children.Add(headerLeft);

            // Version badge
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock { Text = "v1.0", FontSize = 9, Foreground = Brushes.White };
            Grid.SetColumn(badge, 1);
            headerGrid.Children.Add(badge);

            header.Child = headerGrid;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ── Row 1: Change banner ──
            _changeBanner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(223, 246, 221)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(16, 124, 16)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 6, 12, 6),
                Visibility = Visibility.Collapsed,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _changeBanner.MouseLeftButtonUp += ChangeBanner_Click;
            var bannerGrid = new Grid();
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var bannerIcon = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(SuccessGreen),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
            };
            bannerIcon.Child = new TextBlock { Text = "!", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(bannerIcon, 0);
            bannerGrid.Children.Add(bannerIcon);

            var bannerTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _changeText = new TextBlock { Text = "Model updated", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(30, 80, 30)), FontWeight = FontWeights.Medium };
            _changeDeltaText = new TextBlock { Text = "", FontSize = 10, Foreground = new SolidColorBrush(TextSecondary) };
            bannerTextStack.Children.Add(_changeText);
            bannerTextStack.Children.Add(_changeDeltaText);
            Grid.SetColumn(bannerTextStack, 1);
            bannerGrid.Children.Add(bannerTextStack);

            var liveLabel = new Border { Background = new SolidColorBrush(SuccessGreen), CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            liveLabel.Child = new TextBlock { Text = "LIVE", FontSize = 8, Foreground = Brushes.White, FontWeight = FontWeights.Bold };
            Grid.SetColumn(liveLabel, 2);
            bannerGrid.Children.Add(liveLabel);

            var dismissBtn = new Button { Content = "x", Background = Brushes.Transparent, Foreground = new SolidColorBrush(TextMuted), BorderThickness = new Thickness(0), FontSize = 12, Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand };
            dismissBtn.Click += DismissBanner_Click;
            Grid.SetColumn(dismissBtn, 3);
            bannerGrid.Children.Add(dismissBtn);

            _changeBanner.Child = bannerGrid;
            Grid.SetRow(_changeBanner, 1);
            root.Children.Add(_changeBanner);

            // ── Row 2: Total card ──
            _totalCard = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(12, 12, 12, 0),
                Padding = new Thickness(16)
            };
            var totalStack = new StackPanel();

            var totalHeader = new Grid();
            totalHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            totalHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            totalHeader.Children.Add(new TextBlock { Text = "Estimated Total Cost", FontSize = 11, Foreground = new SolidColorBrush(TextSecondary), FontWeight = FontWeights.Medium });
            var rmLabel = new TextBlock { Text = "MYR (RM)", FontSize = 9, Foreground = new SolidColorBrush(TextMuted), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(rmLabel, 1);
            totalHeader.Children.Add(rmLabel);
            totalStack.Children.Add(totalHeader);

            _grandTotalText = new TextBlock { Text = "RM 0", FontSize = 28, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(TextPrimary), Margin = new Thickness(0, 4, 0, 8) };
            totalStack.Children.Add(_grandTotalText);
            _markupNoteText = new TextBlock { Text = "", FontSize = 9, Foreground = new SolidColorBrush(TextMuted), Margin = new Thickness(0, -6, 0, 6), Visibility = Visibility.Collapsed };
            totalStack.Children.Add(_markupNoteText);
            _estimateRangeText = new TextBlock { Text = "", FontSize = 9, Foreground = new SolidColorBrush(WarningAmber), Margin = new Thickness(0, -4, 0, 6), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            totalStack.Children.Add(_estimateRangeText);

            // Stats row
            var statsGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _itemCountText = MakeStatBlock("0", "Items");
            Grid.SetColumn(_itemCountText, 0);
            statsGrid.Children.Add(_itemCountText);

            _levelCountText = MakeStatBlock("0", "Levels");
            Grid.SetColumn(_levelCountText, 1);
            statsGrid.Children.Add(_levelCountText);

            _pricedPercentText = MakeStatBlock("0%", "Priced");
            Grid.SetColumn(_pricedPercentText, 2);
            statsGrid.Children.Add(_pricedPercentText);

            totalStack.Children.Add(statsGrid);

            // Coverage bar
            var coverageLabel = new TextBlock { Text = "Pricing Coverage", FontSize = 9, Foreground = new SolidColorBrush(TextMuted), Margin = new Thickness(0, 0, 0, 3) };
            totalStack.Children.Add(coverageLabel);
            var barBg = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(230, 230, 230)) };
            _coverageBar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0, Foreground = new SolidColorBrush(SuccessGreen), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            barBg.Child = _coverageBar;
            totalStack.Children.Add(barBg);

            _totalCard.Child = totalStack;
            Grid.SetRow(_totalCard, 2);
            root.Children.Add(_totalCard);

            // ── Row 3: Filter bar ──
            var filterCard = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(12, 8, 12, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            var filterRow = new Grid();
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var viewLabel = new TextBlock { Text = "View:", FontSize = 10, Foreground = new SolidColorBrush(TextSecondary), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(viewLabel, 0);
            filterRow.Children.Add(viewLabel);

            var togglePanel = new StackPanel { Orientation = Orientation.Horizontal };
            _byLevelRadio = MakeToggle("Level", true);
            _byLevelRadio.Click += ViewMode_Click;
            _byCategoryRadio = MakeToggle("Category", false);
            _byCategoryRadio.Click += ViewMode_Click;
            togglePanel.Children.Add(_byLevelRadio);
            togglePanel.Children.Add(_byCategoryRadio);
            Grid.SetColumn(togglePanel, 1);
            filterRow.Children.Add(togglePanel);

            _levelFilter = new ComboBox
            {
                MinWidth = 110, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            _levelFilter.Items.Add(new ComboBoxItem { Content = "All Levels", IsSelected = true });
            _levelFilter.SelectionChanged += LevelFilter_Changed;
            Grid.SetColumn(_levelFilter, 3);
            filterRow.Children.Add(_levelFilter);

            filterCard.Child = filterRow;
            Grid.SetRow(filterCard, 3);
            root.Children.Add(filterCard);

            // ── Row 4: Scrollable content ──
            var scroll = new ScrollViewer { Margin = new Thickness(12, 8, 12, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _contentPanel = new StackPanel();
            _contentPanel.Children.Add(new TextBlock
            {
                Text = "Click Refresh to scan the model",
                Foreground = new SolidColorBrush(TextMuted), FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0)
            });
            scroll.Content = _contentPanel;
            Grid.SetRow(scroll, 4);
            root.Children.Add(scroll);

            // ── Row 5: Action bar ──
            var actionBar = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(10, 8, 10, 8)
            };
            var actionStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            var primaryRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            primaryRow.Children.Add(MakeActionButton("Match Prices", AutoMatch_Click, SuccessGreen, true));
            primaryRow.Children.Add(MakeActionButton("Review", ReviewQueue_Click, Color.FromRgb(156, 39, 176), false));
            primaryRow.Children.Add(MakeActionButton("AI Insights", AIInsights_Click, Color.FromRgb(100, 100, 100), false));
            primaryRow.Children.Add(MakeActionButton("Refresh", Refresh_Click, PrimaryBlue, true));

            var secondaryRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            secondaryRow.Children.Add(MakeLinkButton("Export", Export_Click));
            secondaryRow.Children.Add(new TextBlock { Text = "|", Foreground = new SolidColorBrush(BorderColor), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), FontSize = 10 });
            secondaryRow.Children.Add(MakeLinkButton("Import Prices", Import_Click));
            secondaryRow.Children.Add(new TextBlock { Text = "|", Foreground = new SolidColorBrush(BorderColor), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), FontSize = 10 });
            secondaryRow.Children.Add(MakeLinkButton("Import to Master DB", ImportMaster_Click));

            actionStack.Children.Add(primaryRow);
            actionStack.Children.Add(secondaryRow);
            actionBar.Child = actionStack;
            Grid.SetRow(actionBar, 5);
            root.Children.Add(actionBar);

            this.Content = root;
        }

        // ── UI Helpers ──

        private TextBlock MakeStatBlock(string value, string label)
        {
            // We use a single TextBlock with Run for simplicity
            var tb = new TextBlock { TextAlignment = TextAlignment.Center };
            tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextPrimary) });
            tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            tb.Inlines.Add(new System.Windows.Documents.Run(label) { FontSize = 9, Foreground = new SolidColorBrush(TextMuted) });
            tb.Tag = label; // Store label for later update
            return tb;
        }

        private void UpdateStatBlock(TextBlock tb, string value)
        {
            string label = tb.Tag as string ?? "";
            tb.Inlines.Clear();
            tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextPrimary) });
            tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            tb.Inlines.Add(new System.Windows.Documents.Run(label) { FontSize = 9, Foreground = new SolidColorBrush(TextMuted) });
        }

        private RadioButton MakeToggle(string text, bool isChecked)
        {
            return new RadioButton
            {
                Content = text, IsChecked = isChecked, GroupName = "ViewMode",
                FontSize = 10, Foreground = new SolidColorBrush(TextPrimary),
                Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private Button MakeActionButton(string text, RoutedEventHandler click, Color bg, bool primary)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 11,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(bg),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(bg),
                BorderThickness = new Thickness(1),
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += click;
            return btn;
        }

        private Button MakeLinkButton(string text, RoutedEventHandler click)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 10,
                Padding = new Thickness(4, 2, 4, 2),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(PrimaryBlue),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += click;
            return btn;
        }

        private Color GetGroupColor(string name, int index, bool isLevel)
        {
            if (!isLevel) return UI.CategoryIcons.GetColor(name);
            return LevelColors[index % LevelColors.Length];
        }

        // ── Data ──

        public void SetRevitApp(UIApplication uiApp)
        {
            _uiApp = uiApp;
            // Auto-refresh when app is set and no data loaded
            if (_allItems.Count == 0) RefreshData();
        }

        public void RefreshData()
        {
            try
            {
                if (_uiApp?.ActiveUIDocument?.Document == null) { _subtitleText.Text = "No model loaded"; return; }
                Document doc = _uiApp.ActiveUIDocument.Document;
                string projectName = Path.GetFileNameWithoutExtension(doc.PathName ?? "Untitled");
                _priceDb = new PriceDatabase(projectName);
                _allItems = RevitModelWalker.GetAllItems(doc);
                _priceDb.ApplyPrices(_allItems);
                _summary = CostCalculator.Calculate(_allItems);
                UpdateHeader(projectName);
                UpdateTotalCard();
                UpdateLevelFilter(doc);
                UpdateContent();
            }
            catch (Exception ex) { _subtitleText.Text = $"Error: {ex.Message}"; }
        }

        private void UpdateHeader(string projectName)
        {
            _subtitleText.Text = $"{projectName}  |  {DateTime.Now:HH:mm}";
        }

        private void UpdateTotalCard()
        {
            if (_summary == null) return;
            _grandTotalText.Text = $"RM {_summary.GrandTotal:N0}";
            _markupNoteText.Text = _appliedMarkupPct > 0 ? $"incl. {_appliedMarkupPct:G4}% markup" : "";
            _markupNoteText.Visibility = _appliedMarkupPct > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateStatBlock(_itemCountText, $"{_summary.TotalItems:N0}");
            UpdateStatBlock(_levelCountText, $"{_summary.LevelCount}");
            int pricedPct = _summary.TotalItems > 0 ? (int)((_summary.PricedItems / (double)_summary.TotalItems) * 100) : 0;
            UpdateStatBlock(_pricedPercentText, $"{pricedPct}%");
            _coverageBar.Value = pricedPct;
            _coverageBar.Foreground = new SolidColorBrush(pricedPct >= 80 ? SuccessGreen : pricedPct >= 50 ? WarningAmber : Color.FromRgb(200, 50, 50));

            // Estimate range: an incomplete pricing pass makes the single grand
            // total a floor, not the answer — extrapolate the ceiling from the
            // priced fraction and say what's missing instead of implying certainty.
            int unitMismatches = _allItems?.Count(i => i.UnitMismatch) ?? 0;
            if (_summary.TotalItems > 0 && _summary.GrandTotal > 0 && (pricedPct < 100 || unitMismatches > 0))
            {
                double pricedFraction = _summary.PricedItems / (double)_summary.TotalItems;
                double high = pricedFraction > 0 ? _summary.GrandTotal / pricedFraction : _summary.GrandTotal;
                string mismatchNote = unitMismatches > 0 ? $", {unitMismatches} unit-mismatch" : "";
                _estimateRangeText.Text = $"est. RM {_summary.GrandTotal:N0} – RM {high:N0} ({pricedPct}% priced{mismatchNote})";
                _estimateRangeText.Visibility = Visibility.Visible;
            }
            else
            {
                _estimateRangeText.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateLevelFilter(Document doc)
        {
            _levelFilter.Items.Clear();
            _levelFilter.Items.Add(new ComboBoxItem { Content = "All Levels", IsSelected = true });
            foreach (var level in RevitModelWalker.GetLevelNames(doc))
                _levelFilter.Items.Add(new ComboBoxItem { Content = level });
        }

        private void UpdateContent()
        {
            _contentPanel.Children.Clear();
            if (_summary == null || _allItems.Count == 0)
            {
                _contentPanel.Children.Add(new TextBlock { Text = "No items found. Click Refresh.", Foreground = new SolidColorBrush(TextMuted), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) });
                return;
            }

            var groups = _showByLevel ? _summary.ByLevel : _summary.ByCategory;
            double maxCost = groups.Any() ? groups.Max(g => g.TotalCost) : 1;
            for (int i = 0; i < groups.Count; i++)
                _contentPanel.Children.Add(CreateCostRow(groups[i], i, maxCost));
        }

        private Border CreateCostRow(CostGroup group, int index, double maxCost)
        {
            var iconColor = GetGroupColor(group.Name, index, _showByLevel);
            double barPct = maxCost > 0 ? (group.TotalCost / maxCost) * 100 : 0;

            var row = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(RowHover);
            row.MouseLeave += (s, e) => row.Background = Brushes.White;
            row.MouseLeftButtonUp += (s, e) => ShowGroupDetail(group);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon — vector icon for categories, numbered circle for levels
            UIElement iconElement;
            if (_showByLevel)
                iconElement = UI.CategoryIcons.CreateLevelIcon(group.Name, index, 30);
            else
                iconElement = UI.CategoryIcons.CreateIconBadge(group.Name, 30);

            Grid.SetColumn(iconElement, 0);
            grid.Children.Add(iconElement);

            // Name + count + bar
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            nameStack.Children.Add(new TextBlock { Text = group.Name, FontSize = 12, Foreground = new SolidColorBrush(TextPrimary), FontWeight = FontWeights.Medium });

            var subRow = new StackPanel { Orientation = Orientation.Horizontal };
            subRow.Children.Add(new TextBlock { Text = $"{group.ItemCount} items", FontSize = 10, Foreground = new SolidColorBrush(TextMuted), Margin = new Thickness(0, 0, 8, 0) });
            subRow.Children.Add(new TextBlock { Text = $"{group.Percentage:F1}%", FontSize = 10, Foreground = new SolidColorBrush(iconColor), FontWeight = FontWeights.Medium });
            nameStack.Children.Add(subRow);

            // Mini bar
            var miniBarBg = new Border { Height = 3, Width = 80, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(230, 230, 230)), Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            miniBarBg.Child = new Border { Height = 3, Width = Math.Max(1, barPct * 0.8), CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(iconColor), HorizontalAlignment = HorizontalAlignment.Left };
            nameStack.Children.Add(miniBarBg);
            Grid.SetColumn(nameStack, 1);
            grid.Children.Add(nameStack);

            // Amount
            var amountStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            amountStack.Children.Add(new TextBlock { Text = $"RM {group.TotalCost:N0}", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextPrimary), TextAlignment = TextAlignment.Right });
            amountStack.Children.Add(new TextBlock { Text = ">", FontSize = 10, Foreground = new SolidColorBrush(TextMuted), TextAlignment = TextAlignment.Right });
            Grid.SetColumn(amountStack, 2);
            grid.Children.Add(amountStack);

            row.Child = grid;
            return row;
        }

        private void ShowGroupDetail(CostGroup group)
        {
            _contentPanel.Children.Clear();

            // Back button
            var backBtn = new Button { Content = "< Back", Background = Brushes.Transparent, Foreground = new SolidColorBrush(PrimaryBlue), BorderThickness = new Thickness(0), FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            backBtn.Click += (s, e) => UpdateContent();
            _contentPanel.Children.Add(backBtn);

            // Title
            var titleCard = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock { Text = group.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextPrimary) });
            titleStack.Children.Add(new TextBlock { Text = $"RM {group.TotalCost:N0}  |  {group.ItemCount} items  |  {group.Percentage:F1}%", FontSize = 10, Foreground = new SolidColorBrush(TextSecondary), Margin = new Thickness(0, 2, 0, 0) });
            string confidenceLine = BuildGroupConfidence(group);
            if (confidenceLine != null)
                titleStack.Children.Add(new TextBlock { Text = confidenceLine, FontSize = 9, Foreground = new SolidColorBrush(TextMuted), Margin = new Thickness(0, 2, 0, 0) });
            titleCard.Child = titleStack;
            _contentPanel.Children.Add(titleCard);

            // Header row
            _contentPanel.Children.Add(CreateDetailRow("Item", "Qty", "Rate", "Total", true));

            // Items
            var grouped = group.Items
                .Where(i => !string.IsNullOrEmpty(i.JkrCode))
                .GroupBy(i => i.JkrCode)
                .Select(g => new { Code = g.Key, Name = g.First().Name, Qty = g.Sum(i => i.Quantity), Unit = g.First().Unit, UnitPrice = g.First().UnitPrice, Total = g.Sum(i => i.TotalPrice), Provenance = BuildProvenance(g.First()) })
                .OrderByDescending(x => x.Total);

            int rowIdx = 0;
            foreach (var item in grouped)
            {
                string displayName = $"{item.Code}  {TruncateName(item.Name, 25)}";
                var detailRow = CreateDetailRow(displayName, $"{item.Qty:F1} {item.Unit}", item.UnitPrice > 0 ? $"{item.UnitPrice:N0}" : "-", item.Total > 0 ? $"{item.Total:N0}" : "-", false, rowIdx % 2 == 1, item.Provenance);
                _contentPanel.Children.Add(detailRow);
                rowIdx++;
            }

            var noCode = group.Items.Where(i => string.IsNullOrEmpty(i.JkrCode)).ToList();
            if (noCode.Any())
                _contentPanel.Children.Add(new TextBlock { Text = $"+ {noCode.Count} items without JKR code", Foreground = new SolidColorBrush(TextMuted), FontSize = 10, Margin = new Thickness(0, 8, 0, 0), FontStyle = FontStyles.Italic });

            // Subtotal
            var subtotalBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(BorderColor), Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 6, 0, 0) };
            var stGrid = new Grid();
            stGrid.Children.Add(new TextBlock { Text = "Subtotal", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(TextPrimary) });
            stGrid.Children.Add(new TextBlock { Text = $"RM {group.TotalCost:N0}", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(TextPrimary), HorizontalAlignment = HorizontalAlignment.Right });
            subtotalBorder.Child = stGrid;
            _contentPanel.Children.Add(subtotalBorder);
        }

        /// <summary>
        /// One-line group confidence roll-up: "8 AI-matched (6 high, 2 medium) ·
        /// ⚠ 1 unit-mismatch · 3 unpriced". Null when nothing to say (all manual).
        /// </summary>
        private static string BuildGroupConfidence(CostGroup group)
        {
            var aiMatched = group.Items.Where(i => !string.IsNullOrEmpty(i.MatchLayer)).ToList();
            int mismatches = group.Items.Count(i => i.UnitMismatch);
            int unpriced = group.Items.Count(i => i.UnitPrice <= 0 && !i.UnitMismatch);
            if (aiMatched.Count == 0 && mismatches == 0 && unpriced == 0) return null;

            var parts = new List<string>();
            if (aiMatched.Count > 0)
            {
                int high = aiMatched.Count(i => i.MatchConfidence == "high");
                int medium = aiMatched.Count(i => i.MatchConfidence == "medium");
                int other = aiMatched.Count - high - medium;
                var bands = new List<string>();
                if (high > 0) bands.Add($"{high} high");
                if (medium > 0) bands.Add($"{medium} medium");
                if (other > 0) bands.Add($"{other} low");
                parts.Add($"{aiMatched.Count} AI-matched ({string.Join(", ", bands)})");
            }
            if (mismatches > 0) parts.Add($"⚠ {mismatches} unit-mismatch");
            if (unpriced > 0) parts.Add($"{unpriced} unpriced");
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// Provenance badge for a priced item: "AI · layer3_vector · sim 0.63 ·
        /// unit OK". Null (no badge) for manual/imported rows with no match info.
        /// </summary>
        private static string BuildProvenance(CostItem item)
        {
            if (item.UnitMismatch)
                return "⚠ unit mismatch — rate not applied, needs review";
            if (string.IsNullOrEmpty(item.PriceSource) && string.IsNullOrEmpty(item.MatchLayer))
                return null;
            var parts = new List<string> { (item.PriceSource ?? "?").ToUpperInvariant() };
            if (!string.IsNullOrEmpty(item.MatchLayer)) parts.Add(item.MatchLayer);
            // Similarity lives in the server reasoning string ("similarity: 0.63")
            var m = System.Text.RegularExpressions.Regex.Match(item.MatchReasoning ?? "", @"similarity: ([\d.]+)");
            if (m.Success) parts.Add($"sim {m.Groups[1].Value}");
            if (!string.IsNullOrEmpty(item.MatchConfidence)) parts.Add(item.MatchConfidence);
            parts.Add("unit OK");
            return string.Join(" · ", parts);
        }

        private Grid CreateDetailRow(string name, string qty, string price, string total, bool isHeader = false, bool altRow = false, string subtitle = null)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 1) };
            if (altRow) grid.Background = new SolidColorBrush(RowAlt);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            var fg = new SolidColorBrush(isHeader ? TextMuted : TextPrimary);
            var fw = isHeader ? FontWeights.SemiBold : FontWeights.Normal;
            double fs = isHeader ? 9 : 11;

            UIElement t1;
            var nameText = new TextBlock { Text = name, Foreground = fg, FontWeight = fw, FontSize = fs, Padding = new Thickness(2) };
            if (!string.IsNullOrEmpty(subtitle))
            {
                var stack = new StackPanel();
                stack.Children.Add(nameText);
                stack.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    Foreground = new SolidColorBrush(subtitle.StartsWith("⚠") ? Color.FromRgb(180, 100, 0) : TextMuted),
                    FontSize = 8.5,
                    Padding = new Thickness(2, 0, 2, 2)
                });
                t1 = stack;
            }
            else
            {
                t1 = nameText;
            }
            var t2 = new TextBlock { Text = qty, Foreground = new SolidColorBrush(TextSecondary), FontSize = fs, TextAlignment = TextAlignment.Center, Padding = new Thickness(2) };
            var t3 = new TextBlock { Text = price, Foreground = new SolidColorBrush(isHeader ? TextMuted : PrimaryBlue), FontSize = fs, TextAlignment = TextAlignment.Right, Padding = new Thickness(2) };
            var t4 = new TextBlock { Text = total, Foreground = new SolidColorBrush(TextPrimary), FontWeight = FontWeights.SemiBold, FontSize = fs, TextAlignment = TextAlignment.Right, Padding = new Thickness(2) };

            Grid.SetColumn(t1, 0); Grid.SetColumn(t2, 1); Grid.SetColumn(t3, 2); Grid.SetColumn(t4, 3);
            grid.Children.Add(t1); grid.Children.Add(t2); grid.Children.Add(t3); grid.Children.Add(t4);
            return grid;
        }

        private string TruncateName(string name, int maxLen)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Length <= maxLen ? name : name.Substring(0, maxLen) + "...";
        }

        // ── Live Update ──

        public void OnModelChanged(ChangeSummary changeSummary)
        {
            try
            {
                if (_uiApp?.ActiveUIDocument?.Document == null) return;
                _previousTotal = _summary?.GrandTotal ?? 0;
                _previousItemCount = _summary?.TotalItems ?? 0;
                RefreshData();
                double delta = (_summary?.GrandTotal ?? 0) - _previousTotal;
                int itemDelta = (_summary?.TotalItems ?? 0) - _previousItemCount;
                string changeText = changeSummary.ToNotificationText();
                string deltaText = BuildDeltaText(delta, itemDelta);
                _recentChanges.Insert(0, $"{DateTime.Now:HH:mm} - {changeText}{(deltaText != null ? " > " + deltaText : "")}");
                if (_recentChanges.Count > MaxRecentChanges) _recentChanges.RemoveAt(_recentChanges.Count - 1);
                ShowChangeBanner(changeText, deltaText, delta);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA Cost] OnModelChanged error: {ex.Message}"); }
        }

        private string BuildDeltaText(double costDelta, int itemDelta)
        {
            var parts = new List<string>();
            if (Math.Abs(costDelta) >= 1) parts.Add($"{(costDelta > 0 ? "+" : "-")} RM {Math.Abs(costDelta):N0}");
            if (itemDelta != 0) parts.Add($"{(itemDelta > 0 ? "+" : "")}{itemDelta} items");
            return parts.Count > 0 ? string.Join(" | ", parts) : null;
        }

        private void ShowChangeBanner(string changeText, string deltaText, double costDelta)
        {
            _changeText.Text = changeText;
            _changeDeltaText.Text = deltaText ?? "";
            _changeDeltaText.Visibility = string.IsNullOrEmpty(deltaText) ? Visibility.Collapsed : Visibility.Visible;
            _changeBanner.Background = new SolidColorBrush(costDelta > 0 ? Color.FromRgb(253, 235, 208) : costDelta < 0 ? Color.FromRgb(223, 246, 221) : Color.FromRgb(235, 243, 252));
            _changeBanner.Visibility = Visibility.Visible;
            _bannerAutoHideTimer.Stop();
            _bannerAutoHideTimer.Start();
        }

        // ── Event Handlers ──

        private void ChangeBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_recentChanges.Count == 0) return;
            MessageBox.Show(string.Join("\n", _recentChanges), "Recent Model Changes", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void DismissBanner_Click(object sender, RoutedEventArgs e) { _bannerAutoHideTimer.Stop(); _changeBanner.Visibility = Visibility.Collapsed; }

        private void ViewMode_Click(object sender, RoutedEventArgs e) { _showByLevel = _byLevelRadio.IsChecked == true; UpdateContent(); }

        private void LevelFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_levelFilter.SelectedItem is ComboBoxItem item)
            {
                string level = item.Content.ToString();
                _summary = level == "All Levels" ? CostCalculator.Calculate(_allItems) : CostCalculator.CalculateForLevel(_allItems, level);
                UpdateTotalCard(); UpdateContent();
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { RefreshData(); }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_allItems.Count == 0) { MessageBox.Show("No data to export. Click Refresh first.", "BINA Cost"); return; }
                Document doc = _uiApp?.ActiveUIDocument?.Document;
                string projectName = Path.GetFileNameWithoutExtension(doc?.PathName ?? "Untitled");
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export Cost Items", Filter = "Excel (*.xlsx)|*.xlsx", FileName = $"{projectName}_CostExport_{DateTime.Now:yyyyMMdd}", DefaultExt = ".xlsx" };
                if (dlg.ShowDialog() == true)
                {
                    ExcelService.Export(_allItems, dlg.FileName, projectName);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dlg.FileName, UseShellExecute = true });
                }
            }
            catch (Exception ex) { MessageBox.Show($"Export failed: {ex.Message}", "BINA Cost"); }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import Prices from Excel", Filter = "Excel (*.xlsx)|*.xlsx" };
                if (dlg.ShowDialog() == true)
                {
                    var prices = ExcelService.ImportPrices(dlg.FileName);
                    if (prices.Count == 0) { MessageBox.Show("No prices found.\n\nMake sure 'JKR Code' and 'Unit Price' columns have data.", "BINA Cost"); return; }
                    _priceDb?.ImportPrices(prices, "imported"); _priceDb?.Save();
                    _priceDb?.ApplyPrices(_allItems);
                    _summary = CostCalculator.Calculate(_allItems);
                    UpdateTotalCard(); UpdateContent();
                    MessageBox.Show($"Imported {prices.Count} prices.\nTotal: RM {_summary.GrandTotal:N0}", "BINA Cost");
                }
            }
            catch (Exception ex) { MessageBox.Show($"Import failed: {ex.Message}", "BINA Cost"); }
        }

        private async void AutoMatch_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            try
            {
                // Auto-refresh if no data loaded yet
                if (_allItems.Count == 0)
                {
                    RefreshData();
                    if (_allItems.Count == 0) { MessageBox.Show("No elements found in the model.", "BINA Cost"); return; }
                }

                // Disable button and show loading state
                if (btn != null) { btn.IsEnabled = false; btn.Content = "⏳ Matching..."; }
                ShowBanner("🔍 Running 4-layer matching pipeline...", "Exact → Learned → AI → Review", Color.FromRgb(0, 120, 215));
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                int localMatched = 0;

                // Step 1: Local master DB match (fast, offline)
                var masterDb = MasterPriceDatabase.Instance;
                if (masterDb.Count > 0)
                {
                    localMatched = masterDb.AutoMatchPrices(_allItems, _priceDb);
                    if (localMatched > 0)
                        ShowBanner($"✅ Local DB: {localMatched} matched", "Now running AI pipeline...", SuccessGreen);
                }

                // Step 2: AI 4-layer pipeline for ALL items (server handles dedup + layers)
                var aiEstimator = new AICostEstimator();
                bool aiAvailable = await aiEstimator.IsAvailableAsync();

                int pipelineMatched = 0;
                int reviewQueued = 0;
                string matchRate = "0%";

                int unitSkipped = 0;
                if (aiAvailable)
                {
                    ShowBanner($"🤖 AI pipeline: {_allItems.Count} items...", "Layer 1: Exact code → Layer 2: Learned → Layer 3: Vector search", Color.FromRgb(0, 120, 215));
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                    string projectName = _subtitleText?.Text?.Split('|')?.FirstOrDefault()?.Trim() ?? "Untitled";
                    double markupPct = BinaConfig.Load()?.CostMarkupPct ?? 0;
                    var result = await aiEstimator.MatchPipelineAsync(_allItems, projectName, markupPct: markupPct);

                    if (result.Success)
                    {
                        var stats = result.Stats;
                        pipelineMatched = stats.TotalMatched;
                        reviewQueued = stats.Layer4Review;
                        matchRate = stats.MatchRate;
                        _appliedMarkupPct = stats.MarkupPct;
                        double markupFactor = 1 + stats.MarkupPct / 100;

                        // Apply matched prices
                        foreach (var match in result.Matches)
                        {
                            // Only apply confirmed/high/medium confidence
                            if (match.Confidence == "confirmed" || match.Confidence == "high" || match.Confidence == "medium")
                            {
                                var item = _allItems.FirstOrDefault(x => x.ElementId == match.ElementId);
                                if (item != null && item.UnitPrice <= 0 && match.UnitPrice > 0)
                                {
                                    // Unit guard (twin of the server-side check): never
                                    // apply a rate whose unit can't price this quantity
                                    if (!CostUnitRules.Compatible(item.Unit, match.Unit, item.ThicknessMm))
                                    {
                                        item.UnitMismatch = true;
                                        unitSkipped++;
                                        continue;
                                    }
                                    // Server prices total_price with markup; the local
                                    // rate must carry it too so TotalPrice agrees
                                    item.UnitPrice = match.UnitPrice * markupFactor;
                                    item.JkrCode = match.JkrCode;
                                    item.PriceSource = match.MatchLayer == "exact" ? "master" :
                                                       match.MatchLayer == "learned" ? "learned" : "ai";
                                    item.MatchLayer = match.MatchLayer;
                                    item.MatchConfidence = match.Confidence;
                                    item.MatchReasoning = match.Reasoning;
                                    item.UnitMismatch = false;
                                    _priceDb?.SetPrice(item.JkrCode, match.UnitPrice, item.Unit);
                                }
                            }
                        }
                    }
                    else
                    {
                        ShowBanner("⚠️ Pipeline error", result.Error ?? "Unknown error", WarningAmber);
                        await Task.Delay(2000);
                    }
                }
                else
                {
                    ShowBanner("⚠️ AI server not available", "Using local matching only", WarningAmber);
                    await Task.Delay(1500);
                }

                _summary = CostCalculator.Calculate(_allItems); UpdateTotalCard(); UpdateContent();

                int totalMatched = localMatched + pipelineMatched;
                var parts = new List<string>();
                if (localMatched > 0) parts.Add($"Local: {localMatched}");
                if (pipelineMatched > 0) parts.Add($"Pipeline: {pipelineMatched}");
                parts.Add($"Rate: {matchRate}");
                if (unitSkipped > 0) parts.Add($"⚠ {unitSkipped} unit-mismatch (not priced)");
                if (_appliedMarkupPct > 0) parts.Add($"incl. {_appliedMarkupPct:G4}% markup");
                string detail = string.Join(" | ", parts);

                if (reviewQueued > 0)
                {
                    ShowBanner($"✅ Matched {totalMatched} items — RM {_summary.GrandTotal:N0}",
                        $"{detail} | 📋 {reviewQueued} items need review — click Review", WarningAmber);
                }
                else
                {
                    ShowBanner($"✅ Matched {totalMatched} items — RM {_summary.GrandTotal:N0}", detail, SuccessGreen);
                }
            }
            catch (Exception ex) { MessageBox.Show($"Match failed: {ex.Message}", "BINA Cost"); }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "Match Prices"; }
            }
        }

        private async void ReviewQueue_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            try
            {
                if (btn != null) { btn.IsEnabled = false; btn.Content = "⏳ Loading..."; }
                ShowBanner("📋 Loading review queue...", "", Color.FromRgb(156, 39, 176));
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                var aiEstimator = new AICostEstimator();
                bool available = await aiEstimator.IsAvailableAsync();
                if (!available)
                {
                    ShowBanner("⚠️ AI server not available", "Cannot load review queue", WarningAmber);
                    return;
                }

                var stats = await aiEstimator.GetReviewStatsAsync();
                var reviews = await aiEstimator.GetPendingReviewsAsync(200);

                if (!reviews.Any())
                {
                    ShowBanner("✅ No items pending review!",
                        $"🧠 {stats.JkrEntries} JKR codes + {stats.LearnedMappings} learned mappings", SuccessGreen);
                    return;
                }

                BuildReviewWindow(aiEstimator, reviews, stats);

                ShowBanner($"📋 {reviews.Count} items need review",
                    $"{stats.LearnedMappings} mappings learned so far", Color.FromRgb(156, 39, 176));
            }
            catch (Exception ex) { MessageBox.Show($"Review failed: {ex.Message}", "BINA Cost"); }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "Review"; }
            }
        }

        private void BuildReviewWindow(AICostEstimator aiEstimator, List<ReviewItem> reviews, ReviewStats stats)
        {
            // Colors
            var bgDark = Color.FromRgb(24, 24, 27);           // zinc-950
            var bgCard = Color.FromRgb(39, 39, 42);            // zinc-800
            var bgCardHover = Color.FromRgb(52, 52, 56);       // zinc-700
            var bgHeader = Color.FromRgb(88, 28, 135);         // purple-900
            var accentPurple = Color.FromRgb(168, 85, 247);    // purple-500
            var accentGreen = Color.FromRgb(34, 197, 94);      // green-500
            var accentAmber = Color.FromRgb(245, 158, 11);     // amber-500
            var accentRed = Color.FromRgb(239, 68, 68);        // red-500
            var accentBlue = Color.FromRgb(59, 130, 246);      // blue-500
            var textWhite = Color.FromRgb(250, 250, 250);
            var textMuted = Color.FromRgb(161, 161, 170);      // zinc-400
            var textDim = Color.FromRgb(113, 113, 122);        // zinc-500
            var borderSubtle = Color.FromRgb(63, 63, 70);      // zinc-700

            var window = new Window
            {
                Title = "BINA Cost — Review Queue",
                Width = 860, Height = 720,
                MinWidth = 700, MinHeight = 500,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(bgDark),
                ResizeMode = ResizeMode.CanResize
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // Header
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // Stats bar
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // List
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // Footer

            // ══════════════════════════════════════════════
            // HEADER
            // ══════════════════════════════════════════════
            var headerBorder = new Border
            {
                Background = new LinearGradientBrush(bgHeader, Color.FromRgb(59, 7, 100), new Point(0, 0), new Point(1, 0)),
                Padding = new Thickness(24, 18, 24, 18)
            };
            var headerStack = new StackPanel();
            headerStack.Children.Add(new TextBlock
            {
                Text = "📋 Review Queue",
                FontSize = 22, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            });
            headerStack.Children.Add(new TextBlock
            {
                Text = $"{reviews.Count} items need your confirmation  •  Each confirmation teaches the AI permanently",
                FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(216, 180, 254)), // purple-300
                Margin = new Thickness(0, 4, 0, 0)
            });
            headerBorder.Child = headerStack;
            Grid.SetRow(headerBorder, 0);
            rootGrid.Children.Add(headerBorder);

            // ══════════════════════════════════════════════
            // STATS BAR
            // ══════════════════════════════════════════════
            var statsBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)),
                Padding = new Thickness(24, 10, 24, 10),
                BorderBrush = new SolidColorBrush(borderSubtle),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var statsRow = new StackPanel { Orientation = Orientation.Horizontal };

            Action<string, string, Color> addStat = (label, value, color) =>
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 24, 0) };
                sp.Children.Add(new Border
                {
                    Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
                sp.Children.Add(new TextBlock
                {
                    FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(textMuted)
                });
                var tb = sp.Children[1] as TextBlock;
                tb.Inlines.Add(new System.Windows.Documents.Run(value + " ") { FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(textWhite) });
                tb.Inlines.Add(new System.Windows.Documents.Run(label));
                statsRow.Children.Add(sp);
            };

            int withSuggestions = reviews.Count(r => r.AiSuggestions != null && r.AiSuggestions.Any());
            int noSuggestions = reviews.Count - withSuggestions;

            addStat("JKR Codes", stats.JkrEntries.ToString(), accentBlue);
            addStat("Learned", stats.LearnedMappings.ToString(), accentGreen);
            addStat("Pending", reviews.Count.ToString(), accentAmber);
            addStat("With Suggestions", withSuggestions.ToString(), accentPurple);
            if (noSuggestions > 0) addStat("Manual Needed", noSuggestions.ToString(), accentRed);

            statsBorder.Child = statsRow;
            Grid.SetRow(statsBorder, 1);
            rootGrid.Children.Add(statsBorder);

            // ══════════════════════════════════════════════
            // REVIEW CARDS LIST
            // ══════════════════════════════════════════════
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(16, 12, 16, 12)
            };
            var cardsPanel = new StackPanel();

            // Track per-card state for individual accept
            var cardStates = new Dictionary<string, Border>();

            foreach (var review in reviews)
            {
                bool hasSugg = review.AiSuggestions != null && review.AiSuggestions.Any();
                var topSugg = hasSugg ? review.AiSuggestions.First() : null;

                // ── Card container ──
                var card = new Border
                {
                    Background = new SolidColorBrush(bgCard),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(16, 12, 16, 12),
                    BorderBrush = new SolidColorBrush(borderSubtle),
                    BorderThickness = new Thickness(1),
                    Tag = review.Id
                };
                cardStates[review.Id] = card;

                var cardGrid = new Grid();
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // ── Left: Item info ──
                var infoStack = new StackPanel();

                // Element name
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                nameRow.Children.Add(new TextBlock
                {
                    Text = review.ElementName ?? "Unknown",
                    FontSize = 14, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(textWhite),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 450
                });
                infoStack.Children.Add(nameRow);

                // Category + qty row
                var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

                if (!string.IsNullOrEmpty(review.Category))
                {
                    metaRow.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(30, accentBlue.R, accentBlue.G, accentBlue.B)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(0, 0, 8, 0),
                        Child = new TextBlock
                        {
                            Text = review.Category,
                            FontSize = 10, Foreground = new SolidColorBrush(accentBlue)
                        }
                    });
                }

                metaRow.Children.Add(new TextBlock
                {
                    Text = $"Qty: {review.Qty:F1} {review.Unit}",
                    FontSize = 11, Foreground = new SolidColorBrush(textDim),
                    VerticalAlignment = VerticalAlignment.Center
                });
                infoStack.Children.Add(metaRow);

                // ── Suggestions ──
                if (hasSugg)
                {
                    var suggStack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

                    for (int si = 0; si < Math.Min(review.AiSuggestions.Count, 3); si++)
                    {
                        var sugg = review.AiSuggestions[si];
                        bool isTop = si == 0;
                        var simPct = (sugg.Similarity * 100);

                        var suggRow = new Border
                        {
                            Background = new SolidColorBrush(isTop ? Color.FromArgb(20, accentGreen.R, accentGreen.G, accentGreen.B) : Colors.Transparent),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(8, 4, 8, 4),
                            Margin = new Thickness(0, 0, 0, 2),
                            BorderBrush = isTop ? new SolidColorBrush(Color.FromArgb(40, accentGreen.R, accentGreen.G, accentGreen.B)) : null,
                            BorderThickness = isTop ? new Thickness(1) : new Thickness(0)
                        };

                        var suggContent = new StackPanel { Orientation = Orientation.Horizontal };

                        // Rank indicator
                        suggContent.Children.Add(new TextBlock
                        {
                            Text = isTop ? "★" : $"#{si + 1}",
                            FontSize = 10,
                            Foreground = new SolidColorBrush(isTop ? accentGreen : textDim),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 6, 0),
                            FontWeight = isTop ? FontWeights.Bold : FontWeights.Normal
                        });

                        // JKR code badge
                        suggContent.Children.Add(new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(25, accentPurple.R, accentPurple.G, accentPurple.B)),
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(5, 1, 5, 1),
                            Margin = new Thickness(0, 0, 6, 0),
                            Child = new TextBlock
                            {
                                Text = sugg.JkrCode ?? "?",
                                FontSize = 11, FontWeight = FontWeights.SemiBold,
                                Foreground = new SolidColorBrush(accentPurple),
                                FontFamily = new System.Windows.Media.FontFamily("Consolas")
                            }
                        });

                        // Description (truncated)
                        string desc = sugg.Description ?? "";
                        if (desc.Length > 40) desc = desc.Substring(0, 40) + "…";
                        suggContent.Children.Add(new TextBlock
                        {
                            Text = desc,
                            FontSize = 11, Foreground = new SolidColorBrush(textMuted),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        });

                        // Price
                        suggContent.Children.Add(new TextBlock
                        {
                            Text = $"RM {sugg.UnitPrice:N0}",
                            FontSize = 11, FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(isTop ? accentGreen : textWhite),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        });

                        // Similarity bar
                        var simColor = simPct >= 60 ? accentGreen : simPct >= 45 ? accentAmber : accentRed;
                        var barBg = new Border
                        {
                            Width = 40, Height = 4, CornerRadius = new CornerRadius(2),
                            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 4, 0)
                        };
                        var barFill = new Border
                        {
                            Width = Math.Max(4, 40 * sugg.Similarity), Height = 4,
                            CornerRadius = new CornerRadius(2),
                            Background = new SolidColorBrush(simColor),
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        barBg.Child = barFill;
                        suggContent.Children.Add(barBg);

                        suggContent.Children.Add(new TextBlock
                        {
                            Text = $"{simPct:F0}%",
                            FontSize = 9, Foreground = new SolidColorBrush(simColor),
                            VerticalAlignment = VerticalAlignment.Center
                        });

                        suggRow.Child = suggContent;
                        suggStack.Children.Add(suggRow);
                    }
                    infoStack.Children.Add(suggStack);
                }
                else
                {
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = "⚠ No AI suggestions — needs manual JKR code",
                        FontSize = 11, Foreground = new SolidColorBrush(accentAmber),
                        Margin = new Thickness(0, 6, 0, 0),
                        FontStyle = FontStyles.Italic
                    });
                }

                Grid.SetColumn(infoStack, 0);
                cardGrid.Children.Add(infoStack);

                // ── Right: Action buttons ──
                var actionStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

                if (hasSugg)
                {
                    var acceptBtn = new Button
                    {
                        Content = "✓ Accept",
                        FontSize = 11, FontWeight = FontWeights.SemiBold,
                        Padding = new Thickness(14, 6, 14, 6),
                        Background = new SolidColorBrush(accentGreen),
                        Foreground = Brushes.White,
                        BorderThickness = new Thickness(0),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Margin = new Thickness(0, 0, 0, 4),
                        Tag = review // store review for click handler
                    };
                    acceptBtn.Click += async (s, ev) =>
                    {
                        var r = (ReviewItem)((Button)s).Tag;
                        var top = r.AiSuggestions.First();
                        ((Button)s).IsEnabled = false;
                        ((Button)s).Content = "⏳...";

                        var result = await aiEstimator.ResolveReviewAsync(
                            r.Id, top.JkrCode, top.UnitPrice, r.Unit ?? "unit", top.Description ?? "");

                        if (result.Success)
                        {
                            card.Background = new SolidColorBrush(Color.FromArgb(20, accentGreen.R, accentGreen.G, accentGreen.B));
                            card.BorderBrush = new SolidColorBrush(Color.FromArgb(60, accentGreen.R, accentGreen.G, accentGreen.B));
                            ((Button)s).Content = "✓ Learned";
                            ((Button)s).Background = new SolidColorBrush(Color.FromRgb(22, 101, 52)); // green-800

                            // Update the count in the header if we track it
                        }
                        else
                        {
                            ((Button)s).Content = "✗ Error";
                            ((Button)s).Background = new SolidColorBrush(accentRed);
                        }
                    };
                    actionStack.Children.Add(acceptBtn);
                }

                var skipBtn = new Button
                {
                    Content = "Skip",
                    FontSize = 10,
                    Padding = new Thickness(14, 4, 14, 4),
                    Background = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                    Foreground = new SolidColorBrush(textMuted),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                skipBtn.Click += (s, ev) =>
                {
                    card.Opacity = 0.3;
                    card.IsHitTestVisible = false;
                };
                actionStack.Children.Add(skipBtn);

                Grid.SetColumn(actionStack, 1);
                cardGrid.Children.Add(actionStack);

                card.Child = cardGrid;

                // Hover effect
                card.MouseEnter += (s, ev) => card.Background = new SolidColorBrush(bgCardHover);
                card.MouseLeave += (s, ev) =>
                {
                    // Don't reset if accepted
                    if (card.BorderBrush is SolidColorBrush b && b.Color.G > 150) return;
                    card.Background = new SolidColorBrush(bgCard);
                };

                cardsPanel.Children.Add(card);
            }

            scroll.Content = cardsPanel;
            Grid.SetRow(scroll, 2);
            rootGrid.Children.Add(scroll);

            // ══════════════════════════════════════════════
            // FOOTER
            // ══════════════════════════════════════════════
            var footer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)),
                Padding = new Thickness(24, 12, 24, 12),
                BorderBrush = new SolidColorBrush(borderSubtle),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };

            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left: info text
            var footerInfo = new TextBlock
            {
                Text = "💡 Accepted mappings persist forever — next project, same elements auto-match instantly.",
                FontSize = 11, Foreground = new SolidColorBrush(textDim),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(footerInfo, 0);
            footerGrid.Children.Add(footerInfo);

            // Right: buttons
            var footerButtons = new StackPanel { Orientation = Orientation.Horizontal };

            // Accept All button
            var acceptAllBtn = new Button
            {
                Padding = new Thickness(20, 8, 20, 8),
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(accentGreen),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0)
            };
            // Use a StackPanel for rich button content
            var acceptAllContent = new StackPanel { Orientation = Orientation.Horizontal };
            acceptAllContent.Children.Add(new TextBlock { Text = "✓ Accept All", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            acceptAllContent.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock { Text = withSuggestions.ToString(), FontSize = 11, Foreground = Brushes.White, FontWeight = FontWeights.Bold }
            });
            acceptAllBtn.Content = acceptAllContent;

            var progressText = new TextBlock
            {
                Text = "",
                FontSize = 11, Foreground = new SolidColorBrush(accentGreen),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            acceptAllBtn.Click += async (s, ev) =>
            {
                acceptAllBtn.IsEnabled = false;
                var reviewsWithSugg = reviews.Where(r => r.AiSuggestions != null && r.AiSuggestions.Any()).ToList();
                int confirmed = 0;
                int total = reviewsWithSugg.Count;

                foreach (var review in reviewsWithSugg)
                {
                    var top = review.AiSuggestions.First();
                    var result = await aiEstimator.ResolveReviewAsync(
                        review.Id, top.JkrCode, top.UnitPrice, review.Unit ?? "unit", top.Description ?? "");

                    if (result.Success)
                    {
                        confirmed++;
                        // Update the card visually
                        if (cardStates.ContainsKey(review.Id))
                        {
                            var c = cardStates[review.Id];
                            c.Background = new SolidColorBrush(Color.FromArgb(15, accentGreen.R, accentGreen.G, accentGreen.B));
                            c.BorderBrush = new SolidColorBrush(Color.FromArgb(40, accentGreen.R, accentGreen.G, accentGreen.B));
                        }
                    }

                    progressText.Text = $"✓ {confirmed}/{total}";
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                }

                progressText.Text = $"✅ {confirmed} learned!";
                acceptAllBtn.Content = new TextBlock { Text = $"✓ Done — {confirmed} learned", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
                acceptAllBtn.Background = new SolidColorBrush(Color.FromRgb(22, 101, 52));

                ShowBanner($"✅ {confirmed} mappings learned!", "Re-run Match Prices to apply", SuccessGreen);
            };

            footerButtons.Children.Add(progressText);
            footerButtons.Children.Add(acceptAllBtn);

            var closeBtn = new Button
            {
                Content = "Close",
                Padding = new Thickness(16, 8, 16, 8),
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                Foreground = new SolidColorBrush(textMuted),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            closeBtn.Click += (s, ev) => window.Close();
            footerButtons.Children.Add(closeBtn);

            Grid.SetColumn(footerButtons, 1);
            footerGrid.Children.Add(footerButtons);

            footer.Child = footerGrid;
            Grid.SetRow(footer, 3);
            rootGrid.Children.Add(footer);

            window.Content = rootGrid;
            window.Show();
        }

        private void ShowBanner(string text, string detail, Color bgColor)
        {
            _changeBanner.Background = new SolidColorBrush(Color.FromArgb(40, bgColor.R, bgColor.G, bgColor.B));
            _changeBanner.BorderBrush = new SolidColorBrush(bgColor);
            _changeText.Text = text;
            _changeDeltaText.Text = detail;
            _changeBanner.Visibility = Visibility.Visible;
            _bannerAutoHideTimer.Stop();
        }

        private async void AIInsights_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            try
            {
                if (_summary == null || _allItems.Count == 0)
                {
                    RefreshData();
                    if (_summary == null || _allItems.Count == 0) { MessageBox.Show("No elements found.", "BINA Cost"); return; }
                }

                if (btn != null) { btn.IsEnabled = false; btn.Content = "⏳ Analyzing..."; }
                ShowBanner("🧠 Analyzing costs...", "Getting AI insights", Color.FromRgb(0, 120, 215));
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                var aiEstimator = new AICostEstimator();
                bool aiAvailable = await aiEstimator.IsAvailableAsync();

                if (aiAvailable)
                {
                    // Call server-side analysis (benchmarks + LLM insights)
                    string projectName = _subtitleText?.Text?.Split('|')?.FirstOrDefault()?.Trim() ?? "Untitled";
                    var result = await aiEstimator.AnalyzeCostsAsync(_summary, _allItems, projectName);

                    if (result.Success && result.Insights.Any())
                    {
                        var lines = new List<string>();

                        if (!string.IsNullOrEmpty(result.BenchmarkComparison))
                            lines.Add($"📊 {result.BenchmarkComparison}\n");

                        foreach (var insight in result.Insights)
                        {
                            string icon = insight.Type switch
                            {
                                "warning" => "⚠️",
                                "suggestion" => "💡",
                                "saving" => "💰",
                                _ => "ℹ️"
                            };
                            lines.Add($"{icon} {insight.Title}");
                            lines.Add($"   {insight.Description}");
                            if (insight.PotentialSaving.HasValue && insight.PotentialSaving > 0)
                                lines.Add($"   Potential saving: RM {insight.PotentialSaving:N0}");
                            lines.Add("");
                        }

                        if (!string.IsNullOrEmpty(result.SummaryText))
                            lines.Add($"\n{result.SummaryText}");

                        ShowBanner("✅ Analysis complete", $"{result.Insights.Count} insights", SuccessGreen);
                        MessageBox.Show(string.Join("\n", lines), "BINA Cost — AI Insights");
                    }
                    else
                    {
                        // Fallback to local analysis
                        ShowLocalInsights();
                    }
                }
                else
                {
                    // Offline fallback
                    ShowBanner("⚠️ AI server offline", "Showing local analysis", WarningAmber);
                    ShowLocalInsights();
                }
            }
            catch (Exception ex) { MessageBox.Show($"Analysis failed: {ex.Message}", "BINA Cost"); }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "AI Insights"; }
            }
        }

        private void ShowLocalInsights()
        {
            var lines = new List<string> { "Cost Analysis (Offline)\n" };
            int pricedPct = _summary.TotalItems > 0 ? (int)((_summary.PricedItems / (double)_summary.TotalItems) * 100) : 0;
            lines.Add($"Coverage: {pricedPct}% ({_summary.PricedItems}/{_summary.TotalItems})");
            double floorArea = _allItems.Where(i => i.Category == "Floors" && (i.Unit == "m²" || i.Unit == "m2")).Sum(i => i.Quantity);
            if (floorArea > 0 && _summary.GrandTotal > 0)
            {
                double cpm2 = _summary.GrandTotal / floorArea;
                lines.Add($"Cost/m²: RM {cpm2:N0}");
                lines.Add(cpm2 < 1500 ? "  Below typical (RM 1,500-3,000/m²)" : cpm2 > 3000 ? "  Above typical (RM 1,500-3,000/m²)" : "  Within typical JKR range");
            }
            lines.Add("\nTop cost drivers:");
            foreach (var c in _summary.ByCategory.Take(5))
                lines.Add($"  {c.Name}: RM {c.TotalCost:N0} ({c.Percentage:F1}%)");
            int unpriced = _allItems.Count(i => i.UnitPrice <= 0);
            if (unpriced > 0) lines.Add($"\n{unpriced} items unpriced");
            MessageBox.Show(string.Join("\n", lines), "BINA Cost - Analysis");
        }

        private void ImportMaster_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import to Master Database", Filter = "Excel (*.xlsx)|*.xlsx" };
                if (dlg.ShowDialog() == true)
                {
                    var prices = ExcelService.ImportPrices(dlg.FileName);
                    if (prices.Count == 0) { MessageBox.Show("No prices found.", "BINA Cost"); return; }
                    var masterDb = MasterPriceDatabase.Instance;
                    var (added, updated) = masterDb.ImportEntries(prices); masterDb.Save();
                    MessageBox.Show($"Master DB updated!\n\nAdded: {added}\nUpdated: {updated}\nTotal: {masterDb.Count} entries\n\nAvailable across all projects.", "BINA Cost");
                }
            }
            catch (Exception ex) { MessageBox.Show($"Import failed: {ex.Message}", "BINA Cost"); }
        }
    }
}
