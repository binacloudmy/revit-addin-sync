using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            UpdateStatBlock(_itemCountText, $"{_summary.TotalItems:N0}");
            UpdateStatBlock(_levelCountText, $"{_summary.LevelCount}");
            int pricedPct = _summary.TotalItems > 0 ? (int)((_summary.PricedItems / (double)_summary.TotalItems) * 100) : 0;
            UpdateStatBlock(_pricedPercentText, $"{pricedPct}%");
            _coverageBar.Value = pricedPct;
            _coverageBar.Foreground = new SolidColorBrush(pricedPct >= 80 ? SuccessGreen : pricedPct >= 50 ? WarningAmber : Color.FromRgb(200, 50, 50));
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
            titleCard.Child = titleStack;
            _contentPanel.Children.Add(titleCard);

            // Header row
            _contentPanel.Children.Add(CreateDetailRow("Item", "Qty", "Rate", "Total", true));

            // Items
            var grouped = group.Items
                .Where(i => !string.IsNullOrEmpty(i.JkrCode))
                .GroupBy(i => i.JkrCode)
                .Select(g => new { Code = g.Key, Name = g.First().Name, Qty = g.Sum(i => i.Quantity), Unit = g.First().Unit, UnitPrice = g.First().UnitPrice, Total = g.Sum(i => i.TotalPrice) })
                .OrderByDescending(x => x.Total);

            int rowIdx = 0;
            foreach (var item in grouped)
            {
                string displayName = $"{item.Code}  {TruncateName(item.Name, 25)}";
                var detailRow = CreateDetailRow(displayName, $"{item.Qty:F1} {item.Unit}", item.UnitPrice > 0 ? $"{item.UnitPrice:N0}" : "-", item.Total > 0 ? $"{item.Total:N0}" : "-", false, rowIdx % 2 == 1);
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

        private Grid CreateDetailRow(string name, string qty, string price, string total, bool isHeader = false, bool altRow = false)
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

            var t1 = new TextBlock { Text = name, Foreground = fg, FontWeight = fw, FontSize = fs, Padding = new Thickness(2) };
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
                ShowBanner("🔍 Matching prices...", "", Color.FromRgb(0, 120, 215));

                int localMatched = 0;
                int aiMatched = 0;

                // Step 1: Local master DB match (fast, offline)
                var masterDb = MasterPriceDatabase.Instance;
                if (masterDb.Count > 0)
                {
                    localMatched = masterDb.AutoMatchPrices(_allItems, _priceDb);
                    if (localMatched > 0)
                        ShowBanner($"✅ Local DB: {localMatched} matched", "", SuccessGreen);
                }

                // Step 2: AI vector search for remaining unpriced items
                var unpriced = _allItems.Where(i => i.UnitPrice <= 0).ToList();
                if (unpriced.Any())
                {
                    ShowBanner($"🤖 AI matching {unpriced.Count} items...", "Searching JKR knowledge base", Color.FromRgb(0, 120, 215));

                    // Allow UI to update
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                    var aiEstimator = new AICostEstimator();
                    bool aiAvailable = await aiEstimator.IsAvailableAsync();

                    if (aiAvailable)
                    {
                        // Process in batches of 50 for speed (each batch = 1 API call)
                        int batchSize = 50;
                        for (int i = 0; i < unpriced.Count; i += batchSize)
                        {
                            var batch = unpriced.Skip(i).Take(batchSize).ToList();
                            int progress = Math.Min(i + batchSize, unpriced.Count);
                            ShowBanner($"🤖 AI matching... ({progress}/{unpriced.Count})", $"{aiMatched} matched so far", Color.FromRgb(0, 120, 215));
                            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                            var suggestions = await aiEstimator.SuggestMatchesAsync(batch, new List<MasterPriceEntry>());

                            foreach (var suggestion in suggestions)
                            {
                                var item = _allItems.FirstOrDefault(x => x.ElementId == suggestion.ElementId);
                                if (item != null && item.UnitPrice <= 0 && suggestion.SuggestedPrice > 0)
                                {
                                    item.UnitPrice = suggestion.SuggestedPrice;
                                    item.JkrCode = suggestion.SuggestedJkrCode;
                                    item.PriceSource = "ai";
                                    _priceDb?.SavePrice(item.JkrCode, item.UnitPrice, item.Unit);
                                    aiMatched++;
                                }
                            }
                        }
                    }
                    else
                    {
                        ShowBanner("⚠️ AI server not available", "Using local matching only", WarningAmber);
                        await Task.Delay(1500);
                    }
                }

                _summary = CostCalculator.Calculate(_allItems); UpdateTotalCard(); UpdateContent();

                int totalMatched = localMatched + aiMatched;
                int stillUnpriced = _allItems.Count(i => i.UnitPrice <= 0);
                var parts = new List<string>();
                if (localMatched > 0) parts.Add($"Local DB: {localMatched}");
                if (aiMatched > 0) parts.Add($"AI: {aiMatched}");
                string detail = parts.Any() ? string.Join(" | ", parts) : "No matches found";

                ShowBanner($"✅ Matched {totalMatched} items — RM {_summary.GrandTotal:N0}", detail, SuccessGreen);

                if (totalMatched == 0 && stillUnpriced > 0)
                    ShowBanner($"⚠️ No matches found", $"{stillUnpriced} items unpriced — try importing a master price list", WarningAmber);
            }
            catch (Exception ex) { MessageBox.Show($"Match failed: {ex.Message}", "BINA Cost"); }
            finally
            {
                // Restore button
                if (btn != null) { btn.IsEnabled = true; btn.Content = "Match Prices"; }
            }
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
            try
            {
                if (_summary == null || _allItems.Count == 0) { MessageBox.Show("No data. Click Refresh first.", "BINA Cost"); return; }
                var lines = new List<string> { "Cost Analysis\n" };
                int pricedPct = _summary.TotalItems > 0 ? (int)((_summary.PricedItems / (double)_summary.TotalItems) * 100) : 0;
                lines.Add($"Coverage: {pricedPct}% ({_summary.PricedItems}/{_summary.TotalItems})");
                double floorArea = _allItems.Where(i => i.Category == "Floors" && i.Unit == "m2").Sum(i => i.Quantity);
                if (floorArea > 0 && _summary.GrandTotal > 0)
                {
                    double cpm2 = _summary.GrandTotal / floorArea;
                    lines.Add($"Cost/m2: RM {cpm2:N0}");
                    lines.Add(cpm2 < 1500 ? "  Below typical (RM 1,500-3,000/m2)" : cpm2 > 3000 ? "  Above typical (RM 1,500-3,000/m2)" : "  Within typical JKR range");
                }
                lines.Add("\nTop cost drivers:");
                foreach (var c in _summary.ByCategory.Take(5))
                    lines.Add($"  {c.Name}: RM {c.TotalCost:N0} ({c.Percentage:F1}%)");
                int unpriced = _allItems.Count(i => i.UnitPrice <= 0);
                if (unpriced > 0) lines.Add($"\n{unpriced} items unpriced");
                MessageBox.Show(string.Join("\n", lines), "BINA Cost - Analysis");
            }
            catch (Exception ex) { MessageBox.Show($"Analysis failed: {ex.Message}", "BINA Cost"); }
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
