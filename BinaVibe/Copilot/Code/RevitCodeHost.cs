#nullable enable
// Revit-bound half of run_revit_code: the Revit references, Print formatting
// of Revit values, and the two ways a prepared snippet runs (spec §7.8).
//
//   RunRead:  CallbackScope(Read) -> TransactionGroup (always rolled back) with
//             NO transaction open, so Revit itself refuses any change -> the
//             group is rolled back -> fail (policy_rejected) if the external
//             revision moved or anything was recorded. Phase read.
//   RunWrite: inside the runner's preview/commit TransactionGroup (W3) ->
//             one Transaction through TxGuard (warnings swallowed and
//             collected; FailureScope backstop; CommitOrThrow) -> the
//             DocumentChanged recording (stopped after our commit, before the
//             runner's group ends) gives the
//             changes; nothing recorded = noop with an inline warning; on
//             commit, added elements must exist and deleted ones must be gone.
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

        // ---- read ---------------------------------------------------------------------------------

        public static Envelope RunRead(Document doc, UIDocument? uidoc, RunRevitCodeArgs args, PreparedCode prepared)
        {
            if (prepared.Snippet == null) return prepared.Failure!.ToEnvelope(EnvelopePhase.Read);

            var output = NewOutput();
            CodeRunResult run;
            ChangeSet recorded;
            long before, after;
            using (CallbackScope.Enter(doc, CallbackKind.Read))
            {
                before = ExternalRevision.Get(doc);
                using (var recording = ChangeRecorder.Start(doc))
                using (var group = new TransactionGroup(doc, ReadGroupName))
                {
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
                        run = CodeRunner.Execute(prepared.Snippet, HostArgs(doc, uidoc), LoopBudget.FromPolicy(), output, readMode: true);
                        // Stop BEFORE the rollback: the recorder nets a rolled-back group to nothing.
                        recorded = recording.Stop();
                    }
                    finally
                    {
                        if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                    }
                }
                after = ExternalRevision.Get(doc);
            }

            if (after != before || recorded.Added.Count + recorded.Modified.Count + recorded.Deleted.Count > 0)
            {
                // Cannot happen through the policy (no transaction can be opened); fail loudly if it does.
                return new CodeFailure
                {
                    Code = ErrorCodes.PolicyRejected,
                    Summary = "The code changed the model during a read-only run; the change was rolled back.",
                    Message = "run_revit_code ran this as a read, but the model changed while it ran. Everything was rolled back. "
                              + "Pass expected (adds, modifies or deletes) so the change is previewed and approved.",
                    Next = "Retry with expected set, so the drafter can approve the change.",
                    Data = new Dictionary<string, object?> { ["mode"] = CodePlans.Read },
                }.ToEnvelope(EnvelopePhase.Read);
            }

            return run.Ok ? CodeEnvelopes.Read(args.Intent, output, run) : WithOutput(run.Failure!, output).ToEnvelope(EnvelopePhase.Read);
        }

        // ---- write --------------------------------------------------------------------------------

        public static Envelope RunWrite(Document doc, UIDocument? uidoc, IHandleStore? handles, RunRevitCodeArgs args,
            PreparedCode prepared, WritePhase phase)
        {
            var phaseName = EnvelopePhase.Of(phase);
            if (prepared.Snippet == null) return prepared.Failure!.ToEnvelope(phaseName);

            var output = NewOutput();
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
                            run = CodeRunner.Execute(prepared.Snippet, HostArgs(doc, uidoc), LoopBudget.FromPolicy(), output, readMode: false);
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
            return CodeEnvelopes.Write(phase, args.Intent, output, run, capped, sideEffects, counts, args.Expected, verified, handle, warnings, fault);
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

        private static Envelope Busy(Exception ex, string phase) =>
            EnvelopeBuilder.Fail(ErrorCodes.RevitBusy,
                "Revit cannot start a change right now (" + ex.Message + "). Finish or cancel the current edit mode or dialog, then ask again.",
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
