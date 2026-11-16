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

        /// <summary>The model the fixes were found in (full path). When set, a batch is
        /// refused if another model is active: the element ids belong to that model.</summary>
        public string ExpectedDocPath { get; set; } = "";

        /// <summary>Same model file? (case-insensitive full path; an unsaved model has no path to check).</summary>
        public static bool SameModel(string expected, string active) =>
            string.IsNullOrEmpty(expected)
            || string.Equals(System.IO.Path.GetFullPath(expected), System.IO.Path.GetFullPath(string.IsNullOrEmpty(active) ? "." : active),
                             StringComparison.OrdinalIgnoreCase);

        public void Execute(UIApplication app)
        {
            var result = new RenameResult();
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
                    result.Error = RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.NoDocument();
                else if (!SameModel(ExpectedDocPath, doc.PathName))
                    result.Error = RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.WrongModel(System.IO.Path.GetFileName(ExpectedDocPath));
                else if (!batch.IsEmpty)
                {
                    // Perf guard (kept from the original one-shot Fix All): with a 3D /
                    // rendered view active, regenerations during the batch force
                    // expensive re-renders. Switch to a lightweight view for the batch
                    // and switch back afterwards. Must happen outside any transaction.
                    restoreView = SwitchAwayFrom3D(uidoc);
                    ApplyBatch(doc, batch, undoName, result);
                }
            }
            catch (Exception ex)
            {
                // The using blocks in ApplyBatch already rolled the group back.
                result.Error = ex.Message;
                result.ClearApplied();
            }
            finally
            {
                try { if (restoreView != null) uidoc.ActiveView = restoreView; } catch { }
            }

            Finish(undoName, batch, result);
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

        /// <summary>
        /// All steps in one Transaction inside one TransactionGroup (one undo step). A
        /// step that fails when it runs is recorded and the batch continues.
        ///
        /// Revit can also refuse a change only when the transaction commits (an
        /// error-level failure found while regenerating). That rolls back the whole
        /// transaction, so one bad fix used to discard every other fix in the batch.
        /// Now such a commit is caught without Revit's error dialog, and the batch is
        /// retried inside the same group:
        ///   1. without the steps on the elements Revit named in the failure, in one
        ///      transaction (fast; the usual case);
        ///   2. if that fails too, or Revit named no element we changed, step by
        ///      step, each in its own transaction — a step Revit refuses is recorded
        ///      as failed with Revit's reason, every other step is kept.
        /// The group is still assimilated as one undo step. If no step changed
        /// anything, the group is rolled back, so no empty undo entry is left.
        /// </summary>
        private static void ApplyBatch(Document doc, JkrFixBatch<JkrFixAction> batch, string undoName,
                                       RenameResult result)
        {
            var applicator = new JkrFixApplicator(doc);
            using (var group = new TransactionGroup(doc, undoName))
            {
                group.Start();
                var steps = batch.Steps.ToList();
                var first = RunSteps(doc, applicator, steps, undoName);
                if (first.Committed)
                {
                    result.Absorb(first.Result);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA Handler] commit refused ({first.Status}): {string.Join(" | ", first.Errors)} — retrying without the failing steps");
                    var (suspects, rest) = JkrBatchRetry.Split(steps, s => s.ElementId, first.FailingIds);
                    var restDone = false;
                    if (suspects.Count > 0 && rest.Count > 0)
                    {
                        var second = RunSteps(doc, applicator, rest, undoName);
                        if (second.Committed) { result.Absorb(second.Result); restDone = true; }
                    }
                    foreach (var step in restDone ? suspects : steps)
                    {
                        var one = RunSteps(doc, applicator, new List<JkrFixBatch<JkrFixAction>.Step> { step }, undoName);
                        if (one.Committed)
                            result.Absorb(one.Result);
                        else
                            result.RecordFailure(KeyOf(step), step.ElementId, WhatOf(step),
                                                 JkrBatchRetry.CommitReason(one.Errors, one.Status.ToString()));
                    }
                }

                if (result.Renamed + result.ParamFixed == 0)
                    group.RollBack();
                else
                    group.Assimilate();
            }
        }

        private sealed class StepsRun
        {
            public bool Committed;
            public TransactionStatus Status;
            public RenameResult Result = new RenameResult();
            public List<string> Errors = new List<string>();
            public HashSet<long> FailingIds = new HashSet<long>();
        }

        /// <summary>Apply steps in one transaction. Error-level failures at commit are
        /// collected (no Revit dialog) and the transaction rolls back.</summary>
        private static StepsRun RunSteps(Document doc, JkrFixApplicator applicator,
                                         List<JkrFixBatch<JkrFixAction>.Step> steps, string name)
        {
            var run = new StepsRun();
            var collector = new CommitFailureCollector();
            using (var tx = new Transaction(doc, name))
            {
                var options = tx.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(collector);
                options.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(options);
                tx.Start();
                foreach (var step in steps)
                    ApplyOne(doc, applicator, step, run.Result);
                run.Status = tx.Commit();
                if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            }
            run.Committed = run.Status == TransactionStatus.Committed;
            run.Errors.AddRange(collector.Errors);
            run.FailingIds.UnionWith(collector.ElementIds);
            return run;
        }

        /// <summary>Keeps warnings as Revit normally shows them; on an error, records
        /// the message and the elements involved and rolls back without a dialog.</summary>
        private sealed class CommitFailureCollector : IFailuresPreprocessor
        {
            public readonly List<string> Errors = new List<string>();
            public readonly HashSet<long> ElementIds = new HashSet<long>();

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var error = false;
                foreach (var f in accessor.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) continue;
                    error = true;
                    var text = f.GetDescriptionText();
                    if (!string.IsNullOrWhiteSpace(text) && !Errors.Contains(text)) Errors.Add(text);
                    foreach (var id in f.GetFailingElementIds()) ElementIds.Add(IdValue(id));
                }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }

        private static long IdValue(ElementId id)
        {
#if REVIT2023_24
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        private static string KeyOf(JkrFixBatch<JkrFixAction>.Step step) =>
            step.IsRename
                ? RenameResult.MakeFixKey("rename_type", step.ElementId, null)
                : RenameResult.MakeFixKey("set_parameter", step.Fix.ElementId, step.Fix.ParameterName);

        private static string WhatOf(JkrFixBatch<JkrFixAction>.Step step) =>
            step.IsRename
                ? RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.RenameWhat(step.ElementId, step.NewName)
                : RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.ParamWhat(step.Fix.ParameterName, step.Fix.ElementId);

        /// <summary>Apply one step inside the caller's open Transaction. Per-step
        /// try/catch so one bad fix is recorded as a failure and the batch keeps going.
        ///
        /// No partly applied fix is left behind:
        ///  - A rename, or a write to a parameter the element already has, is ONE Revit
        ///    API call (Name = … or Parameter.Set). It either changes the model or
        ///    throws / returns false with the model unchanged, so it runs directly in
        ///    the batch transaction. (A SubTransaction per fix meant a regeneration per
        ///    fix and froze Revit on large batches — commit 9d15e3a.)
        ///  - A fix that may first bind a shared parameter makes two changes (bind,
        ///    then write), so it runs in its own SubTransaction that is rolled back on
        ///    failure. Bindings are not undone by a SubTransaction rollback, so
        ///    JkrFixApplicator removes a binding it placed when the write then fails.</summary>
        private static void ApplyOne(Document doc, JkrFixApplicator applicator, JkrFixBatch<JkrFixAction>.Step step,
                                     RenameResult result)
        {
            if (step.IsRename)
            {
                var key = RenameResult.MakeFixKey("rename_type", step.ElementId, null);
                var what = RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.RenameWhat(step.ElementId, step.NewName);
                try
                {
                    var reason = TryRename(doc, step.ElementId, step.NewName, out var skipped);
                    if (reason == null)
                    {
                        result.Renamed++;
                        System.Diagnostics.Debug.WriteLine($"[BINA Rename] OK elem={step.ElementId} → '{step.NewName}'");
                    }
                    else
                    {
                        result.RecordFailure(key, step.ElementId, what, reason, skipped);
                        System.Diagnostics.Debug.WriteLine($"[BINA Rename] FAIL elem={step.ElementId} → '{step.NewName}' reason='{reason}'");
                    }
                }
                catch (Exception ex)
                {
                    result.RecordFailure(key, step.ElementId, what, ex.Message);
                }
                return;
            }

            var fix = step.Fix;
            var fixKey = RenameResult.MakeFixKey("set_parameter", fix.ElementId, fix.ParameterName);
            var fixWhat = RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.ParamWhat(fix.ParameterName, fix.ElementId);
            try
            {
                FixResult fixResult;
                if (applicator.FixNeedsIsolation(fix))
                {
                    using (var subTx = new SubTransaction(doc))
                    {
                        subTx.Start();
                        try { fixResult = applicator.ApplyFixInExistingTx(fix); }
                        catch (Exception ex) { fixResult = new FixResult { Success = false, Message = ex.Message }; }

                        if (fixResult.Success)
                        {
                            var committed = subTx.Commit();
                            if (committed != TransactionStatus.Committed)
                                fixResult = new FixResult { Success = false, Message = $"Revit did not accept this change ({committed})." };
                        }
                        if (subTx.HasStarted() && !subTx.HasEnded()) subTx.RollBack();
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
                    result.RecordFailure(fixKey, fix.ElementId, fixWhat, fixResult.Message);
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA ParamFix] FAIL elem={fix.ElementId} param='{fix.ParameterName}' reason='{fixResult.Message}'");
                }
            }
            catch (Exception ex)
            {
                // Never let one step abort the batch.
                result.RecordFailure(fixKey, fix.ElementId, fixWhat, ex.Message);
            }
        }

        /// <summary>Most failure lines listed in the post-batch summary dialog.</summary>
        public const int MaxListedFailures = 20;

        /// <summary>Fire OnCompleted exactly once with the batch result, after
        /// resetting the handler so a batch started from the callback begins clean.</summary>
        private void Finish(string undoName, JkrFixBatch<JkrFixAction> batch, RenameResult result)
        {
            result.FailDetails = result.BuildFailDetails(MaxListedFailures);

            System.Diagnostics.Debug.WriteLine(
                $"[BINA Handler] '{undoName}' completed (one undo step): changes={batch.ChangeCount} " +
                $"elements={batch.ElementCount} renamed={result.Renamed} paramFixed={result.ParamFixed} " +
                $"failed={result.Failed} skipped={result.Skipped} error={result.Error ?? ""}");

            var completed = OnCompleted;
            TransactionGroupName = DefaultUndoName;
            ExpectedDocPath = "";
            try { completed?.Invoke(result); }
            catch (Exception ex)
            {
                // The pane's callback runs inline on Revit's thread: never let it escape Execute.
                System.Diagnostics.Debug.WriteLine($"[BINA Handler] completion callback failed: {ex}");
            }
        }

        /// <summary>Rename one element's type (or a Grid/Level itself). One API write:
        /// returns null when it changed the model, else why not (model unchanged).
        /// <paramref name="skipped"/> is true when there was nothing to rename.</summary>
        private static string TryRename(Document doc, long elemId, string newName, out bool skipped)
        {
            skipped = false;
            if (string.IsNullOrEmpty(newName))
            {
                skipped = true;
                return "Rename target is empty.";
            }

            var elem = doc.GetElement(ElemIds.From(elemId));
            if (elem == null)
            {
                skipped = true;
                return $"Element {elemId} was not found in the model.";
            }

            try
            {
                // Grids and Levels are Elements (not ElementTypes) — rename via their
                // own Name property. Family/loadable types keep the elemType.Name path
                // so shared instances all update.
                if (elem is Grid || elem is Level || elem is ElementType)
                {
                    elem.Name = newName;
                    return null;
                }

                ElementId typeId = elem.GetTypeId();
                var elemType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) as ElementType : null;
                if (elemType != null)
                {
                    elemType.Name = newName;
                    return null;
                }

                // Last-ditch: if the element itself has a settable Name, try it.
                elem.Name = newName;
                return null;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                // Typically: the name is already used by another type.
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
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

}
