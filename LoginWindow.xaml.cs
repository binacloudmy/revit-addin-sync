using System;
using System.Threading.Tasks;
using System.Windows;

namespace BinaConnector
{
    public partial class LoginWindow : Window
    {
        public string Email { get; private set; }
        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public DateTime TokenExpiry { get; private set; }
        public int UserId { get; private set; }

        public LoginWindow()
        {
            InitializeComponent();
            EmailTextBox.Focus();
        }

        public LoginWindow(string prefillEmail) : this()
        {
            if (!string.IsNullOrEmpty(prefillEmail))
            {
                EmailTextBox.Text = prefillEmail;
                PasswordBox.Focus();
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string email = EmailTextBox.Text?.Trim();
            string password = PasswordBox.Password;

            // Validate input
            if (string.IsNullOrEmpty(email))
            {
                ShowError("Please enter your email address.");
                EmailTextBox.Focus();
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                ShowError("Please enter your password.");
                PasswordBox.Focus();
                return;
            }

            // Show loading state
            SetLoading(true);
            HideError();

            try
            {
                LoginResponse loginResponse;
                try
                {
                    loginResponse = await Task.Run(() => BinaApiService.LoginWithCredentialsAsync(email, password));
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    ShowError(NetworkErrors.Friendly(ex));
                    SetLoading(false);
                    return;
                }
                catch (TaskCanceledException ex)
                {
                    ShowError(NetworkErrors.Friendly(ex));
                    SetLoading(false);
                    return;
                }

                if (loginResponse == null || string.IsNullOrEmpty(loginResponse.AccessToken))
                {
                    ShowError("Sign in failed. Please check your email and password.");
                    SetLoading(false);
                    return;
                }

                // Store results. Password is intentionally NOT retained.
                Email = email;
                AccessToken = loginResponse.AccessToken;
                RefreshToken = loginResponse.RefreshToken;
                UserId = loginResponse.UserId;

                TokenExpiry = loginResponse.AccessTokenExpiry > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(loginResponse.AccessTokenExpiry).DateTime
                    : DateTime.Now.AddHours(24);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError($"Sign in error: {ex.Message}");
                SetLoading(false);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            Dispatcher.Invoke(() =>
            {
                ErrorMessage.Text = message;
                ErrorMessage.Visibility = Visibility.Visible;
            });
        }

        private void HideError()
        {
            Dispatcher.Invoke(() =>
            {
                ErrorMessage.Visibility = Visibility.Collapsed;
            });
        }

        private void SetLoading(bool isLoading)
        {
            Dispatcher.Invoke(() =>
            {
                LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
                LoginButton.IsEnabled = !isLoading;
                CancelButton.IsEnabled = !isLoading;
                EmailTextBox.IsEnabled = !isLoading;
                PasswordBox.IsEnabled = !isLoading;
            });
        }
    }
}
