#nullable enable
// Revit-bound half of run_revit_code: the Revit references, Print formatting
// of Revit values, and the two ways a prepared snippet runs (spec §7.8).
//
//   Read:  always CallbackScope(Read) + a TransactionGroup that is always rolled
//          back, with NO transaction open, so Revit itself refuses any model
//          change (ruling R59). The DocumentChanged recorder is the check:
//          anything recorded fails the call (policy_rejected). A transaction
//          already open means someone else's change: refused (W3 opens nothing
//          for reads, ruling R53).
//   Write: only inside W3's own TxGuard transaction (its Preview/Commit scope):
//          the code runs in it, added/deleted ids are measured around the run
//          (the card's touched list, the commit's exists read-back), and W3's
//          DocumentChanged record decorates edits, side effects, warnings and
//          noop after it commits. A write anywhere else is refused: nothing in
//          the add-in runs a write outside the confirm-card runner.
//   UI:    the code's view, zoom and selection requests reach the host shims
//          (UiRequestQueue). A read applies them after its own rollback; a
//          commit hands them to ctx.AfterCommit, which W3 runs only after the
//          batch is assimilated and drops on rollback (ruling R76); a preview
//          never applies them. Ids or views gone by then are dropped, reported.
// No commit call here at all: W3 commits.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using BinaVibe.Copilot.Guard;
using Microsoft.CodeAnalysis;

namespace BinaVibe.Copilot.Code
{
    internal static class RevitCodeHost
    {
        private const string ReadGroupName = "BINA: read code";

        /// <summary>The narrow reference set with the RUNNING Revit's API assemblies, so the compiler
        /// catches members this Revit version does not have.</summary>
        public static IReadOnlyList<MetadataReference> References() =>
            CodeReferences.Build(new[] { typeof(Document).Assembly.Location, typeof(UIDocument).Assembly.Location });

        public static PreparedCode Prepare(string code) => CodeRunner.Prepare(code, References(), WrapperProfile.Revit);

        public static OutputBuffer NewOutput() => new OutputBuffer(special: FormatRevit);

        private static object?[] HostArgs(Document doc, UIDocument? uidoc) =>
            new object?[] { doc, uidoc, uidoc?.ActiveView ?? doc.ActiveView };

        // ---- read ---------------------------------------------------------------------------------

        /// <summary>Runs read-classified code; phase read.</summary>
        public static Envelope Read(Document doc, UIDocument? uidoc, RunRevitCodeArgs args, PreparedCode prepared)
        {
            // Our own read group cannot start inside a transaction, and running inside someone else's
            // would let read code change what they then commit.
            if (doc.IsModifiable) return Busy("another change is open in this document", EnvelopePhase.Read);
            if (prepared.Snippet == null) return prepared.Failure!.ToEnvelope(EnvelopePhase.Read);

            var output = NewOutput();
            var (uiHook, uiQueue) = UiCollector(doc);
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
                        return Busy(ex.Message, EnvelopePhase.Read);
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
            // After the rollback: the view, zoom and selection the code asked for (rulings R59, R76). W4 owns
            // read-mode UI: applied here, once, and never put on ctx.AfterCommit (no list is passed).
            var ui = new UiOutcome();
            UiDispatch.Deliver(UiRequestQueue.DeliveryFor(null), uiQueue, null, () => ui = ApplyUi(doc, uidoc, uiQueue));
            return CodeEnvelopes.Read(args.Intent, output, run, ui.Data, ui.Warnings);
        }

        // ---- write --------------------------------------------------------------------------------

