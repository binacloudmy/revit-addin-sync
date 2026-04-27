using System.Windows;
using System.Windows.Controls;

namespace BinaConnector
{
    public partial class EulaWindow : Window
    {
        public bool Accepted { get; private set; }

        public EulaWindow()
        {
            InitializeComponent();
            EulaText.Text = EulaService.EulaText;
        }

        private void EulaScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Enable the consent checkbox once the user has scrolled to (or near) the bottom.
            if (EulaScroll.VerticalOffset + EulaScroll.ViewportHeight >= EulaScroll.ExtentHeight - 4)
            {
                AgreeCheckBox.IsEnabled = true;
            }
        }

        private void AgreeCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            AcceptButton.IsEnabled = AgreeCheckBox.IsChecked == true;
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            Accepted = true;
            DialogResult = true;
            Close();
        }

        private void DeclineButton_Click(object sender, RoutedEventArgs e)
        {
            Accepted = false;
            DialogResult = false;
            Close();
        }
    }
}
