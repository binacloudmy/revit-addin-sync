using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>Tier-1 vetted tool form — fields (select/text/seg), live preview, green Run.</summary>
    public partial class ToolFormView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;

        public ToolFormView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => Rebuild();
        }

        private CopilotViewModel _hooked;
        private void Hook()
        {
            if (_hooked != null) _hooked.PropertyChanged -= OnVm;
            _hooked = Vm;
            if (_hooked != null) _hooked.PropertyChanged += OnVm;
            Rebuild();
        }

        private void OnVm(object s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CopilotViewModel.Screen) && Vm?.Screen == CpScreen.ToolForm)
                Rebuild();
        }

        private void Rebuild()
        {
            var vm = Vm;
            var tool = vm?.CurrentTool;
            if (tool == null || FieldsHost == null) return;

            Header.Tool = tool;
            FieldsHost.Children.Clear();

            foreach (var f in tool.Fields)
                FieldsHost.Children.Add(BuildField(f));

            Refresh();
        }

        private void Refresh()
        {
            var vm = Vm; var tool = vm?.CurrentTool;
            if (tool == null) return;
            PreviewText.Text = tool.PlanText?.Invoke(vm.FormValues);
            RunButton.Content = MakeRunContent(tool.RunLabel?.Invoke(vm.FormValues));
        }

        private static object MakeRunContent(string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new System.Windows.Shapes.Path
            {
                Width = 11, Height = 11, Stretch = Stretch.Uniform, Fill = Brushes.White,
                Data = CopilotIcons.Get("play"), Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            sp.Children.Add(new TextBlock { Text = label ?? "Run", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        // ─── Field builders ──────────────────────────────────────────────────
        private FrameworkElement BuildField(FieldDef f)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(new TextBlock
            {
                Text = f.Label, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#374151"), Margin = new Thickness(0, 0, 0, 5),
            });

            switch (f.Kind)
            {
                case CpFieldKind.Select: panel.Children.Add(BuildSelect(f)); break;
                case CpFieldKind.Text: panel.Children.Add(BuildText(f)); break;
                case CpFieldKind.Seg: panel.Children.Add(BuildSeg(f)); break;
            }

            if (!string.IsNullOrEmpty(f.Hint))
                panel.Children.Add(new TextBlock
                {
                    Text = f.Hint, FontSize = 11, Foreground = CopilotColors.From("#6b7280"), Margin = new Thickness(0, 4, 0, 0),
                });
            return panel;
        }

        private ComboBox BuildSelect(FieldDef f)
        {
            var options = OptionsFor(f);
            string current = (Vm?.FormValues != null && Vm.FormValues.TryGetValue(f.Id, out var v) ? v : f.Default) as string;
            // If the stored value isn't in the (possibly live) option list, snap to the first.
            if ((current == null || (options.Length > 0 && !options.Contains(current))) && options.Length > 0)
            {
                current = options[0];
                Vm?.SetForm(f.Id, current);
            }
            var cb = new ComboBox
            {
                ItemsSource = options,
                SelectedItem = current,
                FontSize = 13, Padding = new Thickness(10, 6, 10, 6),
                FontFamily = (FontFamily)TryFindResource("Cp.Font"),
            };
            cb.SelectionChanged += (_, __) => { Vm?.SetForm(f.Id, cb.SelectedItem as string); Refresh(); };
            return cb;
        }

        // Live options for fields that should reflect the real model (open-view's view list,
        // filtered by the selected view type); static catalog options otherwise.
        private string[] OptionsFor(FieldDef f)
        {
            var tool = Vm?.CurrentTool;
            if (tool != null && tool.Id == "open-view" && f.Id == "view" && CopilotModelData.Current != null)
            {
                string type = (Vm.FormValues != null && Vm.FormValues.TryGetValue("type", out var tv)) ? tv as string : null;
                var live = CopilotModelData.Current.Views(type);
                if (live != null && live.Count > 0) return live.ToArray();
            }
            return f.Options ?? new string[0];
        }

        private TextBox BuildText(FieldDef f)
        {
            var tb = new TextBox
            {
                Text = (Vm?.FormValues != null && Vm.FormValues.TryGetValue(f.Id, out var v) ? v : f.Default) as string ?? "",
                FontSize = 13, Padding = new Thickness(12, 8, 12, 8),
                FontFamily = (FontFamily)TryFindResource("Cp.Font"),
                BorderBrush = CopilotColors.From("#e5e7eb"),
            };
            tb.TextChanged += (_, __) => { Vm?.SetForm(f.Id, tb.Text); Refresh(); };
            return tb;
        }

        private FrameworkElement BuildSeg(FieldDef f)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(8), BorderBrush = CopilotColors.From("#e5e7eb"),
                BorderThickness = new Thickness(1), Background = CopilotColors.From("#f1f3f5"), Padding = new Thickness(3),
            };
            var grid = new UniformGrid { Rows = 1, Columns = f.Options.Length };
            string current = (Vm?.FormValues != null && Vm.FormValues.TryGetValue(f.Id, out var v) ? v : f.Default) as string;

            foreach (var opt in f.Options)
            {
                var btn = new Button
                {
                    Content = opt, FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
                    BorderThickness = new Thickness(0), Margin = new Thickness(1, 0, 1, 0),
                    FontFamily = (FontFamily)TryFindResource("Cp.Font"),
                };
                StyleSeg(btn, opt == current);
                btn.Click += (_, __) =>
                {
                    Vm?.SetForm(f.Id, opt);
                    foreach (var child in grid.Children)
                        if (child is Button b) StyleSeg(b, ReferenceEquals(b, btn));
                    // Changing open-view's type re-filters the (live) view dropdown.
                    if (Vm?.CurrentTool?.Id == "open-view" && f.Id == "type") Rebuild();
                    else Refresh();
                };
                grid.Children.Add(btn);
            }
            border.Child = grid;
            return border;
        }

        private static void StyleSeg(Button b, bool active)
        {
            b.Background = active ? Brushes.White : Brushes.Transparent;
            b.Foreground = active ? CopilotColors.From("#0b0d12") : CopilotColors.From("#6b7280");
            b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            b.Padding = new Thickness(6);
            b.Template = SegTemplate();
        }

        private static ControlTemplate _segTemplate;
        private static ControlTemplate SegTemplate()
        {
            if (_segTemplate != null) return _segTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            _segTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
            return _segTemplate;
        }
    }
}
