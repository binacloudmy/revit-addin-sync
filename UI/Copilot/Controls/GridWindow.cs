#nullable enable
// "Open as grid": a result table in a small window of its own, one cell per value, that sorts and copies
// (select cells, Ctrl+C: with the header row). Read-only. The rows come from ChangeTable.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class GridWindow
    {
        public static void Show(string title, IReadOnlyList<string> columns, IReadOnlyList<string[]> rows)
        {
            try
            {
                var grid = new DataGrid
                {
                    AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
                    HeadersVisibility = DataGridHeadersVisibility.Column,
                    GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                    SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
                    ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
                    ItemsSource = rows.ToList(),
                    BorderThickness = new Thickness(0), FontSize = 12.5, RowHeaderWidth = 0,
                    CanUserResizeRows = false, MinRowHeight = 34,
                };
                Dark(grid);
                for (var i = 0; i < columns.Count; i++)
                    grid.Columns.Add(new DataGridTextColumn { Header = columns[i], Binding = new Binding("[" + i + "]"), IsReadOnly = true });
                var window = new Window
                {
                    Title = title, Width = 780, Height = 420, Content = grid, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                window.SetResourceReference(Window.BackgroundProperty, "Cp.PanelBg");
                window.SetResourceReference(Window.FontFamilyProperty, "Cp.Font");
                try { new WindowInteropHelper(window).Owner = Process.GetCurrentProcess().MainWindowHandle; }
                catch (Exception) { /* no owner: still a window */ }
                window.Show();
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] open as grid: " + ex.Message); }
        }

        /// <summary>The pane's dark look for the grid (the table of the card): card ground, muted header on the raised fill, hairlines,
        /// 10/8 cell padding, a quiet selection. Tokens only; the window is outside the pane but reads the app-level theme.</summary>
        private static void Dark(DataGrid grid)
        {
            DynamicResourceExtension Res(string key) => new DynamicResourceExtension(key);
            grid.SetResourceReference(Control.BackgroundProperty, "Cp.Bg");
            grid.SetResourceReference(Control.ForegroundProperty, "Cp.Ink");
            grid.SetResourceReference(DataGrid.RowBackgroundProperty, "Cp.Bg");
            grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Cp.Bg");
            grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Cp.Line");
            grid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "Cp.Line");

            var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.BackgroundProperty, Res("Cp.Raised")));
            header.Setters.Add(new Setter(Control.ForegroundProperty, Res("Cp.Muted")));
            header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Medium));
            header.Setters.Add(new Setter(Control.FontSizeProperty, 11.5));
            header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 9, 12, 9)));
            header.Setters.Add(new Setter(Control.BorderBrushProperty, Res("Cp.Line")));
            header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            header.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            grid.ColumnHeaderStyle = header;

            var cell = new Style(typeof(DataGridCell));
            cell.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            cell.Setters.Add(new Setter(Control.ForegroundProperty, Res("Cp.Ink")));
            cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
            cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            cell.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Res("Cp.Hover")));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Res("Cp.Ink")));
            selected.Setters.Add(new Setter(Control.BorderBrushProperty, Res("Cp.Run.Info")));
            selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            cell.Triggers.Add(selected);
            grid.CellStyle = cell;

            var rowHeader = new Style(typeof(System.Windows.Controls.Primitives.DataGridRowHeader));
            rowHeader.Setters.Add(new Setter(Control.BackgroundProperty, Res("Cp.Bg")));
            grid.RowHeaderStyle = rowHeader;
        }
    }
}
