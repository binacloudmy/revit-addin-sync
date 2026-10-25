#nullable enable
// The Revit half of the write batch runner (spec §7.3, §7.4; plan §5 W3 Task 3).
//
// Preview (one callback on Revit's thread, CallbackScope PREVIEW):
//   document checks -> Plan every call BEFORE any change -> plan hash over
//   frozen copies of the write plans -> run read-mode code at once (ruling
//   R13; code that changed the model anyway fails policy_rejected) ->
//   worksharing pre-check of the write targets
//   (WorksharingUtils; others' elements fail not_owned with the editable ones
//   as candidates, nothing is borrowed) -> TransactionGroup -> per call one
//   transaction (TxGuard) around Execute(Preview), recorded by ChangeRecorder
//   and FailureLog -> shape of what was added -> thumbnail of the additions ->
//   RollBack -> THEN capture the external revision R.
// Commit (a second callback after the card, CallbackScope COMMIT):
//   document checks -> R unchanged? -> re-plan the stored calls; the hash must
//   equal the approved one -> ownership again -> TransactionGroup
//   "BINA: <label>" -> Execute(Commit) with those re-planned (hash-identical)
//   plans -> ShapeComparer against the preview -> count_delta for a call that
//   reported no read-back -> Assimilate (one undo entry), or RollBack and
//   report drift -> selection highlight.
// Every group runs inside W1's FailureScope (ruling R34) and every call inside
// FailureLog.Begin(), so Revit's warnings reach the envelopes. Changes to
// elements with no category are side effects under "class:<ClassName>" and
// take part in the shape check unless their class is benign (ruling R43).
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

            // The hash binds the plans as resolved here, before any change, over frozen copies.
            var writes = Enumerable.Range(0, n).Where(i => !WriteModes.IsRead(outcome.Plans[i])).ToList();
            outcome.PlanHash = PlanHash.Compute(writes.Select(i => PlanHash.Freeze(outcome.Plans[i])).ToList());

            // 2. Read-mode code runs at once, once, and takes no further part (R13).
            for (var i = 0; i < n; i++)
                if (WriteModes.IsRead(outcome.Plans[i]))
                    outcome.Reads[i] = RunRead(doc!, tools[i]!, contexts[i], batch.Calls[i], outcome.Plans[i]);
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
            var before = CategoriesOf(doc, writePlans);
            var existing = new Dictionary<int, List<long>>();
            using (CallbackScope.Enter(doc, CallbackKind.Preview))
            using (FailureScope.Begin(doc.Application))
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
                            var run = RunCall(doc, tools[i]!, contexts[i], batch.Calls[i], outcome.Plans[i], WritePhase.Preview, outcome.GroupName, builder, before);
                            if (run.Failure != null)
                            {
                                outcome.Failures[i] = run.Failure;
                                break;
                            }
                            outcome.Envelopes[i] = run.Envelope!;
                            existing[i] = run.Existing;
                            if (firstWithAdds < 0 && run.Changes != null && run.Changes.Added.Count > 0) firstWithAdds = i;
                        }

                        if (!outcome.HasFailures)
                        {
                            outcome.Shape = builder.Build(id => Describe(doc, id));
                            RevitValueFingerprints.Fill(doc, outcome.Shape, builder.AddedIds);   // R75, before the rollback
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

            // Code plans name no targets, so their ownership check runs on what the preview
            // measured — after the rollback, so deleted elements exist again and the checkout
            // status is the one from before the preview (read-only; R is unaffected). Others'
            // elements fail not_owned with the editable ones as candidates, before any card.
            if (!outcome.HasFailures && doc.IsWorkshared)
                foreach (var i in writes.Where(i => string.Equals(batch.Calls[i].Tool, ToolNames.RunRevitCode, StringComparison.Ordinal)))
                {
                    var refusalOf = CheckOwnership(doc, existing.TryGetValue(i, out var ids) ? ids : new List<long>(), "elements this code changes");
                    if (refusalOf != null) { outcome.Failures[i] = refusalOf; break; }
                }

            // R is read only after the preview's own rollback, outside our scope.
            outcome.Revision = ExternalRevision.Get(doc);
            return outcome;
        }

        // Read-mode code (rulings R13, R17, R53): the runner opens NO transaction, group or
        // callback scope here. W4's tool runs the code itself, once, inside a TransactionGroup it
        // always rolls back, under CallbackKind.Read; it is called with WritePhase.Commit because
        // this is its only run. The recorder and the external revision only catch a change that
        // escaped that rollback; W4 reports one it rolled back itself.
        private static Envelope RunRead(Document doc, IWriteTool tool, ToolContext ctx, ToolCallDto call, WritePlan plan)
        {
            var revisionBefore = ExternalRevision.Get(doc);
            Envelope result;
            ChangeSet? kept = null;
            using (FailureLog.Begin())
            using (var dialogs = DialogWatch.Collect())
            using (FailureScope.Begin(doc.Application))
            {
                ChangeRecording? recording = null;
                try
                {
                    recording = ChangeRecorder.Start(doc);
                    result = tool.Execute(ctx, call.Args, plan, WritePhase.Commit)
                             ?? throw new InvalidOperationException(call.Tool + " returned no result.");
                }
                catch (Exception ex)
                {
                    result = FromException(ex, EnvelopePhase.Read);
                }
                finally
                {
                    kept = recording?.Stop();
                    recording?.Dispose();
                }
                AddWarnings(result, FailureLog.Current.Snapshot());
                AddDialogs(result, dialogs.Snapshot());
            }

            var keptCount = kept == null ? 0 : kept.Added.Count + kept.Modified.Count + kept.Deleted.Count;
            if (keptCount > 0 || ExternalRevision.Get(doc) != revisionBefore)
                return WriteEnvelopes.Failure(ErrorCodes.PolicyRejected,
                    "This code changed the model although it was classified as read-only" +
                    (keptCount > 0 ? ", and " + keptCount.ToString(CultureInfo.InvariantCulture) + " change(s) were kept" : ", and a change was kept") +
                    ". Check the model and use Revit's Undo list; send code that changes the model as a change, so it is previewed and approved.",
                    EnvelopePhase.Read);

            result.Phase = EnvelopePhase.Read;
            result.Changes = null;
            if (result.Count == 0 && result.Searched == null) result.Count = null;
            // Read-mode UI belongs to W4 alone (applied after its own rollback): the runner runs
            // no queued UI work for a read (re-review 3, m5).
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
            var changedPerCall = new int[n];
            var builder = new BatchShapeBuilder();
            var anyChange = false;
            var before = CategoriesOf(doc!, fresh);
            string? capWarning = null;
            string? unreadWarning = null;
            using (CallbackScope.Enter(doc!, CallbackKind.Commit))
            using (FailureScope.Begin(doc!.Application))
            {
                var group = new TransactionGroup(doc, preview.GroupName);
                try
                {
                    var busy = StartGroup(group, EnvelopePhase.Commit);
                    if (busy != null) return new CommitOutcome { Kind = CommitKind.Failed, Reason = busy.Error?.Message, Envelopes = Fill(n, busy) };

                    for (var i = 0; i < n; i++)
                    {
                        // The stored calls, re-planned now and proven identical to the approved plans by
                        // the hash above (frozen at preview time), so a tool that mutated its preview
                        // plan object cannot change what commits.
                        var run = RunCall(doc!, tools[i], contexts[i], batch.Calls[i], fresh[i], WritePhase.Commit, preview.GroupName, builder, before);
                        if (run.Failure != null)
                        {
                            RollBackIfOpen(group);
                            return Failed(batch, i, run.Failure);
                        }
                        envelopes[i] = run.Envelope!;
                        changedPerCall[i] = run.Changed;
                        if (run.Changes != null && run.Changes.Added.Count + run.Changes.Modified.Count + run.Changes.Deleted.Count > 0)
                            anyChange = true;
                    }

                    var shape = builder.Build(id => Describe(doc!, id));
                    RevitValueFingerprints.Fill(doc!, shape, builder.AddedIds);   // R75, compared with the preview's
                    var verdict = ShapeComparer.Compare(fresh, preview.Shape, shape);
                    capWarning = ValueFingerprint.CapWarning(shape);
                    if (preview.Shape != null) unreadWarning = ValueFingerprint.UnreadWarning(preview.Shape, shape);
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

                    // ok on a commit means committed and verified: a call whose tool reported no
                    // read-back is verified by the shape check that just matched (count_delta).
                    for (var i = 0; i < n; i++)
                    {
                        var e = envelopes[i];
                        if (e.Ok && e.Status != EnvelopeStatus.Noop && (e.Verified == null || e.Verified.Method == VerifyMethods.None))
                            e.Verified = new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = changedPerCall[i] };
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
            if (capWarning != null && n > 0) AddWarning(envelopes[0], capWarning);
            if (unreadWarning != null && n > 0) AddWarning(envelopes[0], unreadWarning);

            // R76: the tools' queued UI work runs now — committed and assimilated — in call order;
            // a failing step is a warning, never a failed commit. When a tool queued UI work, its
            // selection and view win: the highlight is then only measured, after that work ran,
            // so the envelope describes the selection and view the drafter actually has.
            var queued = contexts.Any(c => c.AfterCommit != null && c.AfterCommit.Count > 0);
            if (queued && AfterCommitRunner.ShouldRun(CommitKind.Committed, undoEntry: true))
                foreach (var f in AfterCommitRunner.Run(contexts.Select(c => (IReadOnlyList<Action>)c.AfterCommit).ToList()))
                    AddWarning(envelopes[f.Call], AfterCommitRunner.WarningText(f));
            Highlight(uidoc!, doc!, envelopes, builder.AddedIds, select: !queued);
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
            /// <summary>Changed elements that count (benign classes excluded).</summary>
            public int Changed;
            /// <summary>Existing elements the call modified or deleted (benign excluded).</summary>
            public List<long> Existing = new();
        }

        private static CallRun RunCall(Document doc, IWriteTool tool, ToolContext ctx, ToolCallDto call, WritePlan plan,
                                       WritePhase phase, string transactionName, BatchShapeBuilder builder,
                                       IReadOnlyDictionary<long, string> categoriesBefore)
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

                var raw = run.Changes ?? new ChangeSet();
                var benign = new HashSet<long>();
                var labelled = Classify(doc, raw, categoriesBefore, benign);
                run.Changed = labelled.Added.Count(id => !benign.Contains(id)) + labelled.Modified.Count(id => !benign.Contains(id))
                              + labelled.Deleted.Count(id => !benign.Contains(id));
                run.Existing = labelled.Modified.Concat(labelled.Deleted).Where(id => !benign.Contains(id)).ToList();
                // run_revit_code has no target ids, so on its envelope every recorded change is
                // touched (never a side effect that would trigger the hard-warning card), and for
                // the add-only judgement every recorded change is its own edit (R57). Other tools:
                // what they reported is their edit, the rest is recompute (R35).
                var reported = ReportedIds(run.Envelope!).ToList();
                var isCode = string.Equals(call.Tool, ToolNames.RunRevitCode, StringComparison.Ordinal);
                var touchedOnEnvelope = isCode
                    ? labelled.Added.Concat(labelled.Modified).Concat(labelled.Deleted).Where(id => !benign.Contains(id)).ToList()
                    : reported;
                var recorded = labelled.Added.Concat(labelled.Modified).Concat(labelled.Deleted).Where(id => !benign.Contains(id));
                builder.AddCall(
                    labelled.Added.Where(id => !benign.Contains(id)),
                    labelled.Modified.Where(id => !benign.Contains(id)),
                    labelled.Deleted.Where(id => !benign.Contains(id)),
                    labelled.CategoryOf,
                    ChangeClassifier.TouchedForApproval(call.Tool, recorded, reported));   // R57
                Decorate(run.Envelope!, labelled, benign, touchedOnEnvelope, warnings, shown, phaseName, isCode);
                return run;
            }
        }

        /// <summary>Adds what the recorder saw beyond the tool's own report (side effects),
        /// Revit's warnings and the dialogs answered, and says noop when nothing changed.</summary>
        private static void Decorate(Envelope env, ChangeSet changes, HashSet<long> benign, IReadOnlyCollection<long> touchedIds,
                                     IReadOnlyList<WarningDto> warnings, IReadOnlyList<DialogDto> dialogs, string phaseName, bool isCode)
        {
            env.Phase = phaseName;
            // Uncategorised ids already carry "class:<ClassName>" (Classify); only benign classes are ignored.
            var (touched, side) = ChangeSetMath.Split(changes, touchedIds, benign);
            if (env.Changes == null)
            {
                env.Changes = new ChangeReportDto { Touched = touched, SideEffects = side };
            }
            else if (isCode)
            {
                // Keep the code tool's own field rows; every recorded change is touched.
                var rows = env.Changes.Touched?.Modified?.Where(m => !string.IsNullOrEmpty(m.Field)).ToDictionary(m => m.Id) ?? new Dictionary<long, FieldChangeDto>();
                touched.Modified = touched.Modified.Select(m => rows.TryGetValue(m.Id, out var row) ? row : m).ToList();
                env.Changes = new ChangeReportDto { Touched = touched, SideEffects = side };
            }
            else
            {
                env.Changes.SideEffects = side;
            }

            AddWarnings(env, warnings);
            AddDialogs(env, dialogs);

            var nothing = changes.Added.Concat(changes.Modified).Concat(changes.Deleted).All(benign.Contains);
            if (nothing && env.Status == EnvelopeStatus.Done)
            {
                env.Status = EnvelopeStatus.Noop;
                if (isCode)
                {
                    env.Warnings ??= new List<WarningDto>();
                    env.Warnings.Add(new WarningDto { Text = "The code ran but changed nothing.", Severity = WarningSeverity.Warning });
                }
            }
        }

        /// <summary>A copy of <paramref name="raw"/> whose CategoryOf names every id: the recorder's
        /// category; "class:<ClassName>" for an element with no category (R43, never silently
        /// bookkeeping); for a deleted id, its category read before the batch ran, else "Other".
        /// Ids of a benign class (ChangeClassifier.BenignClasses) go into <paramref name="benign"/>.</summary>
        private static ChangeSet Classify(Document doc, ChangeSet raw, IReadOnlyDictionary<long, string> categoriesBefore, HashSet<long> benign)
        {
            var labelled = new ChangeSet { Added = raw.Added.ToList(), Modified = raw.Modified.ToList(), Deleted = raw.Deleted.ToList() };
            foreach (var id in raw.Added.Concat(raw.Modified))
            {
                raw.CategoryOf.TryGetValue(id, out var category);
                if (!string.IsNullOrWhiteSpace(category) && category != ChangeSetMath.BookkeepingCategory)
                {
                    labelled.CategoryOf[id] = category!;
                    continue;
                }
                string className;
                try { className = doc.GetElement(ElemIds.From(id))?.GetType().Name ?? "Element"; }
                catch (Exception) { className = "Element"; }
                if (ChangeClassifier.IsBenign(className)) benign.Add(id);
                labelled.CategoryOf[id] = ChangeClassifier.ClassKey(className);
            }
            foreach (var id in raw.Deleted)
            {
                if (raw.CategoryOf.TryGetValue(id, out var category) && !string.IsNullOrWhiteSpace(category)
                    && category != ChangeSetMath.BookkeepingCategory && category != ChangeSetMath.UnknownCategory)
                    labelled.CategoryOf[id] = category!;
                else if (categoriesBefore.TryGetValue(id, out var known))
                    labelled.CategoryOf[id] = known;
                else
                    labelled.CategoryOf[id] = ChangeSetMath.UnknownCategory;
            }
            return labelled;
        }

        /// <summary>Categories of the plans' existing targets, read before anything runs, so a
        /// deleted target is still counted under its category.</summary>
        private static Dictionary<long, string> CategoriesOf(Document doc, IEnumerable<WritePlan> plans)
        {
            var map = new Dictionary<long, string>();
            foreach (var id in plans.SelectMany(p => p.TargetIds ?? Array.Empty<long>()).Distinct())
            {
                try
                {
                    var e = doc.GetElement(ElemIds.From(id));
                    if (e == null) continue;
                    map[id] = e.Category?.Name is string name && name.Length > 0 ? name
                        : e is Family ? "Families"
                        : e is ElementType ? "Types"
                        : ChangeClassifier.ClassKey(e.GetType().Name);
                }
                catch (Exception)
                {
                    // An id that does not resolve keeps no category ("Other").
                }
            }
            return map;
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

        private static void AddWarning(Envelope env, string text)
        {
            env.Warnings ??= new List<WarningDto>();
            if (env.Warnings.Count < MaxWarnings) env.Warnings.Add(new WarningDto { Text = text, Severity = WarningSeverity.Warning });
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

        private static Envelope? CheckOwnership(Document doc, WritePlan plan) =>
            CheckOwnership(doc, plan.TargetIds ?? Array.Empty<long>(), "targets");

        private static Envelope? CheckOwnership(Document doc, IEnumerable<long> ids, string what)
        {
            var byOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var stale = 0;
            var editable = new List<long>();
            var total = 0;
            foreach (var id in ids.Distinct())
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
                          " " + what + " cannot be edited: " + string.Join("; ", parts) + ". Nothing was borrowed and nothing changed. " +
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
            var className = e.GetType().Name;
            if (e.Category == null && !(e is Family) && !(e is ElementType) && ChangeClassifier.IsBenign(className)) return null;
            return new AddedShape
            {
                Id = id,
                Category = e.Category?.Name is string name && name.Length > 0 ? name
                    : e is Family ? "Families"
                    : e is ElementType ? "Types"
                    : ChangeClassifier.ClassKey(className),
                TypeId = (long)e.GetTypeId().Value,
                LevelId = (long)e.LevelId.Value,
                BboxMm = box,
                ClassName = className,
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
        /// undo entry sits on top of ours; spec §7.6). With <paramref name="select"/> false (a
        /// tool's AfterCommit work owns the selection) it only measures what is selected and
        /// visible now.</summary>
        private static void Highlight(UIDocument uidoc, Document doc, Envelope[] envelopes, IReadOnlyList<long> added, bool select)
        {
            try
            {
                var per = envelopes.Select(e => TouchedIds(e)).ToList();
                var all = new HashSet<long>(per.SelectMany(s => s));
                all.UnionWith(added);
                var ids = all.Select(ElemIds.From).Where(id => doc.GetElement(id) != null).ToList();
                if (ids.Count == 0) return;
                if (select) uidoc.Selection.SetElementIds(ids);
                // What is selected NOW: ours, or what a tool's AfterCommit work selected instead.
                var selectedNow = new HashSet<long>(uidoc.Selection.GetElementIds().Select(id => (long)id.Value));

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
                    envelopes[i].Highlight = new HighlightDto { Selected = mine.Count(selectedNow.Contains), VisibleInView = mine.Count(visible.Contains) };
                }
            }
            catch (Exception)
            {
                // Highlight is evidence, not the change: never fail a committed batch over it.
            }
        }

        /// <summary>Every host id the tool reported as its own change (added, modified, deleted).</summary>
        private static IEnumerable<long> ReportedIds(Envelope e)
        {
            var t = e.Changes?.Touched;
            if (t == null) return Enumerable.Empty<long>();
            return t.Added.Where(r => r.Link == null).Select(r => r.Id)
                .Concat(t.Modified.Select(m => m.Id))
                .Concat(t.Deleted.Where(r => r.Link == null).Select(r => r.Id));
        }

        private static IEnumerable<long> TouchedIds(Envelope e)
        {
            var t = e.Changes?.Touched;
            if (t == null) return Enumerable.Empty<long>();
            return t.Added.Where(r => r.Link == null).Select(r => r.Id).Concat(t.Modified.Select(m => m.Id));
        }
    }
}