        /// <summary>Runs write-classified code inside W3's transaction; phase preview or commit.</summary>
        public static Envelope Write(Document doc, UIDocument? uidoc, IHandleStore? handles, IList<Action>? afterCommit,
            RunRevitCodeArgs args, PreparedCode prepared, WritePhase phase)
        {
            var phaseName = EnvelopePhase.Of(phase);
            var scope = CallbackScope.Current(doc);
            if (!doc.IsModifiable || (scope != CallbackKind.Preview && scope != CallbackKind.Commit))
                return EnvelopeBuilder.Fail(ErrorCodes.NotAllowed,
                    "run_revit_code changes the model only inside the confirm-card runner's own transaction; nothing was run.",
                    null, phase);
            if (prepared.Snippet == null) return prepared.Failure!.ToEnvelope(phaseName);

            var output = NewOutput();
            var (uiHook, uiQueue) = UiCollector(doc);
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
                verified = added.Count + deleted.Count > 0
                    ? Verify(doc, added, deleted)
                    // Only edits: what was checked is that no element appeared or vanished.
                    : new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = before.Count };

            var ui = UiForWrite(doc, uidoc, uiQueue, phase, afterCommit);
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

        /// <summary>Added elements exist and deleted ones are gone, read back in the runner's transaction.</summary>
        private static VerifiedDto Verify(Document doc, IReadOnlyList<long> added, IReadOnlyList<long> deleted)
        {
            var mismatches = new List<MismatchDto>();
            foreach (var id in added)
                if (doc.GetElement(ElemIds.From(id)) == null)
                    mismatches.Add(new MismatchDto { Id = id, Field = "exists", Expected = "added", Actual = "missing" });
            foreach (var id in deleted)
                if (doc.GetElement(ElemIds.From(id)) != null)
                    mismatches.Add(new MismatchDto { Id = id, Field = "exists", Expected = "deleted", Actual = "present" });
            return new VerifiedDto
            {
                Method = VerifyMethods.Exists,
                Checked = added.Count + deleted.Count,
                Mismatches = mismatches.Take(EnvelopeLimits.MaxRows).ToList(),
            };
        }

        // ---- UI requests (rulings R59, R76) ----------------------------------------------------------

        /// <summary>The hook the host shims call, and the queue it fills. Ids, references and zoom targets
        /// are copied at the call: the code may change its objects after asking.</summary>
        private static (Action<int, object?> Hook, UiRequestQueue Queue) UiCollector(Document doc)
        {
            var queue = new UiRequestQueue();
            void Hook(int kind, object? value)
            {
                switch (kind)
                {
                    case UiShimKinds.References:
                        queue.AddReferences(ReferencesOf(doc, value));
                        break;
                    case UiShimKinds.Zoom:
                        if (value is object[] z && z.Length >= 2 && z[0] is UIView uiView && z[1] is int op)
                            queue.AddZoom(uiView.ViewId.Value, op, ZoomArgs(op, z));
                        break;
                    default:
                        queue.Add(kind, IdsOf(value));
                        break;
                }
            }
            return (Hook, queue);
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

        private static List<(long Id, string Stable)> ReferencesOf(Document doc, object? value)
        {
            var refs = new List<(long, string)>();
            if (value is IEnumerable<Reference> list)
                foreach (var r in list)
                {
                    if (r == null) continue;
                    try
                    {
                        refs.Add((r.ElementId.Value, r.ConvertToStableRepresentation(doc)));
                    }
                    catch (Exception)
                    {
                        // A reference Revit cannot write down cannot be restored after the run either.
                    }
                }
            return refs;
        }

        private static double[] ZoomArgs(int op, object[] z)
        {
            if (op == UiRequestQueue.ZoomFactor && z.Length > 2 && z[2] is double factor) return new[] { factor };
            if (op == UiRequestQueue.ZoomRectangle && z.Length > 3 && z[2] is XYZ a && z[3] is XYZ b)
                return new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z };
            return Array.Empty<double>();
        }

        private sealed class UiOutcome
        {
            public Dictionary<string, object?>? Data;
            public List<WarningDto> Warnings = new List<WarningDto>();
        }

