using System.Windows;
using System.Windows.Controls;

namespace RevitWebAppSync
{
    public class CredentialsDialog : Window
    {
        private TextBox _emailTextBox;
        private PasswordBox _passwordBox;
        private Button _okButton;
        private Button _cancelButton;

        public string Email { get; private set; }
        public string Password { get; private set; }

        public CredentialsDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Title = "BINA Credentials";
            Width = 400;
            Height = 200;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var emailLabel = new Label { Content = "Email:", Margin = new Thickness(10) };
            Grid.SetRow(emailLabel, 0);
            Grid.SetColumn(emailLabel, 0);
            grid.Children.Add(emailLabel);

            _emailTextBox = new TextBox { Margin = new Thickness(10) };
            Grid.SetRow(_emailTextBox, 0);
            Grid.SetColumn(_emailTextBox, 1);
            grid.Children.Add(_emailTextBox);

            var passwordLabel = new Label { Content = "Password:", Margin = new Thickness(10) };
            Grid.SetRow(passwordLabel, 1);
            Grid.SetColumn(passwordLabel, 0);
            grid.Children.Add(passwordLabel);

            _passwordBox = new PasswordBox { Margin = new Thickness(10) };
            Grid.SetRow(_passwordBox, 1);
            Grid.SetColumn(_passwordBox, 1);
            grid.Children.Add(_passwordBox);

            var buttonPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10)
            };
            Grid.SetRow(buttonPanel, 2);
            Grid.SetColumn(buttonPanel, 1);

            _okButton = new Button 
            { 
                Content = "OK", 
                Width = 75, 
                Height = 25, 
                Margin = new Thickness(5, 0, 5, 0),
                IsDefault = true
            };
            _okButton.Click += OkButton_Click;
            buttonPanel.Children.Add(_okButton);

            _cancelButton = new Button 
            { 
                Content = "Cancel", 
                Width = 75, 
                Height = 25,
                IsCancel = true
            };
            _cancelButton.Click += CancelButton_Click;
            buttonPanel.Children.Add(_cancelButton);

            grid.Children.Add(buttonPanel);
            Content = grid;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Email = _emailTextBox.Text;
            Password = _passwordBox.Password;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public void SetCredentials(string email, string password)
        {
            _emailTextBox.Text = email ?? string.Empty;
            _passwordBox.Password = password ?? string.Empty;
        }
    }
}