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

namespace RevitWebAppSync.UI
{
    public partial class JkrComplianceDashboardPanel : Page
    {
        private UIApplication _uiApp;
        private JkrExtractionResult _extractionData;
        private JkrComplianceService _service;
        private int _selectedLoi = 300;
        private List<ComplianceIssue> _issues = new List<ComplianceIssue>();
        private string _aiReport = "";
        private List<AIRecommendationDto> _aiRecommendations = new List<AIRecommendationDto>();

        // UI refs
        private TextBlock _subtitleText;
        private TextBlock _summaryPassText;
        private TextBlock _summaryFailText;
        private TextBlock _summaryWarnText;
        private TextBlock _elemCountText;
        private TextBlock _jkrParamCountText;
        private TextBlock _categoryCountText;
        private StackPanel _contentPanel;
        private ComboBox _loiCombo;
        private ProgressBar _complianceBar;
        private TextBlock _compliancePercText;
        private Border _statusBanner;
        private TextBlock _statusText;

        // Colors
        private static readonly Color PrimaryBlue = Color.FromRgb(0, 102, 153);
        private static readonly Color HeaderBg = Color.FromRgb(0, 85, 128);
        private static readonly Color PageBg = Color.FromRgb(241, 241, 241);
        private static readonly Color BorderColor = Color.FromRgb(217, 217, 217);
        private static readonly Color TextPrimary = Color.FromRgb(51, 51, 51);
        private static readonly Color TextSecondary = Color.FromRgb(102, 102, 102);
        private static readonly Color TextMuted = Color.FromRgb(153, 153, 153);
        private static readonly Color SuccessGreen = Color.FromRgb(16, 124, 16);
        private static readonly Color WarningAmber = Color.FromRgb(255, 140, 0);
        private static readonly Color FailRed = Color.FromRgb(220, 53, 69);
        private static readonly Color PassGreen = Color.FromRgb(40, 167, 69);

        public JkrComplianceDashboardPanel()
        {
            InitializeComponent();
            _service = new JkrComplianceService();
            BuildUI();
        }

