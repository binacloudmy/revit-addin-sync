// ChatView: the Home screen shown for an empty thread (board 1). Saved commands
// with Run, then example prompts in BM and English, scoped to the active view and
// the current selection. Read when the thread is (re)built, so it reflects the
// view and selection as of opening the tab / starting a new chat.
using System.Collections.Generic;
using System.Linq;
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

        // Active view name + kind and selection size. Best effort: any Revit API
        // failure (no document, not in an API context) falls back to an unscoped Home.
        private static (string view, HomeKind kind, int selected) ReadScope()
        {
            try
            {
                var uidoc = App.UiApp?.ActiveUIDocument;
                var view = uidoc?.ActiveView;
                if (view == null) return (null, HomeKind.None, 0);
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
                return (view.Name, kind, uidoc.Selection.GetElementIds().Count);
            }
            catch { return (null, HomeKind.None, 0); }
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
            var (view, kind, selected) = ReadScope();
            var col = new StackPanel { Margin = new Thickness(14, 18, 14, 18) };

            var head = new StackPanel { Margin = new Thickness(2, 0, 2, 20) };
            if (!string.IsNullOrEmpty(view))
            {
                var chipText = new TextBlock { Text = "Active view  " + view, FontSize = 11.5, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
                chipText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Accent");
                head.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(9, 3, 9, 3), Background = CopilotColors.From("#172A69C6"), Child = chipText,
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

            col.Children.Add(HomeParts.SavedCommandsSection(RunSavedCommand));
            col.Children.Add(HomeParts.ExamplesSection(HomeExamples(kind, selected), p => Prompt.InsertStarterPrompt(p)));
            return col;
        }
    }
}
