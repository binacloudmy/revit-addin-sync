// ChatView: the Home screen shown for an empty thread (board 1). Saved commands
// with Run, then example prompts in BM and English, scoped to the active view and
// the current selection. Read when the thread is (re)built, so it reflects the
// view and selection as of opening the tab / starting a new chat.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    public partial class ChatView
    {
        /// <summary>Run a saved command from Home / the Commands tab. One with no
        /// required inputs is sent straight away; one that asks for inputs is
        /// picked into the composer so its chips can be filled first.</summary>
        public void RunSavedCommand(SlashTool tool)
        {
            if (tool == null || Vm == null) return;
            if (tool.Inputs != null && tool.Inputs.Any(i => i.Required)) { Prompt.PickTool(tool); return; }
            Vm.ChatSendSlashCommand(tool, "", null);
        }

        private enum HomeKind { None, Plan, ThreeD, Sheet, Schedule, Other }

        // Active view name + kind and selection size. This is a Revit API read, so it
        // must run on Revit's own thread (RevitThread), never from this UI thread: a direct
        // read while Revit is opening a model (its modal dialogs are up) crashes Revit.
        // Home draws from the last value read and refreshes itself when a read finishes.
        // summary is the "<Category> in view" card's data (null: no card), read on the same trip.
        private static (string view, HomeKind kind, int selected, ViewSummary summary) _scope = (null, HomeKind.None, 0, null);
        private bool _scopeRefreshing;
        private int _scopeRetries;

        private static (string view, HomeKind kind, int selected, ViewSummary summary) ReadScope(Autodesk.Revit.UI.UIApplication app)
        {
            try
            {
                var uidoc = app?.ActiveUIDocument;
                var view = uidoc?.ActiveView;
                if (view == null) return (null, HomeKind.None, 0, null);
                var kind = HomeKind.Other;
                switch (view.ViewType)
                {
                    case Autodesk.Revit.DB.ViewType.FloorPlan:
                    case Autodesk.Revit.DB.ViewType.CeilingPlan:
                    case Autodesk.Revit.DB.ViewType.EngineeringPlan:
                    case Autodesk.Revit.DB.ViewType.AreaPlan: kind = HomeKind.Plan; break;
                    case Autodesk.Revit.DB.ViewType.ThreeD: kind = HomeKind.ThreeD; break;
                    case Autodesk.Revit.DB.ViewType.DrawingSheet: kind = HomeKind.Sheet; break;
                    case Autodesk.Revit.DB.ViewType.Schedule: kind = HomeKind.Schedule; break;
                }
                var summary = kind == HomeKind.Sheet || kind == HomeKind.Schedule ? null : CachedViewSummary(uidoc.Document, view);
                return (view.Name, kind, uidoc.Selection.GetElementIds().Count, summary);
            }
            catch { return (null, HomeKind.None, 0, null); }
        }

        // The model elements the active view shows, as (category, type) pairs for ViewSummary. Runs on
        // Revit's thread (called from ReadScope only). Capped by time: a huge view returns no card rather
        // than holding Revit's thread. Any failure just hides the card.
        private const int SummaryBudgetMs = 1500;

        // Home redraws on a theme switch, a resize, a tab change: each asks for the scope again, and each ask would
        // walk the whole view on Revit's thread (up to the budget). The last read is kept for a few seconds per
        // document and view, a "too big" (null) read included, so a burst of redraws costs one pass.
        // Only touched on Revit's thread (from ReadScope).
        private static readonly TimeSpan SummaryKeep = TimeSpan.FromSeconds(10);
        private static (string key, DateTime at, ViewSummary summary) _summaryRead = (null, DateTime.MinValue, null);

        private static ViewSummary CachedViewSummary(Autodesk.Revit.DB.Document doc, Autodesk.Revit.DB.View view)
        {
            var key = (doc.PathName ?? "") + "|" + doc.Title + "|" + view.Id;
            if (_summaryRead.key == key && DateTime.UtcNow - _summaryRead.at < SummaryKeep) return _summaryRead.summary;
            var read = ReadViewSummary(doc, view);
            _summaryRead = (key, DateTime.UtcNow, read);
            return read;
        }

        private static ViewSummary ReadViewSummary(Autodesk.Revit.DB.Document doc, Autodesk.Revit.DB.View view)
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var typeNames = new Dictionary<Autodesk.Revit.DB.ElementId, string>();
                var pairs = new List<(string, string)>();
                var n = 0;
                using (var col = new Autodesk.Revit.DB.FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                {
                    foreach (var e in col)
                    {
                        if ((++n & 0x3FF) == 0 && clock.ElapsedMilliseconds > SummaryBudgetMs) { e.Dispose(); return null; }
                        try
                        {
                            var cat = e.Category;
                            if (cat == null || cat.CategoryType != Autodesk.Revit.DB.CategoryType.Model) continue;
                            var tid = e.GetTypeId();
                            string type = "";
                            if (tid != null && tid != Autodesk.Revit.DB.ElementId.InvalidElementId && !typeNames.TryGetValue(tid, out type))
                                typeNames[tid] = type = (doc.GetElement(tid) as Autodesk.Revit.DB.ElementType)?.Name ?? "";
                            pairs.Add((cat.Name, type));
                        }
                        finally { e.Dispose(); }
                    }
                }
                return ViewSummary.Build(pairs);
            }
            catch { return null; }
        }

        // Ask Revit's thread for the scope; when it differs from what Home shows, redraw.
        // Fire and forget: a busy or detached Revit just leaves Home unscoped.
        private void RefreshScopeAsync()
        {
            if (_scopeRefreshing) return;
            _scopeRefreshing = true;
            BinaVibe.Copilot.Writes.RevitThread.RunAsync(app => ReadScope(app), default)
                .ContinueWith(t =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _scopeRefreshing = false;
                        var ok = t.Status == TaskStatus.RanToCompletion;
                        // No view yet (Revit is still opening a model): try again shortly, a few times.
                        if ((!ok || t.Result.view == null) && _scopeRetries++ < 60)
                        {
                            Task.Delay(4000).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(RefreshScopeAsync)), TaskScheduler.Default);
                            return;
                        }
                        if (!ok) return;
                        _scopeRetries = 0;
                        if (t.Result.Equals(_scope)) return;   // ViewSummary compares by value, so an unchanged view does not redraw
                        _scope = t.Result;
                        Rebuild();
                    }));
                }, TaskScheduler.Default);
        }

        private static List<(string text, string lang)> HomeExamples(HomeKind kind, int selected)
        {
            if (selected > 0)
                return new List<(string, string)>
                {
                    ("Tolong isolate elemen yang dipilih dalam view ini", "BM"),
                    ("Which of the selected elements have an empty Mark or Comments?", "EN"),
                    ("Count the selected elements by type", "EN"),
                };
            switch (kind)
            {
                case HomeKind.Sheet:
                    return new List<(string, string)>
                    {
                        ("Senaraikan semua view yang diletakkan pada sheet ini", "BM"),
                        ("Which views on this sheet have no title?", "EN"),
                        ("Rename this sheet to follow the project sheet naming", "EN"),
                    };
                case HomeKind.Schedule:
                    return new List<(string, string)>
                    {
                        ("Tunjukkan baris dalam jadual ini yang parameternya kosong", "BM"),
                        ("Which rows in this schedule have an empty value?", "EN"),
                        ("Export this schedule to Excel", "EN"),
                    };
                case HomeKind.None:
                    return new List<(string, string)>
                    {
                        ("Berapa banyak pintu dalam model ini?", "BM"),
                        ("List the levels in this model", "EN"),
                    };
                default:
                    return new List<(string, string)>
                    {
                        ("Tolong kira semua elemen mengikut kategori dalam view ini", "BM"),
                        ("Which family types in this view have an empty Model or Manufacturer?", "EN"),
                        ("Tag all untagged doors and rooms in this view", "EN"),
                    };
            }
        }

        private FrameworkElement HomeState()
        {
            var (view, kind, selected, summary) = _scope;
            RefreshScopeAsync();
            var col = new StackPanel { Margin = new Thickness(14, 18, 14, 18) };

            var head = new StackPanel { Margin = new Thickness(2, 0, 2, 20) };
            if (!string.IsNullOrEmpty(view))
            {
                var chipText = new TextBlock { Text = "Active view  " + view, FontSize = 11.5, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
                chipText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Accent");
                head.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(9, 3, 9, 3), Background = CopilotColors.Tint("Cp.Brand", 0x17), Child = chipText,
                });
            }
            var title = new TextBlock
            {
                Text = string.IsNullOrEmpty(view) ? "What needs doing?" : "What needs doing in " + view + "?",
                FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            head.Children.Add(title);
            var scope = new TextBlock
            {
                Text = string.IsNullOrEmpty(view) ? "Open a model to scope Copilot to a view."
                    : selected > 0 ? selected + (selected == 1 ? " element" : " elements") + " selected. Copilot works on just those."
                    : "Nothing selected. Select elements first to work on just those.",
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            };
            scope.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            head.Children.Add(scope);
            col.Children.Add(head);

            if (summary != null) col.Children.Add(HomeParts.ViewSummaryCard(summary));
            col.Children.Add(HomeParts.SavedCommandsSection(RunSavedCommand));
            col.Children.Add(HomeParts.ExamplesSection(HomeExamples(kind, selected), p => Prompt.InsertStarterPrompt(p)));
            return col;
        }
    }
}
