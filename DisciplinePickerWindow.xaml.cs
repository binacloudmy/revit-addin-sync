using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitWebAppSync
{
    /// <summary>
    /// Replaces the old fixed 4-CommandLink TaskDialog (Architecture/Structure/
    /// HVAC/Electrical) — a Revit TaskDialog only supports 4 CommandLinks, which
    /// can't represent an arbitrary per-project discipline list (the 6 system
    /// disciplines alone already exceed that, before any custom ones). Modeled
    /// on ProjectPickerWindow, but the discipline list is passed in already
    /// fetched rather than loaded async on Window.Loaded — the caller already
    /// has it (it needed it to build the picker in the first place).
    ///
    /// MainFile is intentionally never one of the list items — it's a
    /// federation output, not a user-selectable discipline — so it gets its
    /// own dedicated button instead, matching the old dialog's "OK = MainFile"
    /// default-button behaviour.
    /// </summary>
    public partial class DisciplinePickerWindow : Window
    {
        /// <summary>Code of the selected discipline (or "MainFile"), set only
        /// when ShowDialog() returns true.</summary>
        public string SelectedDisciplineCode { get; private set; }

        public DisciplinePickerWindow(List<BimDiscipline> disciplines, string fileName)
        {
            InitializeComponent();

            SubHeaderText.Text = $"File: {fileName}\n\nChoose the discipline this file belongs to, or upload it as the General Model.";

            var options = (disciplines ?? new List<BimDiscipline>())
                .Where(d => d != null && !d.IsMainFile)
                .OrderBy(d => d.SortOrder)
                .Select(d => new DisciplineOptionVm(d))
                .ToList();

            DisciplinesListBox.ItemsSource = options;
            DisciplinesListBox.SelectionChanged += DisciplinesListBox_SelectionChanged;

            if (options.Count == 0)
            {
                ShowError("This project has no disciplines configured besides the coordinated model. Use 'General Model' below, or add disciplines from the web app first.");
            }
        }

        private void DisciplinesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectButton.IsEnabled = DisciplinesListBox.SelectedItem != null;
        }

        private void DisciplinesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DisciplinesListBox.SelectedItem != null)
            {
                SelectDiscipline();
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            SelectDiscipline();
        }

        private void SelectDiscipline()
        {
            var selected = DisciplinesListBox.SelectedItem as DisciplineOptionVm;
            if (selected == null)
            {
                ShowError("Please select a discipline.");
                return;
            }

            // Code, never Name — the immutable identity persisted server-side.
            SelectedDisciplineCode = selected.Discipline.Code;
            DialogResult = true;
            Close();
        }

        private void MainFileButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedDisciplineCode = "MainFile";
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorMessage.Text = message;
            ErrorMessage.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Display wrapper around BimDiscipline: adds a parsed WPF
    /// Color for the swatch (the API gives a "#RRGGBB" string, not a WPF
    /// Color), falling back to a neutral gray if it doesn't parse.</summary>
    public class DisciplineOptionVm
    {
        public BimDiscipline Discipline { get; }
        public string Name => Discipline.Name;
        public string Code => Discipline.Code;
        public Color SwatchColor { get; }

        public DisciplineOptionVm(BimDiscipline discipline)
        {
            Discipline = discipline;
            SwatchColor = TryParseColor(discipline.Color) ?? Color.FromRgb(0x8c, 0x8c, 0x8c);
        }

        private static Color? TryParseColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try
            {
                var converted = ColorConverter.ConvertFromString(hex);
                return converted == null ? (Color?)null : (Color)converted;
            }
            catch
            {
                return null;
            }
        }
    }
}
