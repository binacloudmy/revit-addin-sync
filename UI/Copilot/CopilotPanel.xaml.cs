using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot.Model;
using RevitWebAppSync.UI.Copilot.Screens;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// The Revit Copilot dockable-pane body. Hosts the chrome (CopilotPanel.xaml) and swaps
    /// the active screen UserControl based on CopilotViewModel.Screen / .Tab.
    /// </summary>
    public partial class CopilotPanel : Page
    {
        private readonly CopilotViewModel _vm = new CopilotViewModel();
        private readonly Highlights.HighlightOverlay _overlay = new Highlights.HighlightOverlay();
        private UIApplication _uiApp;

        // Cached screen views (created on first use).
        private LibraryView _library;
        private ToolFormView _toolForm;
        private ToolReviewView _toolReview;
        private RunningView _running;
        private ResultView _result;
        private ChatView _chat;
        private HistoryView _history;
        private SavedView _saved;

        public CopilotPanel()
        {
            InitializeComponent();
            _vm.Executor = new RevitCopilotExecutor();
            _vm.Router = new RevitChatRouter(() => _uiApp);
            Controls.MentionInput.DefaultProvider = new RevitMentionProvider(() => _uiApp);
            DataContext = _vm;
            _vm.PropertyChanged += OnVmChanged;
            _vm.Highlights.CollectionChanged += OnHighlightsChanged;
            UpdateBody();
        }

        /// <summary>Pushed in by OpenCopilotCommand each time the pane is shown.</summary>
        public void SetRevitContext(UIApplication uiApp)
        {
            _uiApp = uiApp;

            // First name for the chat greeting; fall back to "there".
            var user = uiApp?.Application?.Username;
            _vm.UserFirstName = string.IsNullOrWhiteSpace(user) ? "there" : user.Split(' ', '.', '@')[0];

            var doc = uiApp?.ActiveUIDocument?.Document;
            if (doc != null)
                _vm.ModelName = string.IsNullOrWhiteSpace(doc.Title) ? "Main Model" : Path.GetFileNameWithoutExtension(doc.Title);
        }

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CopilotViewModel.Screen) || e.PropertyName == nameof(CopilotViewModel.Tab))
                UpdateBody();
        }

        private void OnHighlightsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_vm.Highlights.Count > 0)
                _overlay.Show(_uiApp, _vm.Highlights, () => _vm.ClearHighlightsCommand.Execute(null));
            else
                _overlay.Hide();
        }

        private void UpdateBody()
        {
            switch (_vm.Screen)
            {
                case CpScreen.ToolForm: BodyHost.Content = View(ref _toolForm); return;
                case CpScreen.ToolReview: BodyHost.Content = View(ref _toolReview); return;
                case CpScreen.Running: BodyHost.Content = View(ref _running); return;
                case CpScreen.Result: BodyHost.Content = View(ref _result); return;
            }

            switch (_vm.Tab)
            {
                case CpTab.Library: BodyHost.Content = View(ref _library); break;
                case CpTab.History: BodyHost.Content = View(ref _history); break;
                case CpTab.Saved: BodyHost.Content = View(ref _saved); break;
                default: BodyHost.Content = View(ref _chat); break;
            }
        }

        private T View<T>(ref T cache) where T : UserControl, new()
        {
            if (cache == null)
            {
                cache = new T();
                cache.DataContext = _vm;
            }
            return cache;
        }
    }
}
