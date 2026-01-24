using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync
{
    public enum StepStatus
    {
        Pending,
        InProgress,
        Success,
        Failed
    }

    public partial class UploadProgressWindow : Window
    {
        private static readonly SolidColorBrush PendingBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"));
        private static readonly SolidColorBrush InProgressBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2"));
        private static readonly SolidColorBrush SuccessBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
        private static readonly SolidColorBrush FailedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));

        public UploadProgressWindow()
        {
            InitializeComponent();
        }

        public void SetFileName(string fileName)
        {
            Dispatcher.Invoke(() =>
            {
                FileNameText.Text = fileName;
            });
        }

        public void UpdateStep(int stepIndex, StepStatus status)
        {
            Dispatcher.Invoke(() =>
            {
                Border badge;
                TextBlock text;

                switch (stepIndex)
                {
                    case 0:
                        badge = Step1Badge;
                        text = Step1Text;
                        break;
                    case 1:
                        badge = Step2Badge;
                        text = Step2Text;
                        break;
                    case 2:
                        badge = Step3Badge;
                        text = Step3Text;
                        break;
                    default:
                        return;
                }

                switch (status)
                {
                    case StepStatus.Pending:
                        badge.Background = PendingBrush;
                        text.Text = "...";
                        break;
                    case StepStatus.InProgress:
                        badge.Background = InProgressBrush;
                        text.Text = "...";
                        break;
                    case StepStatus.Success:
                        badge.Background = SuccessBrush;
                        text.Text = "OK";
                        break;
                    case StepStatus.Failed:
                        badge.Background = FailedBrush;
                        text.Text = "FAIL";
                        break;
                }

                // Update progress bar based on completed steps
                UpdateProgressBar();
            });
        }

        private void UpdateProgressBar()
        {
            int completedSteps = 0;

            if (Step1Text.Text == "OK" || Step1Text.Text == "FAIL") completedSteps++;
            if (Step2Text.Text == "OK" || Step2Text.Text == "FAIL") completedSteps++;
            if (Step3Text.Text == "OK" || Step3Text.Text == "FAIL") completedSteps++;

            UploadProgressBar.Value = (completedSteps / 3.0) * 100;
        }

        public void SetCompleted(bool hasErrors)
        {
            Dispatcher.Invoke(() =>
            {
                UploadProgressBar.Value = 100;
                CloseButton.IsEnabled = true;

                if (hasErrors)
                {
                    HeaderText.Text = "Upload Complete (with errors)";
                    HeaderText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F57C00"));
                }
                else
                {
                    HeaderText.Text = "Upload Complete";
                    HeaderText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32"));
                }
            });
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
