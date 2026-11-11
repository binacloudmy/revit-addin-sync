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
                };
                for (var i = 0; i < columns.Count; i++)
                    grid.Columns.Add(new DataGridTextColumn { Header = columns[i], Binding = new Binding("[" + i + "]"), IsReadOnly = true });
                var window = new Window
                {
                    Title = title, Width = 780, Height = 420, Content = grid, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                try { new WindowInteropHelper(window).Owner = Process.GetCurrentProcess().MainWindowHandle; }
                catch (Exception) { /* no owner: still a window */ }
                window.Show();
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] open as grid: " + ex.Message); }
        }
    }
}
