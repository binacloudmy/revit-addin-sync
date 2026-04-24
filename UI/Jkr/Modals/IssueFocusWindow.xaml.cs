using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RevitWebAppSync.UI.Jkr.Controls;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Modals
{
    public partial class IssueFocusWindow : Window
    {
        private PanelVm _vm;
        private bool _stepsOpen = true;
        private bool _specOpen = false;

        // Back-reference so action clicks route through the panel's DispatchAction
        // (which handles backend auto-fix + audit persistence). Set by the panel
        // after construction — nullable-safe on old callsites.
        internal RevitWebAppSync.UI.JkrComplianceDashboardPanel HostPanel { get; set; }

        public IssueFocusWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            KeyDown += OnKey;
        }

        private void OnLoaded(object s, RoutedEventArgs e)
        {
            try
            {
                var st = new ScaleTransform(0.97, 0.97);
                Card.RenderTransform = st;
                Card.RenderTransformOrigin = new Point(0.5, 0.5);
                var grow = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase() };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
            catch { /* animation is cosmetic */ }
        }

        public void SetContext(PanelVm vm)
        {
            if (_vm != null) _vm.PropertyChanged -= Vm_Changed;
            _vm = vm;
            if (_vm != null) _vm.PropertyChanged += Vm_Changed;
            Render();
        }

        private bool _renderQueued;

        private void Vm_Changed(object s, PropertyChangedEventArgs e)
        {
            if (_renderQueued) return;
            _renderQueued = true;
            Dispatcher.InvokeAsync(() =>
            {
                _renderQueued = false;
                Render();
            }, System.Windows.Threading.DispatcherPriority.DataBind);
        }

        private void Render()
        {
            try { RenderInternal(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] FocusWindow render error: {ex}");
            }
        }

        private void RenderInternal()
        {
            if (_vm?.ActiveIssue == null) return;
            var i = _vm.ActiveIssue;

            IssueIdxText.Text = _vm.ActiveIndexDisplay.ToString();
            IssueOfText.Text = $" of {_vm.Filtered.Count}";
            CategorySuffix.Text = _vm.ActiveCategory != null ? $" · {_vm.ActiveCategory}" : "";

            QueueBar.Width = Math.Max(0, _vm.QueueProgress * (Card.ActualWidth - 0));
            // Fallback if ActualWidth not ready
            if (Card.ActualWidth < 10)
                QueueBar.Width = _vm.QueueProgress * 600;

            PrioBadge.Background = i.PriorityBg;
            PrioLabel.Foreground = i.PriorityColor;
            PrioLabel.Text = i.PriorityLabel.ToUpperInvariant();
            CatLabel.Text = i.Category;
            IdLabel.Text = i.Id;

            if (i.IsResolved)
            {
                StatusBadgeHost.Content = new StatusBadge { DataContext = i };
            }
            else
            {
                StatusBadgeHost.Content = null;
            }

            TitleText.Text = i.Title;
            DescText.Text = i.Description;
            ElementText.Inlines.Clear();
            ElementText.Inlines.Add(new System.Windows.Documents.Run(i.Element?.Name ?? ""));
            if (!string.IsNullOrEmpty(i.Element?.Id) && i.Element.Id != "—")
            {
                ElementText.Inlines.Add(new System.Windows.Documents.Run($"  [{i.Element.Id}]")
                { Foreground = JkrTheme.Brush("Ink4") });
            }
            RequiredText.Text = i.Required;
            ActualText.Text = i.Actual;

            bool actionable = i.IsActionable;
            bool resolved = i.IsResolved;
            OpenActions.Visibility = actionable ? Visibility.Visible : Visibility.Collapsed;
            ResolvedActions.Visibility = resolved ? Visibility.Visible : Visibility.Collapsed;

            // Show before/after diff on resolved auto-fixes
            if (resolved && i.AutoFixable && !string.IsNullOrEmpty(i.Actual))
            {
                ResolvedDiffCard.Visibility = Visibility.Visible;
                ResolvedDiffMinus.Text = i.Actual;
                ResolvedDiffPlus.Text = string.IsNullOrEmpty(i.Example) ? i.Required : i.Example;
            }
            else
            {
                ResolvedDiffCard.Visibility = Visibility.Collapsed;
            }

            // Hide "Locate in 3D" for project-level checks (element_id=0).
            var locateVis = i.Locatable ? Visibility.Visible : Visibility.Collapsed;
            LocateBtn.Visibility = locateVis;
            LocateBtnResolved.Visibility = locateVis;

            if (actionable)
            {
                AutoFixCard.Visibility = i.AutoFixable ? Visibility.Visible : Visibility.Collapsed;
                ManualCard.Visibility = i.AutoFixable ? Visibility.Collapsed : Visibility.Visible;
                if (i.AutoFixable)
                {
                    DiffMinus.Text = i.Actual;
                    DiffPlus.Text = string.IsNullOrEmpty(i.Example) ? i.Required : i.Example;
                    // Show the JKR doc reference for this fix
                    if (!string.IsNullOrEmpty(i.FixReference))
                    {
                        FixRefText.Text = i.FixReference;
                        FixRefText.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        FixRefText.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    int n = i.Steps != null ? i.Steps.Count : 0;
                    ManualTitle.Text = n > 0 ? $"How to fix manually — {n} steps" : "How to fix manually";
                    RebuildSteps(i);
                    StepsBody.Visibility = _stepsOpen ? Visibility.Visible : Visibility.Collapsed;
                    StepsCaret.Glyph = _stepsOpen ? "caretDn" : "caretR";
                }
                AcceptBtn.Visibility = i.ShowAcceptButton ? Visibility.Visible : Visibility.Collapsed;
            }

            var spec = SpecDoc.Get(i.Spec.Doc);
            // Clause is optional — the v2 pipeline surfaces page + doc_number but not section yet.
            var clauseText = string.IsNullOrEmpty(i.Spec.Clause) ? "" : $" · Clause {i.Spec.Clause}";
            SpecShort.Text = $"{spec.Short}{clauseText}";
            SpecFull.Text = $"{spec.Full} ({spec.Year})";
            SpecQuote.Text = string.IsNullOrEmpty(i.Spec.Quote) ? "" : $"\"{i.Spec.Quote}\"";
            var pageText = i.Spec.Page > 0 ? $"Page {i.Spec.Page}" : "";
            SpecMeta.Text = string.IsNullOrEmpty(clauseText) ? pageText : $"Clause {i.Spec.Clause} · {pageText}";
            // Only show the PDF link when we actually have a doc key to open.
            OpenPdfLink.Visibility = string.IsNullOrEmpty(i.Spec.Doc)
                ? Visibility.Collapsed
                : Visibility.Visible;
            SpecBody.Visibility = _specOpen ? Visibility.Visible : Visibility.Collapsed;
            SpecCaret.Glyph = _specOpen ? "caretDn" : "caretR";

            PrevBtn.IsEnabled = _vm.ActiveIndexDisplay > 1;
            NextBtn.IsEnabled = _vm.ActiveIndexDisplay < _vm.Filtered.Count;
        }

        private void RebuildSteps(IssueVm i)
        {
            StepsList.Children.Clear();
            if (i.Steps != null && i.Steps.Count > 0)
            {
                for (int n = 0; n < i.Steps.Count; n++)
                {
                    var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var badge = new Border
                    {
                        Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                        Background = JkrTheme.Brush("OkBg"),
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 10, 0),
                        Child = new TextBlock
                        {
                            Text = (n + 1).ToString(),
                            FontSize = 10, FontWeight = FontWeights.Bold,
                            Foreground = JkrTheme.Brush("Ok"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    };
                    Grid.SetColumn(badge, 0);
                    row.Children.Add(badge);

                    var txt = new TextBlock
                    {
                        Text = i.Steps[n], FontSize = 11.5, Foreground = JkrTheme.Brush("Ink2"),
                        TextWrapping = TextWrapping.Wrap, LineHeight = 18,
                    };
                    Grid.SetColumn(txt, 1);
                    row.Children.Add(txt);

                    StepsList.Children.Add(row);
                }
            }
            else if (!string.IsNullOrEmpty(i.HowToFix))
            {
                StepsList.Children.Add(new TextBlock
                {
                    Text = i.HowToFix, FontSize = 11.5, Foreground = JkrTheme.Brush("Ink2"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        // ─── clicks ───
        private void Scrim_Click(object s, MouseButtonEventArgs e) { if (e.Source == s) Close(); }
        private void Card_Click(object s, MouseButtonEventArgs e) { e.Handled = true; }
        private void Close_Click(object s, RoutedEventArgs e) => Close();
        private void Back_Click(object s, RoutedEventArgs e) => Close();
        private void Prev_Click(object s, RoutedEventArgs e) { if (_vm?.ActiveIndexDisplay > 1) _vm.ActiveIssue = _vm.Filtered[_vm.ActiveIndexDisplay - 2]; }
        private void Next_Click(object s, RoutedEventArgs e) { if (_vm?.ActiveIndexDisplay < _vm.Filtered.Count) _vm.ActiveIssue = _vm.Filtered[_vm.ActiveIndexDisplay]; }
        private void ApplyFix_Click(object s, RoutedEventArgs e) => _Act(IssueStatus.Fixed,    advance: true);
        private void Accept_Click(object s, RoutedEventArgs e)   => _Act(IssueStatus.Accepted, advance: true);
        private void Approve_Click(object s, RoutedEventArgs e)  => _Act(IssueStatus.Approved, advance: true);
        private void Reopen_Click(object s, RoutedEventArgs e)   => _Act(IssueStatus.Open,     advance: false);
        private void Locate_Click(object s, RoutedEventArgs e)
        {
            if (_vm?.ActiveIssue == null) return;
            HostPanel?.LocateInRevit(_vm.ActiveIssue);
        }

        private void OpenPdf_Click(object s, MouseButtonEventArgs e)
        {
            var issue = _vm?.ActiveIssue;
            var doc = issue?.Spec?.Doc;
            if (string.IsNullOrEmpty(doc)) return;
            RevitWebAppSync.Services.JkrSpecPdfBootstrap.Open(doc);
            e.Handled = true;
        }

        private void _Act(IssueStatus newStatus, bool advance)
        {
            var issue = _vm?.ActiveIssue;
            if (issue == null) return;
            if (newStatus == IssueStatus.Approved && !issue.CanApprove) return;
            if (newStatus == IssueStatus.Accepted && !issue.CanAccept) return;

            // Route through the panel so backend auto-fix + audit persistence fire.
            // Falls back to in-memory flip only if the modal was opened without a host
            // (shouldn't happen in prod — guard keeps the window self-sufficient for tests).
            if (HostPanel != null) HostPanel.InvokeAction(issue, newStatus, advance);
            else _vm.ApplyAction(issue, newStatus, advance);
        }
        private void SpecToggle_Click(object s, RoutedEventArgs e) { _specOpen = !_specOpen; Render(); }
        private void StepsToggle_Click(object s, RoutedEventArgs e) { _stepsOpen = !_stepsOpen; Render(); }

        private void OnKey(object s, KeyEventArgs e)
        {
            if (_vm == null) return;
            switch (e.Key)
            {
                case Key.Escape: Close(); e.Handled = true; break;
                case Key.Right:
                case Key.J:
                case Key.Down:
                    Next_Click(s, null); e.Handled = true; break;
                case Key.Left:
                case Key.K:
                case Key.Up:
                    Prev_Click(s, null); e.Handled = true; break;
                case Key.F:
                    if (_vm.ActiveIssue?.AutoFixable == true && _vm.ActiveIssue.IsActionable) ApplyFix_Click(s, null); break;
                case Key.A:
                    if (_vm.ActiveIssue?.IsOpen == true && _vm.ActiveIssue.CanAccept) Accept_Click(s, null); break;
                // Key.X (approve) removed — merged with Accept.
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_vm != null) _vm.PropertyChanged -= Vm_Changed;
            base.OnClosed(e);
        }
    }
}
