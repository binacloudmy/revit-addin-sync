using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BinaVibe.Auth;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Modal sign-in for "Login to CDE": email + password, then the emailed code
    /// when bina-be asks for one (a 2FA account). Refusals are shown in the
    /// window so the user can correct and retry without starting over.
    ///
    /// The window only reports the outcome: <see cref="Tokens"/> is the session
    /// on success and null when the user backed out. The caller persists it. The
    /// password is handed to <see cref="BinaPasswordSignIn"/> once and cleared.
    /// </summary>
    public partial class CloudSignInWindow : Window
    {
        private readonly BinaPasswordSignIn _signIn;
        private readonly Action<PasswordSignInResult> _onFailure;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        // Set while the code step is showing: the account being verified and
        // the challenge its code must be sent back with.
        private string _email;
        private string _otpToken;
        private bool _busy;
        private bool _closed;

        /// <summary>The session; null until sign-in succeeds, and if the user cancels.</summary>
        public BinaTokenSet Tokens { get; private set; }

        /// <summary>The email that signed in; set together with <see cref="Tokens"/>.</summary>
        public string Email { get; private set; }

        /// <param name="onFailure">Told about each refusal or network failure, for telemetry.</param>
        public CloudSignInWindow(BinaPasswordSignIn signIn, string prefillEmail = null,
            Action<PasswordSignInResult> onFailure = null)
        {
            InitializeComponent();
            _signIn = signIn;
            _onFailure = onFailure;
            HostText.Text = $"Server: {signIn.Host}";
            EmailBox.Text = prefillEmail ?? "";
            ShowCredentialsStep();
            Loaded += (s, e) =>
            {
                if (EmailBox.Text.Length == 0) EmailBox.Focus();
                else PasswordInput.Focus();
            };
        }

        private void ShowCredentialsStep()
        {
            _email = null;
            _otpToken = null;
            HeadingText.Text = "Sign in to BINA";
            StatusText.Text = "Use the email and password of your BINA Cloud account.";
            CredentialsPanel.Visibility = Visibility.Visible;
            CodePanel.Visibility = Visibility.Collapsed;
            SubmitButton.Content = "Sign in";
        }

        private void ShowCodeStep(string email, string otpToken)
        {
            _email = email;
            _otpToken = otpToken;
            HeadingText.Text = "Enter your verification code";
            StatusText.Text = $"We emailed a 6-digit code to {email}. Enter it to finish signing in.";
            CredentialsPanel.Visibility = Visibility.Collapsed;
            CodePanel.Visibility = Visibility.Visible;
            SubmitButton.Content = "Verify";
            CodeBox.Clear();
            CodeBox.Focus();
        }

        private async void Submit_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            if (_otpToken != null)
            {
                string code = CodeBox.Text.Trim();
                if (code.Length == 0)
                {
                    ShowError("Enter the code from the email.");
                    CodeBox.Focus();
                    return;
                }
                string email = _email, otpToken = _otpToken;
                Handle(await RunAsync(ct => _signIn.VerifyOtpAsync(email, code, otpToken, ct)), email);
            }
            else
            {
                string email = EmailBox.Text.Trim();
                string password = PasswordInput.Password;
                if (email.Length == 0)
                {
                    ShowError("Enter your email address.");
                    EmailBox.Focus();
                    return;
                }
                if (password.Length == 0)
                {
                    ShowError("Enter your password.");
                    PasswordInput.Focus();
                    return;
                }
                Handle(await RunAsync(ct => _signIn.SignInAsync(email, password, ct)), email);
            }
        }

        private async void Resend_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _otpToken == null) return;
            string email = _email;
            Handle(await RunAsync(ct => _signIn.ResendOtpAsync(email, ct)), email);
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            HideError();
            ShowCredentialsStep();
            PasswordInput.Focus();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Runs one request off the UI thread. Null means the window closed under it.</summary>
        private async Task<PasswordSignInResult> RunAsync(Func<CancellationToken, Task<PasswordSignInResult>> request)
        {
            SetBusy(true);
            HideError();
            try
            {
                return await Task.Run(() => request(_cts.Token));
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void Handle(PasswordSignInResult result, string email)
        {
            if (result == null || _closed) return;

            switch (result.Status)
            {
                case PasswordSignInStatus.SignedIn:
                    Tokens = result.Tokens;
                    Email = email;
                    Close();
                    break;

                case PasswordSignInStatus.OtpRequired:
                    // The password has done its job; "Use a different account"
                    // asks for it again.
                    PasswordInput.Clear();
                    ShowCodeStep(email, result.OtpToken);
                    break;

                case PasswordSignInStatus.CodeSent:
                    StatusText.Text = $"We emailed a new code to {email}. Enter it to finish signing in.";
                    CodeBox.Clear();
                    CodeBox.Focus();
                    break;

                default:
                    try { _onFailure?.Invoke(result); } catch { /* telemetry must not break sign-in */ }
                    ShowError(result.Message);
                    break;
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BusyBar.IsIndeterminate = busy;
            BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            SubmitButton.IsEnabled = !busy;
            CredentialsPanel.IsEnabled = !busy;
            CodePanel.IsEnabled = !busy;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

        protected override void OnClosed(EventArgs e)
        {
            // Stops a request still in flight; its result is then ignored.
            _closed = true;
            _cts.Cancel();
            PasswordInput.Clear();
            base.OnClosed(e);
        }
    }
}
