using System.Windows;
using System.Windows.Controls;

namespace RevitWebAppSync
{
    public partial class SaveCommandDialog : Window
    {
        public string CommandName => NameInput.Text?.Trim();
        public string Category => (CategoryCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                  ?? CategoryCombo.Text?.Trim()
                                  ?? "General";
        public string Icon => SelectedIcon.Text;

        public SaveCommandDialog(string prompt, string explanation)
        {
            InitializeComponent();

            // Show prompt preview
            PromptPreview.Text = prompt;

            // Suggest a name based on prompt
            if (!string.IsNullOrEmpty(explanation))
            {
                // Use first sentence of explanation as suggested name
                var firstSentence = explanation.Split('.')[0].Trim();
                if (firstSentence.Length > 50)
                    firstSentence = firstSentence.Substring(0, 47) + "...";
                NameInput.Text = firstSentence;
            }
            else if (!string.IsNullOrEmpty(prompt))
            {
                // Use prompt as suggested name
                var name = prompt.Length > 50 ? prompt.Substring(0, 47) + "..." : prompt;
                NameInput.Text = name;
            }

            // Default category
            CategoryCombo.SelectedIndex = 0;

            // Focus name input
            NameInput.Focus();
            NameInput.SelectAll();
        }

        private void IconSelect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string icon)
            {
                SelectedIcon.Text = icon;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CommandName))
            {
                MessageBox.Show("Please enter a command name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                NameInput.Focus();
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
