using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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

        // Color palette for categories/levels
        private static readonly string[] Colors = {
            "#4a9eff", "#4aff7a", "#ffaa4a", "#aa4aff",
            "#ff4aaa", "#4affff", "#aaaa4a", "#ff6666"
        };

        public CostDashboardPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Set the Revit application context (called when panel is shown)
        /// </summary>
        public void SetRevitApp(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }

        /// <summary>
        /// Refresh all cost data from the current model
        /// </summary>
        public void RefreshData()
        {
            try
            {
                if (_uiApp?.ActiveUIDocument?.Document == null)
                {
                    SubtitleText.Text = "No model loaded";
                    return;
                }

                Document doc = _uiApp.ActiveUIDocument.Document;
                string projectName = Path.GetFileNameWithoutExtension(doc.PathName ?? "Untitled");

                // Load price database
                _priceDb = new PriceDatabase(projectName);

                // Walk model
                _allItems = RevitModelWalker.GetAllItems(doc);

                // Apply prices
                _priceDb.ApplyPrices(_allItems);

                // Calculate summary
                _summary = CostCalculator.Calculate(_allItems);

                // Update UI
                UpdateHeader(projectName);
                UpdateTotalCard();
                UpdateLevelFilter(doc);
                UpdateContent();
            }
            catch (Exception ex)
            {
                SubtitleText.Text = $"Error: {ex.Message}";
            }
        }

        private void UpdateHeader(string projectName)
        {
            SubtitleText.Text = $"{projectName} — Updated: {DateTime.Now:HH:mm}";
        }

        private void UpdateTotalCard()
        {
            if (_summary == null) return;

            GrandTotalText.Text = $"RM {_summary.GrandTotal:N0}";
            ItemCountText.Text = $"📐 {_summary.TotalItems:N0} items";
            LevelCountText.Text = $"🏢 {_summary.LevelCount} levels";

            int pricedPct = _summary.TotalItems > 0
                ? (int)((_summary.PricedItems / (double)_summary.TotalItems) * 100)
                : 0;
            PricedPercentText.Text = $"✅ {pricedPct}% priced";
        }

        private void UpdateLevelFilter(Document doc)
        {
            LevelFilter.Items.Clear();
            LevelFilter.Items.Add(new ComboBoxItem { Content = "All Levels", IsSelected = true });

            var levels = RevitModelWalker.GetLevelNames(doc);
            foreach (var level in levels)
            {
                LevelFilter.Items.Add(new ComboBoxItem { Content = level });
            }
        }

        private void UpdateContent()
        {
            ContentPanel.Children.Clear();

            if (_summary == null || _allItems.Count == 0)
            {
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = "No items found. Click 'Refresh' to scan the model.",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666666")),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                });
                return;
            }

            var groups = _showByLevel ? _summary.ByLevel : _summary.ByCategory;
            double maxCost = groups.Any() ? groups.Max(g => g.TotalCost) : 1;

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                string color = Colors[i % Colors.Length];
                var row = CreateCostRow(group, color, maxCost);
                ContentPanel.Children.Add(row);
            }
        }

        private Border CreateCostRow(CostGroup group, string accentColor, double maxCost)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentColor));
            double barWidth = maxCost > 0 ? (group.TotalCost / maxCost) * 100 : 0;

            var border = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2a2a2a")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            // Hover effect
            border.MouseEnter += (s, e) => border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
            border.MouseLeave += (s, e) => border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2a2a2a"));

            // Click to expand/drill down
            border.MouseLeftButtonUp += (s, e) => ShowGroupDetail(group);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon
            var icon = new Border
            {
                Width = 32, Height = 32,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentColor + "33")),
                Child = new TextBlock
                {
                    Text = group.Name.Length > 0 ? group.Name.Substring(0, 1).ToUpper() : "?",
                    Foreground = brush,
                    FontSize = 14, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(icon, 0);

            // Name + count
            var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            namePanel.Children.Add(new TextBlock
            {
                Text = group.Name,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e0e0e0")),
                FontSize = 13, FontWeight = FontWeights.Medium
            });
            namePanel.Children.Add(new TextBlock
            {
                Text = $"{group.ItemCount} items",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888")),
                FontSize = 11
            });
            Grid.SetColumn(namePanel, 1);

            // Amount + percentage + bar
            var amountPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            amountPanel.Children.Add(new TextBlock
            {
                Text = $"RM {group.TotalCost:N0}",
                Foreground = Brushes.White,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right
            });
            amountPanel.Children.Add(new TextBlock
            {
                Text = $"{group.Percentage:F1}%",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888")),
                FontSize = 10,
                TextAlignment = TextAlignment.Right
            });

            // Progress bar
            var barBg = new Border
            {
                Height = 3, Width = 100,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333")),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var barFill = new Border
            {
                Height = 3,
                Width = barWidth,
                CornerRadius = new CornerRadius(2),
                Background = brush,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            barBg.Child = barFill;
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
            ContentPanel.Children.Clear();

            // Back button
            var backBtn = new Button
            {
                Content = $"← Back to {(_showByLevel ? "Levels" : "Categories")}",
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078d4")),
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8)
            };
            backBtn.Click += (s, e) => UpdateContent();
            ContentPanel.Children.Add(backBtn);

            // Group title
            ContentPanel.Children.Add(new TextBlock
            {
                Text = $"{group.Name} — RM {group.TotalCost:N0}",
                Foreground = Brushes.White,
                FontSize = 16, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Column headers
            var headerGrid = CreateDetailRow("Item", "Qty", "Unit RM", "Total RM", isHeader: true);
            ContentPanel.Children.Add(headerGrid);

            // Items grouped by JKR code to avoid duplicates
            var grouped = group.Items
                .Where(i => !string.IsNullOrEmpty(i.JkrCode))
                .GroupBy(i => i.JkrCode)
                .Select(g => new
                {
                    Code = g.Key,
                    Name = g.First().Name,
                    Qty = g.Sum(i => i.Quantity),
                    Unit = g.First().Unit,
                    UnitPrice = g.First().UnitPrice,
                    Total = g.Sum(i => i.TotalPrice)
                })
                .OrderByDescending(x => x.Total);

            foreach (var item in grouped)
            {
                string displayName = $"{item.Code} — {TruncateName(item.Name, 30)}";
                var row = CreateDetailRow(
                    displayName,
                    $"{item.Qty:F1}",
                    item.UnitPrice > 0 ? $"{item.UnitPrice:N0}" : "-",
                    item.Total > 0 ? $"{item.Total:N0}" : "-");
                ContentPanel.Children.Add(row);
            }

            // Items without JKR code
            var noCode = group.Items.Where(i => string.IsNullOrEmpty(i.JkrCode)).ToList();
            if (noCode.Any())
            {
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = $"\n{noCode.Count} items without JKR code",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666666")),
                    FontSize = 11,
                    Margin = new Thickness(0, 8, 0, 4)
                });
            }

            // Subtotal
            ContentPanel.Children.Add(new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#444444")),
                Padding = new Thickness(0, 8, 0, 0),
                Margin = new Thickness(0, 8, 0, 0),
                Child = new Grid
                {
                    Children =
                    {
                        new TextBlock { Text = "Subtotal", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13 },
                        new TextBlock
                        {
                            Text = $"RM {group.TotalCost:N0}",
                            Foreground = Brushes.White,
                            FontWeight = FontWeights.Bold,
                            FontSize = 13,
                            HorizontalAlignment = HorizontalAlignment.Right
                        }
                    }
                }
            });
        }

        private Grid CreateDetailRow(string name, string qty, string price, string total, bool isHeader = false)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            var fg = isHeader
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cccccc"));
            var fw = isHeader ? FontWeights.SemiBold : FontWeights.Normal;
            double fs = isHeader ? 10 : 12;

            var t1 = new TextBlock { Text = name, Foreground = fg, FontWeight = fw, FontSize = fs };
            var t2 = new TextBlock { Text = qty, Foreground = fg, FontSize = fs, TextAlignment = TextAlignment.Center };
            var t3 = new TextBlock { Text = price, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4a9eff")), FontSize = fs, TextAlignment = TextAlignment.Right };
            var t4 = new TextBlock { Text = total, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = fs, TextAlignment = TextAlignment.Right };

            if (isHeader) t3.Foreground = fg;

            Grid.SetColumn(t1, 0);
            Grid.SetColumn(t2, 1);
            Grid.SetColumn(t3, 2);
            Grid.SetColumn(t4, 3);

            grid.Children.Add(t1);
            grid.Children.Add(t2);
            grid.Children.Add(t3);
            grid.Children.Add(t4);

            return grid;
        }

        private string TruncateName(string name, int maxLen)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Length <= maxLen ? name : name.Substring(0, maxLen) + "...";
        }

        // --- Event Handlers ---

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            _showByLevel = ByLevelRadio.IsChecked == true;
            UpdateContent();
        }

        private void LevelFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LevelFilter.SelectedItem is ComboBoxItem item)
            {
                string level = item.Content.ToString();
                if (level == "All Levels")
                {
                    _summary = CostCalculator.Calculate(_allItems);
                }
                else
                {
                    _summary = CostCalculator.CalculateForLevel(_allItems, level);
                }
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
                if (_allItems.Count == 0)
                {
                    MessageBox.Show("No data to export. Click Refresh first.", "BINA Cost");
                    return;
                }

                Document doc = _uiApp?.ActiveUIDocument?.Document;
                string projectName = Path.GetFileNameWithoutExtension(doc?.PathName ?? "Untitled");

                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export Cost Items",
                    Filter = "Excel Files (*.xlsx)|*.xlsx",
                    FileName = $"{projectName}_CostExport_{DateTime.Now:yyyyMMdd}",
                    DefaultExt = ".xlsx"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    ExcelService.Export(_allItems, saveDialog.FileName, projectName);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = saveDialog.FileName,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "BINA Cost");
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import Prices from Excel",
                    Filter = "Excel Files (*.xlsx)|*.xlsx"
                };

                if (openDialog.ShowDialog() == true)
                {
                    var prices = ExcelService.ImportPrices(openDialog.FileName);
                    if (prices.Count == 0)
                    {
                        MessageBox.Show("No prices found in the file.", "BINA Cost");
                        return;
                    }

                    _priceDb?.ImportPrices(prices, "imported");
                    _priceDb?.Save();

                    // Re-apply and refresh
                    _priceDb?.ApplyPrices(_allItems);
                    _summary = CostCalculator.Calculate(_allItems);
                    UpdateTotalCard();
                    UpdateContent();

                    MessageBox.Show($"Imported {prices.Count} prices.\nTotal: RM {_summary.GrandTotal:N0}", "BINA Cost");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "BINA Cost");
            }
        }
    }
}
