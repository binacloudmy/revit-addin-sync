#nullable enable
// Revit-bound half of run_revit_code: the Revit references, Print formatting
// of Revit values, and the ways a prepared snippet runs (spec §7.8).
//
//   Read:     always CallbackScope(Read) + a TransactionGroup that is always
//             rolled back, with NO transaction open, so Revit itself refuses
//             any model change (ruling R59). The DocumentChanged recorder is
//             the check: anything recorded fails the call (policy_rejected).
//             UI requests (view, zoom, selection) were queued by the host shims
//             and are applied after the rollback; ids that no longer exist are
//             dropped and reported. A transaction already open means someone
//             else's change: refused (W3 opens nothing for reads, ruling R53).
//   Write:    with no transaction open (the legacy path, or a runner that opens
//             only the group): one TxGuard transaction, DocumentChanged changes,
//             noop with an inline warning, exists/count read-back on commit.
//             Inside W3's own transaction (its Preview/Commit scope): run in it,
//             measure added/deleted ids around the run, leave the rest to the
//             runner's record. UI requests apply on commit, never in a preview.
// The only commit call is TxGuard.CommitOrThrow (the copilot fence allows no other).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using BinaVibe.Copilot.Guard;
using BinaVibe.Mcp.Tools;
using Microsoft.CodeAnalysis;

namespace BinaVibe.Copilot.Code
{
    internal static class RevitCodeHost
    {
        private const string ReadGroupName = "BINA: read code";
        private const string WriteTransactionName = "BINA: run code";

        /// <summary>The narrow reference set with the RUNNING Revit's API assemblies, so the compiler
        /// catches members this Revit version does not have.</summary>
        public static IReadOnlyList<MetadataReference> References() =>
            CodeReferences.Build(new[] { typeof(Document).Assembly.Location, typeof(UIDocument).Assembly.Location });

        public static PreparedCode Prepare(string code) => CodeRunner.Prepare(code, References(), WrapperProfile.Revit);

        public static OutputBuffer NewOutput() => new OutputBuffer(special: FormatRevit);

        private static object?[] HostArgs(Document doc, UIDocument? uidoc) =>
            new object?[] { doc, uidoc, uidoc?.ActiveView ?? doc.ActiveView };

        // ---- routing ------------------------------------------------------------------------------

        /// <summary>Runs read-classified code; phase read.</summary>
        public static Envelope Read(Document doc, UIDocument? uidoc, RunRevitCodeArgs args, PreparedCode prepared)
        {
            // Our own read group cannot start inside a transaction, and running inside someone else's
            // would let read code change what they then commit.
            if (doc.IsModifiable) return Busy("another change is open in this document", EnvelopePhase.Read);
            return RunRead(doc, uidoc, args, prepared);
        }

        /// <summary>Runs write-classified code; phase preview or commit.</summary>
        public static Envelope Write(Document doc, UIDocument? uidoc, IHandleStore? handles, RunRevitCodeArgs args,
            PreparedCode prepared, WritePhase phase)
        {
            if (!doc.IsModifiable) return RunWrite(doc, uidoc, handles, args, prepared, phase);
            // W3's engine opens its own TxGuard transaction around Execute, inside its Preview/Commit scope.
            var scope = CallbackScope.Current(doc);
            if (scope == CallbackKind.Preview || scope == CallbackKind.Commit)
                return RunWriteHosted(doc, uidoc, handles, args, prepared, phase);
            return Busy("another change is open in this document", EnvelopePhase.Of(phase));
        }