        private void BuildUI()
        {
            var root = new Grid { Background = new SolidColorBrush(PageBg) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Status
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Summary
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // LOi selector
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Results
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // Actions

            // ── Row 0: Header ──
            var header = new Border
            {
                Background = new LinearGradientBrush(HeaderBg, Color.FromRgb(0, 60, 90), new Point(0, 0), new Point(1, 0)),
                Padding = new Thickness(16, 12, 16, 12)
            };
            var headerStack = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = "📋", FontSize = 16, Margin = new Thickness(0, 0, 6, 0) });
            titleRow.Children.Add(new TextBlock { Text = "BINA", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0) });
            titleRow.Children.Add(new TextBlock { Text = "JKR BIM Compliance", FontSize = 16, FontWeight = FontWeights.Light, Foreground = new SolidColorBrush(Color.FromRgb(180, 220, 240)) });
            headerStack.Children.Add(titleRow);
            _subtitleText = new TextBlock { Text = "Document 09 — Spesifikasi Parameter JKR", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(150, 200, 220)), Margin = new Thickness(0, 2, 0, 0) };
            headerStack.Children.Add(_subtitleText);
            header.Child = headerStack;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ── Row 1: Status banner ──
            _statusBanner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(235, 243, 252)),
                Padding = new Thickness(12, 6, 12, 6),
                Visibility = Visibility.Collapsed
            };
            _statusText = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(TextPrimary), TextWrapping = TextWrapping.Wrap };
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

            var compHeader = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            compHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            compHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            compHeader.Children.Add(new TextBlock { Text = "Compliance Score", FontSize = 11, Foreground = new SolidColorBrush(TextSecondary), FontWeight = FontWeights.Medium });
            _compliancePercText = new TextBlock { Text = "—", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(TextMuted), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_compliancePercText, 1);
            compHeader.Children.Add(_compliancePercText);
            summaryStack.Children.Add(compHeader);

            var barBg = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(230, 230, 230)), Margin = new Thickness(0, 0, 0, 12) };
            _complianceBar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = 0, Foreground = new SolidColorBrush(SuccessGreen), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            barBg.Child = _complianceBar;
            summaryStack.Children.Add(barBg);

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

            _summaryWarnText = MakeStatBlock("0", "⚠️ Warning", WarningAmber);
            Grid.SetColumn(_summaryWarnText, 2);
            statsGrid.Children.Add(_summaryWarnText);

            summaryStack.Children.Add(statsGrid);

            var infoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _elemCountText = new TextBlock { Text = "— elements", FontSize = 10, Foreground = new SolidColorBrush(TextMuted), Margin = new Thickness(0, 0, 12, 0) };
            _jkrParamCountText = new TextBlock { Text = "— with JKR params", FontSize = 10, Foreground = new SolidColorBrush(TextMuted), Margin = new Thickness(0, 0, 12, 0) };
            _categoryCountText = new TextBlock { Text = "— categories", FontSize = 10, Foreground = new SolidColorBrush(TextMuted) };
            infoRow.Children.Add(_elemCountText);
            infoRow.Children.Add(_jkrParamCountText);
            infoRow.Children.Add(_categoryCountText);
            summaryStack.Children.Add(infoRow);

            summaryCard.Child = summaryStack;
            Grid.SetRow(summaryCard, 2);
            root.Children.Add(summaryCard);

            // ── Row 3: LOi selector ──
            var selectorCard = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Margin = new Thickness(12, 8, 12, 0), Padding = new Thickness(10, 6, 10, 6)
            };
            var selectorRow = new StackPanel { Orientation = Orientation.Horizontal };
            selectorRow.Children.Add(new TextBlock { Text = "LOi Level:", FontSize = 10, Foreground = new SolidColorBrush(TextSecondary), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _loiCombo = new ComboBox { MinWidth = 200, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            _loiCombo.Items.Add(new ComboBoxItem { Content = "LOi 200 — Schematic Design", Tag = 200 });
            _loiCombo.Items.Add(new ComboBoxItem { Content = "LOi 300 — Detail Design", Tag = 300, IsSelected = true });
            _loiCombo.Items.Add(new ComboBoxItem { Content = "LOi 400 — Construction", Tag = 400 });
            _loiCombo.Items.Add(new ComboBoxItem { Content = "LOi 500 — As-Built / FM", Tag = 500 });
            _loiCombo.SelectionChanged += LoiCombo_Changed;
            selectorRow.Children.Add(_loiCombo);
            selectorCard.Child = selectorRow;
            Grid.SetRow(selectorCard, 3);
            root.Children.Add(selectorCard);

            // ── Row 4: Content ──
            var scroll = new ScrollViewer { Margin = new Thickness(12, 8, 12, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _contentPanel = new StackPanel();
            _contentPanel.Children.Add(new TextBlock
            {
                Text = "Select LOi level and click Check JKR Compliance to scan the model against Document 09.",
                Foreground = new SolidColorBrush(TextMuted), FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 40, 0, 0)
            });
            scroll.Content = _contentPanel;
            Grid.SetRow(scroll, 4);
            root.Children.Add(scroll);

            // ── Row 5: Actions ──
            var actionBar = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 8, 10, 8)
            };
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            actionRow.Children.Add(MakeActionButton("Check JKR Compliance", CheckCompliance_Click, PrimaryBlue, true));
            actionRow.Children.Add(MakeActionButton("Refresh", Refresh_Click, Color.FromRgb(100, 100, 100), false));
            actionBar.Child = actionRow;
            Grid.SetRow(actionBar, 5);
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

        // ── Events ──

        public void SetRevitApp(UIApplication uiApp) => _uiApp = uiApp;

        private void LoiCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loiCombo.SelectedItem is ComboBoxItem item && item.Tag is int loi)
                _selectedLoi = loi;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => ScanModel();

        private void ScanModel()
        {
            try
            {
                if (_uiApp?.ActiveUIDocument?.Document == null) { ShowStatus("No model loaded", WarningAmber); return; }
                _extractionData = JkrBuildingInfoExtractor.Extract(_uiApp.ActiveUIDocument.Document);

                _subtitleText.Text = $"{_extractionData.ProjectName}  |  {DateTime.Now:HH:mm}";
                _elemCountText.Text = $"{_extractionData.TotalElements} elements";
                _jkrParamCountText.Text = $"{_extractionData.ElementsWithJkrParams} with JKR params";
                _categoryCountText.Text = $"{_extractionData.Categories.Count} categories";

                ShowStatus($"Scanned: {_extractionData.TotalElements} elements, {_extractionData.ElementsWithJkrParams} with JKR params, {_extractionData.ElementsWithJkrCode} with JKR codes", Color.FromRgb(235, 243, 252));
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
                if (btn != null) { btn.IsEnabled = false; btn.Content = "⏳ Checking..."; }

                if (_extractionData == null) ScanModel();
                if (_extractionData == null) return;

                ShowStatus($"🔍 Checking {_extractionData.TotalElements} elements against JKR Doc 09 at LOi {_selectedLoi}...", Color.FromRgb(235, 243, 252));
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                var request = new JkrComplianceRequest
                {
                    ProjectName = _extractionData.ProjectName,
                    FileName = _extractionData.FileName,
                    Discipline = _extractionData.Discipline,
                    LoiLevel = _selectedLoi,
                    Elements = _extractionData.Elements,
                };

                var response = await _service.CheckJkrComplianceAsync(request);

                if (!string.IsNullOrEmpty(response.Error))
                {
                    ShowStatus($"❌ {response.Error}", Color.FromRgb(253, 235, 208));
                    return;
                }

                _issues = new List<ComplianceIssue>();
                foreach (var req in response.BuildingRequirements)
                    _issues.Add(DtoToIssue(req));
                foreach (var elem in response.ElementIssues)
                    _issues.Add(DtoToIssue(elem));

                _aiReport = response.AIReport ?? "";
                _aiRecommendations = response.AIRecommendations ?? new List<AIRecommendationDto>();

                UpdateSummary();
                UpdateIssuesList();

                int fails = _issues.Count(i => i.Status == "fail");
                double pct = response.Summary.ContainsKey("compliance_percentage")
                    ? Convert.ToDouble(response.Summary["compliance_percentage"]) : 0;

                if (fails > 0)
                    ShowStatus($"📋 {pct:F1}% compliance — {fails} issue(s) found at LOi {_selectedLoi}", Color.FromRgb(253, 235, 220));
                else
                    ShowStatus($"✅ {pct:F1}% compliance — all checks pass at LOi {_selectedLoi}", Color.FromRgb(223, 246, 221));
            }
            catch (Exception ex)
            {
                ShowStatus($"Error: {ex.Message}", Color.FromRgb(253, 235, 208));
            }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "Check JKR Compliance"; }
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
                Issue = dto.Status == "fail" ? $"{dto.ActualValue} — Required: {dto.RequiredValue}" : null,
                Bylaws = dto.Bylaw ?? "",
                ElementId = dto.ElementId,
                TableSource = dto.TableSource ?? "",
                RequiredValue = dto.RequiredValue,
                ActualValue = dto.ActualValue,
                Category = dto.Category ?? "",
                TypeName = dto.TypeName ?? "",
                LevelName = dto.LevelName ?? "",
            };
        }

        private void UpdateSummary()
        {
            int pass = _issues.Count(i => i.Status == "pass");
            int fail = _issues.Count(i => i.Status == "fail");
            int warn = _issues.Count(i => i.Status == "warning");

            UpdateStatBlock(_summaryPassText, pass.ToString(), PassGreen);
            UpdateStatBlock(_summaryFailText, fail.ToString(), FailRed);
            UpdateStatBlock(_summaryWarnText, warn.ToString(), WarningAmber);

            int total = pass + fail + warn;
            int pct = total > 0 ? (int)((pass / (double)total) * 100) : 0;
            _compliancePercText.Text = $"{pct}%";
            _compliancePercText.Foreground = new SolidColorBrush(pct >= 80 ? PassGreen : pct >= 50 ? WarningAmber : FailRed);
            _complianceBar.Value = pct;
            _complianceBar.Foreground = new SolidColorBrush(pct >= 80 ? PassGreen : pct >= 50 ? WarningAmber : FailRed);
        }

        private void UpdateIssuesList()
        {
            _contentPanel.Children.Clear();

            // Failures
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

            // Warnings
            var warnings = _issues.Where(i => i.Status == "warning").ToList();
            if (warnings.Any())
            {
                var warnExpander = new Expander
                {
                    Header = $"⚠️ WARNINGS ({warnings.Count})",
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(WarningAmber),
                    IsExpanded = false,
                    Margin = new Thickness(0, 8, 0, 4)
                };
                var warnStack = new StackPanel();
                foreach (var w in warnings)
                    warnStack.Children.Add(CreateIssueCard(w));
                warnExpander.Content = warnStack;
                _contentPanel.Children.Add(warnExpander);
            }

            // AI Report
            if (!string.IsNullOrEmpty(_aiReport))
            {
                var reportCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(245, 250, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0, 102, 153)),
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
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 85, 128)),
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

            // AI Recommendations
            if (_aiRecommendations.Any())
            {
                _contentPanel.Children.Add(new TextBlock
                {
                    Text = $"🔧 AI RECOMMENDATIONS ({_aiRecommendations.Count})",
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(PrimaryBlue),
                    Margin = new Thickness(0, 12, 0, 4)
                });

                foreach (var rec in _aiRecommendations)
                {
                    var recCard = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = new SolidColorBrush(PrimaryBlue),
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 0, 0, 4),
                        Padding = new Thickness(10, 8, 10, 8)
                    };
                    var recStack = new StackPanel();
                    if (!string.IsNullOrEmpty(rec.FixSuggestion))
                        recStack.Children.Add(new TextBlock { Text = rec.FixSuggestion, FontSize = 10, Foreground = new SolidColorBrush(TextPrimary), TextWrapping = TextWrapping.Wrap });
                    if (!string.IsNullOrEmpty(rec.MaterialOption))
                    {
                        var matBorder = new Border
                        {
                            Background = new SolidColorBrush(Color.FromRgb(240, 248, 255)),
                            CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 4, 8, 4),
                            Margin = new Thickness(0, 4, 0, 0)
                        };
                        matBorder.Child = new TextBlock { Text = $"💡 {rec.MaterialOption}", FontSize = 10, Foreground = new SolidColorBrush(PrimaryBlue), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Medium };
                        recStack.Children.Add(matBorder);
                    }
                    if (!string.IsNullOrEmpty(rec.Reference))
                        recStack.Children.Add(new TextBlock { Text = rec.Reference, FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215)), Margin = new Thickness(0, 2, 0, 0) });
                    recCard.Child = recStack;
                    _contentPanel.Children.Add(recCard);
                }
            }

            // Passes (collapsed)
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
                              issue.Status == "pass" ? PassGreen :
                              issue.Status == "warning" ? WarningAmber : Color.FromRgb(0, 120, 215);

            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(borderCol),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(10, 8, 10, 8),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var stack = new StackPanel();

            // Title + schedule badge
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(new TextBlock
            {
                Text = issue.Query, FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(TextPrimary), TextWrapping = TextWrapping.Wrap
            }, 0);
            var queryText = new TextBlock
            {
                Text = issue.Query, FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(TextPrimary), TextWrapping = TextWrapping.Wrap
            };
            titleRow.Children.Add(queryText);

            if (!string.IsNullOrEmpty(issue.Schedule))
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                    CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1),
                    VerticalAlignment = VerticalAlignment.Top
                };
                badge.Child = new TextBlock { Text = issue.Schedule, FontSize = 8, Foreground = new SolidColorBrush(TextSecondary) };
                Grid.SetColumn(badge, 1);
                titleRow.Children.Add(badge);
            }
            stack.Children.Add(titleRow);

            // Content/reason
            if (!string.IsNullOrEmpty(issue.Content))
                stack.Children.Add(new TextBlock { Text = issue.Content, FontSize = 10, Foreground = new SolidColorBrush(TextPrimary), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

            // Required/Actual
            if (!string.IsNullOrEmpty(issue.RequiredValue) || !string.IsNullOrEmpty(issue.ActualValue))
            {
                var valRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                if (!string.IsNullOrEmpty(issue.RequiredValue))
                    valRow.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(230, 245, 230)),
                        CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(0, 0, 4, 0),
                        Child = new TextBlock { Text = $"Required: {issue.RequiredValue}", FontSize = 9, Foreground = new SolidColorBrush(PassGreen) }
                    });
                if (!string.IsNullOrEmpty(issue.ActualValue))
                    valRow.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(issue.Status == "fail" ? Color.FromRgb(253, 230, 230) : Color.FromRgb(230, 245, 230)),
                        CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1, 4, 1),
                        Child = new TextBlock { Text = $"Actual: {issue.ActualValue}", FontSize = 9, Foreground = new SolidColorBrush(issue.Status == "fail" ? FailRed : PassGreen) }
                    });
                stack.Children.Add(valRow);
            }

            // Fix suggestion (generated inline based on issue type)
            string fixText = GetFixSuggestion(issue);
            if (!string.IsNullOrEmpty(fixText))
            {
                var fixBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(232, 245, 233)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 6, 0, 0)
                };
                var fixStack = new StackPanel();
                fixStack.Children.Add(new TextBlock
                {
                    Text = "💡 How to fix:",
                    FontSize = 9, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(27, 94, 32)),
                    Margin = new Thickness(0, 0, 0, 2)
                });
                fixStack.Children.Add(new TextBlock
                {
                    Text = fixText,
                    FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50)),
                    TextWrapping = TextWrapping.Wrap
                });
                fixBorder.Child = fixStack;
                stack.Children.Add(fixBorder);
            }

            // Table source
            if (!string.IsNullOrEmpty(issue.TableSource))
            {
                var tableExp = new Expander
                {
                    Header = "📋 View JKR Spec Source", FontSize = 9,
                    Foreground = new SolidColorBrush(PrimaryBlue),
                    IsExpanded = false, Margin = new Thickness(0, 4, 0, 0)
                };
                tableExp.Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(248, 248, 252)),
                    CornerRadius = new CornerRadius(2), Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 2, 0, 0),
                    Child = new TextBlock { Text = issue.TableSource, FontSize = 9, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(TextPrimary), TextWrapping = TextWrapping.Wrap }
                };
                stack.Children.Add(tableExp);
            }

            // Bylaw reference
            if (!string.IsNullOrEmpty(issue.Bylaws))
                stack.Children.Add(new TextBlock { Text = issue.Bylaws, FontSize = 8, Foreground = new SolidColorBrush(PrimaryBlue), Margin = new Thickness(0, 2, 0, 0) });

            card.Child = stack;

            if (issue.ElementId > 0)
            {
                card.MouseLeftButtonUp += (s, ev) => SelectElementInRevit(issue.ElementId);
                card.ToolTip = $"Click to select element {issue.ElementId}";
            }

            return card;
        }

        private string GetFixSuggestion(ComplianceIssue issue)
        {
            if (issue.Status != "fail" && issue.Status != "warning") return null;

            string rule = (issue.Query ?? "").ToLower();
            string category = issue.Category ?? "";
            string typeName = issue.TypeName ?? "";

            // Naming convention issues
            if (rule.Contains("naming convention not followed") || rule.Contains("naming convention"))
            {
                string disc = "AR"; // default
                string catPart = category.Replace(" ", "").ToLower();
                string suggested = $"jkr{disc}_{catPart}_{typeName.Replace(" ", "_").ToLower()}";
                return $"Rename this element type in Revit:\n" +
                       $"  1. Select the element → Edit Type → Rename\n" +
                       $"  2. Use format: jkr{{Discipline}}_{{category}}_{{subcategory}}_{{spec}}\n" +
                       $"  3. Example: {suggested}\n" +
                       $"  Discipline codes: AR=Architecture, ST=Structure, ME=Mechanical, EL=Electrical";
            }

            // File naming
            if (rule.Contains("file") && rule.Contains("prefix"))
            {
                return "Rename the Revit file to start with 'jkr' followed by the discipline code.\n" +
                       "  Format: jkr{Discipline}{Code}_{Phase}_({ProjectID})_{Zone}_{Level}_{Status}_{Date}\n" +
                       "  Example: jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a";
            }

            // Invalid discipline code
            if (rule.Contains("invalid discipline code"))
            {
                return "Change the discipline code in the element/file name to a valid JKR code:\n" +
                       "  AR=Architecture, ST=Structure, ME=Mechanical, EL=Electrical,\n" +
                       "  CD=Civil, LD=Landscape, ID=Interior Design, SP=Specialist";
            }

            // Missing parameter
            if (rule.Contains("missing") && rule.Contains("parameter"))
            {
                string param = issue.RequiredValue ?? "unknown";
                return $"Add the shared parameter '{param}' to this family:\n" +
                       $"  1. Open the family in Family Editor (Edit Family)\n" +
                       $"  2. Manage tab → Shared Parameters → Add '{param}'\n" +
                       $"  3. Load the family back into the project\n" +
                       $"  4. Fill in the parameter value for all instances\n" +
                       $"  Tip: Use JKR shared parameter file if available from JKR BIM Unit.";
            }

            // Empty parameter
            if (rule.Contains("empty") && rule.Contains("parameter"))
            {
                string param = issue.RequiredValue ?? "unknown";
                return $"Fill in the value for parameter '{param}':\n" +
                       $"  1. Select the element → Properties panel\n" +
                       $"  2. Find '{param}' and enter the correct value\n" +
                       $"  3. Use Schedule/Quantities view to bulk-fill across multiple elements";
            }

            // No JKR code
            if (rule.Contains("no jkr code"))
            {
                return $"Assign a JKR code to this {category} element:\n" +
                       $"  1. Add shared parameter 'Kod_Komponen_jkr_stt' or 'Kod_DAK_Komponen_jkr_stt'\n" +
                       $"  2. Enter the correct JKR classification code (e.g., DFd311a for brick wall)\n" +
                       $"  3. Refer to JKR Document 03, Section 5 for code tables\n" +
                       $"  Tip: Consistent JKR codes enable automated cost tracking and BQ generation.";
            }

            // Element naming adoption (project-level)
            if (rule.Contains("element naming adoption"))
            {
                return "Too few elements use JKR naming. Bulk-rename types:\n" +
                       "  1. Use a Revit Dynamo script to batch-rename element types\n" +
                       "  2. Or manually rename via Project Browser → Families → right-click Rename\n" +
                       "  3. Target ≥80% adoption for compliance";
            }

            // No param rules defined
            if (rule.Contains("no parameter rules defined"))
            {
                return $"Category '{category}' has no specific JKR parameter rules in Document 09.\n" +
                       $"  This is informational — manually verify if this category needs JKR parameters.\n" +
                       $"  Contact the JKR BIM Unit if your project requires custom parameter specs.";
            }

            // Element has no name
            if (rule.Contains("element has no name"))
            {
                return "This element has no type name. Rename it in the Family Editor or via Properties.";
            }

            return null;
        }

        private void SelectElementInRevit(int elementId)
        {
            try
            {
                if (_uiApp?.ActiveUIDocument == null) return;
                var uidoc = _uiApp.ActiveUIDocument;
                var doc = uidoc.Document;
                var elemId = new ElementId(elementId);
                if (doc.GetElement(elemId) == null) return;
                uidoc.Selection.SetElementIds(new List<ElementId> { elemId });
                uidoc.ShowElements(elemId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Select element error: {ex.Message}");
            }
        }
    }

    // ComplianceIssue class is defined in ComplianceDashboardPanel.xaml.cs (shared)
}
