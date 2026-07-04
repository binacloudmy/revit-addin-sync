using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Autodesk.Revit.UI;
// Shapes.Path collides with System.IO.Path (used once, fully-qualified below).
using Path = System.Windows.Shapes.Path;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;
using RevitWebAppSync.UI.Copilot.Screens;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// The Revit Copilot dockable-pane body. Hosts the chrome (CopilotPanel.xaml) and swaps
    /// the active screen UserControl based on CopilotViewModel.Screen / .Tab.
    /// </summary>
    public partial class CopilotPanel : Page
    {
        private readonly CopilotViewModel _vm = new CopilotViewModel();
        private readonly Highlights.HighlightOverlay _overlay = new Highlights.HighlightOverlay();
        private IFeedbackService _feedback;

        /// <summary>The active view-model, reachable in-process from non-UI code
        /// (e.g. the MCP tunnel) via App.CopilotPaneHost?.Panel?.ViewModel.</summary>
        public CopilotViewModel ViewModel => _vm;
        private UIApplication _uiApp;

        // Cached screen views (created on first use).
        private LibraryView _library;
        private ToolFormView _toolForm;
        private ToolReviewView _toolReview;
        private RunningView _running;
        private ResultView _result;
        private ChatView _chat;
        private HistoryView _history;
        private SavedView _saved;

        public CopilotPanel()
        {
            // The Cp.* DynamicResources live in CopilotTokens/CopilotStyles,
            // merged into Application resources. The Revit pane host does this
            // in CopilotPaneHost, but other hosts (UiHarness) construct the
            // panel directly — without this call every Cp.* lookup silently
            // fails and default WPF chrome leaks through.
            CopilotTheme.EnsureLoaded();
            InitializeComponent();
            // Arm the executor with a backend client so the bounded self-heal
            // loop actually engages. Without Ai set, RevitCopilotExecutor.Run
            // takes the RunOnce path (Ai == null) and a compile/runtime error
            // surfaces on the FIRST try with no retry — exactly the "Sorry —
            // that didn't run. Compilation failed…" the codegen-chat path hit.
            var cfg = BinaConfig.Load();
            _vm.Executor = new RevitCopilotExecutor
            {
                Ai = new AIService(),
                AccessTokenProvider = () => BinaConfig.Load().AccessToken,
                UserId = cfg?.UserId,
            };
            // Fall back to the Idling-captured global when _uiApp is null (the
            // pane was auto-restored docked, so OpenCopilotCommand never pushed
            // the UIApplication). Fixes the agent seeing an empty model context.
            _vm.Router = new RevitChatRouter(() => _uiApp ?? App.UiApp);
            Controls.MentionInput.DefaultProvider = new RevitMentionProvider(() => _uiApp ?? App.UiApp);
            DataContext = _vm;
            _vm.PropertyChanged += OnVmChanged;
            _vm.Highlights.CollectionChanged += OnHighlightsChanged;
            _vm.RateRequested += () => ShowSheet(BuildRateSheet());
            // Local sink for ratings / bug reports (JSONL under %APPDATA%). Model
            // name + user id are captured lazily so they reflect the live context.
            _feedback = new LocalFeedbackService(() => _vm.ModelName, () => cfg?.UserId.ToString());
            UpdateThemeIcon();
            UpdateBody();
        }

        /// <summary>Pushed in by OpenCopilotCommand each time the pane is shown.</summary>
        public void SetRevitContext(UIApplication uiApp)
        {
            _uiApp = uiApp;

            // Feedback context rows show "Copilot x.y.z · Revit NNNN".
            var rv = uiApp?.Application?.VersionNumber;
            if (!string.IsNullOrWhiteSpace(rv))
                Model.CopilotContext.RevitVersion = "Revit " + rv;

            // First name for the chat greeting; fall back to "there".
            var user = uiApp?.Application?.Username;
            _vm.UserFirstName = string.IsNullOrWhiteSpace(user) ? "there" : user.Split(' ', '.', '@')[0];

            var doc = uiApp?.ActiveUIDocument?.Document;
            if (doc != null)
                _vm.ModelName = string.IsNullOrWhiteSpace(doc.Title) ? "Main Model" : System.IO.Path.GetFileNameWithoutExtension(doc.Title);

            // On open: warm the cloud mirror (fast READS), then wait for the
            // Revit model to be warm (first regen paid by the add-in warm-up)
            // before releasing the send gate — so the user's first BUILD runs
            // warm (~60ms) instead of paying the ~70s cold regen. Both gate via
            // IsIndexing and run on the UI thread (no ConfigureAwait) so the
            // warm-up ExternalEvent can interleave. Fire-and-forget, best-effort.
            _ = SeedThenWarmAsync();
        }

        private async System.Threading.Tasks.Task SeedThenWarmAsync()
        {
            await _vm.EnsureMirrorSeededAsync();
            await _vm.EnsureModelWarmAsync();
        }

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CopilotViewModel.Screen) || e.PropertyName == nameof(CopilotViewModel.Tab))
                UpdateBody();
        }

        private void OnHighlightsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_vm.Highlights.Count > 0)
                _overlay.Show(_uiApp, _vm.Highlights, () => _vm.ClearHighlightsCommand.Execute(null));
            else
                _overlay.Hide();
        }

        private void UpdateBody()
        {
            switch (_vm.Screen)
            {
                case CpScreen.ToolForm: BodyHost.Content = View(ref _toolForm); return;
                case CpScreen.ToolReview: BodyHost.Content = View(ref _toolReview); return;
                case CpScreen.Running: BodyHost.Content = View(ref _running); return;
                case CpScreen.Result: BodyHost.Content = View(ref _result); return;
            }

            switch (_vm.Tab)
            {
                case CpTab.Library: BodyHost.Content = View(ref _library); break;
                case CpTab.History: BodyHost.Content = View(ref _history); break;
                case CpTab.Saved: BodyHost.Content = View(ref _saved); break;
                default: BodyHost.Content = View(ref _chat); break;
            }
        }

        private T View<T>(ref T cache) where T : UserControl, new()
        {
            if (cache == null)
            {
                cache = new T();
                cache.DataContext = _vm;
                // The chat composer's usage popover escalates to the upgrade sheet.
                if (cache is Screens.ChatView chat)
                    chat.Prompt.UpgradeRequested += ShowUpgradeSheet;
            }
            return cache;
        }

        // ══════════ Header actions: theme · new chat · menu ══════════

        // Stroked outline icons (24-box, uniform-scaled). Moon = "switch to dark",
        // sun = "switch to light" — we show the mode you'll flip TO.
        private const string MoonData = "M21,12.8 A9,9 0 1 1 11.2,3 A7,7 0 0 0 21,12.8 Z";
        private const string SunData =
            "M12,7.5 A4.5,4.5 0 1 1 11.99,7.5 Z M12,1.5 V3.6 M12,20.4 V22.5 " +
            "M1.5,12 H3.6 M20.4,12 H22.5 M4.4,4.4 L5.9,5.9 M18.1,18.1 L19.6,19.6 " +
            "M19.6,4.4 L18.1,5.9 M5.9,18.1 L4.4,19.6";

        private void OnToggleTheme(object sender, RoutedEventArgs e)
        {
            CopilotTheme.Toggle();   // flips + persists; mutates every Cp.* brush in place
            UpdateThemeIcon();
        }

        private void UpdateThemeIcon()
        {
            if (ThemeIcon == null) return;
            ThemeIcon.Data = Geometry.Parse(CopilotTheme.IsDark ? SunData : MoonData);
        }

        private void OnNewChat(object sender, RoutedEventArgs e)
        {
            _vm.ClearChatCommand.Execute(null);   // clears thread + resets router session
            _vm.GoTab(CpTab.Chat);
        }

        private void OnOpenMenu(object sender, RoutedEventArgs e) => MenuPopup.IsOpen = true;

        private void OnRate(object sender, RoutedEventArgs e)
        {
            MenuPopup.IsOpen = false;
            ShowSheet(BuildRateSheet());
        }

        private void OnReportBug(object sender, RoutedEventArgs e)
        {
            MenuPopup.IsOpen = false;
            ShowSheet(BuildReportSheet());
        }

        // ══════════ Bottom-sheet plumbing ══════════
        // The scrim fades in and the card slides up via BeginAnimation on a
        // TranslateTransform — NOT a XAML Storyboard, which crashes inside a
        // Revit dockable pane (same rule the indexing overlay / msgRise follow).

        private void ShowSheet(FrameworkElement sheet)
        {
            SheetHost.Content = sheet;
            SheetLayer.Visibility = Visibility.Visible;

            SheetScrim.Opacity = 0;
            SheetScrim.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180))));

            var tt = new TranslateTransform(0, 360);
            sheet.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(360, 0, new Duration(TimeSpan.FromMilliseconds(260)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private void HideSheet()
        {
            var sheet = SheetHost.Content as FrameworkElement;

            var fade = new DoubleAnimation(SheetScrim.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(170)));
            fade.Completed += (_, __) =>
            {
                SheetLayer.Visibility = Visibility.Collapsed;
                SheetHost.Content = null;
            };
            SheetScrim.BeginAnimation(OpacityProperty, fade);

            if (sheet != null)
            {
                var tt = sheet.RenderTransform as TranslateTransform ?? new TranslateTransform();
                sheet.RenderTransform = tt;
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(tt.Y, 360, new Duration(TimeSpan.FromMilliseconds(200)))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            }
        }

        private void OnScrimClick(object sender, MouseButtonEventArgs e) => HideSheet();

        // Swap the sheet body for a green-check confirmation, then auto-dismiss.
        private void ShowThanksThenClose(string message)
        {
            var body = new StackPanel { Margin = new Thickness(0, 8, 0, 6) };
            var check = new Path
            {
                Width = 30, Height = 30, Stretch = Stretch.Uniform, StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Data = Geometry.Parse("M4,12.5 L9.5,18 20,5")
            };
            check.SetResourceReference(Shape.StrokeProperty, "Cp.Green");
            body.Children.Add(check);
            var t = new TextBlock
            {
                Text = message, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0)
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            body.Children.Add(t);

            SheetHost.Content = SheetChrome(null, null, body);

            var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(1300) };
            timer.Tick += (_, __) => { timer.Stop(); HideSheet(); };
            timer.Start();
        }

        /// <summary>Open the "Choose your plan" sheet (usage popover, blocked state, harness).</summary>
        public void ShowUpgradeSheet() =>
            ShowSheet(SheetChrome("Choose your plan", "Swipe to compare", Controls.UpgradeSheet.Build()));

        // ══════════ Sheet content builders ══════════

        private Border SheetChrome(string title, string subtitle, UIElement body)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(18, 18, 0, 0),
                BorderThickness = new Thickness(1, 1, 1, 0),
                Padding = new Thickness(20, 14, 20, 22)
            };
            card.SetResourceReference(Border.BackgroundProperty, "Cp.Menu");
            card.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");

            var root = new StackPanel();

            var grab = new Border
            {
                Width = 36, Height = 4, CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12)
            };
            grab.SetResourceReference(Border.BackgroundProperty, "Cp.Hair2");
            root.Children.Add(grab);

            if (!string.IsNullOrEmpty(title))
            {
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var tt = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                tt.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
                head.Children.Add(tt);
                var close = CloseButton();
                Grid.SetColumn(close, 1);
                head.Children.Add(close);
                root.Children.Add(head);
            }

            if (!string.IsNullOrEmpty(subtitle))
            {
                var s = new TextBlock
                {
                    Text = subtitle, FontSize = 12.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap
                };
                s.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                root.Children.Add(s);
            }

            root.Children.Add(new ContentControl { Content = body, Margin = new Thickness(0, 16, 0, 0) });
            card.Child = root;
            return card;
        }

        private Button CloseButton()
        {
            var b = new Button { Width = 28, Height = 28, Cursor = Cursors.Hand };
            b.SetResourceReference(StyleProperty, "Cp.IconButton");
            var x = new Path
            {
                Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M5,5 L15,15 M15,5 L5,15")
            };
            x.SetResourceReference(Shape.StrokeProperty, "Cp.Muted");
            b.Content = x;
            b.Click += (_, __) => HideSheet();
            return b;
        }

        private Button PrimaryButton(string text)
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 18, 0, 0) };
            b.SetResourceReference(StyleProperty, "Cp.RunDark");
            return b;
        }

        private System.Windows.Controls.TextBox MultilineBox(double height)
        {
            var box = new System.Windows.Controls.TextBox
            {
                Height = height, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(1)
            };
            box.SetResourceReference(BackgroundProperty, "Cp.Sunken");
            box.SetResourceReference(ForegroundProperty, "Cp.Ink");
            box.SetResourceReference(Control.BorderBrushProperty, "Cp.Line");
            box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "Cp.Ink");
            return box;
        }

        private FrameworkElement BuildRateSheet()
        {
            int rating = 0;
            var stars = new Path[5];
            Button submit = null;

            void Paint()
            {
                for (int i = 0; i < 5; i++)
                {
                    bool on = i < rating;
                    if (on) stars[i].SetResourceReference(Shape.FillProperty, "Cp.Meter");
                    else stars[i].Fill = Brushes.Transparent;
                    stars[i].SetResourceReference(Shape.StrokeProperty, on ? "Cp.Meter" : "Cp.Faint");
                }
            }

            var starRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            };
            for (int i = 0; i < 5; i++)
            {
                int idx = i;
                var star = new Path
                {
                    Width = 32, Height = 32, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent,
                    Data = Geometry.Parse("M12,2 l3.1,6.3 6.9,1 -5,4.9 1.2,6.8 L12,17.8 5.8,21 7,14.2 2,9.3 l6.9,-1 Z")
                };
                stars[i] = star;
                var wrap = new Border { Background = Brushes.Transparent, Padding = new Thickness(3), Cursor = Cursors.Hand, Child = star };
                wrap.MouseLeftButtonDown += (_, __) => { rating = idx + 1; Paint(); if (submit != null) submit.IsEnabled = true; };
                starRow.Children.Add(wrap);
            }
            Paint();

            var commentLabel = new TextBlock { Text = "Anything you'd like to add? (optional)", FontSize = 11.5, Margin = new Thickness(0, 16, 0, 6) };
            commentLabel.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            var comment = MultilineBox(70);

            submit = PrimaryButton("Submit rating");
            submit.IsEnabled = false;
            submit.Click += (_, __) =>
            {
                _feedback.SubmitRating(rating, comment.Text?.Trim());
                var p = CopilotPrefs.Load();
                p.RatingSubmitted = true;
                p.Save();
                ShowThanksThenClose("Thanks for the feedback!");
            };

            var body = new StackPanel();
            body.Children.Add(starRow);
            body.Children.Add(commentLabel);
            body.Children.Add(comment);
            body.Children.Add(submit);
            return SheetChrome("Rate BINA Copilot", "How's your experience so far?", body);
        }

        private FrameworkElement BuildReportSheet()
        {
            var box = MultilineBox(120);
            var submit = PrimaryButton("Send report");
            submit.IsEnabled = false;
            box.TextChanged += (_, __) => submit.IsEnabled = box.Text.Trim().Length > 0;
            submit.Click += (_, __) =>
            {
                _feedback.ReportBug(box.Text.Trim());
                ShowThanksThenClose("Thanks — we'll take a look.");
            };

            var body = new StackPanel();
            body.Children.Add(box);
            body.Children.Add(submit);
            return SheetChrome("Report a bug", "What went wrong? Steps to reproduce help most.", body);
        }
    }
}