        /// <summary>Inside the runner's transaction: run, and measure added/deleted ids around the run
        /// (DocumentChanged only fires when the runner commits, so it decorates edits and noop).</summary>
        private static Envelope RunWriteHosted(Document doc, UIDocument? uidoc, IHandleStore? handles, RunRevitCodeArgs args,
            PreparedCode prepared, WritePhase phase)
        {
            var phaseName = EnvelopePhase.Of(phase);
            if (prepared.Snippet == null) return prepared.Failure!.ToEnvelope(phaseName);

            var output = NewOutput();
            var (uiHook, uiQueue) = UiCollector();
            var before = AllElementIds(doc);
            var run = CodeRunner.Execute(prepared.Snippet, HostArgs(doc, uidoc), LoopBudget.FromPolicy(), output, readMode: false, ui: uiHook);
            if (!run.Ok) return WithOutput(run.Failure!, output).ToEnvelope(phaseName); // not ok: the runner rolls back

            var after = AllElementIds(doc);
            var added = after.Where(id => !before.Contains(id)).OrderBy(id => id).ToList();
            var deleted = before.Where(id => !after.Contains(id)).OrderBy(id => id).ToList();
            var touched = new ChangesDto
            {
                Added = added.Select(id => new ElementRef(id, null)).ToList(),
                Deleted = deleted.Select(id => new ElementRef(id, null)).ToList(),
            };
            var capped = CodeEnvelopes.Cap(touched, out var total);
            string? handle = null;
            if (total > EnvelopeLimits.MaxRows && handles != null)
                handle = handles.Put(added.Concat(deleted).Select(id => new ElementRef(id, null)).ToList(), "run_revit_code: " + args.Intent);

            VerifiedDto? verified = null;
            if (phase == WritePhase.Commit)
            {
                var measured = new ChangeSet { Added = added, Deleted = deleted };
                verified = added.Count + deleted.Count > 0
                    ? Verify(doc, measured)
                    // Only edits: what was checked is that no element appeared or vanished.
                    : new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = before.Count };
            }
            var ui = UiForWrite(doc, uidoc, uiQueue, phase);
            return CodeEnvelopes.HostedWrite(phase, args.Intent, output, run, capped, added.Count, deleted.Count, args.Expected, verified, handle, ui.Data, ui.Warnings);
        }

