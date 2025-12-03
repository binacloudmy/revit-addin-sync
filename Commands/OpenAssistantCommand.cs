using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace RevitWebAppSync.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenAssistantCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;

                if (uidoc == null)
                {
                    TaskDialog.Show("AI Assistant", "Please open a Revit project first.");
                    return Result.Cancelled;
                }

                // Open chat window with ExternalEvent and Handler from App
                var window = new AIAssistantWindow(uidoc, App.AIExternalEvent, App.AIHandler);
                window.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"Failed to open AI Assistant: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
