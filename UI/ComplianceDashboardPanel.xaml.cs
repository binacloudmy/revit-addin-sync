using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace RevitWebAppSync.UI
{
    public partial class ComplianceDashboardPanel : Page
    {
        private UIApplication _uiApp;
        private BuildingComplianceData _buildingData;
        private ComplianceService _complianceService;
        private string _selectedPurposeGroup = null;
        private List<ComplianceIssue> _issues = new List<ComplianceIssue>();
        private string _aiReport = "";
        private List<AIRecommendationDto> _aiRecommendations = new List<AIRecommendationDto>();

        // UI refs
        private TextBlock _subtitleText;
        private TextBlock _summaryPassText;
        private TextBlock _summaryFailText;
        private TextBlock _summaryUnknownText;
        private TextBlock _storeyText;
        private TextBlock _heightText;
        private TextBlock _areaText;
        private StackPanel _contentPanel;
        private ComboBox _purposeGroupCombo;
        private ProgressBar _complianceBar;
        private TextBlock _compliancePercText;
        private TextBox _askTextBox;
        private StackPanel _chatPanel;
        private Border _statusBanner;
        private TextBlock _statusText;

        // Colors matching cost dashboard
        private static readonly Color PrimaryRed = Color.FromRgb(200, 50, 50);
        private static readonly Color HeaderBg = Color.FromRgb(180, 40, 40);
        private static readonly Color CardBg = Color.FromRgb(255, 255, 255);
        private static readonly Color PageBg = Color.FromRgb(241, 241, 241);
        private static readonly Color BorderColor = Color.FromRgb(217, 217, 217);
        private static readonly Color TextPrimary = Color.FromRgb(51, 51, 51);
        private static readonly Color TextSecondary = Color.FromRgb(102, 102, 102);
        private static readonly Color TextMuted = Color.FromRgb(153, 153, 153);
        private static readonly Color SuccessGreen = Color.FromRgb(16, 124, 16);
        private static readonly Color WarningAmber = Color.FromRgb(255, 140, 0);
        private static readonly Color FailRed = Color.FromRgb(220, 53, 69);
        private static readonly Color PassGreen = Color.FromRgb(40, 167, 69);

        public ComplianceDashboardPanel()
        {
            InitializeComponent();
            _complianceService = new ComplianceService();
            BuildUI();
        }

        private void BuildUI()
        {
            var root = new Grid { Background = new SolidColorBrush(PageBg) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Status banner
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Summary card
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Purpose group selector
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Results/Chat
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Ask box
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Actions

            // ── Row 0: Header ──
            var header = new Border
            {
                Background = new LinearGradientBrush(HeaderBg, Color.FromRgb(140, 30, 30), new Point(0, 0), new Point(1, 0)),
                Padding = new Thickness(16, 12, 16, 12)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerLeft = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = "🔥", FontSize = 16, Margin = new Thickness(0, 0, 6, 0) });
            titleRow.Children.Add(new TextBlock { Text = "BINA", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0) });
            titleRow.Children.Add(new TextBlock { Text = "Fire Compliance", FontSize = 16, FontWeight = FontWeights.Light, Foreground = new SolidColorBrush(Color.FromRgb(240, 180, 180)) });
            headerLeft.Children.Add(titleRow);

            _subtitleText = new TextBlock { Text = "UKBS 1984 — Jadual 5-11", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(230, 150, 150)), Margin = new Thickness(0, 2, 0, 0) };
            headerLeft.Children.Add(_subtitleText);
            Grid.SetColumn(headerLeft, 0);
            headerGrid.Children.Add(headerLeft);

            var badge = new Border { Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center };
            badge.Child = new TextBlock { Text = "v1.0", FontSize = 9, Foreground = Brushes.White };
            Grid.SetColumn(badge, 1);
            headerGrid.Children.Add(badge);

            header.Child = headerGrid;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ── Row 1: Status banner ──
            _statusBanner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(235, 243, 252)),
                Padding = new Thickness(12, 6, 12, 6),
                Visibility = Visibility.Collapsed
            };
            _statusText = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(TextPrimary) };
            _statusBanner.Child = _statusText;
            Grid.SetRow(_statusBanner, 1);
            root.Children.Add(_statusBanner);

            // ── Row 2: Summary card ──
            var summaryCard = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Margin = new Thickness(12, 12, 12, 0), Padding = new Thickness(16)
            };
            var summaryStack = new StackPanel();

            // Compliance percentage bar
            var complianceHeader = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            complianceHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            complianceHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            complianceHeader.Children.Add(new TextBlock { Text = "Compliance Score", FontSize = 11, Foreground = new SolidColorBrush(TextSecondary), FontWeight = FontWeights.Medium });
            _compliancePercText = new TextBlock { Text = "—", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(TextMuted), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_compliancePercText, 1);
            complianceHeader.Children.Add(_compliancePercText);
            summaryStack.Children.Add(complianceHeader);

            var barBg = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(230, 230, 230)), Margin = new Thickness(0, 0, 0, 12) };
            _complianceBar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = 0, Foreground = new SolidColorBrush(SuccessGreen), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            barBg.Child = _complianceBar;
            summaryStack.Children.Add(barBg);

            // Stats: Pass / Fail / Unknown
            var statsGrid = new Grid();
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _summaryPassText = MakeStatBlock("0", "✅ Pass", PassGreen);
            Grid.SetColumn(_summaryPassText, 0);
            statsGrid.Children.Add(_summaryPassText);

            _summaryFailText = MakeStatBlock("0", "❌ Fail", FailRed);
            Grid.SetColumn(_summaryFailText, 1);
            statsGrid.Children.Add(_summaryFailText);

            _summaryUnknownText = MakeStatBlock("0", "❓ No Data", TextMuted);
            Grid.SetColumn(_summaryUnknownText, 2);
            statsGrid.Children.Add(_summaryUnknownText);

            summaryStack.Children.Add(statsGrid);

            // Building info row
            var infoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _storeyText = new TextBlock { Text = "— storeys", FontSize = 10, Foreground = new SolidColorBrush(TextMuted), Margin = new Thickness(0, 0, 12, 0) };
            _heightText = new TextBlock { Text = "— m height", FontSize = 10, Foreground = new SolidColorBrush(TextMuted), Margin = new Thickness(0, 0, 12, 0) };
            _areaText = new TextBlock { Text = "— m² total", FontSize = 10, Foreground = new SolidColorBrush(TextMuted) };
            infoRow.Children.Add(_storeyText);
            infoRow.Children.Add(_heightText);
            infoRow.Children.Add(_areaText);
            summaryStack.Children.Add(infoRow);

            summaryCard.Child = summaryStack;
            Grid.SetRow(summaryCard, 2);
            root.Children.Add(summaryCard);

            // ── Row 3: Purpose group selector ──
            var selectorCard = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Margin = new Thickness(12, 8, 12, 0), Padding = new Thickness(10, 6, 10, 6)
            };
            var selectorRow = new StackPanel { Orientation = Orientation.Horizontal };
            selectorRow.Children.Add(new TextBlock { Text = "Purpose Group:", FontSize = 10, Foreground = new SolidColorBrush(TextSecondary), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _purposeGroupCombo = new ComboBox { MinWidth = 200, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            _purposeGroupCombo.Items.Add(new ComboBoxItem { Content = "Select purpose group...", IsSelected = true });
            _purposeGroupCombo.Items.Add(new ComboBoxItem { Content = "I — Small residential", Tag = "I" });
            _purposeGroupCombo.Items.Add(new ComboBoxItem { Content = "II — Institutional (hospital, school)", Tag = "II" });
            _purposeGroupCombo.Items.Add(new ComboBoxItem { Content = "III — Other residential (hotel, hostel)", Tag = "III" });
            _purposeGroupCombo.Items.Add(new ComboBoxItem { Content = "IV — Office", Tag = "IV" });
            _purposeGroupCombo.Items.Add(new ComboBoxItem { Content = "V — Shop", Tag = "V" });
            _purposeGroupCombo.Items.Add(new ComboBoxItem { Content = "VI — Factory", Tag = "VI" });
            _purposeGroupCombo.Items.Add(new ComboBoxItem { Content = "VII — Place of assembly", Tag = "VII" });
            _purposeGroupCombo.Items.Add(new ComboBoxItem { Content = "VIII — Storage and general", Tag = "VIII" });
            _purposeGroupCombo.SelectionChanged += PurposeGroup_Changed;
            selectorRow.Children.Add(_purposeGroupCombo);
            selectorCard.Child = selectorRow;
            Grid.SetRow(selectorCard, 3);
            root.Children.Add(selectorCard);

            // ── Row 4: Content (issues list + chat) ──
            var scroll = new ScrollViewer { Margin = new Thickness(12, 8, 12, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _contentPanel = new StackPanel();
            _contentPanel.Children.Add(new TextBlock
            {
                Text = "Select a purpose group and click Check Compliance to scan the model.",
                Foreground = new SolidColorBrush(TextMuted), FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 40, 0, 0)
            });
            scroll.Content = _contentPanel;
            Grid.SetRow(scroll, 4);
            root.Children.Add(scroll);

            // ── Row 5: Ask compliance question ──
            var askCard = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Margin = new Thickness(12, 4, 12, 4), Padding = new Thickness(8)
            };
            var askRow = new Grid();
            askRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            askRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _askTextBox = new TextBox
            {
                FontSize = 11,
                Padding = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(248, 248, 248)),
                Text = "",
                VerticalAlignment = VerticalAlignment.Center
            };
            // Placeholder behaviour
            _askTextBox.GotFocus += (s, e) => { if (_askTextBox.Foreground is SolidColorBrush b && b.Color == TextMuted) { _askTextBox.Text = ""; _askTextBox.Foreground = new SolidColorBrush(TextPrimary); } };
            _askTextBox.LostFocus += (s, e) => { if (string.IsNullOrWhiteSpace(_askTextBox.Text)) { _askTextBox.Text = "Ask about UKBS compliance..."; _askTextBox.Foreground = new SolidColorBrush(TextMuted); } };
            _askTextBox.Text = "Ask about UKBS compliance...";
            _askTextBox.Foreground = new SolidColorBrush(TextMuted);
            _askTextBox.KeyDown += AskTextBox_KeyDown;
            Grid.SetColumn(_askTextBox, 0);
            askRow.Children.Add(_askTextBox);

            var askBtn = new Button
            {
                Content = "Ask", FontSize = 11, Padding = new Thickness(12, 4, 12, 4),
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)), Foreground = Brushes.White,
                BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            askBtn.Click += AskButton_Click;
            Grid.SetColumn(askBtn, 1);
            askRow.Children.Add(askBtn);
            askCard.Child = askRow;
            Grid.SetRow(askCard, 5);
            root.Children.Add(askCard);

            // ── Row 6: Actions ──
            var actionBar = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 8, 10, 8)
            };
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            actionRow.Children.Add(MakeActionButton("Check Compliance", CheckCompliance_Click, PrimaryRed, true));
            actionRow.Children.Add(MakeActionButton("Refresh", Refresh_Click, Color.FromRgb(0, 120, 215), false));
            actionBar.Child = actionRow;
            Grid.SetRow(actionBar, 6);
            root.Children.Add(actionBar);

            this.Content = root;
        }

        // ── Helpers ──

        private TextBlock MakeStatBlock(string value, string label, Color color)
        {
            var tb = new TextBlock { TextAlignment = TextAlignment.Center };
            tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color) });
            tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            tb.Inlines.Add(new System.Windows.Documents.Run(label) { FontSize = 9, Foreground = new SolidColorBrush(TextMuted) });
            tb.Tag = label;
            return tb;
        }

        private void UpdateStatBlock(TextBlock tb, string value, Color color)
        {
            string label = tb.Tag as string ?? "";
            tb.Inlines.Clear();
            tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color) });
            tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            tb.Inlines.Add(new System.Windows.Documents.Run(label) { FontSize = 9, Foreground = new SolidColorBrush(TextMuted) });
        }

        private Button MakeActionButton(string text, RoutedEventHandler click, Color bg, bool primary)
        {
            var btn = new Button
            {
                Content = text, FontSize = 11, Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(bg), Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(bg), BorderThickness = new Thickness(1),
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += click;
            return btn;
        }

        private void ShowStatus(string text, Color bg)
        {
            _statusBanner.Background = new SolidColorBrush(bg);
            _statusText.Text = text;
            _statusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => _statusBanner.Visibility = Visibility.Collapsed;

        // ── Events ──

        public void SetRevitApp(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }

        private void PurposeGroup_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_purposeGroupCombo.SelectedItem is ComboBoxItem item && item.Tag is string pg)
                _selectedPurposeGroup = pg;
            else
                _selectedPurposeGroup = null;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => ScanModel();

        private void ScanModel()
        {
            try
            {
                if (_uiApp?.ActiveUIDocument?.Document == null) { ShowStatus("No model loaded", WarningAmber); return; }
                _buildingData = BuildingInfoExtractor.Extract(_uiApp.ActiveUIDocument.Document);
                _buildingData.PurposeGroup = _selectedPurposeGroup;

                _subtitleText.Text = $"{_buildingData.ProjectName}  |  {DateTime.Now:HH:mm}";
                _storeyText.Text = $"{_buildingData.StoreyCount} storeys";
                _heightText.Text = $"{_buildingData.BuildingHeightM:F1} m";
                _areaText.Text = $"{_buildingData.TotalFloorAreaM2:N0} m²";

                ShowStatus($"Scanned: {_buildingData.TotalElements} elements ({_buildingData.Walls.Count} walls, {_buildingData.Doors.Count} doors, {_buildingData.Floors.Count} floors, {_buildingData.Stairs.Count} stairs)", Color.FromRgb(235, 243, 252));
            }
            catch (Exception ex)
            {
                ShowStatus($"Scan error: {ex.Message}", Color.FromRgb(253, 235, 208));
            }
        }

        private async void CheckCompliance_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            try
            {
                if (_selectedPurposeGroup == null)
                {
                    ShowStatus("⚠️ Please select a Purpose Group first", WarningAmber);
                    return;
                }

                if (btn != null) { btn.IsEnabled = false; btn.Content = "⏳ Checking..."; }

                // Scan model if not done yet
                if (_buildingData == null) ScanModel();
                if (_buildingData == null) return;
                _buildingData.PurposeGroup = _selectedPurposeGroup;

                ShowStatus("🔍 Checking fire compliance against UKBS 1984...", Color.FromRgb(235, 243, 252));
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                // Build model check request with all elements
                var request = new ModelCheckRequest
                {
                    PurposeGroup = _selectedPurposeGroup,
                    Storeys = _buildingData.StoreyCount,
                    HeightM = _buildingData.BuildingHeightM,
                    FloorAreaM2 = _buildingData.TotalFloorAreaM2,
                    IsSprinklered = false, // TODO: detect from model
                    Elements = new List<ModelCheckElement>()
                };

                // Add walls (deduplicated by type per level)
                var wallsByTypeLevel = _buildingData.Walls
                    .GroupBy(w => $"{w.TypeName}|{w.LevelName}")
                    .Select(g => g.First());
                foreach (var w in wallsByTypeLevel)
                    request.Elements.Add(new ModelCheckElement
                    {
                        ElementId = w.ElementId, Category = "Walls",
                        TypeName = w.TypeName, FamilyName = w.FamilyName,
                        LevelName = w.LevelName, FireRating = w.FireRating,
                        ThicknessMm = w.ThicknessMm
                    });

                // Add floors
                var floorsByType = _buildingData.Floors.GroupBy(f => f.TypeName).Select(g => g.First());
                foreach (var f in floorsByType)
                    request.Elements.Add(new ModelCheckElement
                    {
                        ElementId = f.ElementId, Category = "Floors",
                        TypeName = f.TypeName, LevelName = f.LevelName,
                        FireRating = f.FireRating, AreaM2 = f.AreaM2
                    });

                // Add stairs
                foreach (var s in _buildingData.Stairs)
                    request.Elements.Add(new ModelCheckElement
                    {
                        ElementId = s.ElementId, Category = "Stairs",
                        TypeName = s.TypeName, LevelName = s.LevelName,
                        WidthMm = s.WidthMm
                    });

                ShowStatus($"🔍 Checking {request.Elements.Count} elements against UKBS 1984...", Color.FromRgb(235, 243, 252));
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                // Call the deterministic check-model endpoint
                var response = await _complianceService.CheckModelAsync(request);

                if (!string.IsNullOrEmpty(response.Error))
                {
                    ShowStatus($"❌ Server error: {response.Error}", Color.FromRgb(253, 235, 208));
                    return;
                }

                // Convert to display issues
                _issues = new List<ComplianceIssue>();
                foreach (var req in response.BuildingRequirements)
                    _issues.Add(DtoToIssue(req));
                foreach (var elem in response.ElementIssues)
                    _issues.Add(DtoToIssue(elem));

                // Store AI results
                _aiReport = response.AIReport ?? "";
                _aiRecommendations = response.AIRecommendations ?? new List<AIRecommendationDto>();

                UpdateSummary();
                UpdateIssuesList();

                int fails = _issues.Count(i => i.Status == "fail");
                int total = response.Summary.ContainsKey("total_elements_checked")
                    ? Convert.ToInt32(response.Summary["total_elements_checked"]) : 0;
                if (fails > 0)
                    ShowStatus($"🔥 {fails} compliance issue(s) found in {total} elements!", Color.FromRgb(253, 220, 220));
                else
                    ShowStatus($"✅ {total} elements checked — all comply with UKBS 1984", Color.FromRgb(223, 246, 221));
            }
            catch (Exception ex)
            {
                ShowStatus($"Error: {ex.Message}", Color.FromRgb(253, 235, 208));
            }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "Check Compliance"; }
            }
        }

        private ComplianceIssue DtoToIssue(ComplianceIssueDto dto)
        {
            return new ComplianceIssue
            {
                Query = dto.Rule ?? "",
                Status = dto.Status ?? "info",
                Schedule = dto.Schedule ?? "",
                Section = dto.Bylaw ?? "",
                Content = dto.Reason ?? "",
                Issue = dto.Status == "fail" ? $"{dto.Actual} — Required: {dto.RequiredValue}" : null,
                Bylaws = dto.Bylaw ?? "",
                ElementId = dto.ElementId,
                TableSource = dto.TableSource ?? "",
                RequiredValue = dto.RequiredValue,
                ActualValue = dto.ActualValue,
            };
        }

        // Kept for reference but no longer used by check-model flow
        private List<ComplianceCheckItem> BuildComplianceQueries()
        {
            var items = new List<ComplianceCheckItem>();
            string pg = _selectedPurposeGroup;

            // Fire resistance for walls
            var wallTypes = _buildingData.Walls
                .GroupBy(w => w.TypeName)
                .Select(g => g.First());
            foreach (var wall in wallTypes)
            {
                items.Add(new ComplianceCheckItem
                {
                    Query = $"minimum fire resistance for wall {wall.TypeName} in {_buildingData.StoreyCount} storey building",
                    PurposeGroup = pg,
                    Storeys = _buildingData.StoreyCount,
                    HeightM = _buildingData.BuildingHeightM,
                });
            }

            // Fire resistance for floors
            var floorTypes = _buildingData.Floors
                .GroupBy(f => f.TypeName)
                .Select(g => g.First());
            foreach (var floor in floorTypes)
            {
                items.Add(new ComplianceCheckItem
                {
                    Query = $"minimum fire resistance for floor slab {floor.TypeName}",
                    PurposeGroup = pg,
                    Storeys = _buildingData.StoreyCount,
                });
            }

            // Fire-rated doors
            items.Add(new ComplianceCheckItem
            {
                Query = $"fire rated door requirements for purpose group {pg}",
                PurposeGroup = pg,
                Storeys = _buildingData.StoreyCount,
            });

            // Staircase width (Jadual 11)
            if (_buildingData.Stairs.Any())
            {
                items.Add(new ComplianceCheckItem
                {
                    Query = $"minimum staircase width and landing depth requirements",
                    PurposeGroup = pg,
                });
            }

            // Travel distance (Jadual 7)
            items.Add(new ComplianceCheckItem
            {
                Query = $"maximum travel distance for purpose group {pg}",
                PurposeGroup = pg,
            });

            // Fire alarm and extinguishment (Jadual 10)
            items.Add(new ComplianceCheckItem
            {
                Query = $"fire alarm and extinguishment system requirements for purpose group {pg}",
                PurposeGroup = pg,
                Storeys = _buildingData.StoreyCount,
                FloorAreaM2 = _buildingData.TotalFloorAreaM2,
            });

            return items;
        }

        private List<ComplianceIssue> ProcessComplianceResults(ComplianceCheckResponse response)
        {
            var issues = new List<ComplianceIssue>();

            foreach (var result in response.Results)
            {
                if (!result.Matches.Any()) continue;

                var topMatch = result.Matches.First();
                issues.Add(new ComplianceIssue
                {
                    Query = result.Query,
                    Status = topMatch.Similarity >= 0.55 ? "info" : "unknown",
                    Schedule = topMatch.Schedule,
                    Section = topMatch.Section,
                    Content = topMatch.Content,
                    Similarity = topMatch.Similarity,
                    Bylaws = topMatch.Metadata?.ContainsKey("bylaws") == true
                        ? string.Join(", ", ((Newtonsoft.Json.Linq.JArray)topMatch.Metadata["bylaws"]).Select(b => b.ToString()))
                        : "",
                });
            }

            // Check walls fire ratings against requirements
            foreach (var wall in _buildingData.Walls)
            {
                if (string.IsNullOrEmpty(wall.FireRating))
                {
                    issues.Add(new ComplianceIssue
                    {
                        Query = $"Wall: {wall.TypeName} (Level: {wall.LevelName})",
                        Status = "fail",
                        Schedule = "Ninth Schedule",
                        Content = $"No fire rating assigned. UKBS 1984 requires all structural elements to have fire resistance ratings as per Ninth Schedule.",
                        Issue = "Missing fire rating parameter",
                        ElementId = wall.ElementId,
                    });
                }
            }

            // Check stairs width (Jadual 11: min 1.2m)
            foreach (var stair in _buildingData.Stairs)
            {
                if (stair.WidthMm.HasValue && stair.WidthMm < 1200)
                {
                    issues.Add(new ComplianceIssue
                    {
                        Query = $"Staircase: {stair.TypeName} (Level: {stair.LevelName})",
                        Status = "fail",
                        Schedule = "Eleventh Schedule",
                        Content = $"Staircase width {stair.WidthMm}mm is below minimum 1,200mm per Eleventh Schedule [By-law 224(8)(b)].",
                        Issue = $"Width {stair.WidthMm}mm < 1,200mm minimum",
                        ElementId = stair.ElementId,
                    });
                }
            }

            return issues;
        }

        private void UpdateSummary()
        {
            int pass = _issues.Count(i => i.Status == "pass");
            int fail = _issues.Count(i => i.Status == "fail");
            int unknown = _issues.Count(i => i.Status != "pass" && i.Status != "fail");

            UpdateStatBlock(_summaryPassText, pass.ToString(), PassGreen);
            UpdateStatBlock(_summaryFailText, fail.ToString(), FailRed);
            UpdateStatBlock(_summaryUnknownText, unknown.ToString(), TextMuted);

            int total = pass + fail + unknown;
            int pct = total > 0 ? (int)((pass / (double)total) * 100) : 0;
            _compliancePercText.Text = $"{pct}%";
            _compliancePercText.Foreground = new SolidColorBrush(pct >= 80 ? PassGreen : pct >= 50 ? WarningAmber : FailRed);
            _complianceBar.Value = pct;
            _complianceBar.Foreground = new SolidColorBrush(pct >= 80 ? PassGreen : pct >= 50 ? WarningAmber : FailRed);
        }

        private void UpdateIssuesList()
        {
            _contentPanel.Children.Clear();

            // Failures first
            var failures = _issues.Where(i => i.Status == "fail").ToList();
            if (failures.Any())
            {
                _contentPanel.Children.Add(new TextBlock
                {
                    Text = $"❌ NON-COMPLIANT ({failures.Count})",
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(FailRed),
                    Margin = new Thickness(0, 8, 0, 4)
                });
                foreach (var issue in failures)
                    _contentPanel.Children.Add(CreateIssueCard(issue));
            }

            // AI Report card
            if (!string.IsNullOrEmpty(_aiReport))
            {
                var reportCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(250, 245, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 10, 0, 4),
                    Padding = new Thickness(12, 10, 12, 10)
                };
                var reportStack = new StackPanel();
                reportStack.Children.Add(new TextBlock
                {
                    Text = "🤖 AI Compliance Report",
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 33, 168)),
                    Margin = new Thickness(0, 0, 0, 6)
                });
                reportStack.Children.Add(new TextBlock
                {
                    Text = _aiReport,
                    FontSize = 10, Foreground = new SolidColorBrush(TextPrimary),
                    TextWrapping = TextWrapping.Wrap
                });
                reportCard.Child = reportStack;
                _contentPanel.Children.Add(reportCard);
            }

            // AI Recommendations per failed element
            if (_aiRecommendations.Any())
            {
                _contentPanel.Children.Add(new TextBlock
                {
                    Text = $"🔧 AI FIX SUGGESTIONS ({_aiRecommendations.Count})",
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(168, 85, 247)),
                    Margin = new Thickness(0, 12, 0, 4)
                });

                foreach (var rec in _aiRecommendations)
                {
                    var recCard = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247)),
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 0, 0, 4),
                        Padding = new Thickness(10, 8, 10, 8),
                        Cursor = System.Windows.Input.Cursors.Hand
                    };

                    var recStack = new StackPanel();

                    // Element reference
                    var failedElem = _issues.FirstOrDefault(i => i.ElementId == rec.ElementId);
                    if (failedElem != null)
                    {
                        recStack.Children.Add(new TextBlock
                        {
                            Text = $"🔧 {failedElem.Query}",
                            FontSize = 10, FontWeight = FontWeights.Medium,
                            Foreground = new SolidColorBrush(TextPrimary),
                            TextWrapping = TextWrapping.Wrap
                        });
                    }

                    // Fix suggestion
                    if (!string.IsNullOrEmpty(rec.FixSuggestion))
                    {
                        recStack.Children.Add(new TextBlock
                        {
                            Text = rec.FixSuggestion,
                            FontSize = 10, Foreground = new SolidColorBrush(TextPrimary),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 4, 0, 0)
                        });
                    }

                    // Material option (highlighted)
                    if (!string.IsNullOrEmpty(rec.MaterialOption))
                    {
                        var matBorder = new Border
                        {
                            Background = new SolidColorBrush(Color.FromRgb(240, 253, 244)),
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(8, 4, 8, 4),
                            Margin = new Thickness(0, 4, 0, 0)
                        };
                        matBorder.Child = new TextBlock
                        {
                            Text = $"💡 {rec.MaterialOption}",
                            FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52)),
                            TextWrapping = TextWrapping.Wrap,
                            FontWeight = FontWeights.Medium
                        };
                        recStack.Children.Add(matBorder);
                    }

                    // Reference
                    if (!string.IsNullOrEmpty(rec.Reference))
                    {
                        recStack.Children.Add(new TextBlock
                        {
                            Text = rec.Reference,
                            FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }

                    recCard.Child = recStack;

                    // Click to select element
                    if (rec.ElementId > 0)
                    {
                        recCard.MouseLeftButtonUp += (s, e) => SelectElementInRevit(rec.ElementId);
                        recCard.ToolTip = $"Click to select element {rec.ElementId} in Revit";
                    }

                    _contentPanel.Children.Add(recCard);
                }
            }

            // Info/reference items
            var infos = _issues.Where(i => i.Status == "info" || i.Status == "unknown").ToList();
            if (infos.Any())
            {
                _contentPanel.Children.Add(new TextBlock
                {
                    Text = $"📋 UKBS REQUIREMENTS ({infos.Count})",
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                    Margin = new Thickness(0, 12, 0, 4)
                });
                foreach (var info in infos)
                    _contentPanel.Children.Add(CreateIssueCard(info));
            }

            // Pass items (collapsed)
            var passes = _issues.Where(i => i.Status == "pass").ToList();
            if (passes.Any())
            {
                var passExpander = new Expander
                {
                    Header = $"✅ COMPLIANT ({passes.Count})",
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(PassGreen),
                    IsExpanded = false,
                    Margin = new Thickness(0, 12, 0, 4)
                };
                var passStack = new StackPanel();
                foreach (var p in passes)
                    passStack.Children.Add(CreateIssueCard(p));
                passExpander.Content = passStack;
                _contentPanel.Children.Add(passExpander);
            }
        }

        private Border CreateIssueCard(ComplianceIssue issue)
        {
            Color borderCol = issue.Status == "fail" ? FailRed :
                              issue.Status == "pass" ? PassGreen : Color.FromRgb(0, 120, 215);

            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(10, 8, 10, 8),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            // Left border color indicates status
            card.BorderBrush = new SolidColorBrush(borderCol);
            card.BorderThickness = new Thickness(3, 0, 0, 0);

            var stack = new StackPanel();

            // Title row
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var queryText = new TextBlock
            {
                Text = issue.Query,
                FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(TextPrimary),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(queryText, 0);
            titleRow.Children.Add(queryText);

            if (!string.IsNullOrEmpty(issue.Schedule))
            {
                var scheduleBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(4, 1, 4, 1),
                    VerticalAlignment = VerticalAlignment.Top
                };
                scheduleBadge.Child = new TextBlock
                {
                    Text = issue.Schedule,
                    FontSize = 8, Foreground = new SolidColorBrush(TextSecondary)
                };
                Grid.SetColumn(scheduleBadge, 1);
                titleRow.Children.Add(scheduleBadge);
            }
            stack.Children.Add(titleRow);

            // Issue description
            if (!string.IsNullOrEmpty(issue.Issue))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"⚠️ {issue.Issue}",
                    FontSize = 10, Foreground = new SolidColorBrush(FailRed),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            // WHY — the reason for pass/fail
            if (!string.IsNullOrEmpty(issue.Content))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = issue.Content,
                    FontSize = 10, Foreground = new SolidColorBrush(TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            // Required vs Actual values
            if (!string.IsNullOrEmpty(issue.RequiredValue) || !string.IsNullOrEmpty(issue.ActualValue))
            {
                var valRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                if (!string.IsNullOrEmpty(issue.RequiredValue))
                {
                    valRow.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(230, 245, 230)),
                        CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(0, 0, 4, 0),
                        Child = new TextBlock { Text = $"Required: {issue.RequiredValue}", FontSize = 9, Foreground = new SolidColorBrush(PassGreen) }
                    });
                }
                if (!string.IsNullOrEmpty(issue.ActualValue))
                {
                    var actualColor = issue.Status == "fail" ? FailRed : PassGreen;
                    var actualBg = issue.Status == "fail" ? Color.FromRgb(253, 230, 230) : Color.FromRgb(230, 245, 230);
                    valRow.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(actualBg),
                        CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1),
                        Child = new TextBlock { Text = $"Actual: {issue.ActualValue}", FontSize = 9, Foreground = new SolidColorBrush(actualColor) }
                    });
                }
                stack.Children.Add(valRow);
            }

            // TABLE SOURCE — the actual UKBS table data
            if (!string.IsNullOrEmpty(issue.TableSource))
            {
                var tableExpander = new Expander
                {
                    Header = "📋 View UKBS Table Source",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                    IsExpanded = false,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                tableExpander.Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(248, 248, 252)),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 2, 0, 0),
                    Child = new TextBlock
                    {
                        Text = issue.TableSource,
                        FontSize = 9,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = new SolidColorBrush(TextPrimary),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                stack.Children.Add(tableExpander);
            }

            // By-law reference
            if (!string.IsNullOrEmpty(issue.Bylaws))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = issue.Bylaws,
                    FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            card.Child = stack;

            // Click to select element in Revit
            if (issue.ElementId > 0)
            {
                card.MouseLeftButtonUp += (s, e) => SelectElementInRevit(issue.ElementId);
                card.ToolTip = $"Click to select element {issue.ElementId} in Revit";
            }

            return card;
        }

        private void SelectElementInRevit(int elementId)
        {
            try
            {
                if (_uiApp?.ActiveUIDocument == null) return;
                var uidoc = _uiApp.ActiveUIDocument;
                var doc = uidoc.Document;
                var elemId = new ElementId(elementId);
                var elem = doc.GetElement(elemId);
                if (elem == null) return;

                // If current view is a rendered/perspective 3D view, switch to a floor plan first
                var activeView = doc.ActiveView;
                bool needsViewSwitch = activeView.ViewType == ViewType.ThreeD ||
                                       activeView.ViewType == ViewType.Rendering ||
                                       activeView.ViewType == ViewType.Walkthrough;

                if (needsViewSwitch)
                {
                    // Try to find the floor plan that contains this element's level
                    View targetView = null;
                    var levelParam = elem.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)
                                  ?? elem.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                                  ?? elem.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);

                    if (levelParam != null)
                    {
                        var levelId = levelParam.AsElementId();
                        // Find a floor plan for this level
                        targetView = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewPlan))
                            .Cast<ViewPlan>()
                            .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == levelId)
                            .FirstOrDefault();
                    }

                    // Fallback: any non-template floor plan
                    if (targetView == null)
                    {
                        targetView = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewPlan))
                            .Cast<ViewPlan>()
                            .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                            .FirstOrDefault();
                    }

                    if (targetView != null)
                    {
                        uidoc.ActiveView = targetView;
                    }
                }

                // Select the element
                uidoc.Selection.SetElementIds(new List<ElementId> { elemId });

                // Zoom to the element
                uidoc.ShowElements(elemId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Select element error: {ex.Message}");
            }
        }

        // ── Ask Compliance Question ──

        private void AskTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) AskQuestion();
        }

        private void AskButton_Click(object sender, RoutedEventArgs e) => AskQuestion();

        private async void AskQuestion()
        {
            string question = _askTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(question) || question == "Ask about UKBS compliance...") return;

            _askTextBox.Text = "";
            ShowStatus($"🔍 Searching UKBS 1984: \"{question}\"...", Color.FromRgb(235, 243, 252));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            try
            {
                var response = await _complianceService.AskComplianceAsync(question, _selectedPurposeGroup);

                if (!string.IsNullOrEmpty(response.Error))
                {
                    ShowStatus($"❌ {response.Error}", Color.FromRgb(253, 235, 208));
                    return;
                }

                // Show results in content panel
                _contentPanel.Children.Clear();

                _contentPanel.Children.Add(new TextBlock
                {
                    Text = $"🔍 \"{question}\"",
                    FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 8)
                });

                if (response.Results.Any())
                {
                    foreach (var result in response.Results)
                    {
                        foreach (var match in result.Matches)
                        {
                            var card = new Border
                            {
                                Background = Brushes.White,
                                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                                BorderThickness = new Thickness(2, 0, 0, 0),
                                CornerRadius = new CornerRadius(4),
                                Margin = new Thickness(0, 0, 0, 6),
                                Padding = new Thickness(10, 8, 10, 8)
                            };
                            var stack = new StackPanel();

                            // Schedule + similarity
                            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
                            headerRow.Children.Add(new TextBlock
                            {
                                Text = match.Schedule,
                                FontSize = 10, FontWeight = FontWeights.SemiBold,
                                Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215))
                            });
                            headerRow.Children.Add(new TextBlock
                            {
                                Text = $"  ({match.Similarity:P0} match)",
                                FontSize = 9, Foreground = new SolidColorBrush(TextMuted)
                            });
                            stack.Children.Add(headerRow);

                            stack.Children.Add(new TextBlock
                            {
                                Text = match.Content,
                                FontSize = 10, Foreground = new SolidColorBrush(TextPrimary),
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 4, 0, 0),
                                MaxHeight = 150
                            });

                            card.Child = stack;
                            _contentPanel.Children.Add(card);
                        }
                    }
                }

                // Back button
                var backBtn = new Button
                {
                    Content = "← Back to Compliance Results",
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                    BorderThickness = new Thickness(0),
                    FontSize = 10, Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                backBtn.Click += (s, ev) => { if (_issues.Any()) UpdateIssuesList(); };
                _contentPanel.Children.Add(backBtn);

                HideStatus();
            }
            catch (Exception ex)
            {
                ShowStatus($"Error: {ex.Message}", Color.FromRgb(253, 235, 208));
            }
        }
    }

    // --- Issue Model ---

    public class ComplianceIssue
    {
        public string Query { get; set; }
        public string Status { get; set; } // "pass", "fail", "info", "unknown"
        public string Schedule { get; set; }
        public string Section { get; set; }
        public string Content { get; set; } // WHY — the reason
        public string Issue { get; set; }
        public string Bylaws { get; set; }
        public double Similarity { get; set; }
        public int ElementId { get; set; }
        public string TableSource { get; set; } // Actual UKBS table data
        public string RequiredValue { get; set; }
        public string ActualValue { get; set; }
        public string Category { get; set; }
        public string TypeName { get; set; }
        public string LevelName { get; set; }
    }
}