        /// <summary>Every element and element type id in the document.</summary>
        private static HashSet<long> AllElementIds(Document doc)
        {
            var all = new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true));
            var ids = new HashSet<long>();
            foreach (var id in new FilteredElementCollector(doc).WherePasses(all).ToElementIds()) ids.Add(id.Value);
            return ids;
        }

        // ---- read ---------------------------------------------------------------------------------

        private static Envelope RunRead(Document doc, UIDocument? uidoc, RunRevitCodeArgs args, PreparedCode prepared)
        {
            if (prepared.Snippet == null) return prepared.Failure!.ToEnvelope(EnvelopePhase.Read);

            var output = NewOutput();
            var (uiHook, uiQueue) = UiCollector();
            CodeRunResult run;
            ChangeSet recorded;
            var rolledBack = false;
            using (CallbackScope.Enter(doc, CallbackKind.Read))
            using (var recording = ChangeRecorder.Start(doc))
            using (var group = new TransactionGroup(doc, ReadGroupName))
            {
                // A read-only document cannot change at all; anything else runs inside the group.
                if (!doc.IsReadOnly)
                {
                    try
                    {
                        group.Start();
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        return Busy(ex, EnvelopePhase.Read);
                    }
                }
                try
                {
                    run = CodeRunner.Execute(prepared.Snippet, HostArgs(doc, uidoc), LoopBudget.FromPolicy(), output, readMode: true, ui: uiHook);
                    // Stop BEFORE the rollback: the recorder nets a rolled-back group to nothing.
                    recorded = recording.Stop();
                }
                finally
                {
                    if (group.HasStarted() && !group.HasEnded())
                    {
                        group.RollBack();
                        rolledBack = true;
                    }
                }
            }

            if (recorded.Added.Count + recorded.Modified.Count + recorded.Deleted.Count > 0)
            {
                // The policy leaves read code no way to commit; if the recorder still saw a change, fail loudly
                // and say only what is true about the rollback.
                var undone = rolledBack
                    ? "The run was rolled back, so nothing was kept."
                    : "The document is read-only here; check it before going on.";
                return new CodeFailure
                {
                    Code = ErrorCodes.PolicyRejected,
                    Summary = "The code changed the model during a read-only run. " + undone,
                    Message = "run_revit_code ran this as a read, but the model changed while it ran. " + undone
                              + " Pass expected (adds, modifies or deletes) so the change is previewed and approved.",
                    Next = "Retry with expected set, so the drafter can approve the change.",
                    Data = new Dictionary<string, object?> { ["mode"] = CodePlans.Read, ["rolled_back"] = rolledBack },
                }.ToEnvelope(EnvelopePhase.Read);
            }

            if (!run.Ok) return WithOutput(run.Failure!, output).ToEnvelope(EnvelopePhase.Read);
            // After the rollback: the view, zoom and selection the code asked for (ruling R59).
            var ui = ApplyUi(doc, uidoc, uiQueue);
            return CodeEnvelopes.Read(args.Intent, output, run, ui.Data, ui.Warnings);
        }

        // ---- UI requests (ruling R59) ---------------------------------------------------------------

        private static (Action<int, object?> Hook, UiRequestQueue Queue) UiCollector()
        {
            var queue = new UiRequestQueue();
            // Ids are copied now: the code may change its list after asking.
            return ((kind, value) => queue.Add(kind, IdsOf(value)), queue);
        }

        private static List<long> IdsOf(object? value)
        {
            switch (value)
            {
                case Element e: return new List<long> { e.Id.Value };
                case ElementId id: return new List<long> { id.Value };
                case IEnumerable<ElementId> ids: return ids.Where(i => i != null).Select(i => (long)i.Value).ToList();
                default: return new List<long>();
            }
        }

        private sealed class UiOutcome
        {
            public Dictionary<string, object?>? Data;
            public List<WarningDto> Warnings = new List<WarningDto>();
        }

        /// <summary>A write applies its UI requests once, on commit; a preview only says what it will do.</summary>
        private static UiOutcome UiForWrite(Document doc, UIDocument? uidoc, UiRequestQueue queue, WritePhase phase)
        {
            if (queue.Count == 0) return new UiOutcome();
            if (phase == WritePhase.Commit) return ApplyUi(doc, uidoc, queue);
            return new UiOutcome { Data = new Dictionary<string, object?> { ["applied"] = false, ["requests"] = queue.Count, ["when"] = "on commit" } };
        }

        private static UiOutcome ApplyUi(Document doc, UIDocument? uidoc, UiRequestQueue queue)
        {
            var outcome = new UiOutcome();
            if (queue.Count == 0) return outcome;
            var plan = queue.Plan(id => doc.GetElement(ElemIds.From(id)) != null, id => doc.GetElement(ElemIds.From(id)) is View);
            outcome.Data = plan.ToData(applied: uidoc != null);
            if (uidoc == null)
            {
                outcome.Warnings.Add(new WarningDto { Text = "There is no open view to apply the code's view, zoom or selection requests to.", Severity = WarningSeverity.Warning });
                return outcome;
            }
            Try(outcome, "switch view", () =>
            {
                if (plan.ViewId is long viewId && doc.GetElement(ElemIds.From(viewId)) is View target) uidoc.RequestViewChange(target);
            });
            Try(outcome, "zoom to elements", () =>
            {
                if (plan.Show.Length > 0) uidoc.ShowElements(plan.Show.Select(ElemIds.From).ToList());
            });
            Try(outcome, "select elements", () =>
            {
                if (plan.HasSelection) uidoc.Selection.SetElementIds(plan.Select.Select(ElemIds.From).ToList());
            });
            if (plan.Dropped.Length > 0)
                outcome.Warnings.Add(new WarningDto
                {
                    Text = plan.Dropped.Length + " element(s) the code asked to open, show or select no longer exist after the run; they were skipped.",
                    Severity = WarningSeverity.Warning,
                    Ids = plan.Dropped.Take(EnvelopeLimits.MaxRows).ToList(),
                });
            return outcome;
        }

        private static void Try(UiOutcome outcome, string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(new WarningDto { Text = "Could not " + what + ": " + ex.Message, Severity = WarningSeverity.Warning });
            }
        }

        // ---- write --------------------------------------------------------------------------------

        private static Envelope RunWrite(Document doc, UIDocument? uidoc, IHandleStore? handles, RunRevitCodeArgs args,
            PreparedCode prepared, WritePhase phase)
        {
            var phaseName = EnvelopePhase.Of(phase);
            if (prepared.Snippet == null) return prepared.Failure!.ToEnvelope(phaseName);

            var output = NewOutput();
            var (uiHook, uiQueue) = UiCollector();
            CodeRunResult run;
            ChangeSet changes;
            IReadOnlyList<WarningDto> warnings;
            var outerLog = FailureLog.Current;
            using (FailureLog.Begin())
            using (FailureScope.Begin(doc.Application))
            {
                using (var recording = ChangeRecorder.Start(doc))
                {
                    using (var tx = new Transaction(doc, WriteTransactionName))
                    {
                        try
                        {
                            TxGuard.StartSwallowing(tx);
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                        {
                            return Busy(ex, phaseName);
                        }
                        try
                        {
                            run = CodeRunner.Execute(prepared.Snippet, HostArgs(doc, uidoc), LoopBudget.FromPolicy(), output, readMode: false, ui: uiHook);
                            if (!run.Ok) return WithOutput(run.Failure!, output).ToEnvelope(phaseName); // finally rolls back
                            try
                            {
                                TxGuard.CommitOrThrow(tx);
                            }
                            catch (Exception ex)
                            {
                                return new CodeFailure
                                {
                                    Code = ErrorCodes.RolledBack,
                                    Summary = "Revit rejected the change and rolled it back: " + ex.Message,
                                    Message = "Revit rolled the change back: " + ex.Message + " Nothing was changed.",
                                    Next = "Fix the cause Revit reports, then retry.",
                                    Data = new Dictionary<string, object?> { ["output"] = output.Text },
                                }.ToEnvelope(phaseName);
                            }
                        }
                        finally
                        {
                            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        }
                    }
                    changes = recording.Stop();
                }
                warnings = FailureLog.Current.Snapshot();
            }
            // Nested logs do not merge: hand this call's failures to the batch's log too.
            foreach (var w in warnings) outerLog.Add(w.Text, w.Severity, w.DefinitionId, w.Ids);

            var counts = new ChangeCounts(changes.Added.Count, changes.Modified.Count, changes.Deleted.Count);
            var allIds = changes.Added.Concat(changes.Modified).Concat(changes.Deleted).Distinct().ToList();
            var (touched, sideEffects) = ChangeSetMath.Split(changes, allIds, Array.Empty<long>());
            var capped = CodeEnvelopes.Cap(touched, out var total);
            string? handle = null;
            if (total > EnvelopeLimits.MaxRows && handles != null)
                handle = handles.Put(allIds.Select(id => new ElementRef(id, null)).ToList(), "run_revit_code: " + args.Intent);

            var verified = phase == WritePhase.Commit ? Verify(doc, changes) : null;
            var fault = verified != null && verified.Mismatches.Count > 0
                ? verified.Mismatches.Count + " element(s) are not as the code left them after the commit: inspect them before going on."
                : null;
            var ui = UiForWrite(doc, uidoc, uiQueue, phase);
            return CodeEnvelopes.Write(phase, args.Intent, output, run, capped, sideEffects, counts, args.Expected, verified, handle,
                warnings.Concat(ui.Warnings).ToList(), fault, ui.Data);
        }

        /// <summary>Added elements exist and deleted ones are gone; with only modifications, the count delta.</summary>
        private static VerifiedDto Verify(Document doc, ChangeSet changes)
        {
            if (changes.Added.Count + changes.Deleted.Count == 0)
                return new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = changes.Modified.Count };

            var mismatches = new List<MismatchDto>();
            foreach (var id in changes.Added)
                if (doc.GetElement(ElemIds.From(id)) == null)
                    mismatches.Add(new MismatchDto { Id = id, Field = "exists", Expected = "added", Actual = "missing" });
            foreach (var id in changes.Deleted)
                if (doc.GetElement(ElemIds.From(id)) != null)
                    mismatches.Add(new MismatchDto { Id = id, Field = "exists", Expected = "deleted", Actual = "present" });
            return new VerifiedDto
            {
                Method = VerifyMethods.Exists,
                Checked = changes.Added.Count + changes.Deleted.Count,
                Mismatches = mismatches.Take(EnvelopeLimits.MaxRows).ToList(),
            };
        }

        /// <summary>What the code printed before it failed helps the model find the problem.</summary>
        private static CodeFailure WithOutput(CodeFailure failure, OutputBuffer output)
        {
            if (output.Lines > 0) failure.Data["output"] = output.Text;
            return failure;
        }

        private static Envelope Busy(Exception ex, string phase) => Busy(ex.Message, phase);

        private static Envelope Busy(string why, string phase) =>
            EnvelopeBuilder.Fail(ErrorCodes.RevitBusy,
                "Revit cannot start a change right now (" + why + "). Finish or cancel the current edit mode or dialog, then ask again.",
                null, CodeEnvelopes.WritePhaseOf(phase));

        // ---- Print(obj) of Revit values ---------------------------------------------------------

        private static string? FormatRevit(object value)
        {
            try
            {
                switch (value)
                {
                    case Element e:
                        if (!e.IsValidObject) return "(deleted element)";
                        var name = string.IsNullOrEmpty(e.Name) ? e.GetType().Name : e.Name;
                        return name + " [" + (e.Category?.Name ?? e.GetType().Name) + " " + e.Id.Value.ToString(CultureInfo.InvariantCulture) + "]";
                    case ElementId id:
                        return id.Value.ToString(CultureInfo.InvariantCulture);
                    case XYZ p:
                        return "(" + F(p.X) + ", " + F(p.Y) + ", " + F(p.Z) + ")";
                    case Parameter p:
                        return (p.Definition?.Name ?? "?") + " = " + (p.AsValueString() ?? p.AsString() ?? "");
                    case Category c:
                        return c.Name;
                    case ForgeTypeId f:
                        return f.TypeId;
                    default:
                        return null;
                }
            }
            catch
            {
                return value.GetType().Name;
            }
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
