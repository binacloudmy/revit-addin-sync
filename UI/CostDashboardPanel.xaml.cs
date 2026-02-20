using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Events;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI
{
    public partial class CostDashboardPanel : Page
    {
        private UIApplication _uiApp;
        private List<CostItem> _allItems = new List<CostItem>();
        private CostSummary _summary;
        private PriceDatabase _priceDb;
        private bool _showByLevel = true;

        // Live update tracking
        private double _previousTotal;
        private int _previousItemCount;
        private DispatcherTimer _bannerAutoHideTimer;
        private readonly List<string> _recentChanges = new List<string>();
        private const int MaxRecentChanges = 5;

        // UI elements built in code
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

        private static readonly string[] Colors = {
            "#4a9eff", "#4aff7a", "#ffaa4a", "#aa4aff",
            "#ff4aaa", "#4affff", "#aaaa4a", "#ff6666"
        };

        public CostDashboardPanel()
        {
            InitializeComponent();
            BuildUI();

            _bannerAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _bannerAutoHideTimer.Tick += (s, e) =>
            {
                _bannerAutoHideTimer.Stop();
                _changeBanner.Visibility = Visibility.Collapsed;
            };
        }

        private void BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 0: Header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 1: Banner
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 2: Total
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 3: Filter
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 4: Content
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 5: Actions

            // Row 0: Header
            var header = new Border { Background = MakeBrush("#1a1a2e"), Padding = new Thickness(16) };
            var headerStack = new StackPanel();
            headerStack.Children.Add(new TextBlock { Text = "BINA Cost Tracker", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            _subtitleText = new TextBlock { Text = "No model loaded", FontSize = 11, Foreground = MakeBrush("#888888"), Margin = new Thickness(0, 4, 0, 0) };
            headerStack.Children.Add(_subtitleText);
            header.Child = headerStack;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Row 1: Change banner
            _changeBanner = new Border { Background = MakeBrush("#1a3a1a"), Padding = new Thickness(10, 8, 10, 8), Visibility = Visibility.Collapsed, Cursor = System.Windows.Input.Cursors.Hand };
            _changeBanner.MouseLeftButtonUp += ChangeBanner_Click;
            var bannerGrid = new Grid();
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var bannerTextStack = new StackPanel();
            _changeText = new TextBlock { Text = "Model changed", FontSize = 11, Foreground = MakeBrush("#88cc88"), FontWeight = FontWeights.Medium };
            _changeDeltaText = new TextBlock { Text = "", FontSize = 10, Foreground = MakeBrush("#668866"), Margin = new Thickness(0, 1, 0, 0) };
            bannerTextStack.Children.Add(_changeText);
            bannerTextStack.Children.Add(_changeDeltaText);
            Grid.SetColumn(bannerTextStack, 0);
            bannerGrid.Children.Add(bannerTextStack);

            var liveText = new TextBlock { Text = "LIVE", FontSize = 9, Foreground = MakeBrush("#44cc44"), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(liveText, 1);
            bannerGrid.Children.Add(liveText);

            var dismissBtn = new Button { Content = "X", Background = Brushes.Transparent, Foreground = MakeBrush("#668866"), BorderThickness = new Thickness(0), FontSize = 10, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
            dismissBtn.Click += DismissBanner_Click;
            Grid.SetColumn(dismissBtn, 2);
            bannerGrid.Children.Add(dismissBtn);

            _changeBanner.Child = bannerGrid;
            Grid.SetRow(_changeBanner, 1);
            root.Children.Add(_changeBanner);

            // Row 2: Total card
            var totalCard = new Border { Margin = new Thickness(12, 12, 12, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(20) };
            totalCard.Background = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#0f3460"),
                (Color)ColorConverter.ConvertFromString("#16213e"),
                new Point(0, 0), new Point(1, 1));
            var totalStack = new StackPanel();
            totalStack.Children.Add(new TextBlock { Text = "ESTIMATED TOTAL COST", FontSize = 11, Foreground = MakeBrush("#7eb8da"), FontWeight = FontWeights.SemiBold });
            _grandTotalText = new TextBlock { Text = "RM 0", FontSize = 32, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0) };
            totalStack.Children.Add(_grandTotalText);
            var statsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _itemCountText = new TextBlock { Text = "0 items", FontSize = 11, Foreground = MakeBrush("#7eb8da"), Margin = new Thickness(0, 0, 16, 0) };
            _levelCountText = new TextBlock { Text = "0 levels", FontSize = 11, Foreground = MakeBrush("#7eb8da"), Margin = new Thickness(0, 0, 16, 0) };
            _pricedPercentText = new TextBlock { Text = "0% priced", FontSize = 11, Foreground = MakeBrush("#7eb8da") };
            statsRow.Children.Add(_itemCountText);
            statsRow.Children.Add(_levelCountText);
            statsRow.Children.Add(_pricedPercentText);
            totalStack.Children.Add(statsRow);
            totalCard.Child = totalStack;
            Grid.SetRow(totalCard, 2);
            root.Children.Add(totalCard);

            // Row 3: Filter bar
            var filterBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 0) };
            _byLevelRadio = new RadioButton { Content = "By Level", IsChecked = true, GroupName = "ViewMode", Foreground = MakeBrush("#aaaaaa"), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 6, 0), FontSize = 11 };
            _byLevelRadio.Click += ViewMode_Click;
            _byCategoryRadio = new RadioButton { Content = "By Category", GroupName = "ViewMode", Foreground = MakeBrush("#aaaaaa"), Padding = new Thickness(8, 5, 8, 5), FontSize = 11 };
            _byCategoryRadio.Click += ViewMode_Click;
            _levelFilter = new ComboBox { Margin = new Thickness(12, 0, 0, 0), MinWidth = 120, FontSize = 11 };
            _levelFilter.Items.Add(new ComboBoxItem { Content = "All Levels", IsSelected = true });
            _levelFilter.SelectionChanged += LevelFilter_Changed;
            filterBar.Children.Add(_byLevelRadio);
            filterBar.Children.Add(_byCategoryRadio);
            filterBar.Children.Add(_levelFilter);
            Grid.SetRow(filterBar, 3);
            root.Children.Add(filterBar);

            // Row 4: Content
            var scrollViewer = new ScrollViewer { Margin = new Thickness(12, 8, 12, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _contentPanel = new StackPanel();
            _contentPanel.Children.Add(new TextBlock { Text = "Click Refresh to calculate costs", Foreground = MakeBrush("#666666"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) });
            scrollViewer.Content = _contentPanel;
            Grid.SetRow(scrollViewer, 4);
            root.Children.Add(scrollViewer);

            // Row 5: Action bar
            var actionBar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = MakeBrush("#333333"), Padding = new Thickness(8) };
            var actionStack = new StackPanel();

            var primaryRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            primaryRow.Children.Add(MakeButton("Match Prices", AutoMatch_Click, "#1a5c1a", "#88cc88", "#2a6c2a", true));
            primaryRow.Children.Add(MakeButton("AI Insights", AIInsights_Click, "#1a3a5c", "#88aacc", "#2a4a6c", false));
            primaryRow.Children.Add(MakeButton("Refresh", Refresh_Click, "#0078d4", "#ffffff", "#0078d4", true));

            var secondaryRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            secondaryRow.Children.Add(MakeButton("Export", Export_Click, "#333333", "#aaaaaa", "#444444", false, 10));
            secondaryRow.Children.Add(MakeButton("Import Prices", Import_Click, "#333333", "#aaaaaa", "#444444", false, 10));
            secondaryRow.Children.Add(MakeButton("Import to Master", ImportMaster_Click, "#333333", "#aaaaaa", "#444444", false, 10));

            actionStack.Children.Add(primaryRow);
            actionStack.Children.Add(secondaryRow);
            actionBar.Child = actionStack;
            Grid.SetRow(actionBar, 5);
            root.Children.Add(actionBar);

            this.Content = root;
        }

        private Button MakeButton(string text, RoutedEventHandler click, string bg, string fg, string border, bool bold, double fontSize = 11)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = fontSize,
                Background = MakeBrush(bg),
                Foreground = MakeBrush(fg),
                BorderBrush = MakeBrush(border),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
            };
            btn.Click += click;
            return btn;
        }

        private static SolidColorBrush MakeBrush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        // --- Revit Context ---

        public void SetRevitApp(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }

        public void RefreshData()
        {
            try
            {
                if (_uiApp?.ActiveUIDocument?.Document == null)
                {
                    _subtitleText.Text = "No model loaded";
                    return;
                }

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
            catch (Exception ex)
            {
                _subtitleText.Text = $"Error: {ex.Message}";
            }
        }

        private void UpdateHeader(string projectName)
        {
            _subtitleText.Text = $"{projectName} -- Updated: {DateTime.Now:HH:mm}";
        }

        private void UpdateTotalCard()
        {
            if (_summary == null) return;
            _grandTotalText.Text = $"RM {_summary.GrandTotal:N0}";
            _itemCountText.Text = $"{_summary.TotalItems:N0} items";
            _levelCountText.Text = $"{_summary.LevelCount} levels";
            int pricedPct = _summary.TotalItems > 0
                ? (int)((_summary.PricedItems / (double)_summary.TotalItems) * 100)
                : 0;
            _pricedPercentText.Text = $"{pricedPct}% priced";
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
                _contentPanel.Children.Add(new TextBlock { Text = "No items found. Click Refresh.", Foreground = MakeBrush("#666666"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) });
                return;
            }

            var groups = _showByLevel ? _summary.ByLevel : _summary.ByCategory;
            double maxCost = groups.Any() ? groups.Max(g => g.TotalCost) : 1;
            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                string color = Colors[i % Colors.Length];
                _contentPanel.Children.Add(CreateCostRow(group, color, maxCost));
            }
        }

        private Border CreateCostRow(CostGroup group, string accentColor, double maxCost)
        {
            var brush = MakeBrush(accentColor);
            double barWidth = maxCost > 0 ? (group.TotalCost / maxCost) * 100 : 0;

            var border = new Border { Background = MakeBrush("#2a2a2a"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 4), Cursor = System.Windows.Input.Cursors.Hand };
            border.MouseEnter += (s, e) => border.Background = MakeBrush("#333333");
            border.MouseLeave += (s, e) => border.Background = MakeBrush("#2a2a2a");
            border.MouseLeftButtonUp += (s, e) => ShowGroupDetail(group);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = MakeBrush(accentColor + "33") };
            icon.Child = new TextBlock { Text = group.Name.Length > 0 ? group.Name.Substring(0, 1).ToUpper() : "?", Foreground = brush, FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 0);

            var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            namePanel.Children.Add(new TextBlock { Text = group.Name, Foreground = MakeBrush("#e0e0e0"), FontSize = 13, FontWeight = FontWeights.Medium });
            namePanel.Children.Add(new TextBlock { Text = $"{group.ItemCount} items", Foreground = MakeBrush("#888888"), FontSize = 11 });
            Grid.SetColumn(namePanel, 1);

            var amountPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            amountPanel.Children.Add(new TextBlock { Text = $"RM {group.TotalCost:N0}", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right });
            amountPanel.Children.Add(new TextBlock { Text = $"{group.Percentage:F1}%", Foreground = MakeBrush("#888888"), FontSize = 10, TextAlignment = TextAlignment.Right });

            var barBg = new Border { Height = 3, Width = 100, CornerRadius = new CornerRadius(2), Background = MakeBrush("#333333"), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            barBg.Child = new Border { Height = 3, Width = barWidth, CornerRadius = new CornerRadius(2), Background = brush, HorizontalAlignment = HorizontalAlignment.Left };
            amountPanel.Children.Add(barBg);
            Grid.SetColumn(amountPanel, 2);

            grid.Children.Add(icon);
            grid.Children.Add(namePanel);
            grid.Children.Add(amountPanel);
            border.Child = grid;
            return border;
        }

        private void ShowGroupDetail(CostGroup group)
        {
            _contentPanel.Children.Clear();

            var backBtn = new Button { Content = "< Back", Background = Brushes.Transparent, Foreground = MakeBrush("#0078d4"), BorderThickness = new Thickness(0), FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            backBtn.Click += (s, e) => UpdateContent();
            _contentPanel.Children.Add(backBtn);

            _contentPanel.Children.Add(new TextBlock { Text = $"{group.Name} -- RM {group.TotalCost:N0}", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
            _contentPanel.Children.Add(CreateDetailRow("Item", "Qty", "Unit RM", "Total RM", true));

            var grouped = group.Items
                .Where(i => !string.IsNullOrEmpty(i.JkrCode))
                .GroupBy(i => i.JkrCode)
                .Select(g => new { Code = g.Key, Name = g.First().Name, Qty = g.Sum(i => i.Quantity), Unit = g.First().Unit, UnitPrice = g.First().UnitPrice, Total = g.Sum(i => i.TotalPrice) })
                .OrderByDescending(x => x.Total);

            foreach (var item in grouped)
            {
                string displayName = $"{item.Code} - {TruncateName(item.Name, 30)}";
                _contentPanel.Children.Add(CreateDetailRow(displayName, $"{item.Qty:F1}", item.UnitPrice > 0 ? $"{item.UnitPrice:N0}" : "-", item.Total > 0 ? $"{item.Total:N0}" : "-"));
            }

            var noCode = group.Items.Where(i => string.IsNullOrEmpty(i.JkrCode)).ToList();
            if (noCode.Any())
                _contentPanel.Children.Add(new TextBlock { Text = $"{noCode.Count} items without JKR code", Foreground = MakeBrush("#666666"), FontSize = 11, Margin = new Thickness(0, 8, 0, 4) });
        }

        private Grid CreateDetailRow(string name, string qty, string price, string total, bool isHeader = false)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            var fg = MakeBrush(isHeader ? "#888888" : "#cccccc");
            var fw = isHeader ? FontWeights.SemiBold : FontWeights.Normal;
            double fs = isHeader ? 10 : 12;

            var t1 = new TextBlock { Text = name, Foreground = fg, FontWeight = fw, FontSize = fs };
            var t2 = new TextBlock { Text = qty, Foreground = fg, FontSize = fs, TextAlignment = TextAlignment.Center };
            var t3 = new TextBlock { Text = price, Foreground = isHeader ? fg : MakeBrush("#4a9eff"), FontSize = fs, TextAlignment = TextAlignment.Right };
            var t4 = new TextBlock { Text = total, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = fs, TextAlignment = TextAlignment.Right };

            Grid.SetColumn(t1, 0); Grid.SetColumn(t2, 1); Grid.SetColumn(t3, 2); Grid.SetColumn(t4, 3);
            grid.Children.Add(t1); grid.Children.Add(t2); grid.Children.Add(t3); grid.Children.Add(t4);
            return grid;
        }

        private string TruncateName(string name, int maxLen)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Length <= maxLen ? name : name.Substring(0, maxLen) + "...";
        }

        // --- Live Update ---

        public void OnModelChanged(ChangeSummary changeSummary)
        {
            try
            {
                if (_uiApp?.ActiveUIDocument?.Document == null) return;
                _previousTotal = _summary?.GrandTotal ?? 0;
                _previousItemCount = _summary?.TotalItems ?? 0;
                RefreshData();
                double newTotal = _summary?.GrandTotal ?? 0;
                double delta = newTotal - _previousTotal;
                int itemDelta = (_summary?.TotalItems ?? 0) - _previousItemCount;

                string changeText = changeSummary.ToNotificationText();
                string deltaText = BuildDeltaText(delta, itemDelta);

                string logEntry = $"{DateTime.Now:HH:mm} - {changeText}";
                if (!string.IsNullOrEmpty(deltaText)) logEntry += $" > {deltaText}";
                _recentChanges.Insert(0, logEntry);
                if (_recentChanges.Count > MaxRecentChanges) _recentChanges.RemoveAt(_recentChanges.Count - 1);

                ShowChangeBanner(changeText, deltaText, delta);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] OnModelChanged error: {ex.Message}");
            }
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

            if (costDelta > 0) _changeBanner.Background = MakeBrush("#1a2a3a");
            else if (costDelta < 0) _changeBanner.Background = MakeBrush("#1a3a1a");
            else _changeBanner.Background = MakeBrush("#2a2a1a");

            _changeBanner.Visibility = Visibility.Visible;
            _bannerAutoHideTimer.Stop();
            _bannerAutoHideTimer.Start();
        }

        // --- Event Handlers ---

        private void ChangeBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_recentChanges.Count == 0) return;
            MessageBox.Show(string.Join("\n", _recentChanges), "Recent Model Changes", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void DismissBanner_Click(object sender, RoutedEventArgs e)
        {
            _bannerAutoHideTimer.Stop();
            _changeBanner.Visibility = Visibility.Collapsed;
        }

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            _showByLevel = _byLevelRadio.IsChecked == true;
            UpdateContent();
        }

        private void LevelFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_levelFilter.SelectedItem is ComboBoxItem item)
            {
                string level = item.Content.ToString();
                if (level == "All Levels")
                    _summary = CostCalculator.Calculate(_allItems);
                else
                    _summary = CostCalculator.CalculateForLevel(_allItems, level);
                UpdateTotalCard();
                UpdateContent();
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshData();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_allItems.Count == 0) { MessageBox.Show("No data to export. Click Refresh first.", "BINA Cost"); return; }
                Document doc = _uiApp?.ActiveUIDocument?.Document;
                string projectName = Path.GetFileNameWithoutExtension(doc?.PathName ?? "Untitled");
                var saveDialog = new Microsoft.Win32.SaveFileDialog { Title = "Export Cost Items", Filter = "Excel Files (*.xlsx)|*.xlsx", FileName = $"{projectName}_CostExport_{DateTime.Now:yyyyMMdd}", DefaultExt = ".xlsx" };
                if (saveDialog.ShowDialog() == true)
                {
                    ExcelService.Export(_allItems, saveDialog.FileName, projectName);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = saveDialog.FileName, UseShellExecute = true });
                }
            }
            catch (Exception ex) { MessageBox.Show($"Export failed: {ex.Message}", "BINA Cost"); }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new Microsoft.Win32.OpenFileDialog { Title = "Import Prices from Excel", Filter = "Excel Files (*.xlsx)|*.xlsx" };
                if (openDialog.ShowDialog() == true)
                {
                    var prices = ExcelService.ImportPrices(openDialog.FileName);
                    if (prices.Count == 0) { MessageBox.Show("No prices found in the file.\n\nMake sure the 'JKR Code' and 'Unit Price' columns have data.", "BINA Cost"); return; }
                    _priceDb?.ImportPrices(prices, "imported");
                    _priceDb?.Save();
                    _priceDb?.ApplyPrices(_allItems);
                    _summary = CostCalculator.Calculate(_allItems);
                    UpdateTotalCard();
                    UpdateContent();
                    MessageBox.Show($"Imported {prices.Count} prices.\nTotal: RM {_summary.GrandTotal:N0}", "BINA Cost");
                }
            }
            catch (Exception ex) { MessageBox.Show($"Import failed: {ex.Message}", "BINA Cost"); }
        }

        private async void AutoMatch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_allItems.Count == 0) { MessageBox.Show("No data loaded. Click Refresh first.", "BINA Cost"); return; }
                var masterDb = MasterPriceDatabase.Instance;
                if (masterDb.Count == 0) { MessageBox.Show("Master price database is empty.\n\nImport prices first using 'Import to Master' button.", "BINA Cost"); return; }
                int matched = masterDb.AutoMatchPrices(_allItems, _priceDb);
                _summary = CostCalculator.Calculate(_allItems);
                UpdateTotalCard();
                UpdateContent();
                int remaining = _allItems.Count(i => i.UnitPrice <= 0);
                MessageBox.Show($"Matched {matched} items from master database.\nTotal: RM {_summary.GrandTotal:N0}\n{remaining} items still need prices.", "BINA Cost");
            }
            catch (Exception ex) { MessageBox.Show($"Auto-match failed: {ex.Message}", "BINA Cost"); }
        }

        private async void AIInsights_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_summary == null || _allItems.Count == 0) { MessageBox.Show("No data loaded. Click Refresh first.", "BINA Cost"); return; }
                ShowOfflineInsights();
            }
            catch (Exception ex) { MessageBox.Show($"Analysis failed: {ex.Message}", "BINA Cost"); }
        }

        private void ShowOfflineInsights()
        {
            var lines = new List<string>();
            lines.Add("Cost Analysis\n");
            int pricedPct = _summary.TotalItems > 0 ? (int)((_summary.PricedItems / (double)_summary.TotalItems) * 100) : 0;
            lines.Add($"Pricing coverage: {pricedPct}% ({_summary.PricedItems}/{_summary.TotalItems})");
            double floorArea = _allItems.Where(i => i.Category == "Floors" && i.Unit == "m2").Sum(i => i.Quantity);
            if (floorArea > 0 && _summary.GrandTotal > 0)
            {
                double costPerM2 = _summary.GrandTotal / floorArea;
                lines.Add($"Cost per m2: RM {costPerM2:N0}");
                if (costPerM2 < 1500) lines.Add("   Below typical range (RM 1,500-3,000/m2)");
                else if (costPerM2 > 3000) lines.Add("   Above typical range (RM 1,500-3,000/m2)");
                else lines.Add("   Within typical JKR building range");
            }
            lines.Add("\nTop cost drivers:");
            foreach (var cat in _summary.ByCategory.Take(3))
                lines.Add($"   {cat.Name}: RM {cat.TotalCost:N0} ({cat.Percentage:F1}%)");
            int unpriced = _allItems.Count(i => i.UnitPrice <= 0);
            if (unpriced > 0) lines.Add($"\n{unpriced} items have no price");
            MessageBox.Show(string.Join("\n", lines), "BINA Cost - Analysis");
        }

        private void ImportMaster_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new Microsoft.Win32.OpenFileDialog { Title = "Import Prices to Master Database", Filter = "Excel Files (*.xlsx)|*.xlsx" };
                if (openDialog.ShowDialog() == true)
                {
                    var prices = ExcelService.ImportPrices(openDialog.FileName);
                    if (prices.Count == 0) { MessageBox.Show("No prices found in the file.", "BINA Cost"); return; }
                    var masterDb = MasterPriceDatabase.Instance;
                    var (added, updated) = masterDb.ImportEntries(prices);
                    masterDb.Save();
                    MessageBox.Show($"Master database updated!\n\nAdded: {added} new entries\nUpdated: {updated} existing entries\nTotal master entries: {masterDb.Count}\n\nThese prices will be available across all projects.", "BINA Cost");
                }
            }
            catch (Exception ex) { MessageBox.Show($"Master import failed: {ex.Message}", "BINA Cost"); }
        }
    }
}
