using RevitWebAppSync.Models;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Modal dialog that collects values for a saved command's {placeholder} variables.
    /// On OK, <see cref="Values"/> holds variable name -> entered value.
    /// </summary>
    public partial class CommandRunWindow : Window
    {
        private readonly CommandTemplate _command;
        private readonly List<(string name, Control input)> _inputs = new List<(string, Control)>();

        public Dictionary<string, string> Values { get; private set; }

        public CommandRunWindow(CommandTemplate command)
        {
            InitializeComponent();
            _command = command;

            TitleText.Text = command.Name;
            if (string.IsNullOrWhiteSpace(command.Description))
                DescriptionText.Visibility = Visibility.Collapsed;
            else
                DescriptionText.Text = command.Description;

            BuildInputs();
        }

        private void BuildInputs()
        {
            foreach (var v in _command.Variables ?? new List<CommandVariable>())
            {
                VariablesPanel.Children.Add(new TextBlock
                {
                    Text = v.DisplayLabel,
                    Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 0)
                });

                Control input;
                if (string.Equals(v.Type, "select", System.StringComparison.OrdinalIgnoreCase)
                    && v.Options != null && v.Options.Count > 0)
                {
                    var combo = new ComboBox();
                    foreach (var opt in v.Options) combo.Items.Add(opt);
                    combo.SelectedItem = !string.IsNullOrEmpty(v.Default) && v.Options.Contains(v.Default)
                        ? v.Default
                        : v.Options[0];
                    input = combo;
                }
                else
                {
                    input = new TextBox { Text = v.Default ?? "" };
                }

                VariablesPanel.Children.Add(input);
                _inputs.Add((v.Name, input));
            }

            if (_inputs.Count == 0)
            {
                VariablesPanel.Children.Add(new TextBlock
                {
                    Text = "This command has no variables — click Run.",
                    Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
                    FontSize = 11
                });
            }
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var values = new Dictionary<string, string>();
            foreach (var (name, control) in _inputs)
            {
                string value = control is ComboBox cb
                    ? cb.SelectedItem?.ToString() ?? ""
                    : (control as TextBox)?.Text ?? "";
                values[name] = value;
            }
            Values = values;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
