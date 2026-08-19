using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BinaVibe.Mcp;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>
    /// v6-panel "Model" view: key/value facts about the connected document.
    /// The static rows come from the live VM context (document title, addin
    /// version); the statistics rows come from the addin's own
    /// analyze_model_statistics tool via the McpJobPump — the same local job
    /// path the chat's element-id clicks use, no backend round-trip. Outside
    /// Revit (UiHarness) the job no-ops and the view keeps the static rows.
    /// </summary>
    public partial class ModelView : UserControl
    {
        private bool _loading;
        private string _lastSync = "—";

        public ModelView()
        {
            InitializeComponent();
            Loaded += (_, __) => { RenderRows(null); RefreshStats(); };
            DataContextChanged += (_, __) => RenderRows(_stats);
        }

        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private Dictionary<string, object> _stats;

        private void OnResync(object sender, RoutedEventArgs e) => RefreshStats();

        private async void RefreshStats()
        {
            if (_loading) return;
            _loading = true;
            try
            {
                var job = new McpJob
                {
                    Tool = "analyze_model_statistics",
                    Args = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>()),
                };
                McpJobPump.Enqueue(job);
                await job.Done.Task.ConfigureAwait(true);   // stay on UI thread
                if (job.Error == null && job.Result != null)
                {
                    var flat = new Dictionary<string, object>();
                    foreach (var kv in job.Result)
                        if (kv.Value is string || kv.Value is int || kv.Value is long
                            || kv.Value is double || kv.Value is bool)
                            flat[kv.Key] = kv.Value;
                    _stats = flat;
                    _lastSync = DateTime.Now.ToString("HH:mm");
                }
            }
            catch { /* stats are optional evidence — the static rows stand alone */ }
            finally
            {
                _loading = false;
                RenderRows(_stats);
            }
        }

        private void RenderRows(Dictionary<string, object> stats)
        {
            if (Rows == null) return;
            var doc = Vm?.DocumentTitle;
            Subtitle.Text = string.IsNullOrEmpty(doc) ? "No document connected" : doc + " · connected to Revit";

            Rows.Children.Clear();
            AddRow("File", string.IsNullOrEmpty(doc) ? "—" : doc);
            AddRow("Addin", AppInfo.AddinLabel);
            if (stats != null)
                foreach (var kv in stats)
                    AddRow(Prettify(kv.Key), Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture));
            AddRow("Last sync", _lastSync);
        }

        // "wall_count" → "Wall count"
        private static string Prettify(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            var s = key.Replace('_', ' ').Trim();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // One v6 key/value row: 130px muted key, value, bottom hairline.
        private void AddRow(string k, string v)
        {
            var grid = new Grid { Margin = new Thickness(2, 0, 2, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var key = new TextBlock { Text = k, FontSize = 12, Margin = new Thickness(0, 8, 10, 8) };
            key.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            Grid.SetColumn(key, 0);
            grid.Children.Add(key);

            var val = new TextBlock
            {
                Text = v ?? "—", FontSize = 13, Margin = new Thickness(0, 8, 0, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            val.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            Grid.SetColumn(val, 1);
            grid.Children.Add(val);

            var wrap = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
            wrap.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            Rows.Children.Add(wrap);
        }
    }
}
