using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                CreateRibbonTab(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to initialize add-in: {ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private void CreateRibbonTab(UIControlledApplication application)
        {
            string tabName = "Sync";
            application.CreateRibbonTab(tabName);

            RibbonPanel ribbonPanel = application.CreateRibbonPanel(tabName, "Sync Tools");

            PushButtonData buttonData = new PushButtonData(
                "SyncToWebApp",
                "Sync to BINA",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.SyncCommand")
            {
                ToolTip = "Open app.bina.cloud",
                LongDescription = "Opens app.bina.cloud in your default browser.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            PushButtonData choosePathButtonData = new PushButtonData(
                "ChoosePath",
                "Choose Path",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.ChoosePathCommand")
            {
                ToolTip = "Choose Path - Open app.bina.cloud",
                LongDescription = "Opens app.bina.cloud in your default browser to choose your path.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData bimDisciplineButtonData = new PushButtonData(
                "BimDiscipline",
                "Download BIM Disciplines",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.BimDisciplineCommand")
            {
                ToolTip = "Download BIM Discipline Files",
                LongDescription = "Download the latest Architecture, Structure, HVAC, and Electrical discipline files from BINA cloud.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            PushButtonData federateButtonData = new PushButtonData(
                "FederateDisciplines",
                "Federate Disciplines",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.FederateDisciplinesCommand")
            {
                ToolTip = "Link Downloaded Discipline Files",
                LongDescription = "Link previously downloaded discipline files to the current Revit document for coordination and clash detection.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData askAiButtonData = new PushButtonData(
                "AskAI",
                "Ask AI",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.AskAICommand")
            {
                ToolTip = "Ask AI Assistant",
                LongDescription = "Open the AI Assistant to get help with your Revit project and BINA sync operations.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            ribbonPanel.AddItem(buttonData);
            ribbonPanel.AddItem(choosePathButtonData);
            ribbonPanel.AddItem(bimDisciplineButtonData);
            ribbonPanel.AddItem(askAiButtonData);
            // ribbonPanel.AddItem(federateButtonData); // Hidden as requested
        }

        private BitmapImage LoadImage(string resourceName, int size = 32)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                Stream stream = assembly.GetManifestResourceStream(resourceName);
                
                if (stream == null)
                    return null;

                BitmapImage bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = stream;
                bitmapImage.DecodePixelWidth = size;
                bitmapImage.DecodePixelHeight = size;
                bitmapImage.EndInit();
                bitmapImage.Freeze();
                
                return bitmapImage;
            }
            catch
            {
                return null;
            }
        }
    }
}