using System;
using System.Windows;
using System.Windows.Controls;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>The AI-fixer row control (AI-435); see FixerRow.xaml. Raises what the person chose;
    /// the host (the detail window) forwards it to the pane, which writes and re-checks.</summary>
    public partial class FixerRow : UserControl
    {
        public FixerRow() { InitializeComponent(); }

        public event Action AskRequested;
        public event Action AcceptRequested;
        public event Action WriteRequested;

        private void Ask_Click(object s, RoutedEventArgs e) => AskRequested?.Invoke();
        private void Accept_Click(object s, RoutedEventArgs e) => AcceptRequested?.Invoke();
        private void Write_Click(object s, RoutedEventArgs e) => WriteRequested?.Invoke();
    }
}
