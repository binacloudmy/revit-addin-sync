using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.UI.SpacePlanning.Screens
{
    /// <summary>
    /// What a Build actually placed. Reports the counts the mutator returned rather
    /// than what was requested — <c>skipped</c> and <c>created_levels</c> are the two
    /// that surprise people (the padang is never built, and a level the scheme needed
    /// may have been created in their model).
    /// </summary>
    public partial class OutcomeView : UserControl
    {
        private SpacePlanningViewModel Vm => DataContext as SpacePlanningViewModel;
        private SpacePlanningViewModel _hooked;

        public OutcomeView()
        {
            InitializeComponent();
            BackBtn.Click += (_, __) => Vm?.BackToPlanCommand?.Execute(null);
            NewBtn.Click += (_, __) => Vm?.NewPlanCommand?.Execute(null);
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => Hook();
            Unloaded += (_, __)  =>
            {
                if (_hooked != null) _hooked.PropertyChanged -= OnVm;
                _hooked = null;
            };
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.PropertyChanged -= OnVm;
            _hooked = Vm;
            if (_hooked != null) _hooked.PropertyChanged += OnVm;
            Render();
        }

        private void OnVm(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SpacePlanningViewModel.BuildOutcome))
                Render();
        }

        private void Render()
        {
            var outcome = Vm?.BuildOutcome;
            DetailHost.Children.Clear();
            if (outcome == null)
            {
                Headline.Text = "Nothing built yet.";
                GroupLine.Text = "";
                LodCard.Visibility = Visibility.Collapsed;
                return;
            }

            Headline.Text = outcome.Headline ?? (outcome.Ok ? "Done" : "Build failed");
            Card.BorderBrush = Swatch(outcome.Ok ? "Cp.Line" : "Cp.Red");

            if (!outcome.Ok)
            {
                GroupLine.Text = outcome.Error ?? "The scheme was not placed.";
                LodCard.Visibility = Visibility.Collapsed;
                return;
            }

            GroupLine.Text = string.IsNullOrWhiteSpace(outcome.GroupName)
                ? "Placed as one Model Group."
                : $"Model Group: {outcome.GroupName}";

            AddRow("Masses placed", outcome.MassCount.ToString());
            if (outcome.WallCount > 0) AddRow("Walls placed", outcome.WallCount.ToString());
            if (outcome.LevelCount > 0) AddRow("Levels used", outcome.LevelCount.ToString());
            if (outcome.SkippedCount > 0)
                AddRow("Skipped", $"{outcome.SkippedCount} (site-only, e.g. the padang)");
            if (outcome.CreatedLevels != null && outcome.CreatedLevels.Count > 0)
                AddRow("Levels created", string.Join(", ", outcome.CreatedLevels));
            if (!string.IsNullOrWhiteSpace(outcome.Category))
                AddRow("Category", outcome.Category);

            LodCard.Visibility = Visibility.Visible;
            LodNote.Text =
                (string.IsNullOrWhiteSpace(outcome.Lod) ? "LOD 100" : outcome.Lod)
                + " — conceptual masses, not building elements. They carry no floor area, "
                + "so Revit will not schedule or take off GFA from them; the Schedule of "
                + "Accommodation holds the authoritative figures. Delete the Model Group "
                + "to remove the whole proposal in one action.";

            // A level created in the user's model survives deleting the group (Revit
            // refuses to group datums), so it has to be said out loud.
            if (outcome.CreatedLevels != null && outcome.CreatedLevels.Count > 0)
                LodNote.Text += " Levels created for this scheme are NOT part of the group "
                              + "and remain in the model after it is deleted.";
        }

        private void AddRow(string label, string value)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var l = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = Swatch("Cp.Muted"),
                TextWrapping = TextWrapping.Wrap,
            };
            var v = new TextBlock
            {
                Text = value,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Swatch("Cp.Ink"),
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(l, 0);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            DetailHost.Children.Add(grid);
        }

        private static Brush Swatch(string key) =>
            Copilot.CopilotTheme.Brush(key) ?? Brushes.Gray;
    }
}
