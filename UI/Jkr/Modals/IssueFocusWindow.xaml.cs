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

        public IssueFocusWindow()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                var sb = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase() };
                var st = new ScaleTransform(0.97, 0.97);
                Card.RenderTransform = st;
                Card.RenderTransformOrigin = new Point(0.5, 0.5);
                st.BeginAnimation(ScaleTransform.ScaleXProperty, sb);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, sb);
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                this.BeginAnimation(OpacityProperty, fade);
            };
            KeyDown += OnKey;
        }

        public void SetContext(PanelVm vm)
        {
            if (_vm != null) _vm.PropertyChanged -= Vm_Changed;
            _vm = vm;
            if (_vm != null) _vm.PropertyChanged += Vm_Changed;
            Render();
        }

        private void Vm_Changed(object s, PropertyChangedEventArgs e) => Dispatcher.Invoke(Render);

        private void Render()
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

            bool open = i.IsOpen;
            OpenActions.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            ResolvedActions.Visibility = open ? Visibility.Collapsed : Visibility.Visible;

            if (open)
            {
                AutoFixCard.Visibility = i.AutoFixable ? Visibility.Visible : Visibility.Collapsed;
                ManualCard.Visibility = i.AutoFixable ? Visibility.Collapsed : Visibility.Visible;
                if (i.AutoFixable)
                {
                    DiffMinus.Text = i.Actual;
                    DiffPlus.Text = string.IsNullOrEmpty(i.Example) ? i.Required : i.Example;
                }
                else
                {
                    int n = i.Steps != null ? i.Steps.Count : 0;
                    ManualTitle.Text = n > 0 ? $"How to fix manually — {n} steps" : "How to fix manually";
                    RebuildSteps(i);
                    StepsBody.Visibility = _stepsOpen ? Visibility.Visible : Visibility.Collapsed;
                    StepsCaret.Glyph = _stepsOpen ? "caretDn" : "caretR";
                }
                ApproveBtn.Visibility = i.CanApprove ? Visibility.Visible : Visibility.Collapsed;
            }

            var spec = SpecDoc.Get(i.Spec.Doc);
            SpecShort.Text = $"{spec.Short} · Clause {i.Spec.Clause}";
            SpecFull.Text = $"{spec.Full} ({spec.Year})";
            SpecQuote.Text = $"\"{i.Spec.Quote}\"";
            SpecMeta.Text = $"Clause {i.Spec.Clause} · Page {i.Spec.Page}";
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
        private void ApplyFix_Click(object s, RoutedEventArgs e) { if (_vm?.ActiveIssue != null) _vm.ApplyAction(_vm.ActiveIssue, IssueStatus.Fixed, true); }
        private void Accept_Click(object s, RoutedEventArgs e)   { if (_vm?.ActiveIssue != null) _vm.ApplyAction(_vm.ActiveIssue, IssueStatus.Accepted, true); }
        private void Approve_Click(object s, RoutedEventArgs e)  { if (_vm?.ActiveIssue != null && _vm.ActiveIssue.CanApprove) _vm.ApplyAction(_vm.ActiveIssue, IssueStatus.Approved, true); }
        private void Reopen_Click(object s, RoutedEventArgs e)   { if (_vm?.ActiveIssue != null) _vm.ApplyAction(_vm.ActiveIssue, IssueStatus.Open, false); }
        private void Locate_Click(object s, RoutedEventArgs e)   { /* wire to Revit UIDocument.ShowElements later */ }
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
                    if (_vm.ActiveIssue?.AutoFixable == true && _vm.ActiveIssue.IsOpen) ApplyFix_Click(s, null); break;
                case Key.A:
                    if (_vm.ActiveIssue?.IsOpen == true) Accept_Click(s, null); break;
                case Key.X:
                    if (_vm.ActiveIssue?.IsOpen == true && _vm.ActiveIssue.CanApprove) Approve_Click(s, null); break;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_vm != null) _vm.PropertyChanged -= Vm_Changed;
            base.OnClosed(e);
        }
    }
}
