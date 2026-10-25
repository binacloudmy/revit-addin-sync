#nullable enable
// The Revit half of the write batch runner (spec §7.3, §7.4; plan §5 W3 Task 3).
//
// Preview (one callback on Revit's thread, CallbackScope PREVIEW):
//   document checks -> Plan every call BEFORE any change -> run read-mode code
//   at once (ruling R13) -> worksharing pre-check of the write targets
//   (WorksharingUtils; others' elements fail not_owned with the editable ones
//   as candidates, nothing is borrowed) -> TransactionGroup -> per call one
//   transaction (TxGuard) around Execute(Preview), recorded by ChangeRecorder
//   and FailureLog -> shape of what was added -> thumbnail of the additions ->
//   RollBack -> THEN capture the external revision R.
// Commit (a second callback after the card, CallbackScope COMMIT):
//   document checks -> R unchanged? -> re-plan; the plan hash must equal the
//   approved one -> TransactionGroup "BINA: <label>" -> re-run the STORED plans
//   with Execute(Commit) -> ShapeComparer against the preview -> Assimilate
//   (one undo entry), or RollBack and report drift -> selection highlight.
// Nothing is held open between the two callbacks. No bare commits: inner
// transactions go through TxGuard; the group is assimilated or rolled back.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using BinaVibe.Copilot.Tools;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Writes
{
    public sealed class RevitWriteEngine : IWriteEngine
    {
        private const int MaxCandidates = 50;
        private const int MaxThumbnailTargets = 200;
        private const int MaxWarnings = 10;
        private const double FeetToMm = 304.8;

        private readonly IHandleStore _handles;
        private readonly Func<string, IWriteTool?> _toolFor;

        public RevitWriteEngine(IHandleStore handles, Func<string, IWriteTool?>? toolFor = null)
        {
            _handles = handles ?? throw new ArgumentNullException(nameof(handles));
            _toolFor = toolFor ?? (name => CopilotToolCatalog.Find(name) as IWriteTool);
        }

        public Task<PreviewOutcome> PreviewAsync(WriteBatch batch, CancellationToken ct) =>
            RevitThread.RunAsync(app => PreviewBatch(app, batch), ct);

        public Task<CommitOutcome> CommitAsync(WriteBatch batch, PreviewOutcome preview, string planHash, CancellationToken ct) =>
            RevitThread.RunAsync(app => CommitBatch(app, batch, preview, planHash), ct);

        // ------------------------------------------------------------------
        // Preview
        // ------------------------------------------------------------------

        private PreviewOutcome PreviewBatch(UIApplication app, WriteBatch batch)
        {
            var n = batch.Calls.Count;
            var outcome = new PreviewOutcome
            {
                Reads = new Envelope?[n],
                Failures = new Envelope?[n],
                Plans = new WritePlan[n],
                Envelopes = new Envelope[n],
            };

            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            var refusal = CheckDocument(doc, batch, EnvelopePhase.Preview);
            if (refusal != null)
            {
                for (var i = 0; i < n; i++) outcome.Failures[i] = refusal;
                return outcome;
            }

            var tools = new IWriteTool?[n];
            var contexts = new ToolContext[n];
            for (var i = 0; i < n; i++)
            {
                tools[i] = _toolFor(batch.Calls[i].Tool ?? "");
                contexts[i] = Context(app, uidoc!, doc!, batch, batch.Calls[i]);
                if (tools[i] == null) outcome.Failures[i] = EnvelopeFactory.NotAllowed(batch.Calls[i].Tool ?? "");
            }
            if (outcome.HasFailures) return outcome;

            // 1. Plan: resolve every target BEFORE any change.
            for (var i = 0; i < n; i++)
            {
                try
                {
                    outcome.Plans[i] = tools[i]!.Plan(contexts[i], batch.Calls[i].Args)
                                       ?? throw new InvalidOperationException(batch.Calls[i].Tool + " returned no plan.");
                }
                catch (Exception ex)
                {
                    outcome.Failures[i] = FromException(ex, EnvelopePhase.Preview);
                }
            }
            if (outcome.HasFailures) return outcome;

            // 2. Read-mode code runs at once, once, and takes no further part (R13).
            var writes = new List<int>();
            for (var i = 0; i < n; i++)
            {
                if (WriteModes.IsRead(outcome.Plans[i]))
                    outcome.Reads[i] = RunRead(doc!, tools[i]!, contexts[i], batch.Calls[i], outcome.Plans[i]);
                else
                    writes.Add(i);
            }
            if (writes.Count == 0) return outcome;

            // 3. Worksharing: never borrow, never edit others' elements.
            if (doc!.IsWorkshared)
            {
                foreach (var i in writes)
                    outcome.Failures[i] = CheckOwnership(doc, outcome.Plans[i]);
                if (outcome.HasFailures) return outcome;
            }

            var writePlans = writes.Select(i => outcome.Plans[i]).ToList();
            outcome.GroupName = WriteLabels.GroupName(writePlans);

            // 4. A batch Revit cannot preview gets an estimate-only card.
            if (writePlans.Any(p => !p.Previewable))
            {
                foreach (var i in writes)
                    outcome.Envelopes[i] = WriteEnvelopes.Estimate(outcome.Plans[i], WriteLabels.ForPlan(outcome.Plans[i]));
                outcome.Shape = null;
                outcome.Revision = ExternalRevision.Get(doc);
                return outcome;
            }

            // 5. Measured preview, always rolled back.
            var builder = new BatchShapeBuilder();
            using (CallbackScope.Enter(doc, CallbackKind.Preview))
            {
                var group = new TransactionGroup(doc, outcome.GroupName);
                try
                {
                    var busy = StartGroup(group, EnvelopePhase.Preview);
                    if (busy != null)
                    {
                        foreach (var i in writes) outcome.Failures[i] = busy;
                    }
                    else
                    {
                        var firstWithAdds = -1;
                        foreach (var i in writes)
                        {
                            var run = RunCall(doc, tools[i]!, contexts[i], batch.Calls[i], outcome.Plans[i], WritePhase.Preview, outcome.GroupName, builder);
                            if (run.Failure != null)
                            {
                                outcome.Failures[i] = run.Failure;
                                break;
                            }
                            outcome.Envelopes[i] = run.Envelope!;
                            if (firstWithAdds < 0 && run.Changes != null && run.Changes.Added.Count > 0) firstWithAdds = i;
                        }

                        if (!outcome.HasFailures)
                        {
                            outcome.Shape = builder.Build(id => Describe(doc, id));
                            if (firstWithAdds >= 0) AttachThumbnail(contexts[firstWithAdds], outcome.Envelopes[firstWithAdds], builder.AddedIds);
                        }
                    }
                }
                finally
                {
                    RollBackIfOpen(group);
                    group.Dispose();
                }
            }

            // R is read only after the preview's own rollback, outside our scope.
            outcome.Revision = ExternalRevision.Get(doc);
            return outcome;
        }

        private Envelope RunRead(Document doc, IWriteTool tool, ToolContext ctx, ToolCallDto call, WritePlan plan)
        {
            var before = ExternalRevision.Get(doc);
            Envelope result;
            using (CallbackScope.Enter(doc, CallbackKind.Read))
            {
                var group = new TransactionGroup(doc, Undo.BinaTransactionNames.For("read"));
                try
                {
                    var busy = StartGroup(group, EnvelopePhase.Read);
                    if (busy != null) return busy;
                    var run = RunCall(doc, tool, ctx, call, plan, WritePhase.Preview, Undo.BinaTransactionNames.For("read"), new BatchShapeBuilder(), isRead: true);
                    result = run.Failure ?? run.Envelope!;
                    if (run.Changes != null && (run.Changes.Added.Count + run.Changes.Modified.Count + run.Changes.Deleted.Count) > 0)
                    {
                        result.Warnings ??= new List<WarningDto>();
                        result.Warnings.Add(new WarningDto
                        {
                            Text = "This code changed the model although it was classified as read-only; the change was rolled back and nothing was kept.",
                            Severity = WarningSeverity.Warning,
                        });
                    }
                }
                finally
                {
                    RollBackIfOpen(group);
                    group.Dispose();
                }
            }

            if (ExternalRevision.Get(doc) != before)
                return WriteEnvelopes.Failure(ErrorCodes.DocChanged,
                    "The model changed while the code was reading it, so its result may not match the model. Run it again.",
                    EnvelopePhase.Read);

            result.Phase = EnvelopePhase.Read;
            result.Changes = null;   // nothing was kept
            if (result.Count == 0 && result.Searched == null) result.Count = null;
            return result;
        }

        // ------------------------------------------------------------------
        // Commit
        // ------------------------------------------------------------------

        private CommitOutcome CommitBatch(UIApplication app, WriteBatch batch, PreviewOutcome preview, string planHash)
        {
            var n = batch.Calls.Count;
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            var refusal = CheckDocument(doc, batch, EnvelopePhase.Commit);
            if (refusal != null)
                return new CommitOutcome { Kind = refusal.Error?.Code == ErrorCodes.DocChanged ? CommitKind.DocChanged : CommitKind.Failed, Reason = refusal.Error?.Message, Envelopes = Fill(n, refusal) };

            // Someone changed the model after the preview: back to the preview (the runner does it once).
            if (ExternalRevision.Get(doc!) != preview.Revision)
                return new CommitOutcome { Kind = CommitKind.RevisionMoved, Reason = "The model changed after the preview." };

            var tools = new IWriteTool[n];
            var contexts = new ToolContext[n];
            var fresh = new WritePlan[n];
            for (var i = 0; i < n; i++)
            {
                var tool = _toolFor(batch.Calls[i].Tool ?? "");
                if (tool == null) return Failed(batch, i, EnvelopeFactory.NotAllowed(batch.Calls[i].Tool ?? ""));
                tools[i] = tool;
                contexts[i] = Context(app, uidoc!, doc!, batch, batch.Calls[i]);
                try
                {
                    fresh[i] = tool.Plan(contexts[i], batch.Calls[i].Args)
                               ?? throw new InvalidOperationException(batch.Calls[i].Tool + " returned no plan.");
                }
                catch (Exception ex)
                {
                    return Failed(batch, i, FromException(ex, EnvelopePhase.Commit));
                }
            }

            // Ya is bound to the plan hash of the batch the add-in holds.
            if (!string.Equals(PlanHash.Compute(fresh), planHash, StringComparison.Ordinal))
                return new CommitOutcome { Kind = CommitKind.Drift, Reason = "The targets or values resolved differently than in the approved preview." };

            // Borrowing by others does not move R: check ownership again, still borrowing nothing.
            if (doc!.IsWorkshared)
                for (var i = 0; i < n; i++)
                    if (CheckOwnership(doc, fresh[i]) is Envelope notOwned) return Failed(batch, i, notOwned);

            var envelopes = new Envelope[n];
            var builder = new BatchShapeBuilder();
            var anyChange = false;
            using (CallbackScope.Enter(doc!, CallbackKind.Commit))
            {
                var group = new TransactionGroup(doc, preview.GroupName);
                try
                {
                    var busy = StartGroup(group, EnvelopePhase.Commit);
                    if (busy != null) return new CommitOutcome { Kind = CommitKind.Failed, Reason = busy.Error?.Message, Envelopes = Fill(n, busy) };

                    for (var i = 0; i < n; i++)
                    {
                        // The STORED plans, never re-sent arguments resolved anew.
                        var run = RunCall(doc!, tools[i], contexts[i], batch.Calls[i], preview.Plans[i], WritePhase.Commit, preview.GroupName, builder);
                        if (run.Failure != null)
                        {
                            RollBackIfOpen(group);
                            return Failed(batch, i, run.Failure);
                        }
                        envelopes[i] = run.Envelope!;
                        if (run.Changes != null && run.Changes.Added.Count + run.Changes.Modified.Count + run.Changes.Deleted.Count > 0)
                            anyChange = true;
                    }

                    var shape = builder.Build(id => Describe(doc!, id));
                    var verdict = ShapeComparer.Compare(preview.Plans, preview.Shape, shape);
                    if (!verdict.IsMatch)
                    {
                        RollBackIfOpen(group);
                        return new CommitOutcome { Kind = CommitKind.Drift, Reason = verdict.Reason };
                    }

                    if (!anyChange)
                    {
                        // A no-op group must leave no undo entry: undoing it would revert the drafter's own step.
                        RollBackIfOpen(group);
                        foreach (var e in envelopes.Where(e => e.Ok)) e.Status = EnvelopeStatus.Noop;
                        return new CommitOutcome { Kind = CommitKind.Committed, Envelopes = envelopes, UndoEntry = false };
                    }

                    var status = group.Assimilate();
                    if (status != TransactionStatus.Committed)
                    {
                        var fail = WriteEnvelopes.Failure(ErrorCodes.RolledBack,
                            "Revit did not keep the change (group status " + status + "); nothing was changed.", EnvelopePhase.Commit);
                        return new CommitOutcome { Kind = CommitKind.Failed, Reason = fail.Error!.Message, Envelopes = Fill(n, fail) };
                    }
                }
                finally
                {
                    RollBackIfOpen(group);
                    group.Dispose();
                }
            }

            Undo.UndoService.NoteBatchCommitted(doc!, batch.TurnId, preview.GroupName);
            Highlight(uidoc!, doc!, envelopes, builder.AddedIds);
            return new CommitOutcome { Kind = CommitKind.Committed, Envelopes = envelopes, UndoEntry = true };
        }

        private static CommitOutcome Failed(WriteBatch batch, int failedIndex, Envelope failure)
        {
            failure.Phase = EnvelopePhase.Commit;
            var envelopes = new Envelope[batch.Calls.Count];
            for (var i = 0; i < envelopes.Length; i++)
                envelopes[i] = i == failedIndex
                    ? failure
                    : WriteEnvelopes.NotRunBecauseSibling(failedIndex, batch.Calls[failedIndex], failure, EnvelopePhase.Commit);
            return new CommitOutcome { Kind = CommitKind.Failed, Reason = failure.Error?.Message, Envelopes = envelopes };
        }

        // ------------------------------------------------------------------
        // One call: one guarded transaction inside the open group
        // ------------------------------------------------------------------

        private sealed class CallRun
        {
            public Envelope? Envelope;
            public Envelope? Failure;
            public ChangeSet? Changes;
        }

        private static CallRun RunCall(Document doc, IWriteTool tool, ToolContext ctx, ToolCallDto call, WritePlan plan,
                                       WritePhase phase, string transactionName, BatchShapeBuilder builder, bool isRead = false)
        {
            var phaseName = EnvelopePhase.Of(phase);
            var run = new CallRun();
            using (FailureLog.Begin())
            using (var dialogs = DialogWatch.Collect())
            {
                ChangeRecording? recording = null;
                var tx = new Transaction(doc, transactionName);
                try
                {
                    recording = ChangeRecorder.Start(doc);
                    try
                    {
                        TxGuard.StartSwallowing(tx);
                    }
                    catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.InvalidOperationException || ex is System.InvalidOperationException)
                    {
                        run.Failure = WriteEnvelopes.Failure(ErrorCodes.RevitBusy,
                            "Revit cannot start a change now: it is in an edit mode (group, sketch or in-place family) or another change is open. " +
                            "Ask the drafter to finish or cancel it, then try again. (" + ex.Message + ")",
                            phaseName);
                    }

                    if (run.Failure == null)
                    {
                        var env = tool.Execute(ctx, call.Args, plan, phase)
                                  ?? throw new InvalidOperationException(call.Tool + " returned no result.");
                        if (!env.Ok)
                        {
                            run.Failure = env;   // the tool refused: roll its transaction back (finally)
                        }
                        else
                        {
                            TxGuard.CommitOrThrow(tx);
                            run.Envelope = env;
                        }
                    }
                }
                catch (Exception ex)
                {
                    run.Failure = FromException(ex, phaseName);
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    tx.Dispose();
                    run.Changes = recording?.Stop();
                    recording?.Dispose();
                }

                var warnings = FailureLog.Current.Snapshot();
                var shown = dialogs.Snapshot();
                if (run.Failure != null)
                {
                    run.Failure.Phase = phaseName;
                    AddWarnings(run.Failure, warnings);
                    AddDialogs(run.Failure, shown);
                    return run;
                }

                var changes = run.Changes ?? new ChangeSet();
                var bookkeeping = Bookkeeping(doc, changes);
                builder.AddCall(changes.Added, changes.Modified.Where(id => !bookkeeping.Contains(id)), changes.Deleted);
                Decorate(run.Envelope!, changes, bookkeeping, warnings, shown, phaseName, markNoop: !isRead);
                return run;
            }
        }

        /// <summary>Adds what the recorder saw beyond the tool's own report (side effects),
        /// Revit's warnings and the dialogs answered, and says noop when nothing changed.</summary>
        private static void Decorate(Envelope env, ChangeSet changes, HashSet<long> bookkeeping,
                                     IReadOnlyList<WarningDto> warnings, IReadOnlyList<DialogDto> dialogs, string phaseName, bool markNoop)
        {
            env.Phase = phaseName;
            var touchedIds = new HashSet<long>();
            if (env.Changes?.Touched != null)
            {
                touchedIds.UnionWith(env.Changes.Touched.Added.Select(r => r.Id));
                touchedIds.UnionWith(env.Changes.Touched.Modified.Select(m => m.Id));
                touchedIds.UnionWith(env.Changes.Touched.Deleted.Select(r => r.Id));
            }
            var (touched, side) = ChangeSetMath.Split(changes, touchedIds, bookkeeping);
            if (env.Changes == null) env.Changes = new ChangeReportDto { Touched = touched, SideEffects = side };
            else env.Changes.SideEffects = side;

            AddWarnings(env, warnings);
            AddDialogs(env, dialogs);

            var nothing = changes.Added.Count == 0 && changes.Modified.Count == 0 && changes.Deleted.Count == 0;
            if (markNoop && nothing && env.Status == EnvelopeStatus.Done) env.Status = EnvelopeStatus.Noop;
        }

        private static HashSet<long> Bookkeeping(Document doc, ChangeSet changes)
        {
            // Modified elements without a category are Revit's internal bookkeeping
            // (regeneration data, sketches, analytical caches): never "side effects",
            // and never what decides whether a batch only adds.
            var ids = new HashSet<long>();
            foreach (var id in changes.Modified)
            {
                if (changes.CategoryOf.TryGetValue(id, out var cat))
                {
                    if (string.IsNullOrEmpty(cat)) ids.Add(id);
                    continue;
                }
                try
                {
                    if (doc.GetElement(ElemIds.From(id))?.Category == null) ids.Add(id);
                }
                catch (Exception)
                {
                    // An id that no longer resolves is not bookkeeping we can prove.
                }
            }
            return ids;
        }

        private static void AddWarnings(Envelope env, IReadOnlyList<WarningDto> more)
        {
            if (more == null || more.Count == 0) return;
            env.Warnings ??= new List<WarningDto>();
            foreach (var w in more)
            {
                if (env.Warnings.Count >= MaxWarnings) break;
                if (env.Warnings.Any(x => x.Text == w.Text && x.DefinitionId == w.DefinitionId && x.Ids.SequenceEqual(w.Ids))) continue;
                env.Warnings.Add(w);
            }
        }

        private static void AddDialogs(Envelope env, IReadOnlyList<DialogDto> more)
        {
            if (more == null || more.Count == 0) return;
            env.Dialogs ??= new List<DialogDto>();
            env.Dialogs.AddRange(more);
        }

        // ------------------------------------------------------------------
        // Checks and helpers
        // ------------------------------------------------------------------

        private static Envelope? CheckDocument(Document? doc, WriteBatch batch, string phase)
        {
            if (doc == null) return WriteEnvelopes.DocChanged(batch.ExpectedDoc, null, phase);
            var active = RevitDocIdentity.Of(doc);
            if (!DocIdentity.Same(batch.ExpectedDoc, active)) return WriteEnvelopes.DocChanged(batch.ExpectedDoc, active, phase);
            if (doc.IsReadOnly)
                return WriteEnvelopes.Failure(ErrorCodes.NotAllowed,
                    "The document is read-only in this session, so nothing was changed.", phase);
            if (doc.IsModifiable)
                return WriteEnvelopes.Failure(ErrorCodes.RevitBusy,
                    "Another change is already open in Revit, so nothing was changed. Try again when it has finished.", phase);
            return null;
        }

        private static Envelope? CheckOwnership(Document doc, WritePlan plan)
        {
            var byOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var stale = 0;
            var editable = new List<long>();
            var total = 0;
            foreach (var id in plan.TargetIds ?? Array.Empty<long>())
            {
                var eid = ElemIds.From(id);
                if (doc.GetElement(eid) == null) continue;
                total++;
                var status = WorksharingUtils.GetCheckoutStatus(doc, eid, out var owner);
                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    var who = string.IsNullOrWhiteSpace(owner) ? "another user" : owner;
                    byOwner[who] = (byOwner.TryGetValue(who, out var c) ? c : 0) + 1;
                    continue;
                }
                var updates = WorksharingUtils.GetModelUpdatesStatus(doc, eid);
                if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral)
                {
                    stale++;
                    continue;
                }
                editable.Add(id);
            }

            var blocked = byOwner.Values.Sum() + stale;
            if (blocked == 0) return null;

            var parts = new List<string>();
            if (byOwner.Count > 0)
                parts.Add("borrowed by " + string.Join(", ", byOwner.OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key + " (" + kv.Value.ToString(CultureInfo.InvariantCulture) + ")")));
            if (stale > 0)
                parts.Add(stale.ToString(CultureInfo.InvariantCulture) + " changed in central (Reload Latest first)");
            var message = blocked.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) +
                          " targets cannot be edited: " + string.Join("; ", parts) + ". Nothing was borrowed and nothing changed. " +
                          editable.Count.ToString(CultureInfo.InvariantCulture) + " target(s) are editable (candidates).";
            return WriteEnvelopes.Failure(ErrorCodes.NotOwned, message, EnvelopePhase.Preview,
                editable.Take(MaxCandidates).Select(i => i.ToString(CultureInfo.InvariantCulture)));
        }

        private static Envelope? StartGroup(TransactionGroup group, string phase)
        {
            try
            {
                group.Start();
                return null;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.InvalidOperationException || ex is System.InvalidOperationException)
            {
                return WriteEnvelopes.Failure(ErrorCodes.RevitBusy,
                    "Revit cannot start a change now: it is in an edit mode (group, sketch or in-place family) or another change is open. " +
                    "Ask the drafter to finish or cancel it, then try again. (" + ex.Message + ")",
                    phase);
            }
        }

        private static void RollBackIfOpen(TransactionGroup group)
        {
            try
            {
                if (group.IsValidObject && group.HasStarted() && !group.HasEnded()) group.RollBack();
            }
            catch (Exception)
            {
                // Nothing more we can do; Revit rolls an abandoned group back itself.
            }
        }

        private ToolContext Context(UIApplication app, UIDocument uidoc, Document doc, WriteBatch batch, ToolCallDto call) => new ToolContext
        {
            App = app,
            Doc = doc,
            UiDoc = uidoc,
            TurnId = batch.TurnId,
            CallId = call.CallId,
            IdempotencyKey = call.IdempotencyKey,
            Handles = _handles,
            TurnSnapshot = batch.Snapshot,
        };

        internal static Envelope FromException(Exception ex, string phase)
        {
            switch (ex)
            {
                case WriteRefusal r:
                    return WriteEnvelopes.Failure(r.Code, r.Message, phase, r.Candidates);
                case RevitBusyException busy:
                    return WriteEnvelopes.Failure(ErrorCodes.RevitBusy, busy.Message, phase);
                case NotImplementedException:
                    return WriteEnvelopes.Failure(ErrorCodes.NotImplemented, "This tool is not implemented in this add-in build; nothing was changed.", phase);
                case System.ArgumentException:
                case JsonException:
                case FormatException:
                case Autodesk.Revit.Exceptions.ArgumentException:
                    return WriteEnvelopes.Failure(ErrorCodes.InvalidArgs, ex.Message, phase);
                case System.InvalidOperationException io when io.Message.StartsWith("Revit error", StringComparison.Ordinal):
                    return WriteEnvelopes.Failure(ErrorCodes.RolledBack, io.Message + " Revit rolled the change back; nothing was changed.", phase);
                default:
                    return WriteEnvelopes.Failure(ErrorCodes.RuntimeError, ex.GetType().Name + ": " + ex.Message, phase);
            }
        }

        private static Envelope[] Fill(int n, Envelope e)
        {
            var all = new Envelope[n];
            for (var i = 0; i < n; i++) all[i] = e;
            return all;
        }

        private static AddedShape? Describe(Document doc, long id)
        {
            var e = doc.GetElement(ElemIds.From(id));
            if (e == null) return null;
            double[]? box = null;
            try
            {
                var bb = e.get_BoundingBox(null);
                if (bb != null)
                    box = new[] { bb.Min.X * FeetToMm, bb.Min.Y * FeetToMm, bb.Min.Z * FeetToMm, bb.Max.X * FeetToMm, bb.Max.Y * FeetToMm, bb.Max.Z * FeetToMm };
            }
            catch (Exception)
            {
                box = null;
            }
            return new AddedShape
            {
                Id = id,
                Category = e.Category?.Name ?? "",
                TypeId = (long)e.GetTypeId().Value,
                LevelId = (long)e.LevelId.Value,
                BboxMm = box,
            };
        }

        private static void AttachThumbnail(ToolContext ctx, Envelope env, IReadOnlyList<long> added)
        {
            try
            {
                var refs = added.Take(MaxThumbnailTargets).Select(id => new ElementRef(id, null)).ToList();
                var image = LookTool.CaptureElements(ctx, refs, EnvelopeLimits.ImageMaxPx);
                if (image != null) env.Image = image;
            }
            catch (Exception)
            {
                // No GPU / export failed: the card falls back to counts only.
            }
        }

        /// <summary>Selection-only highlight of what the batch touched (no transaction, so no
        /// undo entry sits on top of ours; spec §7.6).</summary>
        private static void Highlight(UIDocument uidoc, Document doc, Envelope[] envelopes, IReadOnlyList<long> added)
        {
            try
            {
                var per = envelopes.Select(e => TouchedIds(e)).ToList();
                var all = new HashSet<long>(per.SelectMany(s => s));
                all.UnionWith(added);
                var ids = all.Select(ElemIds.From).Where(id => doc.GetElement(id) != null).ToList();
                if (ids.Count == 0) return;
                uidoc.Selection.SetElementIds(ids);

                var visible = new HashSet<long>();
                var view = doc.ActiveView;
                if (view != null && !view.IsTemplate)
                    foreach (var id in new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(ids)).ToElementIds())
                        visible.Add((long)id.Value);

                var existing = new HashSet<long>(ids.Select(id => (long)id.Value));
                for (var i = 0; i < envelopes.Length; i++)
                {
                    var mine = per[i].Where(existing.Contains).ToList();
                    if (mine.Count == 0) continue;
                    envelopes[i].Highlight = new HighlightDto { Selected = mine.Count, VisibleInView = mine.Count(visible.Contains) };
                }
            }
            catch (Exception)
            {
                // Highlight is evidence, not the change: never fail a committed batch over it.
            }
        }

        private static IEnumerable<long> TouchedIds(Envelope e)
        {
            var t = e.Changes?.Touched;
            if (t == null) return Enumerable.Empty<long>();
            return t.Added.Where(r => r.Link == null).Select(r => r.Id).Concat(t.Modified.Select(m => m.Id));
        }
    }
}
