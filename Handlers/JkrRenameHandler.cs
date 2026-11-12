using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Applies JKR compliance fixes (renames + parameter sets) via ExternalEvent,
    /// on Revit's main thread.
    ///
    /// One batch = one undo step: every change runs in a single Transaction inside a
    /// TransactionGroup that is assimilated, so Edit > Undo (Ctrl+Z) reverts the whole
    /// batch at once. The batch is applied synchronously in Execute; Revit does not
    /// redraw until it finishes, so the pane shows its busy state BEFORE raising the
    /// event, and the confirm dialog warns when a batch touches
    /// JkrFixBatch.PauseWarningElementCount or more elements.
    /// </summary>
    public class JkrRenameHandler : IExternalEventHandler
    {
        public const string DefaultUndoName = "JKR Fix all";

        /// <summary>
        /// List of (ElementId, newName) pairs to rename.
        /// </summary>
        public List<(long ElementId, string NewName)> RenameQueue { get; set; } = new List<(long, string)>();

        /// <summary>
        /// List of parameter fixes to apply after renames.
        /// </summary>
        public List<JkrFixAction> ParamFixQueue { get; set; } = new List<JkrFixAction>();

        public Action<RenameResult> OnCompleted { get; set; }

        /// <summary>Name of the batch's single entry in Revit's undo history
        /// (Edit > Undo). Reset back to <see cref="DefaultUndoName"/> after each batch.</summary>
        public string TransactionGroupName { get; set; } = DefaultUndoName;

        public void Execute(UIApplication app)
        {
            var result = new RenameResult();
            var failReasons = new List<string>();
            var undoName = string.IsNullOrWhiteSpace(TransactionGroupName) ? DefaultUndoName : TransactionGroupName;

            // Snapshot and clear the public queues first so a stray re-raise can't
            // double-apply the same batch.
            var batch = JkrFixBatch<JkrFixAction>.Build(RenameQueue, ParamFixQueue, f => f.ElementId, f => f.Priority);
            RenameQueue.Clear();
            ParamFixQueue.Clear();

            var uidoc = app.ActiveUIDocument;
            View restoreView = null;
            try
            {
                var doc = uidoc?.Document;
                if (doc == null)
                    result.Error = "No active document.";
                else if (!batch.IsEmpty)
                {
                    // Perf guard (kept from the original one-shot Fix All): with a 3D /
                    // rendered view active, regenerations during the batch force
                    // expensive re-renders. Switch to a lightweight view for the batch
                    // and switch back afterwards. Must happen outside any transaction.
                    restoreView = SwitchAwayFrom3D(uidoc);
                    ApplyBatch(doc, batch, undoName, result, failReasons);
                }
            }
            catch (Exception ex)
            {
                // The using blocks in ApplyBatch already rolled the group back.
                result.Error = ex.Message;
                result.Renamed = 0;
                result.ParamFixed = 0;
            }
            finally
            {
                try { if (restoreView != null) uidoc.ActiveView = restoreView; } catch { }
            }

            Finish(undoName, batch, result, failReasons);
        }

        /// <summary>If a 3D view is active, activate an empty drafting view (else a
        /// plan) and return the view to restore; otherwise null.</summary>
        private static View SwitchAwayFrom3D(UIDocument uidoc)
        {
            try
            {
                if (!(uidoc.ActiveView is View3D)) return null;
                var light = FindLightweightView(uidoc.Document);
                if (light == null) return null;
                var previous = uidoc.ActiveView;
                uidoc.ActiveView = light;
                return previous;
            }
            catch { return null; }
        }

        private static View FindLightweightView(Document doc)
        {
            var drafting = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewDrafting)).Cast<View>()
                .FirstOrDefault(v => v != null && !v.IsTemplate);
            if (drafting != null) return drafting;
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<View>()
                .FirstOrDefault(v => v != null && !v.IsTemplate);
        }

        /// <summary>All steps in one Transaction inside one TransactionGroup. A bad
        /// step is recorded as a failure and the batch continues. If Revit refuses the
        /// commit, the group is rolled back and nothing is written. If no step changed
        /// anything, the group is rolled back too, so no empty undo entry is left.</summary>
        private static void ApplyBatch(Document doc, JkrFixBatch<JkrFixAction> batch, string undoName,
                                       RenameResult result, List<string> failReasons)
        {
            var applicator = new JkrFixApplicator(doc);
            using (var group = new TransactionGroup(doc, undoName))
            {
                group.Start();
                TransactionStatus status;
                using (var tx = new Transaction(doc, undoName))
                {
                    tx.Start();
                    foreach (var step in batch.Steps)
                        ApplyOne(doc, applicator, step, result, failReasons);
                    status = tx.Commit();
                }

                if (status != TransactionStatus.Committed)
                {
                    group.RollBack();
                    result.Renamed = 0;
                    result.ParamFixed = 0;
                    result.Error = $"Revit did not accept the changes ({status}). Nothing was written to the model.";
                    return;
                }

                if (result.Renamed + result.ParamFixed == 0)
                    group.RollBack();
                else
                    group.Assimilate();
            }
        }

        /// <summary>Apply one step inside the caller's open Transaction. Per-step
        /// try/catch so one bad fix is recorded as a failure and the batch keeps going.</summary>
        private static void ApplyOne(Document doc, JkrFixApplicator applicator, JkrFixBatch<JkrFixAction>.Step step,
                                     RenameResult result, List<string> failReasons)
        {
            try
            {
                if (step.IsRename)
                {
                    int rB = result.Renamed, fB = result.Failed + result.Skipped;
                    ApplyRename(doc, step.ElementId, step.NewName, result, failReasons);
                    if (result.Renamed > rB)
                        System.Diagnostics.Debug.WriteLine($"[BINA Rename] OK elem={step.ElementId} → '{step.NewName}'");
                    else if (result.Failed + result.Skipped > fB)
                        System.Diagnostics.Debug.WriteLine($"[BINA Rename] FAIL elem={step.ElementId} → '{step.NewName}'");
                    return;
                }

                var fix = step.Fix;
                FixResult fixResult;
                // Only fixes that may bind a fresh shared parameter need SubTransaction
                // isolation (so a read-only write failure after a binding leaves no
                // residue). Plain writes run directly in the batch transaction.
                if (applicator.FixNeedsIsolation(fix))
                {
                    using (var subTx = new SubTransaction(doc))
                    {
                        subTx.Start();
                        try { fixResult = applicator.ApplyFixInExistingTx(fix); }
                        catch (Exception ex) { subTx.RollBack(); fixResult = new FixResult { Success = false, Message = ex.Message }; }
                        if (fixResult.Success) subTx.Commit();
                        else if (subTx.HasStarted() && !subTx.HasEnded()) subTx.RollBack();
                    }
                }
                else
                {
                    fixResult = applicator.ApplyFixInExistingTx(fix);
                }

                if (fixResult.Success)
                {
                    result.ParamFixed++;
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA ParamFix] OK elem={fix.ElementId} param='{fix.ParameterName}' " +
                        $"value='{fix.Value}' (was '{fix.OldValue}') target={fix.Target}");
                }
                else
                {
                    result.Failed++;
                    result.FailedElementIds.Add(fix.ElementId);
                    result.FailedFixKeys.Add(RenameResult.MakeFixKey("set_parameter", fix.ElementId, fix.ParameterName));
                    failReasons.Add($"{fix.ParameterName} on {fix.ElementId}: {fixResult.Message}");
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA ParamFix] FAIL elem={fix.ElementId} param='{fix.ParameterName}' reason='{fixResult.Message}'");
                }
            }
            catch (Exception ex)
            {
                // Never let one step abort the batch.
                result.Failed++;
                if (!step.IsRename && step.Fix != null)
                {
                    result.FailedElementIds.Add(step.Fix.ElementId);
                    result.FailedFixKeys.Add(RenameResult.MakeFixKey("set_parameter", step.Fix.ElementId, step.Fix.ParameterName));
                    failReasons.Add($"{step.Fix.ParameterName} on {step.Fix.ElementId}: {ex.Message}");
                }
                else
                {
                    result.FailedElementIds.Add(step.ElementId);
                    failReasons.Add($"rename {step.ElementId}: {ex.Message}");
                }
            }
        }

        /// <summary>Fire OnCompleted exactly once with the batch result, after
        /// resetting the handler so a batch started from the callback begins clean.</summary>
        private void Finish(string undoName, JkrFixBatch<JkrFixAction> batch, RenameResult result, List<string> failReasons)
        {
            if (failReasons.Count > 0)
                result.FailDetails = string.Join("\n", failReasons.Take(5));

            System.Diagnostics.Debug.WriteLine(
                $"[BINA Handler] '{undoName}' completed (one undo step): changes={batch.ChangeCount} " +
                $"elements={batch.ElementCount} renamed={result.Renamed} paramFixed={result.ParamFixed} " +
                $"failed={result.Failed} skipped={result.Skipped} error={result.Error ?? ""}");

            var completed = OnCompleted;
            TransactionGroupName = DefaultUndoName;
            completed?.Invoke(result);
        }

        private static void ApplyRename(Document doc, long elemId, string newName,
                                        RenameResult result, List<string> failReasons)
        {
            try
            {
                var elem = doc.GetElement(ElemIds.From(elemId));
                if (elem == null)
                {
                    result.Skipped++;
                    result.FailedElementIds.Add(elemId);
                    result.FailedFixKeys.Add(RenameResult.MakeFixKey("rename_type", elemId, null));
                    return;
                }

                // Grids and Levels are Elements (not ElementTypes) — rename via their
                // own Name property. Family/loadable types keep the elemType.Name path
                // so shared instances all update.
                if (elem is Grid || elem is Level)
                {
                    elem.Name = newName;
                    result.Renamed++;
                    return;
                }

                ElementId typeId = elem.GetTypeId();
                var elemType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) as ElementType : null;

                if (elemType != null)
                {
                    elemType.Name = newName;
                    result.Renamed++;
                }
                else
                {
                    // Last-ditch: if the element itself has a settable Name, try it.
                    try { elem.Name = newName; result.Renamed++; }
                    catch
                    {
                        result.Skipped++;
                        result.FailedElementIds.Add(elemId);
                        result.FailedFixKeys.Add(RenameResult.MakeFixKey("rename_type", elemId, null));
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                result.Failed++;
                result.FailedElementIds.Add(elemId);
                result.FailedFixKeys.Add(RenameResult.MakeFixKey("rename_type", elemId, null));
                failReasons.Add($"rename {elemId} → '{newName}': {ex.Message}");
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.FailedElementIds.Add(elemId);
                result.FailedFixKeys.Add(RenameResult.MakeFixKey("rename_type", elemId, null));
                failReasons.Add($"rename {elemId} → '{newName}': {ex.Message}");
            }
        }

        public string GetName() => "JKR Auto-Fix Handler";

        // JKR category prefix mapping (from JKR Doc 03/09 spec)
        private static readonly Dictionary<string, string> CategoryPrefixMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Walls", "wll" }, { "Doors", "dor" }, { "Windows", "wdw" },
            { "Floors", "flr" }, { "Ceilings", "clg" }, { "Roofs", "rof" },
            { "Stairs", "str" }, { "Columns", "col" }, { "Structural Columns", "col" },
            { "Railings", "rln" }, { "Specialty Equipment", "seq" },
            { "Mechanical Equipment", "meq" }, { "Electrical Equipment", "eeq" },
            { "Electrical Fixtures", "efx" }, { "Lighting Fixtures", "lfx" },
            { "Plumbing Fixtures", "pfx" }, { "Furniture", "fur" },
            { "Curtain Panels", "cpn" }, { "Curtain Wall Mullions", "cwm" },
            { "Generic Models", "gen" }, { "Structural Framing", "sfr" },
            { "Structural Foundations", "sfd" }, { "Duct Accessories", "dac" },
            { "Pipe Accessories", "pac" }, { "Sprinklers", "spk" },
        };

        // Default subtypes per category (first/most common)
        private static readonly Dictionary<string, string> DefaultSubtype = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Walls", "b" }, { "Doors", "k" }, { "Windows", "b" },
            { "Floors", "k" }, { "Roofs", "k" }, { "Stairs", "k" },
            { "Columns", "k" }, { "Structural Columns", "k" },
        };

        /// <summary>
        /// Generate a JKR-compliant name from category and current type name.
        /// Format: jkr{Disc}_{prefix}-{subtype}_{description}
        /// Example: jkrAR_wll-b_(bb02) Batu Bata
        /// </summary>
        public static string GenerateJkrName(string discipline, string category, string currentTypeName)
        {
            string prefix;
            if (!CategoryPrefixMap.TryGetValue(category, out prefix))
            {
                // Fallback: clean category name
                prefix = Regex.Replace(category.Trim(), @"\s+", "").ToLower();
                if (prefix.Length > 3) prefix = prefix.Substring(0, 3);
            }

            // Subtype
            string subtype = "";
            if (DefaultSubtype.TryGetValue(category, out string sub))
                subtype = $"-{sub}";

            // Clean description from current name
            string desc = currentTypeName ?? "";
            // Strip common non-JKR prefixes
            foreach (var strip in new[] { "Basic Wall", "Basic", "Generic", "Default", "Standard" })
                desc = desc.Replace(strip, "").Trim();

            // Extract material code if present in parentheses like (bb02)
            string matPart = "";
            var matMatch = Regex.Match(desc, @"\(([a-z]{2}\d{0,2})\)", RegexOptions.IgnoreCase);
            if (matMatch.Success)
                matPart = $"_({matMatch.Groups[1].Value.ToLower()})";

            // Clean remaining description
            desc = Regex.Replace(desc, @"\([^)]*\)", "").Trim(); // remove parenthesized parts
            if (desc.Length > 30) desc = desc.Substring(0, 30).Trim();
            if (!string.IsNullOrWhiteSpace(desc))
                desc = $" {desc}";

            return $"jkr{discipline.ToUpper()}_{prefix}{subtype}{matPart}{desc}".Trim();
        }
    }

    public class RenameResult
    {
        public int Renamed { get; set; }
        public int ParamFixed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public string Error { get; set; }
        public string FailDetails { get; set; } = "";
        /// <summary>Element IDs that failed/skipped — kept for callers that only need
        /// element-level granularity. Don't use this when an element can have multiple
        /// fixes (rename + several param fixes) and you need to distinguish which
        /// specific fix failed; use FailedFixKeys instead.</summary>
        public HashSet<long> FailedElementIds { get; set; } = new HashSet<long>();

        /// <summary>Per-fix failure keys — distinguishes "this rename failed" from
        /// "this Sistem param fix failed" on the same element. Format:
        ///   "r:{elementId}"           for rename_type
        ///   "p:{elementId}:{paramName}" for set_parameter
        /// Critical for FixAll bookkeeping: when one of three fixes on element X
        /// fails, the other two should still count as successes (and reverse on
        /// Reset). Element-level FailedElementIds.Contains(X) returns true for all
        /// three and over-flags the survivors.</summary>
        public HashSet<string> FailedFixKeys { get; set; } = new HashSet<string>();

        /// <summary>Compute a fix key matching the format used by FailedFixKeys.</summary>
        public static string MakeFixKey(string action, long elementId, string parameterName)
        {
            if (string.Equals(action, "rename_type", System.StringComparison.OrdinalIgnoreCase))
                return $"r:{elementId}";
            return $"p:{elementId}:{parameterName ?? ""}";
        }
    }
}
