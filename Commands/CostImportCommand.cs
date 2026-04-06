using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CostImportCommand : IExternalCommand
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

                // Show file picker
                var openDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import Prices from Excel",
                    Filter = "Excel Files (*.xlsx)|*.xlsx",
                    DefaultExt = ".xlsx"
                };

                if (openDialog.ShowDialog() != true)
                    return Result.Cancelled;

                // Import prices from Excel
                var importedPrices = ExcelService.ImportPrices(openDialog.FileName);

                if (importedPrices.Count == 0)
                {
                    TaskDialog.Show("BINA Cost", "No prices found in the Excel file.\n\nMake sure the 'JKR Code' and 'Unit Price' columns have data.");
                    return Result.Failed;
                }

                // Save to local price database
                string projectName = Path.GetFileNameWithoutExtension(doc.PathName ?? "Untitled");
                var priceDb = new PriceDatabase(projectName);
                int imported = priceDb.ImportPrices(importedPrices, "imported");
                priceDb.Save();

                // Now recalculate with new prices
                var items = RevitModelWalker.GetAllItems(doc);
                int matched = priceDb.ApplyPrices(items);
                var summary = CostCalculator.Calculate(items);

                // Write imported prices to Revit model parameters
                int writtenToModel = 0;
                using (Transaction tx = new Transaction(doc, "BINA: Import Prices to Model"))
                {
                    tx.Start();
                    BINASharedParameters.EnsureParameters(doc);
                    writtenToModel = CostParameterWriter.WritePricesToModel(doc, items);
                    tx.Commit();
                }

                // Build level breakdown string
                string levelBreakdown = "";
                foreach (var level in summary.ByLevel)
                {
                    if (level.TotalCost > 0)
                        levelBreakdown += $"\n  {level.Name}: RM {level.TotalCost:N0} ({level.Percentage:F1}%)";
                }

                TaskDialog.Show("BINA Cost — Import Complete",
                    $"Imported {imported} prices\n" +
                    $"{matched}/{items.Count} items now have prices\n" +
                    $"{writtenToModel} elements updated in model\n\n" +
                    $"TOTAL: RM {summary.GrandTotal:N0}\n" +
                    $"\nBy Level:{levelBreakdown}\n\n" +
                    $"Prices saved to local database and Revit model.\n" +
                    $"View in Revit Schedule: BINA_Unit_Price, BINA_JKR_Code columns.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA Cost — Error", $"Import failed: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
