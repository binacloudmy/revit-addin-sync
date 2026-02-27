using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitWebAppSync.Events;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.UI;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class App : IExternalApplication
    {
        // Static properties for ExternalEvent access from commands
        public static ExternalEvent AIExternalEvent { get; private set; }
        public static CodeExecutionHandler AIHandler { get; private set; }

        // Cost Dashboard dockable pane host
        public static CostDashboardHost CostDashboardHost { get; private set; }

        // Fire Compliance dockable pane host
        public static ComplianceDashboardHost ComplianceDashboardHost { get; private set; }

        // Live cost update handler
        public static CostUpdateHandler CostUpdateHandler { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Create external event handler for AI code execution
                AIHandler = new CodeExecutionHandler();
                AIExternalEvent = ExternalEvent.Create(AIHandler);

                // Register Cost Dashboard dockable pane
                try
                {
                    CostDashboardHost = new CostDashboardHost();
                    application.RegisterDockablePane(
                        CostDashboardHost.PaneId,
                        "BINA Cost Tracker",
                        CostDashboardHost);
                }
                catch (Exception dockEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Cost dockable pane registration failed: {dockEx.Message}");
                }

                // Register Fire Compliance dockable pane
                try
                {
                    ComplianceDashboardHost = new ComplianceDashboardHost();
                    application.RegisterDockablePane(
                        ComplianceDashboardHost.PaneId,
                        "BINA Fire Compliance",
                        ComplianceDashboardHost);
                }
                catch (Exception compEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Compliance dockable pane registration failed: {compEx.Message}");
                }

                // Subscribe to document changes for live cost updates
                try
                {
                    CostUpdateHandler = new CostUpdateHandler(application);
                    CostUpdateHandler.Subscribe();
                }
                catch (Exception evtEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Cost update handler failed: {evtEx.Message}");
                }

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
            // Unsubscribe from document change events
            CostUpdateHandler?.Unsubscribe();

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
                ToolTip = "Open BINA Cloud",
                LongDescription = "Opens BINA Cloud in your default browser.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            PushButtonData loginButtonData = new PushButtonData(
                "Login",
                "Login",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.LoginCommand")
            {
                ToolTip = "Login to BINA Cloud",
                LongDescription = "Opens BINA Cloud in your default browser to login.",
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
                "AI Assistant",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.OpenAssistantCommand")
            {
                ToolTip = "Open AI Assistant",
                LongDescription = "Open the AI Assistant to automate Revit tasks with natural language. Examples: Hide all furniture, Count doors on Level 1, Color walls by phase.",
                Image = LoadImage("RevitWebAppSync.Resources.microchip.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.microchip.png", 32)
            };

            // Cost Tracker buttons
            PushButtonData costExportButtonData = new PushButtonData(
                "CostExport",
                "Export\nCost Items",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.CostExportCommand")
            {
                ToolTip = "Export Cost Items to Excel",
                LongDescription = "Extract all model elements with quantities, JKR codes, and levels. Export to Excel for QS to fill in prices.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData costImportButtonData = new PushButtonData(
                "CostImport",
                "Import\nPrices",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.CostImportCommand")
            {
                ToolTip = "Import Prices from Excel",
                LongDescription = "Import unit prices from a filled Excel file. Prices are saved locally and applied to the cost tracker.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            PushButtonData costDashboardButtonData = new PushButtonData(
                "CostDashboard",
                "Cost\nTracker",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.CostDashboardCommand")
            {
                ToolTip = "Open Cost Tracker Dashboard",
                LongDescription = "Show the cost tracker panel with total cost breakdown by level and category.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            ribbonPanel.AddItem(buttonData);
            ribbonPanel.AddItem(loginButtonData);
            ribbonPanel.AddItem(bimDisciplineButtonData);
            ribbonPanel.AddItem(askAiButtonData);
            PushButtonData complianceButtonData = new PushButtonData(
                "FireCompliance",
                "Fire\nCompliance",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.ComplianceDashboardCommand")
            {
                ToolTip = "Check Fire Compliance (UKBS 1984)",
                LongDescription = "Check building elements against Malaysian UKBS 1984 fire safety requirements (Jadual 5-11). Shows non-compliant elements and allows querying the by-laws.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            ribbonPanel.AddSeparator();
            ribbonPanel.AddItem(costExportButtonData);
            ribbonPanel.AddItem(costImportButtonData);
            ribbonPanel.AddItem(costDashboardButtonData);
            ribbonPanel.AddItem(complianceButtonData);
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
