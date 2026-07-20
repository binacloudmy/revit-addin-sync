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

        /// <summary>Label used for the per-chunk Transactions — surfaces in Revit's
        /// undo history. Defaults to "JKR Quick Fix All"; the Reset path overrides
        /// it to "JKR Reset" so the user can tell the two apart in Edit > Undo.</summary>
        public string TransactionGroupName { get; set; } = "JKR Quick Fix All";

        /// <summary>Optional live-progress callback: (done, total). Fired once per
        /// chunk so the UI can animate a progress bar while the batch applies.</summary>
        public Action<int, int> OnProgress { get; set; }

        // ── Chunked-apply state (Idling-driven; only touched on Revit's UI thread) ──
        private enum WorkKind { Rename, Param }
        private struct WorkItem
        {
            public WorkKind Kind;
            public long ElemId;
            public string NewName;
            public JkrFixAction Fix;
        }
        private UIApplication _uiApp;
        private UIDocument _uidoc;
        private Document _doc;
        private View _restoreView;
        private Queue<WorkItem> _work;
        private JkrFixApplicator _applicator;
        private RenameResult _result;
        private List<string> _failReasons;
        private int _done;
        private int _total;
        private bool _finished;
        private bool _inTick;

        // Time budget per Idling tick. Kept well under Windows' ~5s "Not Responding"
        // threshold so the window keeps pumping messages and the progress bar animates.
        private const long SliceMs = 120;

        public void Execute(UIApplication app)
        {
            // Chunked, Idling-driven apply so Revit never blocks long enough to show
            // "(Not Responding)": each Idling tick applies fixes for a short time slice,
            // commits, updates progress, then yields so Windows pumps messages (see
            // OnIdling / ApplyOne / FinishBatch). Reset is unaffected — every committed
            // fix is still counted into _result, which the panel turns into _appliedFixes
            // exactly as the old one-shot path did.
            _result = new RenameResult();
            _failReasons = new List<string>();
            _restoreView = null;
            _uidoc = null;
            _doc = null;
            _uiApp = app;
            _done = 0;
            _total = 0;
            _finished = false;
            _inTick = false;

            try
            {
                _uidoc = app.ActiveUIDocument;
                var doc = _uidoc?.Document;
                if (doc == null)
                {
                    _result.Error = "No active document.";
                    FinishBatch();
                    return;
                }
                _doc = doc;

                if (!RenameQueue.Any() && !ParamFixQueue.Any())
                {
                    FinishBatch();
                    return;
                }

                // Perf guard: if a 3D / rendered / perspective view is active, every
                // model regen forces an expensive re-render. Switch to a lightweight
                // drafting/plan view for the batch; restored in FinishBatch.
                try
                {
                    if (_uidoc.ActiveView is View3D)
                    {
                        var light = FindLightweightView(doc);
                        if (light != null)
                        {
                            _restoreView = _uidoc.ActiveView;
                            _uidoc.ActiveView = light;
                        }
                    }
                }
                catch { _restoreView = null; }

                // One ordered work list: renames first, then param fixes by priority —
                // the same order the old two-phase path used.
                _work = new Queue<WorkItem>();
                foreach (var (elemId, newName) in RenameQueue)
                    _work.Enqueue(new WorkItem { Kind = WorkKind.Rename, ElemId = elemId, NewName = newName });
                foreach (var fix in ParamFixQueue.OrderBy(f => f.Priority))
                    _work.Enqueue(new WorkItem { Kind = WorkKind.Param, Fix = fix });

                _total = _work.Count;
                _applicator = new JkrFixApplicator(doc);

                // Snapshot taken — clear the public queues so a stray re-raise can't
                // double-apply; the batch now runs off _work.
                RenameQueue.Clear();
                ParamFixQueue.Clear();

                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Handler] '{TransactionGroupName}' starting (chunked): items={_total}");
                OnProgress?.Invoke(0, _total);

                // Drive the batch from Idling so the UI thread yields between slices.
                app.Idling += OnIdling;
            }
            catch (Exception ex)
            {
                _result.Error = ex.Message;
                FinishBatch();
            }
        }

        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_finished || _inTick) return;
            _inTick = true;
            try
            {
                var doc = _doc;
                if (doc == null) { _result.Error = "Document is no longer available."; FinishBatch(); return; }

                // Apply a time-boxed slice inside ONE transaction (one regen at commit),
                // then yield. Always processes at least one item so we can't stall.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var tx = new Transaction(doc, TransactionGroupName))
                {
                    tx.Start();
                    while (_work.Count > 0 && sw.ElapsedMilliseconds < SliceMs)
                    {
                        ApplyOne(doc, _work.Dequeue());
                        _done++;
                    }
                    tx.Commit();
                }

                OnProgress?.Invoke(_done, _total);

                if (_work.Count > 0)
                    e.SetRaiseWithoutDelay(); // keep Idling firing back-to-back until drained
                else
                    FinishBatch();
            }
            catch (Exception ex)
            {
                _result.Error = ex.Message;
                FinishBatch();
            }
            finally
            {
                _inTick = false;
            }
        }

        /// <summary>Apply a single work item inside the caller's open Transaction.
        /// Per-item try/catch so one bad fix is recorded as a failure and the batch
        /// keeps going (committed fixes stay reversible via Reset) instead of aborting
        /// the whole run.</summary>
        private void ApplyOne(Document doc, WorkItem item)
        {
            try
            {
                if (item.Kind == WorkKind.Rename)
                {
                    int rB = _result.Renamed, fB = _result.Failed + _result.Skipped;
                    ApplyRename(doc, item.ElemId, item.NewName, _result, _failReasons);
                    if (_result.Renamed > rB)
                        System.Diagnostics.Debug.WriteLine($"[BINA Rename] OK elem={item.ElemId} → '{item.NewName}'");
                    else if (_result.Failed + _result.Skipped > fB)
                        System.Diagnostics.Debug.WriteLine($"[BINA Rename] FAIL elem={item.ElemId} → '{item.NewName}'");
                    return;
                }

                var fix = item.Fix;
                FixResult fixResult;
                // Only fixes that may bind a fresh shared parameter need SubTransaction
                // isolation (so a read-only write failure after a binding leaves no
                // residue). Plain writes run directly in the chunk transaction.
                if (_applicator.FixNeedsIsolation(fix))
                {
                    using (var subTx = new SubTransaction(doc))
                    {
                        subTx.Start();
                        try { fixResult = _applicator.ApplyFixInExistingTx(fix); }
                        catch (Exception ex) { subTx.RollBack(); fixResult = new FixResult { Success = false, Message = ex.Message }; }
                        if (fixResult.Success) subTx.Commit();
                        else subTx.RollBack();
                    }
                }
                else
                {
                    fixResult = _applicator.ApplyFixInExistingTx(fix);
                }

                if (fixResult.Success)
                {
                    _result.ParamFixed++;
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA ParamFix] OK elem={fix.ElementId} param='{fix.ParameterName}' " +
                        $"value='{fix.Value}' (was '{fix.OldValue}') target={fix.Target}");
                }
                else
                {
                    _result.Failed++;
                    _result.FailedElementIds.Add(fix.ElementId);
                    _result.FailedFixKeys.Add(RenameResult.MakeFixKey("set_parameter", fix.ElementId, fix.ParameterName));
                    _failReasons.Add($"{fix.ParameterName} on {fix.ElementId}: {fixResult.Message}");
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA ParamFix] FAIL elem={fix.ElementId} param='{fix.ParameterName}' reason='{fixResult.Message}'");
                }
            }
            catch (Exception ex)
            {
                // Never let one item abort the batch.
                _result.Failed++;
                if (item.Kind == WorkKind.Param && item.Fix != null)
                {
                    _result.FailedElementIds.Add(item.Fix.ElementId);
                    _result.FailedFixKeys.Add(RenameResult.MakeFixKey("set_parameter", item.Fix.ElementId, item.Fix.ParameterName));
                    _failReasons.Add($"{item.Fix.ParameterName} on {item.Fix.ElementId}: {ex.Message}");
                }
                else
                {
                    _result.FailedElementIds.Add(item.ElemId);
                    _failReasons.Add($"rename {item.ElemId}: {ex.Message}");
                }
            }
        }

        /// <summary>Finish the batch: unsubscribe Idling, restore the view, then fire
        /// OnCompleted exactly once with the accumulated result.</summary>
        private void FinishBatch()
        {
            if (_finished) return;
            _finished = true;

            try { if (_uiApp != null) _uiApp.Idling -= OnIdling; } catch { }
            try { if (_restoreView != null && _uidoc != null) _uidoc.ActiveView = _restoreView; } catch { }

            if (_failReasons != null && _failReasons.Count > 0)
                _result.FailDetails = string.Join("\n", _failReasons.Take(5));

            System.Diagnostics.Debug.WriteLine(
                $"[BINA Handler] '{TransactionGroupName}' completed (chunked): " +
                $"renamed={_result.Renamed} paramFixed={_result.ParamFixed} " +
                $"failed={_result.Failed} skipped={_result.Skipped}");

            var completed = OnCompleted;
            var result = _result;

            // Reset transient state before firing the callback so a re-entrant Fix All
            // (or the Reset path) started from within OnCompleted begins clean.
            _work = null;
            _applicator = null;
            _doc = null;
            OnProgress = null; // don't let a stale Fix All progress closure fire during Reset
            RenameQueue.Clear();
            ParamFixQueue.Clear();
            TransactionGroupName = "JKR Quick Fix All";

            completed?.Invoke(result);
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
