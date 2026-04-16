using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Handles writing cost parameters to Revit model on the main API thread.
    /// Must be invoked via ExternalEvent.Raise() from async/UI code.
    /// </summary>
    public class CostWriteHandler : IExternalEventHandler
    {
        public List<CostItem> Items { get; set; }
        public bool ClearMode { get; set; } = false;
        public int WrittenCount { get; private set; }
        public bool ScheduleCreated { get; private set; }
        public string Error { get; private set; }
        public Action OnCompleted { get; set; }

        public void Execute(UIApplication app)
        {
            WrittenCount = 0;
            ScheduleCreated = false;
            Error = null;

            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null || Items == null || Items.Count == 0)
                {
                    Error = "No active document or no items to write";
                    OnCompleted?.Invoke();
                    return;
                }

                // Step 1: Create shared parameters
                using (var tx1 = new Transaction(doc, "BINA: Create Cost Parameters"))
                {
                    tx1.Start();
                    BINASharedParameters.EnsureParameters(doc);
                    tx1.Commit();
                }

                // Step 2: Write or clear prices on elements
                using (var tx2 = new Transaction(doc, ClearMode ? "BINA: Clear Prices" : "BINA: Write Prices"))
                {
                    tx2.Start();
                    WrittenCount = ClearMode
                        ? CostParameterWriter.ClearPricesFromModel(doc, Items)
                        : CostParameterWriter.WritePricesToModel(doc, Items);
                    tx2.Commit();
                }

                // Step 3: Create cost schedule (skip in ClearMode — keep existing schedule)
                if (!ClearMode)
                {
                    try
                    {
                        using (var tx3 = new Transaction(doc, "BINA: Create Cost Schedule"))
                        {
                            tx3.Start();
                            ScheduleCreated = BINASharedParameters.CreateCostSchedule(doc);
                            if (ScheduleCreated)
                                tx3.Commit();
                            else
                                tx3.RollBack();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA Cost] Schedule creation failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] CostWriteHandler failed: {ex.Message}");
            }

            try { OnCompleted?.Invoke(); }
            catch (Exception cbEx)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] OnCompleted callback failed: {cbEx.Message}");
            }
        }

        public string GetName() => "BINA Cost Write Handler";
    }
}
