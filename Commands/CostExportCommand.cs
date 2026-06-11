using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class CostExportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                Document doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    TaskDialog.Show("BINA Cost", "No active Revit document found.");
                    return Result.Failed;
                }

                // Extract all items from model
                var items = RevitModelWalker.GetAllItems(doc);

                if (items.Count == 0)
                {
                    TaskDialog.Show("BINA Cost", "No priceable elements found in the model.");
                    return Result.Failed;
                }

                // Try to apply existing prices from local DB
                string projectName = Path.GetFileNameWithoutExtension(doc.PathName ?? "Untitled");
                var priceDb = new PriceDatabase(projectName);
                int priced = priceDb.ApplyPrices(items);

                // Get unique JKR codes for summary
                int uniqueCodes = items.Where(i => !string.IsNullOrEmpty(i.JkrCode)).Select(i => i.JkrCode).Distinct().Count();
                var levels = items.Select(i => i.Level).Distinct().Count();

                // Show save dialog
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export Cost Items",
                    Filter = "Excel Files (*.xlsx)|*.xlsx",
                    FileName = $"{projectName}_CostExport_{DateTime.Now:yyyyMMdd}",
                    DefaultExt = ".xlsx"
                };

                if (saveDialog.ShowDialog() != true)
                    return Result.Cancelled;

                // Export to Excel
                ExcelService.Export(items, saveDialog.FileName, projectName);

                // Summary
                var summary = CostCalculator.Calculate(items);
                string totalStr = summary.GrandTotal > 0
                    ? $"\nTotal (with existing prices): RM {summary.GrandTotal:N0}"
                    : "\nNo prices loaded yet — fill in the 'Unit Price' column in Excel.";

                TaskDialog.Show("BINA Cost — Export Complete",
                    $"✅ Exported {items.Count} items to Excel\n\n" +
                    $"📊 {uniqueCodes} unique JKR codes found\n" +
                    $"🏢 {levels} levels\n" +
                    $"💰 {priced} items with existing prices" +
                    totalStr +
                    $"\n\n📁 {saveDialog.FileName}");

                // Open the file
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = saveDialog.FileName,
                    UseShellExecute = true
                });

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA Cost — Error", $"Export failed: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
