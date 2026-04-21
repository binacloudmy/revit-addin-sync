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
        private bool _suppressModelChanged = false;
        private bool _isResetting = false;

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

        // M2 Estimate UI (replaces Sqft)
        private Border _m2EstimateCard;
        private ComboBox _jenisBangunanCombo;
        private ComboBox _subJenisCombo;
        private System.Windows.Controls.TextBox _namaBangunanSearchBox;
        private ListBox _namaBangunanSuggestList;
        private List<string> _allNamaBangunan = new List<string>();
        private string _selectedNamaBangunan;
        private TextBlock _namaBangunanLabel;
        private ComboBox _namaEntryCombo;
        private ComboBox _negeriCombo;
        private TextBlock _luasTapakText;
        private TextBlock _m2ResultText;
        private TextBlock _m2TotalText;
        private StackPanel _m2BreakdownPanel;
        private bool _m2BreakdownVisible = false;

        // M2 data (loaded from API)
        private List<M2BuildingType> _buildingTypes = new List<M2BuildingType>();
        private List<M2Region> _regions = new List<M2Region>();
        private M2CostBreakdown _lastM2Result;

        // Kerja Pakar checkboxes + Kerja Luar search
        private WrapPanel _kerjaPakarPanel;
        private System.Windows.Controls.TextBox _kerjaLuarSearchBox;
        private ListBox _kerjaLuarResultsList;
        private string _selectedKerjaLuarSubJenis;
        private DispatcherTimer _kerjaLuarSearchTimer;

        // Component cost UI
        private Border _componentCard;
        private StackPanel _componentBody;
        private TextBlock _componentToggleText;
        private StackPanel _componentListPanel;

        // Review badge
        private Border _reviewBadge;
        private TextBlock _reviewBadgeText;

        // Loading overlay UI
        private Border _loadingOverlay;
        private StackPanel _loadingStepsPanel;
        private ProgressBar _loadingProgressBar;
        private TextBlock _loadingPercentText;
        private TextBlock _loadingCountText;
        private TextBlock _loadingStatusText;

        // Modern color palette (Slate + vibrant accents)
        private static readonly Color PrimaryBlue = Color.FromRgb(37, 99, 235);       // #2563EB
        private static readonly Color HeaderBg = Color.FromRgb(15, 23, 42);            // #0F172A Slate-900
        private static readonly Color CardBg = Color.FromRgb(255, 255, 255);
        private static readonly Color PageBg = Color.FromRgb(248, 250, 252);           // #F8FAFC Slate-50
        private static readonly Color BorderColor = Color.FromRgb(226, 232, 240);      // #E2E8F0 Slate-200
        private static readonly Color TextPrimary = Color.FromRgb(15, 23, 42);         // #0F172A Slate-900
        private static readonly Color TextSecondary = Color.FromRgb(71, 85, 105);      // #475569 Slate-600
        private static readonly Color TextMuted = Color.FromRgb(148, 163, 184);        // #94A3B8 Slate-400
        private static readonly Color SuccessGreen = Color.FromRgb(22, 163, 74);       // #16A34A Green-600
        private static readonly Color WarningAmber = Color.FromRgb(245, 158, 11);      // #F59E0B Amber-500
        private static readonly Color RowHover = Color.FromRgb(241, 245, 249);         // #F1F5F9 Slate-100
        private static readonly Color RowAlt = Color.FromRgb(248, 250, 252);           // #F8FAFC Slate-50

        // Level colors (vibrant, distinct)
        private static readonly Color[] LevelColors = {
            Color.FromRgb(37, 99, 235),    // Blue
            Color.FromRgb(22, 163, 74),    // Green
            Color.FromRgb(245, 158, 11),   // Amber
            Color.FromRgb(99, 102, 241),   // Indigo
            Color.FromRgb(244, 63, 94),    // Rose
            Color.FromRgb(20, 184, 166),   // Teal
            Color.FromRgb(249, 115, 22),   // Orange
            Color.FromRgb(139, 92, 246),   // Violet
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
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 0: Header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 1: Banner
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: M2 Estimate (scrollable)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 3: Actions

            // ── Row 0: Header ──
            var header = new Border
            {
                Background = new SolidColorBrush(HeaderBg),
                Padding = new Thickness(16, 14, 16, 14)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerLeft = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            titleRow.Children.Add(new TextBlock
            {
                Text = "BINA",
                FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 8, 0)
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = "Cost Tracker",
                FontSize = 18, FontWeight = FontWeights.Light,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerLeft.Children.Add(titleRow);
            _subtitleText = new TextBlock
            {
                Text = "No model loaded",
                FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 0, 0, 0)
            };
            headerLeft.Children.Add(_subtitleText);
            Grid.SetColumn(headerLeft, 0);
            headerGrid.Children.Add(headerLeft);

            // Version badge
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock { Text = "v1.0", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)) };
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

            // ── Row 2: M2 Estimate card ──
            _m2EstimateCard = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(12, 12, 12, 0),
                Padding = new Thickness(16, 14, 16, 14)
            };
            var m2Stack = new StackPanel();

            // ─── Title ───
            m2Stack.Children.Add(new TextBlock
            {
                Text = "Anggaran Kos Per Meter Persegi",
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextPrimary),
                Margin = new Thickness(0, 0, 0, 4)
            });
            m2Stack.Children.Add(new TextBlock
            {
                Text = "Sumber: JKR Kos Purata Semeter Persegi, Jilid 87 (Jan-Jun 2025)",
                FontSize = 9, Foreground = new SolidColorBrush(TextMuted),
                Margin = new Thickness(0, 0, 0, 14)
            });

            // ─── Section 1: Maklumat Bangunan ───
            m2Stack.Children.Add(MakeSectionHeader("Maklumat Bangunan"));

            m2Stack.Children.Add(MakeFieldLabel("Kategori Bangunan"));
            _jenisBangunanCombo = new ComboBox
            {
                FontSize = 11, Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8)
            };
            _jenisBangunanCombo.SelectionChanged += JenisBangunan_Changed;
            m2Stack.Children.Add(_jenisBangunanCombo);

            // Jenis Bangunan (sub_jenis_bangunan — each has own price per kawasan)
            m2Stack.Children.Add(MakeFieldLabel("Jenis Bangunan"));
            _subJenisCombo = new ComboBox
            {
                FontSize = 11, Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8)
            };
            _subJenisCombo.SelectionChanged += SubJenis_Changed;
            m2Stack.Children.Add(_subJenisCombo);

            // Nama Bangunan (optional, searchable with suggestions)
            _namaBangunanLabel = new TextBlock { Visibility = Visibility.Collapsed };
            m2Stack.Children.Add(_namaBangunanLabel);
            m2Stack.Children.Add(MakeFieldLabel("Nama Bangunan (pilihan)"));
            _namaBangunanSearchBox = new System.Windows.Controls.TextBox
            {
                FontSize = 11, Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2),
                Background = new SolidColorBrush(PageBg),
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1)
            };
            _namaBangunanSearchBox.GotFocus += (s, ev) => FilterNamaBangunanSuggestions();
            _namaBangunanSearchBox.LostFocus += (s, ev) =>
            {
                // Delay hide so click on suggestion registers first
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_namaBangunanSuggestList.IsMouseOver)
                        _namaBangunanSuggestList.Visibility = Visibility.Collapsed;
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            _namaBangunanSearchBox.TextChanged += NamaBangunanSearch_Changed;
            m2Stack.Children.Add(_namaBangunanSearchBox);

            _namaBangunanSuggestList = new ListBox
            {
                FontSize = 10, MaxHeight = 150,
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed
            };
            _namaBangunanSuggestList.SelectionChanged += NamaBangunanSuggestion_Selected;
            m2Stack.Children.Add(_namaBangunanSuggestList);

            // Nama Entry (specific building drawing — appears after nama_bangunan selected)
            m2Stack.Children.Add(MakeFieldLabel("Jenis Bangunan Spesifik (pilihan)"));
            _namaEntryCombo = new ComboBox
            {
                FontSize = 11, Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };
            m2Stack.Children.Add(_namaEntryCombo);

            // ─── Section 2: Lokasi ───
            m2Stack.Children.Add(MakeSectionHeader("Lokasi"));

            m2Stack.Children.Add(MakeFieldLabel("Negeri"));
            _negeriCombo = new ComboBox
            {
                FontSize = 11, Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8)
            };
            m2Stack.Children.Add(_negeriCombo);

            // ─── Section 3: Kerja Pakar ───
            m2Stack.Children.Add(MakeSectionHeader("Kerja Pakar (Utilities)"));

            _kerjaPakarPanel = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
            m2Stack.Children.Add(_kerjaPakarPanel);

            // ─── Section 4: Luas Tapak ───
            m2Stack.Children.Add(MakeSectionHeader("Luas Tapak"));

            _luasTapakText = new TextBlock
            {
                Text = "Luas Tapak: \u2014 m\u00B2 (auto dari model)",
                FontSize = 12, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(TextPrimary),
                Margin = new Thickness(0, 2, 0, 8)
            };
            m2Stack.Children.Add(_luasTapakText);

            // Hidden references for kerja luar (not used in UI, auto-resolved from building type)
            _kerjaLuarSearchBox = new System.Windows.Controls.TextBox { Visibility = Visibility.Collapsed };
            _kerjaLuarResultsList = new ListBox { Visibility = Visibility.Collapsed };

            // ─── Calculate Button ───
            m2Stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(BorderColor), Margin = new Thickness(0, 6, 0, 14) });

            var kiraBtn = new Button
            {
                Content = "Kira Anggaran",
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(SuccessGreen),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 10, 0, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            kiraBtn.Click += AutoMatch_Click;
            m2Stack.Children.Add(kiraBtn);

            // ─── Result Section (hidden until calculated) ───
            _m2BreakdownPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 16, 0, 0) };

            _m2ResultText = new TextBlock
            {
                Text = "",
                FontSize = 24, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(PrimaryBlue),
                Margin = new Thickness(0, 0, 0, 4)
            };
            _m2TotalText = new TextBlock
            {
                Text = "",
                FontSize = 11, Foreground = new SolidColorBrush(TextSecondary)
            };
            var resultInner = new StackPanel();
            resultInner.Children.Add(_m2ResultText);
            resultInner.Children.Add(_m2TotalText);
            _m2BreakdownPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 12),
                Child = resultInner
            });

            m2Stack.Children.Add(_m2BreakdownPanel);
            _m2EstimateCard.Child = m2Stack;

            // Wrap M2 card in ScrollViewer so breakdown doesn't overflow
            var m2Scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _m2EstimateCard
            };
            Grid.SetRow(m2Scroll, 2);
            root.Children.Add(m2Scroll);

            // Load dropdowns from API
            LoadM2DropdownsAsync();

            // ── Row 3: Action bar (Refresh only) ──
            var actionBar = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var actionStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var primaryRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            primaryRow.Children.Add(MakeActionButton("Refresh Model", Refresh_Click, PrimaryBlue, true));
            actionStack.Children.Add(primaryRow);
            actionBar.Child = actionStack;
            Grid.SetRow(actionBar, 3);
            root.Children.Add(actionBar);

            // Hidden references to avoid null exceptions in existing code
            _totalCard = new Border { Visibility = Visibility.Collapsed };
            _grandTotalText = new TextBlock();
            _itemCountText = new TextBlock();
            _levelCountText = new TextBlock();
            _pricedPercentText = new TextBlock();
            _coverageBar = new ProgressBar();
            _componentCard = new Border { Visibility = Visibility.Collapsed };
            _componentBody = new StackPanel();
            _componentListPanel = new StackPanel();
            _contentPanel = new StackPanel();
            _reviewBadge = new Border { Visibility = Visibility.Collapsed };
            _reviewBadgeText = new TextBlock();
            _byLevelRadio = new RadioButton { IsChecked = true };
            _byCategoryRadio = new RadioButton();
            _levelFilter = new ComboBox();
            _levelFilter.Items.Add(new ComboBoxItem { Content = "All Levels", IsSelected = true });

            // ── Loading overlay (spans all rows, on top) ──
            BuildLoadingOverlay(root);

            this.Content = root;
        }

        private void BuildLoadingOverlay(Grid root)
        {
            _loadingOverlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 241, 241, 241)),
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(_loadingOverlay, 0);
            Grid.SetRowSpan(_loadingOverlay, 5);

            var centerPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 320
            };

            // Title
            centerPanel.Children.Add(new TextBlock
            {
                Text = "Matching Pipeline",
                FontSize = 16, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            });

            // Percentage text (large)
            _loadingPercentText = new TextBlock
            {
                Text = "0%",
                FontSize = 32, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(PrimaryBlue),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            centerPanel.Children.Add(_loadingPercentText);

            // Item count text
            _loadingCountText = new TextBlock
            {
                Text = "",
                FontSize = 10, Foreground = new SolidColorBrush(TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            centerPanel.Children.Add(_loadingCountText);

            // Progress bar (determinate)
            _loadingProgressBar = new ProgressBar
            {
                Height = 6,
                IsIndeterminate = false,
                Maximum = 100,
                Value = 0,
                Foreground = new SolidColorBrush(PrimaryBlue),
                Background = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 16)
            };
            centerPanel.Children.Add(_loadingProgressBar);

            // Steps panel
            _loadingStepsPanel = new StackPanel();
            string[] stepLabels = { "Local DB matching", "Layer 1: Exact JKR code", "Layer 2: Learned mappings", "Layer 3: AI vector search", "Layer 4: Review queue" };
            foreach (var label in stepLabels)
            {
                var stepRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

                // Circle indicator
                var circle = new Border
                {
                    Width = 18, Height = 18,
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                circle.Child = new TextBlock
                {
                    Text = "·",
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                stepRow.Children.Add(circle);

                // Label + status
                var stepText = new TextBlock
                {
                    Text = label,
                    FontSize = 11, Foreground = new SolidColorBrush(TextMuted),
                    VerticalAlignment = VerticalAlignment.Center
                };
                stepRow.Children.Add(stepText);

                _loadingStepsPanel.Children.Add(stepRow);
            }
            centerPanel.Children.Add(_loadingStepsPanel);

            // Status text
            _loadingStatusText = new TextBlock
            {
                Text = "",
                FontSize = 10, Foreground = new SolidColorBrush(TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            };
            centerPanel.Children.Add(_loadingStatusText);

            _loadingOverlay.Child = centerPanel;
            root.Children.Add(_loadingOverlay);
        }

        private void ShowLoadingOverlay()
        {
            _loadingOverlay.Visibility = Visibility.Visible;
            _loadingProgressBar.IsIndeterminate = false;
            _loadingProgressBar.Value = 0;
            _loadingPercentText.Text = "0%";
            _loadingCountText.Text = "";
            _loadingStatusText.Text = "";

            // Reset all steps to gray/pending
            foreach (StackPanel stepRow in _loadingStepsPanel.Children)
            {
                var circle = stepRow.Children[0] as Border;
                var text = stepRow.Children[1] as TextBlock;
                circle.Background = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                ((TextBlock)circle.Child).Text = "·";
                text.Foreground = new SolidColorBrush(TextMuted);
                // Remove extra status text if any
                while (stepRow.Children.Count > 2)
                    stepRow.Children.RemoveAt(2);
            }
        }

        private void HideLoadingOverlay()
        {
            _loadingOverlay.Visibility = Visibility.Collapsed;
        }

        private void UpdateLoadingStep(int stepIndex, string status, bool complete)
        {
            if (stepIndex < 0 || stepIndex >= _loadingStepsPanel.Children.Count) return;
            var stepRow = _loadingStepsPanel.Children[stepIndex] as StackPanel;
            var circle = stepRow.Children[0] as Border;
            var text = stepRow.Children[1] as TextBlock;

            if (complete)
            {
                circle.Background = new SolidColorBrush(SuccessGreen);
                ((TextBlock)circle.Child).Text = "✓";
                text.Foreground = new SolidColorBrush(SuccessGreen);
            }
            else
            {
                circle.Background = new SolidColorBrush(PrimaryBlue);
                ((TextBlock)circle.Child).Text = "→";
                text.Foreground = new SolidColorBrush(TextPrimary);
                text.FontWeight = FontWeights.SemiBold;
            }

            // Add status detail
            while (stepRow.Children.Count > 2)
                stepRow.Children.RemoveAt(2);
            if (!string.IsNullOrEmpty(status))
            {
                stepRow.Children.Add(new TextBlock
                {
                    Text = $"  {status}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(complete ? SuccessGreen : PrimaryBlue),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        // ── UI Helpers ──

        private TextBlock MakeStatBlock(string value, string label)
        {
            var tb = new TextBlock { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextPrimary) });
            tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            tb.Inlines.Add(new System.Windows.Documents.Run(label) { FontSize = 10, Foreground = new SolidColorBrush(TextMuted) });
            tb.Tag = label;
            return tb;
        }

        private void UpdateStatBlock(TextBlock tb, string value)
        {
            string label = tb.Tag as string ?? "";
            tb.Inlines.Clear();
            tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextPrimary) });
            tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            tb.Inlines.Add(new System.Windows.Documents.Run(label) { FontSize = 10, Foreground = new SolidColorBrush(TextMuted) });
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

        private Border MakeSectionHeader(string text)
        {
            return new Border
            {
                Margin = new Thickness(0, 4, 0, 6),
                Padding = new Thickness(0, 4, 0, 4),
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new TextBlock
                {
                    Text = text.ToUpper(),
                    FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(TextMuted)
                }
            };
        }

        private TextBlock MakeFieldLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(TextSecondary),
                Margin = new Thickness(0, 0, 0, 3)
            };
        }

        private Button MakeActionButton(string text, RoutedEventHandler click, Color bg, bool primary)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 11,
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(bg),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(bg),
                BorderThickness = new Thickness(1),
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            // Rounded corners via template-like approach
            btn.Resources.Add(SystemParameters.FocusVisualStyleKey, null);
            btn.Click += click;
            return btn;
        }

        private Button MakeLinkButton(string text, RoutedEventHandler click)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 10,
                Padding = new Thickness(6, 3, 6, 3),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(PrimaryBlue),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontWeight = FontWeights.Medium
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

            // Show review badge if there are pending items from previous session
            UpdateReviewBadge();

            // Pre-warm the AI backend (async, non-blocking) so first Match Prices is fast
            _ = Task.Run(async () =>
            {
                var estimator = new AICostEstimator();
                await estimator.WarmupAsync();
            });
        }

        public void RefreshData()
        {
            try
            {
                if (_uiApp?.ActiveUIDocument?.Document == null) { _subtitleText.Text = "No model loaded"; return; }
                Document doc = _uiApp.ActiveUIDocument.Document;
                string projectName = Path.GetFileNameWithoutExtension(doc.PathName ?? "Untitled");
                _priceDb = new PriceDatabase(projectName);

                // Preserve matched prices from previous session
                var previousPrices = new Dictionary<int, (double price, string code, string source)>();
                foreach (var item in _allItems)
                {
                    if (item.UnitPrice > 0)
                        previousPrices[item.ElementId] = (item.UnitPrice, item.JkrCode, item.PriceSource);
                }

                _allItems = RevitModelWalker.GetAllItems(doc);

                // Restore prices from previous match (covers grouped elements)
                foreach (var item in _allItems)
                {
                    if (previousPrices.TryGetValue(item.ElementId, out var prev))
                    {
                        item.UnitPrice = prev.price;
                        if (!string.IsNullOrEmpty(prev.code)) item.JkrCode = prev.code;
                        item.PriceSource = prev.source;
                    }
                }

                // Read user edits from model parameters (skip during reset)
                if (!_isResetting)
                {
                    CostParameterWriter.ReadPricesFromModel(doc, _allItems);
                    _priceDb.ApplyPrices(_allItems);
                }

                // No fallback estimation on load — unpriced items stay at RM 0 until
                // user runs Match Prices and reviews them. Prices only come from:
                // - Previous session (preserved prices)
                // - Model parameters (manual edits in Revit schedule)
                // - Project price DB (previously confirmed prices)

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
            // Coverage = priceable items priced / priceable items (excludes Rebar, Fittings, etc.
            // that are rolled into parent prices in Malaysian QS practice)
            int pricedPct = _summary.PriceableItems > 0 ? (int)Math.Round((_summary.PriceablePricedItems / (double)_summary.PriceableItems) * 100) : 0;
            UpdateStatBlock(_pricedPercentText, $"{pricedPct}%");
            _coverageBar.Value = pricedPct;
            _coverageBar.Foreground = new SolidColorBrush(pricedPct >= 80 ? SuccessGreen : pricedPct >= 50 ? WarningAmber : Color.FromRgb(200, 50, 50));

            UpdateLuasTapakDisplay();
            if (_componentBody != null && _componentBody.Visibility == Visibility.Visible)
                UpdateComponentCard();
        }

        private async void UpdateReviewBadge()
        {
            try
            {
                var stats = await new AICostEstimator().GetReviewStatsAsync();
                int pending = stats?.ReviewPending ?? 0;
                Dispatcher.Invoke(() =>
                {
                    if (pending > 0)
                    {
                        _reviewBadgeText.Text = pending.ToString();
                        _reviewBadge.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _reviewBadge.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch
            {
                Dispatcher.Invoke(() => _reviewBadge.Visibility = Visibility.Collapsed);
            }
        }

        // ==================== M2 Estimation Methods ====================

        private async void LoadM2DropdownsAsync()
        {
            try
            {
                var aiEstimator = new AICostEstimator();
                _buildingTypes = await aiEstimator.GetBuildingTypesAsync();
                _regions = await aiEstimator.GetRegionsAsync();

                // Extract all available info from Revit Project Information
                string revitAddress = "";
                string revitBuildingType = "";
                string revitBuildingName = "";
                string revitProjectName = "";
                string revitOrgName = "";
                try
                {
                    if (_uiApp?.ActiveUIDocument?.Document != null)
                    {
                        var doc = _uiApp.ActiveUIDocument.Document;
                        var projInfo = doc.ProjectInformation;
                        revitAddress = projInfo?.Address ?? "";
                        revitBuildingType = projInfo?.LookupParameter("Building Type")?.AsString() ?? "";
                        revitBuildingName = projInfo?.BuildingName ?? "";
                        revitProjectName = projInfo?.Name ?? "";
                        revitOrgName = projInfo?.OrganizationName ?? "";
                    }
                }
                catch { }

                // Combine all text sources for matching (building name, project name, etc.)
                var matchTexts = new List<string> { revitBuildingType, revitBuildingName, revitProjectName, revitOrgName }
                    .Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                string combinedText = string.Join(" ", matchTexts).ToLower();

                Dispatcher.Invoke(() =>
                {
                    // ── 1. Populate & auto-select Kategori Bangunan ──
                    _jenisBangunanCombo.Items.Clear();
                    foreach (var bt in _buildingTypes)
                        _jenisBangunanCombo.Items.Add(new ComboBoxItem { Content = bt.kategori_bangunan, Tag = bt });

                    int selectedKategori = 0;
                    if (!string.IsNullOrEmpty(combinedText))
                    {
                        int bestScore = 0;
                        for (int i = 0; i < _jenisBangunanCombo.Items.Count; i++)
                        {
                            var ci = _jenisBangunanCombo.Items[i] as ComboBoxItem;
                            string kategori = ci?.Content?.ToString() ?? "";
                            int score = FuzzyMatchScore(combinedText: combinedText, candidate: kategori);
                            if (score > bestScore) { bestScore = score; selectedKategori = i; }
                        }
                    }
                    if (_jenisBangunanCombo.Items.Count > 0)
                        _jenisBangunanCombo.SelectedIndex = selectedKategori;
                    // JenisBangunan_Changed fires here → populates _subJenisCombo

                    // ── 2. Auto-select Jenis Bangunan (sub_jenis) ──
                    if (_subJenisCombo.Items.Count > 0 && !string.IsNullOrEmpty(combinedText))
                    {
                        int bestSubIdx = 0;
                        int bestSubScore = 0;
                        for (int i = 0; i < _subJenisCombo.Items.Count; i++)
                        {
                            var ci = _subJenisCombo.Items[i] as ComboBoxItem;
                            string subName = ci?.Content?.ToString() ?? "";
                            int score = FuzzyMatchScore(combinedText: combinedText, candidate: subName);
                            if (score > bestSubScore) { bestSubScore = score; bestSubIdx = i; }
                        }
                        if (bestSubScore > 0)
                            _subJenisCombo.SelectedIndex = bestSubIdx;
                        // SubJenis_Changed fires here → populates _allNamaBangunan
                    }

                    // ── 3. Auto-select Nama Bangunan ──
                    if (_allNamaBangunan.Count > 0 && !string.IsNullOrEmpty(combinedText))
                    {
                        int bestNamaScore = 0;
                        string bestNama = null;
                        foreach (var nama in _allNamaBangunan)
                        {
                            int score = FuzzyMatchScore(combinedText: combinedText, candidate: nama);
                            if (score > bestNamaScore) { bestNamaScore = score; bestNama = nama; }
                        }
                        if (bestNama != null && bestNamaScore > 0)
                        {
                            _selectedNamaBangunan = bestNama;
                            _namaBangunanSearchBox.Text = bestNama;
                        }
                    }

                    // ── 4. Populate & auto-select Negeri ──
                    _negeriCombo.Items.Clear();
                    foreach (var region in _regions)
                        foreach (var negeri in region.negeri)
                            _negeriCombo.Items.Add(new ComboBoxItem { Content = negeri, Tag = region.kawasan });

                    int selectedNegeri = 0;
                    if (!string.IsNullOrEmpty(revitAddress))
                    {
                        for (int i = 0; i < _negeriCombo.Items.Count; i++)
                        {
                            var ci = _negeriCombo.Items[i] as ComboBoxItem;
                            if (ci?.Content?.ToString() != null &&
                                revitAddress.IndexOf(ci.Content.ToString(), StringComparison.OrdinalIgnoreCase) >= 0)
                            { selectedNegeri = i; break; }
                        }
                    }
                    if (_negeriCombo.Items.Count > 0)
                        _negeriCombo.SelectedIndex = selectedNegeri;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load M2 dropdowns: {ex.Message}");
            }
        }

        /// <summary>
        /// Score how well a candidate string matches the combined Revit project text.
        /// Returns number of matching words (case-insensitive). 0 = no match.
        /// </summary>
        private int FuzzyMatchScore(string combinedText, string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return 0;
            string lower = combinedText.ToLower();
            // Check if the whole candidate appears as substring
            if (lower.Contains(candidate.ToLower())) return candidate.Length;
            // Check word-by-word overlap
            var words = candidate.ToLower().Split(new[] { ' ', '/', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            int score = 0;
            foreach (var word in words)
            {
                if (word.Length < 2) continue; // skip tiny words
                if (lower.Contains(word)) score += word.Length;
            }
            return score;
        }

        private void JenisBangunan_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_jenisBangunanCombo.SelectedItem is ComboBoxItem item && item.Tag is M2BuildingType bt)
            {
                // Populate Jenis Bangunan (sub_jenis) dropdown
                _subJenisCombo.Items.Clear();
                if (bt.sub_jenis.Count > 0)
                {
                    foreach (var sub in bt.sub_jenis)
                        _subJenisCombo.Items.Add(new ComboBoxItem { Content = sub.name, Tag = sub });
                    _subJenisCombo.SelectedIndex = 0;
                }
                else
                {
                    // No sub_jenis — populate nama_bangunan directly from kategori
                    var directNama = bt.nama_bangunan ?? new List<string>();
                    UpdateNamaBangunanList(directNama);
                }

                // Update kerja pakar checkboxes based on building type
                UpdateKerjaPakarCheckboxes(bt.kategori_bangunan);
            }
        }

        private void SubJenis_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_subJenisCombo.SelectedItem is ComboBoxItem item && item.Tag is M2SubJenis sub)
            {
                UpdateNamaBangunanList(sub.nama_bangunan);
            }
        }

        private void UpdateNamaBangunanList(List<string> namaBangunanList)
        {
            _allNamaBangunan = namaBangunanList != null
                ? namaBangunanList.Distinct().OrderBy(n => n).ToList()
                : new List<string>();
            _selectedNamaBangunan = null;
            _namaBangunanSearchBox.Text = "";
            _namaBangunanSuggestList.Visibility = Visibility.Collapsed;

            if (_allNamaBangunan.Count > 0)
            {
                _namaBangunanSearchBox.Visibility = Visibility.Visible;
            }
            else
            {
                _namaBangunanSearchBox.Visibility = Visibility.Collapsed;
                _namaBangunanSuggestList.Visibility = Visibility.Collapsed;
            }
        }

        private void NamaBangunanSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // If user edits after selecting, clear the selection
            if (_selectedNamaBangunan != null && _namaBangunanSearchBox.Text != _selectedNamaBangunan)
                _selectedNamaBangunan = null;

            FilterNamaBangunanSuggestions();
        }

        private void FilterNamaBangunanSuggestions()
        {
            string query = _namaBangunanSearchBox.Text?.Trim() ?? "";
            _namaBangunanSuggestList.Items.Clear();

            var filtered = string.IsNullOrEmpty(query)
                ? _allNamaBangunan
                : _allNamaBangunan
                    .Where(n => n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

            if (filtered.Count > 0)
            {
                _namaBangunanSuggestList.Visibility = Visibility.Visible;
                foreach (var nama in filtered)
                {
                    _namaBangunanSuggestList.Items.Add(new ListBoxItem
                    {
                        Content = nama,
                        Tag = nama,
                        FontSize = 10,
                        Padding = new Thickness(6, 3, 6, 3)
                    });
                }
            }
            else
            {
                _namaBangunanSuggestList.Visibility = Visibility.Collapsed;
            }
        }

        private void NamaBangunanSuggestion_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (_namaBangunanSuggestList.SelectedItem is ListBoxItem selected)
            {
                _selectedNamaBangunan = selected.Tag?.ToString();
                _namaBangunanSearchBox.Text = _selectedNamaBangunan;
                _namaBangunanSuggestList.Visibility = Visibility.Collapsed;
                _namaBangunanSearchBox.CaretIndex = _namaBangunanSearchBox.Text.Length;

                // Load individual entries for this nama_bangunan
                LoadNamaEntriesAsync();
            }
        }

        private async void LoadNamaEntriesAsync()
        {
            string kategori = (_jenisBangunanCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (string.IsNullOrEmpty(kategori) || string.IsNullOrEmpty(_selectedNamaBangunan))
            {
                _namaEntryCombo.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var aiEstimator = new AICostEstimator();
                var entries = await aiEstimator.GetNamaEntriesAsync(kategori, _selectedNamaBangunan);

                Dispatcher.Invoke(() =>
                {
                    _namaEntryCombo.Items.Clear();
                    if (entries.Count > 0)
                    {
                        // Add "Gunakan purata kumpulan" option as first item
                        _namaEntryCombo.Items.Add(new ComboBoxItem
                        {
                            Content = "(Gunakan purata kumpulan)",
                            Tag = null,
                            FontStyle = FontStyles.Italic
                        });
                        foreach (var entry in entries)
                        {
                            _namaEntryCombo.Items.Add(new ComboBoxItem
                            {
                                Content = entry.label,
                                Tag = entry
                            });
                        }
                        _namaEntryCombo.SelectedIndex = 0;
                        _namaEntryCombo.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _namaEntryCombo.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load nama entries: {ex.Message}");
                _namaEntryCombo.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateKerjaPakarCheckboxes(string jenisBangunan)
        {
            if (_kerjaPakarPanel == null) return;
            _kerjaPakarPanel.Children.Clear();

            // Known specialist items with their percentages per building type
            var items = new[]
            {
                "Pemasangan Elektrik",
                "Pemasangan Alat Pencegah Kebakaran",
                "Pemasangan Hawa Dingin",
                "Pemasangan Lif",
                "Pemasangan Gas",
                "Pemasangan Pelbagai"
            };

            // Get percentages from last API response or use defaults
            foreach (var itemName in items)
            {
                var cb = new System.Windows.Controls.CheckBox
                {
                    Content = itemName.Replace("Pemasangan ", ""),
                    Tag = itemName,
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 12, 4),
                    IsChecked = true // auto-tick by default
                };
                _kerjaPakarPanel.Children.Add(cb);
            }
        }

        private double GetLuasTapakFromModel()
        {
            return _allItems
                .Where(i => i.Category == "Floors" && (i.Unit == "m\u00B2" || i.Unit == "m2"))
                .Sum(i => i.Quantity);
        }

        private void UpdateLuasTapakDisplay()
        {
            double luas = GetLuasTapakFromModel();
            _luasTapakText.Text = luas > 0
                ? $"{luas:N0} m\u00B2 (auto dari model Revit)"
                : "-- m\u00B2 (tiada data lantai dalam model)";
        }

        private void DisplayM2Result(M2CostBreakdown result)
        {
            _lastM2Result = result;
            _m2BreakdownPanel.Visibility = Visibility.Visible;

            // Clear and rebuild the breakdown panel
            _m2BreakdownPanel.Children.Clear();

            // ─── Hero: Total RM/m2 ───
            var heroBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var heroStack = new StackPanel();
            heroStack.Children.Add(new TextBlock
            {
                Text = $"RM {result.jumlah_kos_per_m2:N2} /m\u00B2",
                FontSize = 24, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(PrimaryBlue)
            });
            heroStack.Children.Add(new TextBlock
            {
                Text = $"Jumlah Anggaran: RM {result.jumlah_anggaran_kos_projek:N0}",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextPrimary),
                Margin = new Thickness(0, 4, 0, 2)
            });
            string subLabel = result.sub_jenis_bangunan != null ? $" / {result.sub_jenis_bangunan}" : "";
            string namaLabel = result.nama_bangunan != null ? $" / {result.nama_bangunan}" : "";
            string fallbackNote = result.fallback_kawasan != null ? $"  (fallback dari Kawasan {result.fallback_kawasan})" : "";
            heroStack.Children.Add(new TextBlock
            {
                Text = $"{result.kategori_bangunan}{subLabel}{namaLabel}\nKawasan {result.kawasan} (FL {result.faktor_lokaliti:F4})  \u2022  {result.luas_tapak:N0} m\u00B2{fallbackNote}",
                FontSize = 10, Foreground = new SolidColorBrush(TextMuted),
                TextWrapping = TextWrapping.Wrap
            });
            heroBox.Child = heroStack;
            _m2BreakdownPanel.Children.Add(heroBox);

            // ─── Step-by-step breakdown ───
            _m2BreakdownPanel.Children.Add(new TextBlock
            {
                Text = "Pecahan Kos",
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextPrimary),
                Margin = new Thickness(0, 0, 0, 6)
            });

            // Step 1: Kerja Utama
            AddBreakdownRow("1. Kos Kerja Utama Bangunan", result.kos_kerja_utama,
                $"Purata: RM {result.purata_sem_malaysia:N2}/m\u00B2 (Sem. M'sia)  |  Bil. Kajian: {result.bilangan_kajian}");

            // Step 2: Kerja Pakar
            string pakarDetail = string.Join(", ", result.kerja_pakar.Select(p =>
                $"{p.jenis_pemasangan.Replace("Pemasangan ", "")} {p.peratusan}%"));
            AddBreakdownRow("2. Kerja Pakar Dalam Bangunan", result.jumlah_kerja_pakar, pakarDetail);

            // Step 3: Kerja Luar
            AddBreakdownRow("3. Kerja Luar Bangunan", result.kos_kerja_luar,
                $"{result.kerja_luar_peratusan}% daripada Kerja Utama (n={result.kerja_luar_bilangan_contoh})");

            // Step 4: Kerja Awalan
            AddBreakdownRow("4. Kerja Awalan (Preliminaries)", result.kos_kerja_awalan,
                $"{result.kerja_awalan_peratusan}% ({result.kerja_awalan_kategori})");

            // Subtotal
            AddBreakdownRow("5. Jumlah Kecil", result.jumlah_kecil, null, true);

            // Step 6: Miscellaneous
            AddBreakdownRow("6. Pelbagai / Miscellaneous", result.kos_pelbagai,
                $"{result.pelbagai_peratusan}% daripada Jumlah Kecil");

            // Divider
            _m2BreakdownPanel.Children.Add(new Border
            {
                Height = 1, Background = new SolidColorBrush(BorderColor),
                Margin = new Thickness(0, 6, 0, 6)
            });

            // Step 7: Final total
            AddBreakdownRow("7. JUMLAH KOS PER M\u00B2", result.jumlah_kos_per_m2, null, true);

            // ─── Exclusions ───
            if (result.pengecualian != null && result.pengecualian.Count > 0)
            {
                _m2BreakdownPanel.Children.Add(new TextBlock
                {
                    Text = "Pengecualian:",
                    FontSize = 9, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(TextMuted),
                    Margin = new Thickness(0, 10, 0, 2)
                });
                foreach (var exc in result.pengecualian)
                {
                    _m2BreakdownPanel.Children.Add(new TextBlock
                    {
                        Text = $"\u2022 {exc}",
                        FontSize = 9, Foreground = new SolidColorBrush(TextMuted),
                        Margin = new Thickness(8, 0, 0, 1)
                    });
                }
            }

            // Source
            _m2BreakdownPanel.Children.Add(new TextBlock
            {
                Text = result.sumber,
                FontSize = 8, Foreground = new SolidColorBrush(TextMuted),
                Margin = new Thickness(0, 8, 0, 0),
                FontStyle = FontStyles.Italic
            });
        }

        private void AddBreakdownRow(string label, double value, string detail = null, bool bold = false)
        {
            var rowGrid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(TextPrimary),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(labelBlock, 0);
            rowGrid.Children.Add(labelBlock);

            var valueBlock = new TextBlock
            {
                Text = $"RM {value:N2}",
                FontSize = 10,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(bold ? PrimaryBlue : TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(valueBlock, 1);
            rowGrid.Children.Add(valueBlock);

            _m2BreakdownPanel.Children.Add(rowGrid);

            if (!string.IsNullOrEmpty(detail))
            {
                _m2BreakdownPanel.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 8, Foreground = new SolidColorBrush(TextMuted),
                    Margin = new Thickness(0, 0, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        // --- Kerja Luar predictive search ---

        private void KerjaLuarSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Debounce: wait 300ms after user stops typing
            if (_kerjaLuarSearchTimer == null)
            {
                _kerjaLuarSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _kerjaLuarSearchTimer.Tick += (s, ev) =>
                {
                    _kerjaLuarSearchTimer.Stop();
                    PerformKerjaLuarSearch();
                };
            }
            _kerjaLuarSearchTimer.Stop();
            _kerjaLuarSearchTimer.Start();
        }

        private async void PerformKerjaLuarSearch()
        {
            string jenis = (_jenisBangunanCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            string query = _kerjaLuarSearchBox?.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(jenis)) return;

            try
            {
                var aiEstimator = new AICostEstimator();
                var results = await aiEstimator.SearchKerjaLuarTypesAsync(jenis, query);

                Dispatcher.Invoke(() =>
                {
                    _kerjaLuarResultsList.Items.Clear();
                    if (results.Count > 0)
                    {
                        _kerjaLuarResultsList.Visibility = Visibility.Visible;
                        foreach (var item in results)
                        {
                            _kerjaLuarResultsList.Items.Add(new ListBoxItem
                            {
                                Content = $"{item.sub_jenis} ({item.peratusan}%, n={item.bilangan_contoh})",
                                Tag = item.sub_jenis,
                                FontSize = 10
                            });
                        }
                    }
                    else
                    {
                        _kerjaLuarResultsList.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Kerja luar search failed: {ex.Message}");
            }
        }

        private void KerjaLuarResult_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (_kerjaLuarResultsList.SelectedItem is ListBoxItem selected)
            {
                _selectedKerjaLuarSubJenis = selected.Tag?.ToString();
                _kerjaLuarSearchBox.Text = _selectedKerjaLuarSubJenis;
                _kerjaLuarResultsList.Visibility = Visibility.Collapsed;
            }
        }

        private List<string> GetSelectedKerjaPakar()
        {
            if (_kerjaPakarPanel == null) return null;
            var selected = new List<string>();
            foreach (var child in _kerjaPakarPanel.Children)
            {
                if (child is System.Windows.Controls.CheckBox cb && cb.IsChecked == true)
                    selected.Add(cb.Tag?.ToString());
            }
            return selected.Count > 0 ? selected : null;
        }

        private void UpdateComponentCard()
        {
            if (_allItems.Count == 0 || _componentListPanel == null) return;

            _componentListPanel.Children.Clear();
            var compSummary = CostCalculator.CalculateComponentSummary(_allItems);

            if (compSummary.Groups.Count == 0)
            {
                _componentListPanel.Children.Add(new TextBlock
                {
                    Text = "No component data",
                    FontSize = 11, Foreground = new SolidColorBrush(TextMuted),
                    Margin = new Thickness(0, 4, 0, 0)
                });
                return;
            }

            foreach (var group in compSummary.Groups)
            {
                // Sub-group detail panel (lazy, starts collapsed)
                var subPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };

                // Category header row
                var groupRow = new Border
                {
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 2, 0, 0),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(PageBg)
                };
                var groupGrid = new Grid();
                groupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                groupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                groupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Arrow + category name
                var arrowText = new TextBlock
                {
                    Text = "\u25B6", FontSize = 8,
                    Foreground = new SolidColorBrush(TextMuted),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Tag = "arrow"
                };
                Grid.SetColumn(arrowText, 0);
                groupGrid.Children.Add(arrowText);

                var nameStack = new StackPanel();
                var catName = new TextBlock
                {
                    Text = group.Category,
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(TextPrimary)
                };
                nameStack.Children.Add(catName);

                var subtitleParts = new List<string> { $"{group.ItemCount} items" };
                if (group.Percentage > 0)
                    subtitleParts.Add($"{group.Percentage:F1}%");
                if (group.UnpricedCount > 0)
                    subtitleParts.Add($"{group.UnpricedCount} unpriced");
                var catSubtitle = new TextBlock
                {
                    Text = string.Join("  |  ", subtitleParts),
                    FontSize = 9,
                    Foreground = group.UnpricedCount > 0 ? new SolidColorBrush(WarningAmber) : new SolidColorBrush(TextMuted)
                };
                nameStack.Children.Add(catSubtitle);
                Grid.SetColumn(nameStack, 1);
                groupGrid.Children.Add(nameStack);

                // Cost amount
                var costText = new TextBlock
                {
                    Text = $"RM {group.TotalCost:N0}",
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(PrimaryBlue),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(costText, 2);
                groupGrid.Children.Add(costText);

                groupRow.Child = groupGrid;

                // Hover effect
                groupRow.MouseEnter += (s, ev) => groupRow.Background = new SolidColorBrush(RowHover);
                groupRow.MouseLeave += (s, ev) => groupRow.Background = new SolidColorBrush(PageBg);

                // Click to expand/collapse sub-groups
                groupRow.MouseLeftButtonDown += (s, ev) =>
                {
                    bool expanding = subPanel.Visibility != Visibility.Visible;
                    subPanel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                    arrowText.Text = expanding ? "\u25BC" : "\u25B6";

                    // Lazy-build sub-group rows on first expand
                    if (expanding && subPanel.Children.Count == 0)
                    {
                        foreach (var sub in group.SubGroups)
                        {
                            var subRow = new Grid { Margin = new Thickness(20, 1, 0, 1) };
                            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                            // Sub-group name + qty
                            var subNameStack = new StackPanel();
                            subNameStack.Children.Add(new TextBlock
                            {
                                Text = sub.Name,
                                FontSize = 10, Foreground = new SolidColorBrush(TextPrimary),
                                TextTrimming = TextTrimming.CharacterEllipsis
                            });
                            var qtyParts = $"{sub.TotalQuantity:N1} {sub.Unit}  ({sub.ItemCount} items)";
                            if (sub.UnpricedCount > 0)
                                qtyParts += $"  [{sub.UnpricedCount} unpriced]";
                            subNameStack.Children.Add(new TextBlock
                            {
                                Text = qtyParts,
                                FontSize = 9,
                                Foreground = sub.UnpricedCount > 0 ? new SolidColorBrush(WarningAmber) : new SolidColorBrush(TextMuted)
                            });
                            Grid.SetColumn(subNameStack, 0);
                            subRow.Children.Add(subNameStack);

                            // Avg unit price
                            if (sub.AverageUnitPrice > 0)
                            {
                                var avgText = new TextBlock
                                {
                                    Text = $"RM {sub.AverageUnitPrice:N2}/{sub.Unit}",
                                    FontSize = 9, Foreground = new SolidColorBrush(TextSecondary),
                                    VerticalAlignment = VerticalAlignment.Center,
                                    Margin = new Thickness(8, 0, 8, 0)
                                };
                                Grid.SetColumn(avgText, 1);
                                subRow.Children.Add(avgText);
                            }

                            // Total cost
                            var subCostText = new TextBlock
                            {
                                Text = $"RM {sub.TotalCost:N0}",
                                FontSize = 10, FontWeight = FontWeights.SemiBold,
                                Foreground = new SolidColorBrush(TextPrimary),
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            Grid.SetColumn(subCostText, 2);
                            subRow.Children.Add(subCostText);

                            subPanel.Children.Add(subRow);
                        }
                    }
                };

                _componentListPanel.Children.Add(groupRow);
                _componentListPanel.Children.Add(subPanel);
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
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            row.MouseEnter += (s, e) => { row.Background = new SolidColorBrush(RowHover); row.BorderBrush = new SolidColorBrush(PrimaryBlue); };
            row.MouseLeave += (s, e) => { row.Background = Brushes.White; row.BorderBrush = new SolidColorBrush(BorderColor); };
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
            var backBtn = new Button { Content = "< Back", Background = Brushes.Transparent, Foreground = new SolidColorBrush(PrimaryBlue), BorderThickness = new Thickness(0), FontSize = 11, FontWeight = FontWeights.Medium, Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            backBtn.Click += (s, e) => UpdateContent();
            _contentPanel.Children.Add(backBtn);

            // Title
            var titleCard = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10) };
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

        /// <summary>
        /// Apply a reviewed/accepted price to matching items in the dashboard and recalculate totals.
        /// </summary>
        private void ApplyReviewPrice(string elementName, string jkrCode, double unitPrice, string source)
        {
            if (unitPrice <= 0 || string.IsNullOrEmpty(elementName)) return;

            foreach (var item in _allItems)
            {
                if (item.Name != elementName) continue;
                // Override if: unpriced, or was a guess (estimated/pending_review)
                bool isGuess = item.PriceSource == "estimated" || item.PriceSource == "pending_review";
                if (item.UnitPrice <= 0 || isGuess)
                {
                    item.UnitPrice = unitPrice;
                    if (!string.IsNullOrEmpty(jkrCode)) item.JkrCode = jkrCode;
                    item.PriceSource = source;
                    _priceDb?.SetPrice(jkrCode ?? "", unitPrice, item.Unit, item.Name, source);
                }
            }
            _priceDb?.Save();
            _summary = CostCalculator.Calculate(_allItems);
            UpdateTotalCard();
        }

        // ── Live Update ──

        public void OnModelChanged(ChangeSummary changeSummary)
        {
            try
            {
                if (_suppressModelChanged) return;
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

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _suppressModelChanged = false; // Reset in case it got stuck
            RefreshData();
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will clear all prices, review queue, and learned mappings.\n\n" +
                "Your price databases (N3C, PWCIC, JKR rates) will NOT be affected.\n\n" +
                "Continue?",
                "BINA Cost — Reset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                // 1. Clear local price DB file
                _priceDb?.Clear();

                // 2. Zero out cost parameters on Revit elements (so prices don't
                //    come back after save + reopen). Uses the ExternalEvent
                //    pattern — need to snapshot items before clearing memory.
                var itemsToClear = new List<CostItem>(_allItems);
                var handler = App.CostWriteHandler;
                var evt = App.CostWriteEvent;
                if (handler != null && evt != null && itemsToClear.Count > 0)
                {
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    handler.Items = itemsToClear;
                    handler.ClearMode = true;
                    _suppressModelChanged = true;
                    handler.OnCompleted = () =>
                    {
                        handler.ClearMode = false; // reset for next Match Prices run
                        tcs.TrySetResult(true);
                    };
                    evt.Raise();
                    // Wait for Revit to finish the transaction (max 10s safety)
                    await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(10000));
                    _suppressModelChanged = false;
                    // Defensive: ensure ClearMode is off even if callback didn't fire (timeout)
                    handler.ClearMode = false;

                    // If the Revit write failed, abort reset so we don't leave
                    // model parameters out of sync with cleared local/backend state.
                    if (handler.Error != null)
                    {
                        MessageBox.Show(
                            $"Reset aborted — could not clear prices from Revit model.\n\n" +
                            $"Error: {handler.Error}\n\n" +
                            "Local data and backend were NOT cleared.",
                            "BINA Cost — Reset Failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }
                }

                // 3. Clear all item prices in memory
                foreach (var item in _allItems)
                {
                    item.UnitPrice = 0;
                    item.JkrCode = null;
                    item.PriceSource = null;
                }

                // 4. Clear backend review_queue + learned_mappings
                var estimator = new AICostEstimator();
                bool serverReset = await estimator.ResetCostDataAsync();
                string serverMsg = serverReset ? "Server data cleared." : "Server not reachable — local data cleared only.";

                // 5. Re-scan model fresh (no preserved prices, no model params)
                _allItems.Clear();
                _suppressModelChanged = false;
                _isResetting = true;
                RefreshData();
                _isResetting = false;

                UpdateReviewBadge();
                MessageBox.Show($"Reset complete. {serverMsg}\n\nClick Match Prices to re-run the pipeline.", "BINA Cost");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Reset failed: {ex.Message}", "BINA Cost");
            }
        }

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
            _suppressModelChanged = true;
            try
            {
                // Auto-refresh if no data loaded yet
                if (_allItems.Count == 0)
                {
                    RefreshData();
                    if (_allItems.Count == 0) { MessageBox.Show("No elements found in the model.", "BINA Cost"); return; }
                }

                // Get luas tapak from model
                double luasTapak = GetLuasTapakFromModel();
                if (luasTapak <= 0)
                {
                    MessageBox.Show("Tiada data lantai (Floor) dalam model.\nLuas tapak diperlukan untuk pengiraan.", "BINA Cost");
                    return;
                }

                // Get selected values from dropdowns
                string jenisBangunan = (_jenisBangunanCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
                string subJenis = (_subJenisCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
                string kawasan = (_negeriCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();

                if (string.IsNullOrEmpty(jenisBangunan) || string.IsNullOrEmpty(kawasan))
                {
                    MessageBox.Show("Sila pilih Jenis Bangunan dan Negeri.", "BINA Cost");
                    return;
                }

                // Disable button
                if (btn != null) { btn.IsEnabled = false; btn.Content = "\u23F3 Mengira..."; }

                // Call m2 estimation API
                var aiEstimator = new AICostEstimator();
                string namaBangunan = _selectedNamaBangunan;

                // Get nama_entry if user selected a specific building entry
                string namaEntry = null;
                string noLukisan = null;
                if (_namaEntryCombo?.SelectedItem is ComboBoxItem entryItem && entryItem.Tag is NamaEntryItem selectedEntry)
                {
                    namaEntry = selectedEntry.nama_entry;
                    noLukisan = selectedEntry.no_lukisan;
                }

                var request = new M2EstimateRequest
                {
                    kategori_bangunan = jenisBangunan,
                    sub_jenis_bangunan = subJenis,
                    nama_bangunan = namaBangunan,
                    nama_entry = namaEntry,
                    no_lukisan = noLukisan,
                    kawasan = kawasan,
                    luas_tapak = luasTapak,
                    kerja_pakar_selected = GetSelectedKerjaPakar(),
                    kerja_luar_sub_jenis = null,
                    project_name = _subtitleText?.Text?.Split('|')?.FirstOrDefault()?.Trim() ?? "Untitled"
                };

                var result = await aiEstimator.EstimateM2CostAsync(request);

                if (result.success && result.breakdown != null)
                {
                    DisplayM2Result(result.breakdown);
                    UpdateLuasTapakDisplay();
                }
                else
                {
                    MessageBox.Show($"Gagal mengira anggaran:\n{result.error}", "BINA Cost");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "BINA Cost");
            }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "Kira Anggaran"; }
                _suppressModelChanged = false;
            }
        }

        // Keep original pipeline method for reference (renamed, not called)
        private async void AutoMatch_Pipeline_Original(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            _suppressModelChanged = true;
            try
            {
                if (_allItems.Count == 0)
                {
                    RefreshData();
                    if (_allItems.Count == 0) { MessageBox.Show("No elements found in the model.", "BINA Cost"); return; }
                }

                // Disable button and show loading overlay
                if (btn != null) { btn.IsEnabled = false; btn.Content = "⏳ Matching..."; }
                ShowLoadingOverlay();
                UpdateLoadingStep(0, $"Scanning {_allItems.Count} items...", false);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                int localMatched = 0;

                // Step 1: Local master DB match (fast, offline)
                var masterDb = MasterPriceDatabase.Instance;
                if (masterDb.Count > 0)
                {
                    localMatched = masterDb.AutoMatchPrices(_allItems, _priceDb);
                }
                // Update cost display after local matching so user sees intermediate total
                _summary = CostCalculator.Calculate(_allItems);
                UpdateTotalCard();
                System.Diagnostics.Debug.WriteLine($"=== COST BEFORE PIPELINE: RM {_summary.GrandTotal:N0} | Priced: {_summary.PriceablePricedItems}/{_summary.PriceableItems} priceable ({(_summary.PriceableItems > 0 ? (int)Math.Round((_summary.PriceablePricedItems / (double)_summary.PriceableItems) * 100) : 0)}%) | Local matched: {localMatched} ===");
                UpdateLoadingStep(0, $"{localMatched} matched", true);
                _loadingPercentText.Text = "5%";
                _loadingProgressBar.Value = 5;
                _loadingCountText.Text = $"{localMatched:N0} / {_allItems.Count:N0} items";
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Step 2: AI 4-layer pipeline for unpriced items only
                var aiEstimator = new AICostEstimator();
                bool aiAvailable = await aiEstimator.IsAvailableAsync();

                int pipelineMatched = 0;
                int reviewQueued = 0;
                string matchRate = "0%";

                if (aiAvailable)
                {
                    // Only send unpriced items — already-matched items don't need re-matching.
                    // Skip categories like Rebar/Fittings/Connections that get rolled into parent prices
                    // in Malaysian QS practice (would otherwise inflate the total via unit mismatches).
                    var unmatchedItems = _allItems
                        .Where(i => i.UnitPrice <= 0 && CostCalculator.IsAutoPriceable(i.Category))
                        .ToList();
                    if (unmatchedItems.Count == 0)
                    {
                        _loadingStatusText.Text = "All items already have prices!";
                        UpdateLoadingStep(1, "skipped", true);
                        UpdateLoadingStep(2, "skipped", true);
                        UpdateLoadingStep(3, "skipped", true);
                        UpdateLoadingStep(4, "skipped", true);
                    }
                    else
                    {
                        int alreadyPricedCount = _allItems.Count - unmatchedItems.Count;
                        _loadingStatusText.Text = alreadyPricedCount > 0
                            ? $"Sending {unmatchedItems.Count} items (skipping {alreadyPricedCount} already priced)..."
                            : $"Sending {unmatchedItems.Count} items...";
                        UpdateLoadingStep(1, "processing...", false);
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                        string projectName = _subtitleText?.Text?.Split('|')?.FirstOrDefault()?.Trim() ?? "Untitled";

                        int totalForPipeline = unmatchedItems.Count;

                        // Use SSE streaming for real-time progress
                        var result = await aiEstimator.MatchPipelineStreamAsync(
                            unmatchedItems, projectName,
                            onProgress: (evt) =>
                            {
                                // Update loading steps and percentage in real-time from SSE events
                                Dispatcher.Invoke(() =>
                                {
                                    // Calculate overall percentage: Local=5%, L1=10%, L2=15%, L3=15-90%, L4=95%
                                    int pct = 5; // Base from local DB step
                                    switch (evt.Layer)
                                    {
                                        case "starting":
                                            pct = 6;
                                            _loadingStatusText.Text = evt.Message;
                                            break;
                                        case "layer1_exact":
                                            pct = 10;
                                            UpdateLoadingStep(1, evt.Message, true);
                                            _loadingStatusText.Text = evt.Message;
                                            break;
                                        case "layer2_learned":
                                            pct = 15;
                                            UpdateLoadingStep(2, evt.Message, true);
                                            _loadingStatusText.Text = evt.Message;
                                            break;
                                        case "layer3_vector":
                                            // Layer 3 spans 15% to 90% — use processed/total for sub-progress
                                            if (evt.Total > 0)
                                                pct = 15 + (int)(75.0 * evt.Processed / evt.Total);
                                            else if (evt.TotalBatches > 0)
                                                pct = 15 + (int)(75.0 * evt.Batch / evt.TotalBatches);
                                            else
                                                pct = 90;

                                            if (evt.Total > 0)
                                                UpdateLoadingStep(3, $"{evt.Processed}/{evt.Total} items", false);
                                            else if (evt.TotalBatches > 0)
                                                UpdateLoadingStep(3, $"batch {evt.Batch}/{evt.TotalBatches}", false);
                                            else
                                                UpdateLoadingStep(3, evt.Message, true);
                                            _loadingStatusText.Text = evt.Message;
                                            break;
                                        case "layer4_review":
                                            pct = 95;
                                            UpdateLoadingStep(4, evt.Message, true);
                                            _loadingStatusText.Text = evt.Message;
                                            break;
                                        default:
                                            _loadingStatusText.Text = evt.Message;
                                            break;
                                    }

                                    _loadingPercentText.Text = $"{pct}%";
                                    _loadingProgressBar.Value = pct;
                                    if (evt.Processed > 0 && evt.Total > 0)
                                        _loadingCountText.Text = $"{evt.Processed:N0} / {evt.Total:N0} items";
                                });
                            });

                        if (result.Success)
                        {
                            var stats = result.Stats;
                            pipelineMatched = stats.TotalMatched;
                            reviewQueued = stats.Layer4Review;
                            matchRate = stats.MatchRate;

                            // Final step updates
                            UpdateLoadingStep(1, $"{stats.Layer1Exact} matched", true);
                            UpdateLoadingStep(2, $"{stats.Layer2Learned} matched", true);
                            UpdateLoadingStep(3, $"{stats.Layer3Vector + stats.Layer3Provisional} matched", true);
                            UpdateLoadingStep(4, reviewQueued > 0 ? $"{reviewQueued} queued" : "0 queued", true);
                            _loadingStatusText.Text = $"Done — {pipelineMatched} matched ({matchRate})";
                            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                            // Apply ALL matches — low confidence items get price applied
                            // but marked as "pending_review" so user can correct them
                            foreach (var match in result.Matches)
                            {
                                if (match.UnitPrice <= 0) continue;
                                var item = _allItems.FirstOrDefault(x => x.ElementId == match.ElementId);
                                if (item != null && item.UnitPrice <= 0)
                                {
                                    item.UnitPrice = match.UnitPrice;
                                    item.JkrCode = match.JkrCode;
                                    if (match.Confidence == "low")
                                    {
                                        item.PriceSource = "pending_review";
                                    }
                                    else
                                    {
                                        string source = match.Reasoning?.Split(',')[0] ?? match.MatchLayer;
                                        item.PriceSource = match.MatchLayer == "layer1_exact" ? "master" :
                                                           match.MatchLayer == "layer2_learned" ? "learned" : source;
                                    }
                                    _priceDb?.SetPrice(item.JkrCode, item.UnitPrice, item.Unit, item.Name, item.PriceSource);
                                }
                            }
                            // Update cost display progressively so user sees gradual increase
                            _summary = CostCalculator.Calculate(_allItems);
                            UpdateTotalCard();

                            // Debug: log top cost items to identify what's driving the total
                            var topItems = _allItems
                                .Where(i => i.TotalPrice > 0)
                                .OrderByDescending(i => i.TotalPrice)
                                .Take(20)
                                .ToList();
                            System.Diagnostics.Debug.WriteLine("=== TOP 20 COST ITEMS (after pipeline) ===");
                            System.Diagnostics.Debug.WriteLine($"Grand Total: RM {_summary.GrandTotal:N0} | Priced: {_summary.PricedItems}/{_summary.TotalItems}");
                            foreach (var ti in topItems)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"  RM {ti.TotalPrice:N0} = {ti.Quantity:N2} {ti.Unit} x RM {ti.UnitPrice:N2} | {ti.Category} | {ti.Name} | JKR: {ti.JkrCode} | Source: {ti.PriceSource} | ElemId: {ti.ElementId}");
                            }

                            // Debug: log items matched by AI pipeline specifically
                            var pipelineItems = _allItems
                                .Where(i => i.PriceSource != null && i.PriceSource != "master" && i.PriceSource != "manual" && i.PriceSource != "imported" && i.UnitPrice > 0)
                                .OrderByDescending(i => i.TotalPrice)
                                .Take(20)
                                .ToList();
                            if (pipelineItems.Any())
                            {
                                double pipelineTotal = pipelineItems.Sum(i => i.TotalPrice);
                                System.Diagnostics.Debug.WriteLine($"=== TOP AI-MATCHED ITEMS (total from AI: RM {_allItems.Where(i => i.PriceSource != "master" && i.PriceSource != "manual" && i.PriceSource != "imported" && i.UnitPrice > 0).Sum(i => i.TotalPrice):N0}) ===");
                                foreach (var pi in pipelineItems)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"  RM {pi.TotalPrice:N0} = {pi.Quantity:N2} {pi.Unit} x RM {pi.UnitPrice:N2} | {pi.Category} | {pi.Name} | JKR: {pi.JkrCode} | Source: {pi.PriceSource}");
                                }
                            }
                            System.Diagnostics.Debug.WriteLine("=== END COST DEBUG ===");
                        }
                        else
                        {
                            _loadingStatusText.Text = result.Error ?? "Pipeline error";
                            UpdateLoadingStep(1, "error", false);
                        }
                    }
                }
                else
                {
                    _loadingStatusText.Text = "AI server not available — local matching only";
                    UpdateLoadingStep(1, "offline", false);
                    UpdateLoadingStep(2, "offline", false);
                    UpdateLoadingStep(3, "offline", false);
                    UpdateLoadingStep(4, "offline", false);
                }

                // Fallback: apply category average to remaining unpriced items AND queue for review.
                // Price is applied immediately (counts toward total) but marked "estimated"
                // so user can correct via Review.
                var stillUnpriced = _allItems
                    .Where(i => i.UnitPrice <= 0 && CostCalculator.IsAutoPriceable(i.Category))
                    .ToList();
                if (stillUnpriced.Count > 0)
                {
                    // Build averages from priced items grouped by Category + Unit
                    var avgPrices = _allItems
                        .Where(i => i.UnitPrice > 0)
                        .GroupBy(i => (i.Category, i.Unit))
                        .ToDictionary(
                            g => g.Key,
                            g => g.Average(i => i.UnitPrice));

                    // Apply estimated price AND collect clones for review queue
                    var estimatedItems = new List<CostItem>();
                    foreach (var item in stillUnpriced)
                    {
                        var key = (item.Category, item.Unit);
                        if (avgPrices.TryGetValue(key, out double avgPrice))
                        {
                            // Apply estimated price to the actual item (counts toward total)
                            item.UnitPrice = Math.Round(avgPrice, 2);
                            item.PriceSource = "estimated";

                            // Clone for review queue so user can correct
                            var clone = new CostItem
                            {
                                ElementId = item.ElementId,
                                Name = item.Name,
                                FamilyName = item.FamilyName,
                                TypeName = item.TypeName,
                                Category = item.Category,
                                Quantity = item.Quantity,
                                Unit = item.Unit,
                                UnitPrice = Math.Round(avgPrice, 2),
                            };
                            estimatedItems.Add(clone);
                        }
                    }

                    // Queue estimated items for user review (correction flow)
                    if (estimatedItems.Count > 0)
                    {
                        string projectName = _subtitleText?.Text?.Split('|')?.FirstOrDefault()?.Trim() ?? "Untitled";
                        var est = new AICostEstimator();
                        await est.QueueEstimatedForReviewAsync(estimatedItems, projectName);
                        reviewQueued += estimatedItems.Count;
                        System.Diagnostics.Debug.WriteLine($"[BINA Cost] Applied + queued {estimatedItems.Count} estimated items for review");
                    }

                    // Log any truly unmatchable items (no category average available)
                    var trulyUnpriced = _allItems.Where(i => i.UnitPrice <= 0 && CostCalculator.IsAutoPriceable(i.Category)).ToList();
                    if (trulyUnpriced.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA Cost] {trulyUnpriced.Count} items still unpriced (no category average):");
                        foreach (var u in trulyUnpriced.Take(20))
                            System.Diagnostics.Debug.WriteLine($"  ElemId: {u.ElementId} | {u.Category} | {u.Name} | Qty: {u.Quantity} {u.Unit} | JKR: {u.JkrCode}");
                    }
                }

                // Show 100% before hiding
                _loadingPercentText.Text = "100%";
                _loadingProgressBar.Value = 100;
                _loadingCountText.Text = $"{_allItems.Count:N0} items processed";
                await Task.Delay(1000);
                HideLoadingOverlay();

                _summary = CostCalculator.Calculate(_allItems); UpdateTotalCard(); UpdateContent();

                // Write matched prices to Revit model parameters via ExternalEvent
                // (must run on Revit's API thread, not the async/UI thread)
                string writeError = null;
                try
                {
                    var handler = App.CostWriteHandler;
                    var evt = App.CostWriteEvent;
                    if (handler != null && evt != null)
                    {
                        handler.Items = _allItems;
                        handler.ClearMode = false; // defensive: ensure we write, not clear
                        _suppressModelChanged = true;
                        var suppressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                        suppressTimer.Tick += (t, te) => { _suppressModelChanged = false; suppressTimer.Stop(); };
                        suppressTimer.Start();
                        handler.OnCompleted = () =>
                        {
                            Dispatcher.Invoke(async () =>
                            {
                                suppressTimer.Stop();
                                _suppressModelChanged = false;

                                // Get actual review count from backend
                                int pendingReview = 0;
                                try
                                {
                                    var stats2 = await new AICostEstimator().GetReviewStatsAsync();
                                    if (stats2 != null) pendingReview = stats2.ReviewPending;
                                }
                                catch { }

                                if (handler.Error != null)
                                {
                                    ShowBanner("Write error: " + handler.Error, "", WarningAmber);
                                }
                                else if (handler.WrittenCount > 0)
                                {
                                    string writeDetail = handler.ScheduleCreated
                                        ? "Open 'BINA Cost Summary' in Project Browser > Schedules to edit prices"
                                        : "Edit prices in 'BINA Cost Summary' schedule";
                                    if (pendingReview > 0)
                                        writeDetail += $" | {pendingReview} items need review";
                                    ShowBanner(
                                        $"Wrote {handler.WrittenCount} prices to model",
                                        writeDetail,
                                        pendingReview > 0 ? WarningAmber : SuccessGreen);
                                }
                                else if (pendingReview > 0)
                                {
                                    ShowBanner($"RM {_summary.GrandTotal:N0} — {pendingReview} items need review",
                                        "Click Review to confirm estimated prices", WarningAmber);
                                }
                            });
                        };
                        evt.Raise();
                    }
                }
                catch (Exception writeEx)
                {
                    writeError = writeEx.Message;
                    _suppressModelChanged = false;
                    System.Diagnostics.Debug.WriteLine($"[BINA Cost] Parameter write failed: {writeEx.Message}");
                }

                // Persist prices to local DB
                _priceDb?.Save();

                int totalMatched = localMatched + pipelineMatched;
                int skippedCount = _allItems.Count(i => !CostCalculator.IsAutoPriceable(i.Category));
                var parts = new List<string>();
                if (localMatched > 0) parts.Add($"Local: {localMatched}");
                if (pipelineMatched > 0) parts.Add($"Pipeline: {pipelineMatched}");
                parts.Add($"Rate: {matchRate}");
                if (skippedCount > 0) parts.Add($"{skippedCount} sub-elements excluded");
                string detail = string.Join(" | ", parts);

                string scheduleHint = "";
                if (writeError != null) scheduleHint = $" | Write error: {writeError}";

                if (reviewQueued > 0)
                {
                    ShowBanner($"Matched {totalMatched} items — RM {_summary.GrandTotal:N0}",
                        $"{detail} | {reviewQueued} items need review{scheduleHint}", WarningAmber);
                }
                else
                {
                    ShowBanner($"Matched {totalMatched} items — RM {_summary.GrandTotal:N0}", $"{detail}{scheduleHint}", SuccessGreen);
                }

                // Update review badge on Review button
                UpdateReviewBadge();
            }
            catch (Exception ex)
            {
                HideLoadingOverlay();
                MessageBox.Show($"Match failed: {ex.Message}", "BINA Cost");
            }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "Kira Anggaran"; }
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

                // ── Declare manual input controls early so suggestions can reference them ──
                var manualPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                var jkrBox = new System.Windows.Controls.TextBox
                {
                    Width = 120, Height = 26, FontSize = 11,
                    Background = new SolidColorBrush(Color.FromRgb(52, 52, 56)),
                    Foreground = new SolidColorBrush(textDim),
                    BorderBrush = new SolidColorBrush(borderSubtle),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 3, 6, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    Text = "JKR Code",
                    CaretBrush = new SolidColorBrush(textWhite)
                };
                jkrBox.GotFocus += (s, ev) => { if (jkrBox.Text == "JKR Code") { jkrBox.Text = ""; jkrBox.Foreground = new SolidColorBrush(textWhite); } };
                jkrBox.LostFocus += (s, ev) => { if (string.IsNullOrWhiteSpace(jkrBox.Text)) { jkrBox.Text = "JKR Code"; jkrBox.Foreground = new SolidColorBrush(textDim); } };
                var priceBox = new System.Windows.Controls.TextBox
                {
                    Width = 80, Height = 26, FontSize = 11,
                    Background = new SolidColorBrush(Color.FromRgb(52, 52, 56)),
                    Foreground = new SolidColorBrush(textDim),
                    BorderBrush = new SolidColorBrush(borderSubtle),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 3, 6, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    Text = "RM",
                    CaretBrush = new SolidColorBrush(textWhite)
                };
                priceBox.GotFocus += (s, ev) => { if (priceBox.Text == "RM") { priceBox.Text = ""; priceBox.Foreground = new SolidColorBrush(textWhite); } };
                priceBox.LostFocus += (s, ev) => { if (string.IsNullOrWhiteSpace(priceBox.Text)) { priceBox.Text = "RM"; priceBox.Foreground = new SolidColorBrush(textDim); } };

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
                            BorderThickness = isTop ? new Thickness(1) : new Thickness(0),
                            Cursor = System.Windows.Input.Cursors.Hand,
                            ToolTip = "Click to use this suggestion"
                        };
                        // Click suggestion to pre-fill manual input
                        var capturedSugg = sugg;
                        suggRow.MouseLeftButtonDown += (s, ev) =>
                        {
                            jkrBox.Text = capturedSugg.JkrCode ?? "";
                            jkrBox.Foreground = new SolidColorBrush(textWhite);
                            priceBox.Text = capturedSugg.UnitPrice.ToString("F2");
                            priceBox.Foreground = new SolidColorBrush(textWhite);
                            manualPanel.Visibility = Visibility.Visible;
                        };
                        suggRow.MouseEnter += (s, ev) => suggRow.Background = new SolidColorBrush(Color.FromArgb(30, accentBlue.R, accentBlue.G, accentBlue.B));
                        suggRow.MouseLeave += (s, ev) => suggRow.Background = new SolidColorBrush(isTop ? Color.FromArgb(20, accentGreen.R, accentGreen.G, accentGreen.B) : Colors.Transparent);

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
                        Text = "⚠ No AI suggestions — enter manually below",
                        FontSize = 11, Foreground = new SolidColorBrush(accentAmber),
                        Margin = new Thickness(0, 6, 0, 0),
                        FontStyle = FontStyles.Italic
                    });
                }

                // ── Manual input panel (controls declared earlier for suggestion click access) ──

                // Toggle link for items with suggestions (collapsed by default)
                if (hasSugg)
                {
                    manualPanel.Visibility = Visibility.Collapsed;
                    var toggleLink = new TextBlock
                    {
                        Text = "✏ Enter price manually...",
                        FontSize = 10, Foreground = new SolidColorBrush(accentBlue),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Margin = new Thickness(0, 6, 0, 0)
                    };
                    var capturedPanel = manualPanel;
                    toggleLink.MouseLeftButtonDown += (s, ev) =>
                    {
                        capturedPanel.Visibility = capturedPanel.Visibility == Visibility.Visible
                            ? Visibility.Collapsed : Visibility.Visible;
                        toggleLink.Text = capturedPanel.Visibility == Visibility.Visible
                            ? "✏ Hide manual input" : "✏ Enter price manually...";
                    };
                    infoStack.Children.Add(toggleLink);
                }

                var manualInputRow = new StackPanel { Orientation = Orientation.Horizontal };
                manualInputRow.Children.Add(jkrBox);
                manualInputRow.Children.Add(priceBox);

                manualInputRow.Children.Add(new TextBlock
                {
                    Text = $"/{review.Unit ?? "unit"}",
                    FontSize = 10, Foreground = new SolidColorBrush(textDim),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });

                var confirmBtn = new Button
                {
                    Content = "Confirm",
                    FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Padding = new Thickness(10, 4, 10, 4),
                    Background = new SolidColorBrush(accentPurple),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = review
                };
                confirmBtn.Click += async (s, ev) =>
                {
                    string jkr = jkrBox.Text.Trim();
                    if (jkr == "JKR Code") jkr = "";
                    string priceText = priceBox.Text.Trim();
                    if (priceText == "RM") priceText = "";

                    double price = 0;
                    if (string.IsNullOrEmpty(jkr) || !double.TryParse(priceText, out price) || price <= 0)
                    {
                        jkrBox.BorderBrush = string.IsNullOrEmpty(jkr) ? new SolidColorBrush(accentRed) : new SolidColorBrush(borderSubtle);
                        priceBox.BorderBrush = (price <= 0 || priceText == "") ? new SolidColorBrush(accentRed) : new SolidColorBrush(borderSubtle);
                        return;
                    }

                    confirmBtn.IsEnabled = false;
                    confirmBtn.Content = "⏳...";

                    var r = (ReviewItem)((Button)s).Tag;
                    var result = await aiEstimator.ResolveReviewAsync(
                        r.Id, jkr, price, r.Unit ?? "unit", r.ElementName ?? "");

                    if (result.Success)
                    {
                        card.Background = new SolidColorBrush(Color.FromArgb(20, accentGreen.R, accentGreen.G, accentGreen.B));
                        card.BorderBrush = new SolidColorBrush(Color.FromArgb(60, accentGreen.R, accentGreen.G, accentGreen.B));
                        confirmBtn.Content = "✓ Saved";
                        confirmBtn.Background = new SolidColorBrush(Color.FromRgb(22, 101, 52));

                        // Apply price to dashboard immediately
                        ApplyReviewPrice(r.ElementName, jkr, price, "manual");
                    }
                    else
                    {
                        confirmBtn.Content = "✗ Error";
                        confirmBtn.Background = new SolidColorBrush(accentRed);
                        confirmBtn.IsEnabled = true;
                    }
                };
                manualInputRow.Children.Add(confirmBtn);

                manualPanel.Children.Add(manualInputRow);
                infoStack.Children.Add(manualPanel);

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
                        if (r.AiSuggestions == null || !r.AiSuggestions.Any()) { ((Button)s).Content = "No suggestions"; return; }
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
                            ((Button)s).Background = new SolidColorBrush(Color.FromRgb(22, 101, 52));

                            // Apply price to dashboard immediately
                            ApplyReviewPrice(r.ElementName, top.JkrCode, top.UnitPrice, "learned");
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
                int total = reviewsWithSugg.Count;
                progressText.Text = $"Resolving {total} items...";
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Build batch request
                var batchItems = reviewsWithSugg.Select(r =>
                {
                    var top = r.AiSuggestions.First();
                    return new BatchResolveItem
                    {
                        ReviewId = r.Id,
                        JkrCode = top.JkrCode,
                        UnitPrice = top.UnitPrice,
                        Unit = r.Unit ?? "unit",
                        Description = top.Description ?? "",
                    };
                }).ToList();

                // Single batch call instead of N serial calls
                var batchResult = await aiEstimator.ResolveReviewBatchAsync(batchItems);
                int confirmed = batchResult.Resolved;

                // Update cards visually and apply prices
                foreach (var review in reviewsWithSugg)
                {
                    if (cardStates.ContainsKey(review.Id))
                    {
                        var c = cardStates[review.Id];
                        c.Background = new SolidColorBrush(Color.FromArgb(15, accentGreen.R, accentGreen.G, accentGreen.B));
                        c.BorderBrush = new SolidColorBrush(Color.FromArgb(40, accentGreen.R, accentGreen.G, accentGreen.B));
                    }
                    var top = review.AiSuggestions.First();
                    ApplyReviewPrice(review.ElementName, top.JkrCode, top.UnitPrice, "learned");
                }

                progressText.Text = $"{confirmed} learned!";
                acceptAllBtn.Content = new TextBlock { Text = $"Done — {confirmed} learned", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
                acceptAllBtn.Background = new SolidColorBrush(Color.FromRgb(22, 101, 52));

                // Write updated prices back to Revit model parameters
                // so the BINA Cost Summary schedule reflects the changes
                var writeHandler = App.CostWriteHandler;
                var writeEvt = App.CostWriteEvent;
                if (writeHandler != null && writeEvt != null)
                {
                    writeHandler.Items = _allItems;
                    writeHandler.ClearMode = false;
                    _suppressModelChanged = true;
                    writeHandler.OnCompleted = () =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _suppressModelChanged = false;
                            ShowBanner($"{confirmed} mappings learned — prices written to model", "", SuccessGreen);
                        });
                    };
                    writeEvt.Raise();
                }
                else
                {
                    ShowBanner($"{confirmed} mappings learned — total updated", "", SuccessGreen);
                }
                UpdateReviewBadge();
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
            int pricedPct = _summary.PriceableItems > 0 ? (int)Math.Round((_summary.PriceablePricedItems / (double)_summary.PriceableItems) * 100) : 0;
            lines.Add($"Coverage: {pricedPct}% ({_summary.PriceablePricedItems}/{_summary.PriceableItems} priceable)");
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
