using System;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Ports
{
    /// <summary>Production IPaneServices: BinaConfig/CopilotPrefs statics, WPF dispatcher, Win32 dialogs.</summary>
    public sealed class PaneServices : IPaneServices
    {
        public BinaConfig LoadConfig() => BinaConfig.Load();
        public CopilotPrefs LoadPrefs() => CopilotPrefs.Load();

        private static System.Windows.Threading.Dispatcher Disp => System.Windows.Application.Current?.Dispatcher;

        public bool IsOnUi { get { var d = Disp; return d == null || d.CheckAccess(); } }

        public void RunOnUi(Action action)
        {
            var d = Disp;
            if (d != null && !d.CheckAccess()) d.Invoke(action); else action();
        }

        public void BeginOnUi(Action action) => Disp?.BeginInvoke(action);

        public string PickSaveFile(string title, string filter, string defaultFileName)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = defaultFileName,
                AddExtension = true,
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }
    }
}
