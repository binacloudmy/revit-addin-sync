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
    /// Applies JKR compliance fixes (renames + parameter sets) via ExternalEvent.
    /// Runs on Revit's main thread.
    /// </summary>
    public class JkrRenameHandler : IExternalEventHandler
    {
        /// <summary>
        /// List of (ElementId, newName) pairs to rename.
        /// </summary>
        public List<(long ElementId, string NewName)> RenameQueue { get; set; } = new List<(long, string)>();

        /// <summary>
        /// List of parameter fixes to apply after renames.
        /// </summary>
        public List<JkrFixAction> ParamFixQueue { get; set; } = new List<JkrFixAction>();

        public Action<RenameResult> OnCompleted { get; set; }

        /// <summary>Label used for the outer TransactionGroup — surfaces in Revit's
        /// undo history. Defaults to "JKR Quick Fix All"; the Reset path overrides
        /// it to "JKR Reset" so the user can tell the two apart in Edit > Undo.</summary>
        public string TransactionGroupName { get; set; } = "JKR Quick Fix All";

        public void Execute(UIApplication app)
        {
            var result = new RenameResult();
            var failReasons = new List<string>();
            UIDocument uidoc = null;
            View restoreView = null;
            try
            {
                uidoc = app.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null)
                {
                    result.Error = "No active document.";
                    OnCompleted?.Invoke(result);
                    return;
                }

                // Perf guard: a Fix All batch regenerates the model many times. If a
                // 3D / rendered / perspective view is active, every regen forces an
                // expensive re-render and Revit hangs ("Not Responding"). Switch to a
                // lightweight drafting/plan view for the duration; restored in finally.
                try
                {
                    if (uidoc.ActiveView is View3D)
                    {
                        var light = FindLightweightView(doc);
                        if (light != null)
                        {
                            restoreView = uidoc.ActiveView;
                            uidoc.ActiveView = light;
                        }
                    }
                }
                catch { restoreView = null; }

                if (!RenameQueue.Any() && !ParamFixQueue.Any())
                {
                    OnCompleted?.Invoke(result);
                    return;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Handler] '{TransactionGroupName}' starting: " +
                    $"renames={RenameQueue.Count} paramFixes={ParamFixQueue.Count}");

                // Wrap renames + param fixes in a single TransactionGroup so the user
                // gets ONE undo step for the whole "Quick Fix All" batch instead of
                // 100+ entries. Assimilate() collapses the inner transactions on
                // success; RollBack() unwinds everything if we hit a fatal error.
                using (var tg = new TransactionGroup(doc, TransactionGroupName))
                {
                    tg.Start();
                    try
                    {
                        // Phase 1: Renames — batched into one inner Transaction.
                        if (RenameQueue.Any())
                        {
                            using (var tx = new Transaction(doc, "JKR Renames"))
                            {
                                tx.Start();
                                int renamesBefore = result.Renamed;
                                int failsBefore = result.Failed + result.Skipped;
                                foreach (var (elemId, newName) in RenameQueue)
                                {
                                    int rB = result.Renamed, fB = result.Failed + result.Skipped;
                                    ApplyRename(doc, elemId, newName, result, failReasons);
                                    if (result.Renamed > rB)
                                        System.Diagnostics.Debug.WriteLine(
                                            $"[BINA Rename] OK elem={elemId} → '{newName}'");
                                    else if (result.Failed + result.Skipped > fB)
                                        System.Diagnostics.Debug.WriteLine(
                                            $"[BINA Rename] FAIL elem={elemId} → '{newName}'");
                                }
                                tx.Commit();
                                System.Diagnostics.Debug.WriteLine(
                                    $"[BINA Handler] Phase 1 done: renamed={result.Renamed - renamesBefore} " +
                                    $"failed={(result.Failed + result.Skipped) - failsBefore}");
                            }
                        }

                        // Phase 2: Parameter fixes — batched into one inner Transaction,
                        // sorted by priority so classification params land before
                        // material params and rename-derived fixes.
                        if (ParamFixQueue.Any())
                        {
                            var applicator = new JkrFixApplicator(doc);
                            using (var tx = new Transaction(doc, "JKR Parameter Fixes"))
                            {
                                tx.Start();
                                foreach (var fix in ParamFixQueue.OrderBy(f => f.Priority))
                                {
                                    // Only fixes that may bind a fresh shared parameter need
                                    // per-fix SubTransaction isolation — so a read-only write
                                    // failure AFTER a binding leaves no residue (which would
                                    // otherwise downgrade the next scan's rule from "value
                                    // invalid" to "empty parameter" and silently drop the
                                    // element from FixableCount post-Reset). Plain writes on
                                    // already-present params commit with the outer Transaction,
                                    // so Revit regenerates ONCE at tx.Commit instead of once per
                                    // fix — the main Fix All speed-up. Bonus: the first fix that
                                    // binds a (param, category) makes it present, so every later
                                    // fix for that param takes the fast direct-write path.
                                    FixResult fixResult;
                                    if (applicator.FixNeedsIsolation(fix))
                                    {
                                        using (var subTx = new SubTransaction(doc))
                                        {
                                            subTx.Start();
                                            try
                                            {
                                                fixResult = applicator.ApplyFixInExistingTx(fix);
                                            }
                                            catch
                                            {
                                                subTx.RollBack();
                                                throw;
                                            }
                                            if (fixResult.Success) subTx.Commit();
                                            else subTx.RollBack();
                                        }
                                    }
                                    else
                                    {
                                        // No binding possible → nothing to unwind on failure →
                                        // no SubTransaction. Runs in the outer Transaction.
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
                                            $"[BINA ParamFix] FAIL elem={fix.ElementId} param='{fix.ParameterName}' " +
                                            $"reason='{fixResult.Message}'");
                                    }
                                }
                                tx.Commit();
                            }
                        }

                        // Collapse all inner transactions into a single undo step.
                        tg.Assimilate();
                    }
                    catch
                    {
                        // Unwind the whole batch on a fatal error so the model isn't
                        // left semi-fixed (the previous two-Transaction layout could
                        // commit renames then fail params, leaving partial state).
                        if (tg.HasStarted() && !tg.HasEnded()) tg.RollBack();
                        throw;
                    }
                }

                if (failReasons.Count > 0)
                    result.FailDetails = string.Join("\n", failReasons.Take(5));

                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Handler] '{TransactionGroupName}' completed: " +
                    $"renamed={result.Renamed} paramFixed={result.ParamFixed} " +
                    $"failed={result.Failed} skipped={result.Skipped}");
                if (failReasons.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA Handler] failure reasons ({failReasons.Count}):");
                    foreach (var r in failReasons)
                        System.Diagnostics.Debug.WriteLine($"  • {r}");
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                // Restore the user's original view (re-renders once, not per-fix).
                if (restoreView != null && uidoc != null)
                {
                    try { uidoc.ActiveView = restoreView; } catch { }
                }
                OnCompleted?.Invoke(result);
                RenameQueue.Clear();
                ParamFixQueue.Clear();
                // Reset to default so a stale Reset-label doesn't leak into the
                // next Fix All if the caller forgets to set it.
                TransactionGroupName = "JKR Quick Fix All";
            }
        }

        /// <summary>Find a cheap-to-activate view (empty drafting view, else a plan)
        /// to host a Fix All batch so model regenerations don't re-render a 3D view.</summary>
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

        private static void ApplyRename(Document doc, long elemId, string newName,
                                        RenameResult result, List<string> failReasons)
        {
            try
            {
                var elem = doc.GetElement(new ElementId(elemId));
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
