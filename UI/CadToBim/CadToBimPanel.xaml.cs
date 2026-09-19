using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.Copilot;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>
    /// The CAD to BIM dockable-pane body: CadOverlayViewport on the left, the six-section sidebar
    /// on the right. All decisions live in CadToBimViewModel; this file wires mouse events,
    /// dialogs (file picker, overwrite, big-brush confirm, settings) and the theme dictionaries.
    /// </summary>
    public partial class CadToBimPanel : UserControl
    {
        private readonly CadToBimViewModel _vm;
        private ResourceDictionary _copilotTheme;
        private ResourceDictionary _cadTheme;
        private bool _themeDark;

        /// <summary>Reachable from the ribbon command via App.CadToBimPaneHost.Panel.ViewModel.</summary>
        public CadToBimViewModel ViewModel => _vm;

        public CadToBimPanel() : this(App.CadToBimBuildHandler, App.CadToBimBuildEvent) { }

        public CadToBimPanel(CadToBimBuildHandler handler, ExternalEvent buildEvent)
        {
            // Cp.* and CadToBim.* live in dictionaries merged at app scope; without this the
            // first {StaticResource} in the XAML below throws inside InitializeComponent.
            CopilotTheme.EnsureLoaded();
            CadToBimTheme.EnsureLoaded();

            _vm = new CadToBimViewModel(handler, new ExternalEventRaiser(buildEvent));
            InitializeComponent();
            DataContext = _vm;

            // Same trick as CopilotPanel: theme brushes mounted on THIS element's resources and
            // swapped on ThemeChanged, because app-scope changes do not re-invalidate
            // DynamicResource inside Revit's dockable-pane host.
            _copilotTheme = CopilotTheme.NewThemeDictionary();
            _cadTheme = CadToBimTheme.NewThemeDictionary();
            _themeDark = CopilotTheme.IsDark;
            Resources.MergedDictionaries.Add(_copilotTheme);
            Resources.MergedDictionaries.Add(_cadTheme);

            Loaded += (_, __) =>
            {
                CopilotTheme.ThemeChanged -= SwapLocalTheme;
                CopilotTheme.ThemeChanged += SwapLocalTheme;
                SwapLocalTheme();
            };
            Unloaded += (_, __) =>
            {
                CopilotTheme.ThemeChanged -= SwapLocalTheme;
                _vm.Cancel();          // closing/hiding the pane cancels an in-flight detect
            };

            Viewport.WallClicked += wall => _vm.ToggleErase(wall);
            Viewport.BoxDragged += box => { var _ = _vm.BrushAsync(box); };
            Viewport.CursorMoved += (x, y) =>
                Coords.Text = string.Format(CultureInfo.InvariantCulture, "x {0:0} · y {1:0} mm", x, y);

            DependencyPropertyDescriptor
                .FromProperty(CadOverlayViewport.ModeProperty, typeof(CadOverlayViewport))
                .AddValueChanged(Viewport, (_, __) => UpdateModeHint());

            _vm.PropertyChanged += OnVmChanged;
            _vm.OverwritePrompt = path => Ask(
                "Overwrite " + System.IO.Path.GetFileName(path) + "?",
                "A file with that name already sits beside the drawing. Replace it with the new model?");

            UpdateModeHint();
        }

        /// <summary>Levels of the open project for the "Add to this project" picker. Reads the
        /// Document, so call it from a valid API context (OpenCadToBimCommand does, before OpenAsync).</summary>
        public void RefreshLevels(Document doc)
        {
            if (doc == null)
            {
                _vm.SetLevels(new List<LevelChoice>());
                return;
            }
            try
            {
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(level => level.Elevation)
                    .Select(level => new LevelChoice { Name = level.Name, Id = ElementIdCompat.ToLong(level.Id) })
                    .ToList();
                _vm.SetLevels(levels);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] CadToBim RefreshLevels: " + ex.Message);
                _vm.SetLevels(new List<LevelChoice>());
            }
        }

        // ───────── view-model → view ─────────

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(CadToBimViewModel.Overlay):
                    OverlaySnapshot o = _vm.Overlay;
                    Viewport.SetOverlay(o.Active, o.Erased, o.Openings, o.Spaces, o.Boxes);
                    break;

                case nameof(CadToBimViewModel.PendingBrushConfirm):
                    if (_vm.PendingBrushConfirm == null) break;
                    // Deferred so BrushAsync's finally releases Busy before the modal opens.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BrushResult pending = _vm.PendingBrushConfirm;
                        if (pending == null) return;
                        bool yes = Ask(
                            pending.Walls.Count + " walls from that box",
                            "That is a lot for one selection, and usually means the box caught something " +
                            "that is not wall (a stair, fittings, a title block). Add them anyway?");
                        _vm.ResolveBrushConfirm(yes);
                    }));
                    break;
            }
        }

        private void UpdateModeHint()
        {
            string hint;
            switch (Viewport.Mode)
            {
                case ViewportMode.Erase: hint = "Click a green wall to leave it out. Click again to restore."; break;
                case ViewportMode.Brush: hint = "Drag a box over linework the classifier skipped. Walls inside are forced in."; break;
                default: hint = null; break;
            }
            ModeHintText.Text = hint ?? "";
            ModeHint.Visibility = hint == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        }

        private void SwapLocalTheme()
        {
            if (_cadTheme != null && _themeDark == CopilotTheme.IsDark) return;
            var dicts = Resources.MergedDictionaries;
            Replace(dicts, ref _copilotTheme, CopilotTheme.NewThemeDictionary());
            Replace(dicts, ref _cadTheme, CadToBimTheme.NewThemeDictionary());
            _themeDark = CopilotTheme.IsDark;
            Viewport.Redraw();     // overlay pens read CadToBim.* at render time
        }

        private static void Replace(IList<ResourceDictionary> dicts, ref ResourceDictionary current, ResourceDictionary next)
        {
            int i = current != null ? dicts.IndexOf(current) : -1;
            if (i >= 0) { dicts.RemoveAt(i); dicts.Insert(i, next); }
            else dicts.Add(next);
            current = next;
        }

        // ───────── clicks ─────────

        private void OnChangeClick(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the drawing to convert",
                Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;
            var _ = _vm.OpenAsync(dialog.FileName);
        }

        private void OnConfirmClick(object sender, RoutedEventArgs e) => _vm.Confirm();

        private void OnRoleClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CadLayerViewModel layer) _vm.CycleRole(layer);
        }

        private void OnGearClick(object sender, RoutedEventArgs e)
        {
            CadToBimSettings settings = _vm.Settings;
            var window = new CadToBimSettingsWindow(settings);
            RevitWindowOwner.SetOwner(window, App.UiApp);
            if (window.ShowDialog() == true) _vm.ApplySettings(settings);
        }

        // Revit's TaskDialog when hosted in Revit; MessageBox when it is not available (UiHarness).
        private static bool Ask(string instruction, string content)
        {
            try
            {
                var dialog = new TaskDialog("BINA CAD to BIM")
                {
                    MainInstruction = instruction,
                    MainContent = content,
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                return dialog.Show() == TaskDialogResult.Yes;
            }
            catch
            {
                return MessageBox.Show(content, instruction, MessageBoxButton.YesNo, MessageBoxImage.Question,
                                       MessageBoxResult.No) == MessageBoxResult.Yes;
            }
        }
    }
}
