using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Writes and reads BINA cost parameters to/from Revit model elements.
    /// Enables cost data to appear in native Revit Schedules.
    /// </summary>
    public static class CostParameterWriter
    {
        /// <summary>
        /// Write cost data from CostItems into Revit element parameters.
        /// Must be called inside an active Transaction.
        /// </summary>
        /// <returns>Number of elements updated</returns>
        public static int WritePricesToModel(Document doc, List<CostItem> items)
        {
            int written = 0;

            foreach (var item in items)
            {
                if (item.UnitPrice <= 0) continue;

                Element elem = doc.GetElement(new ElementId(item.ElementId));
                if (elem == null) continue;

                try
                {
                    bool updated = false;

                    // Write unit price
                    Parameter pPrice = elem.LookupParameter(BINASharedParameters.PARAM_UNIT_PRICE);
                    if (pPrice != null && !pPrice.IsReadOnly)
                    {
                        pPrice.Set(item.UnitPrice);
                        updated = true;
                    }

                    // Write total cost (qty x unit price)
                    Parameter pTotal = elem.LookupParameter(BINASharedParameters.PARAM_TOTAL_COST);
                    if (pTotal != null && !pTotal.IsReadOnly)
                    {
                        pTotal.Set(item.TotalPrice);
                    }

                    // Write JKR code
                    Parameter pCode = elem.LookupParameter(BINASharedParameters.PARAM_JKR_CODE);
                    if (pCode != null && !pCode.IsReadOnly && !string.IsNullOrEmpty(item.JkrCode))
                    {
                        pCode.Set(item.JkrCode);
                    }

                    // Write price source
                    Parameter pSource = elem.LookupParameter(BINASharedParameters.PARAM_PRICE_SOURCE);
                    if (pSource != null && !pSource.IsReadOnly && !string.IsNullOrEmpty(item.PriceSource))
                    {
                        pSource.Set(item.PriceSource);
                    }

                    if (updated) written++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA Cost] Failed to write params for element {item.ElementId}: {ex.Message}");
                }
            }

            return written;
        }

        /// <summary>
        /// Read cost data from Revit element parameters back into CostItems.
        /// Detects user edits made in Revit Schedules and applies them.
        /// Does NOT require a Transaction (read-only).
        /// </summary>
        /// <returns>Number of items updated from model parameters</returns>
        public static int ReadPricesFromModel(Document doc, List<CostItem> items)
        {
            int updated = 0;

            foreach (var item in items)
            {
                Element elem = doc.GetElement(new ElementId(item.ElementId));
                if (elem == null) continue;

                try
                {
                    Parameter pPrice = elem.LookupParameter(BINASharedParameters.PARAM_UNIT_PRICE);
                    if (pPrice == null || !pPrice.HasValue) continue;

                    double modelPrice = pPrice.AsDouble();
                    if (modelPrice <= 0) continue;

                    // Check if user edited the price in Revit Schedule
                    // (model value differs from what we'd assign)
                    if (item.UnitPrice <= 0 || Math.Abs(modelPrice - item.UnitPrice) > 0.001)
                    {
                        item.UnitPrice = modelPrice;

                        // Read source — if it was "manual" user edited it in schedule
                        Parameter pSource = elem.LookupParameter(BINASharedParameters.PARAM_PRICE_SOURCE);
                        string source = pSource != null && pSource.HasValue ? pSource.AsString() : null;

                        // If price differs from what AI assigned, mark as manual
                        if (string.IsNullOrEmpty(source) || item.PriceSource != source)
                            item.PriceSource = "manual";
                        else
                            item.PriceSource = source;

                        // Read JKR code if present
                        Parameter pCode = elem.LookupParameter(BINASharedParameters.PARAM_JKR_CODE);
                        if (pCode != null && pCode.HasValue)
                        {
                            string code = pCode.AsString();
                            if (!string.IsNullOrEmpty(code))
                                item.JkrCode = code;
                        }

                        updated++;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA Cost] Failed to read params for element {item.ElementId}: {ex.Message}");
                }
            }

            return updated;
        }
    }
}
