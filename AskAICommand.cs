using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class AskAICommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Show the dedicated AI Assistant window
                var aiWindow = new AIAssistantWindow();
                aiWindow.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Error opening AI Assistant: {ex.Message}";
                return Result.Failed;
            }
        }
    }
}