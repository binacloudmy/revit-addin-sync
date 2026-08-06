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
            Loaded += (_, __) =>
            {
                Hook();
                // Rows are drawn in code with resolved brushes, so a theme flip has
                // to redraw them — a DynamicResource would have handled itself.
                Copilot.CopilotTheme.ThemeChanged -= Render;
                Copilot.CopilotTheme.ThemeChanged += Render;
            };
            Unloaded += (_, __)  =>
            {
                if (_hooked != null) _hooked.PropertyChanged -= OnVm;
                _hooked = null;
                Copilot.CopilotTheme.ThemeChanged -= Render;
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

            if (outcome.RoomCount > 0)
            {
                AddRow("Rooms placed", outcome.RoomCount.ToString());
                if (outcome.SeparationLineCount > 0)
                    AddRow("Separation lines", outcome.SeparationLineCount.ToString());
            }
            if (outcome.MassCount > 0) AddRow("Masses placed", outcome.MassCount.ToString());
            if (outcome.WallCount > 0) AddRow("Walls placed", outcome.WallCount.ToString());
            if (outcome.LevelCount > 0) AddRow("Levels used", outcome.LevelCount.ToString());
            if (outcome.TagCount > 0) AddRow("Rooms tagged", outcome.TagCount.ToString());
            if (outcome.CreatedViews != null && outcome.CreatedViews.Count > 0)
                AddRow("Views created", string.Join(", ", outcome.CreatedViews));
            // Say where to look. Without this the drafter is left hunting a browser
            // full of plans for the one the scheme actually landed in.
            if (!string.IsNullOrWhiteSpace(outcome.ScheduleName))
                AddRow("Schedule created", outcome.ScheduleName);
            if (!string.IsNullOrWhiteSpace(outcome.OpenedView))
                AddRow("Now showing", outcome.OpenedView);
            // We changed a setting on the drafter's own view — say which, and why.
            if (outcome.UncroppedViews != null && outcome.UncroppedViews.Count > 0)
                AddRow("Crop turned off", string.Join(", ", outcome.UncroppedViews)
                                          + " — it would have cut the scheme");
            if (outcome.TagFailureCount > 0)
                AddRow("⚠ Untagged", $"{outcome.TagFailureCount} room(s) — no room tag family loaded?");
            if (outcome.SkippedCount > 0)
                AddRow("Skipped", $"{outcome.SkippedCount} (site-only, e.g. the padang)");
            if (outcome.CreatedLevels != null && outcome.CreatedLevels.Count > 0)
                AddRow("Levels created", string.Join(", ", outcome.CreatedLevels));
            if (!string.IsNullOrWhiteSpace(outcome.Category))
                AddRow("Category", outcome.Category);

            // An unenclosed room looks correct in plan and then schedules as nothing —
            // the one failure here that is invisible unless it is stated.
            if (outcome.UnenclosedCount > 0)
                AddRow("⚠ Not enclosed", $"{outcome.UnenclosedCount} room(s) — no area");
            if (outcome.RoomFailureCount > 0)
                AddRow("⚠ Could not place", $"{outcome.RoomFailureCount} room(s)");
            if (outcome.OverflowsSite)
                AddRow("⚠ Crosses the site", "the block extends past the boundary + setback");

            LodCard.Visibility = Visibility.Visible;
            string lodText = string.IsNullOrWhiteSpace(outcome.Lod) ? "LOD 100" : outcome.Lod;
            LodNote.Text = outcome.RoomCount > 0
                ? lodText + " — Rooms bounded by room-separation lines. They carry real "
                  + "Revit areas and will appear in a room schedule, but they are space "
                  + "boundaries, not walls: nothing here is a building element yet. The "
                  + "Schedule of Accommodation remains the authoritative area figure."
                : lodText + " — conceptual masses, not building elements. They carry no "
                  + "floor area, so Revit will not schedule or take off GFA from them; the "
                  + "Schedule of Accommodation holds the authoritative figures. Delete the "
                  + "Model Group to remove the whole proposal in one action.";

            // Rooms are not group members (Revit refuses to group them), so "delete
            // the group" does NOT clean them up. Say it here rather than let a user
            // discover a model full of orphan rooms.
            if (outcome.RoomCount > 0 && !outcome.RoomsInGroup)
                LodNote.Text += " Deleting the Model Group removes the separation lines "
                              + "(and any masses) but LEAVES the rooms — Revit does not "
                              + "allow rooms in a group. Ctrl+Z undoes the whole Build.";

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