        /// <summary>A preview never applies UI requests; a commit hands them to ctx.AfterCommit (R76).</summary>
        private static UiOutcome UiForWrite(Document doc, UIDocument? uidoc, UiRequestQueue queue, WritePhase phase, IList<Action>? afterCommit)
        {
            var outcome = new UiOutcome();
            var data = new Dictionary<string, object?> { ["applied"] = false, ["requests"] = queue.Count };
            switch (UiDispatch.Deliver(UiRequestQueue.DeliveryFor(phase), queue, afterCommit, () => ApplyUi(doc, uidoc, queue)))
            {
                case UiDispatchResult.NothingToDo:
                    return outcome;
                case UiDispatchResult.QueuedAfterCommit:
                    data["when"] = "after the commit";
                    break;
                case UiDispatchResult.NoHook:
                    outcome.Warnings.Add(new WarningDto { Text = "The view, zoom and selection requests were not applied: this host has no after-commit step.", Severity = WarningSeverity.Warning });
                    break;
                default:
                    data["when"] = "on commit";
                    break;
            }
            outcome.Data = data;
            return outcome;
        }

        private static UiOutcome ApplyUi(Document doc, UIDocument? uidoc, UiRequestQueue queue)
        {
            var outcome = new UiOutcome();
            if (queue.Count == 0) return outcome;
            var open = uidoc == null ? new List<UIView>() : SafeOpenViews(uidoc);
            var plan = queue.Plan(
                id => doc.GetElement(ElemIds.From(id)) != null,
                id => doc.GetElement(ElemIds.From(id)) is View,
                viewId => open.Any(v => v.ViewId.Value == viewId));
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
            Try(outcome, "select references", () =>
            {
                if (!plan.HasReferences) return;
                var refs = new List<Reference>();
                foreach (var (_, stable) in plan.References)
                    try { refs.Add(Reference.ParseFromStableRepresentation(doc, stable)); }
                    catch (Exception) { /* gone since the call: the element existed, the sub-element did not */ }
                uidoc.Selection.SetReferences(refs);
            });
            foreach (var zoom in plan.Zooms)
                Try(outcome, "zoom the view", () =>
                {
                    var view = open.FirstOrDefault(v => v.ViewId.Value == zoom.ViewId);
                    if (view == null) return;
                    switch (zoom.Op)
                    {
                        case UiRequestQueue.ZoomToFit: view.ZoomToFit(); break;
                        case UiRequestQueue.ZoomSheetSize: view.ZoomSheetSize(); break;
                        case UiRequestQueue.ZoomFactor when zoom.Args.Length == 1: view.Zoom(zoom.Args[0]); break;
                        case UiRequestQueue.ZoomRectangle when zoom.Args.Length == 6:
                            view.ZoomAndCenterRectangle(new XYZ(zoom.Args[0], zoom.Args[1], zoom.Args[2]), new XYZ(zoom.Args[3], zoom.Args[4], zoom.Args[5]));
                            break;
                    }
                });
            if (plan.Dropped.Length > 0)
                outcome.Warnings.Add(new WarningDto
                {
                    Text = plan.Dropped.Length + " element(s) or view(s) the code asked to open, show, zoom or select no longer exist after the run; they were skipped.",
                    Severity = WarningSeverity.Warning,
                    Ids = plan.Dropped.Take(EnvelopeLimits.MaxRows).ToList(),
                });
            return outcome;
        }

        private static List<UIView> SafeOpenViews(UIDocument uidoc)
        {
            try
            {
                return uidoc.GetOpenUIViews().ToList();
            }
            catch (Exception)
            {
                return new List<UIView>();
            }
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

        // ---- shared -------------------------------------------------------------------------------

        /// <summary>What the code printed before it failed helps the model find the problem.</summary>
        private static CodeFailure WithOutput(CodeFailure failure, OutputBuffer output)
        {
            if (output.Lines > 0) failure.Data["output"] = output.Text;
            return failure;
        }

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
