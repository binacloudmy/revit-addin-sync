#nullable enable
// One Undo per turn (spec §7.6; plan §5 W3 Task 5). Body owned by W3.
//
// Keeps UndoModel current from Revit's events and runs the turn's Undo:
//   - the write engine reports every assimilated batch (NoteBatchCommitted);
//   - DocumentChanged outside our callback scopes: a commit is foreign (blocks
//     what lies under it), an undo/redo is either the one we posted or the
//     drafter's own (blocks); sync with central / reload latest block; closing
//     forgets the document;
//   - UndoTurnAsync posts ONE PostCommand(Undo) per Idling, newest entry first.
//     Before EVERY post: the turn's Undo is still allowed (nothing foreign
//     happened since the last step), the next name is the newest live entry,
//     the active document is the entries' document and CanPostCommand is true.
//     After it: DocumentChanged(TransactionUndone) must report exactly the
//     expected "BINA: …" name and nothing else; otherwise it posts Redo at once
//     and stops. No compensating "reverse" transactions.
//   - UndoNewestOursAsync (the legacy receipt's [Undo]) undoes ONE entry, the
//     newest, only if what Revit undid is all BINA-owned.
// Attach (ruling R4: W5 calls it from App.OnStartup) also wires the write
// engine's Revit-thread dispatcher and the dialog policy.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using BinaVibe.Copilot.Writes;

namespace BinaVibe.Copilot.Undo
{
    public static class UndoService
    {
        private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

        private static readonly object Gate = new();
        private static readonly UndoModel Model = new();
        private static bool _attached;
        private static Job? _job;

        private sealed class Job
        {
            /// <summary>The turn whose entries are undone; null for the legacy newest-entry undo.</summary>
            public string? TurnId;
            /// <summary>Accept any BINA-owned undone name (legacy; the group name is unknown).</summary>
            public bool OursOnly;
            /// <summary>Set when something foreign happened between our steps.</summary>
            public string? Blocked;
            public string DocKey = "";
            public Document? Doc;
            public readonly Queue<string> Names = new();
            public int Total;
            public readonly List<string> Undone = new();
            public string? InFlight;
            public bool NeedsRedo;
            public string? MismatchText;
            public DateTime Deadline;
            public CancellationToken Ct;
            public readonly TaskCompletionSource<Envelope> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static void Attach(Autodesk.Revit.UI.UIControlledApplication app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            lock (Gate)
            {
                if (_attached) return;
                RevitThread.Attach(app);
                DialogWatch.Attach(app);
                app.Idling += OnIdling;
                var events = app.ControlledApplication;
                events.DocumentChanged += OnDocumentChanged;
                events.DocumentSynchronizedWithCentral += (s, e) => Disable(e.Document, "the model was synchronized with central");
                events.DocumentReloadedLatest += (s, e) => Disable(e.Document, "the model was reloaded from central");
                events.DocumentClosing += (s, e) => Forget(e.Document);
                _attached = true;
            }
        }

        public static bool CanUndoTurn(string turnId)
        {
            lock (Gate)
            {
                if (_job != null) return false;
                return Model.CanUndoTurn(turnId, out _, out _);
            }
        }

        /// <summary>Why the turn's Undo is not available (for the card), or null when it is.</summary>
        public static string? WhyNotUndo(string turnId)
        {
            lock (Gate)
            {
                if (_job != null) return "An undo is already in progress; wait for it to finish.";
                return Model.CanUndoTurn(turnId, out _, out var reason) ? null : reason;
            }
        }

        public static System.Threading.Tasks.Task<Envelope> UndoTurnAsync(string turnId, System.Threading.CancellationToken ct)
        {
            lock (Gate)
            {
                if (!_attached)
                    return Task.FromResult(WriteEnvelopes.Failure(ErrorCodes.RevitBusy, "Undo is not attached to Revit in this session; use Revit's Undo list.", EnvelopePhase.Commit));
                if (_job != null)
                    return Task.FromResult(WriteEnvelopes.Failure(ErrorCodes.RevitBusy, "An undo is already in progress; wait for it to finish.", EnvelopePhase.Commit));
                if (!Model.CanUndoTurn(turnId, out var docKey, out var reason))
                    return Task.FromResult(WriteEnvelopes.Failure(ErrorCodes.NotAllowed, reason ?? "This turn cannot be undone from here." + UndoModel.UseRevitUndoList, EnvelopePhase.Commit));
                return Start(new Job { DocKey = docKey!, TurnId = turnId, Ct = ct }, Model.PlanUndo(turnId));
            }
        }

        /// <summary>The write engine assimilated a batch into one undo entry named <paramref name="groupName"/>.</summary>
        internal static void NoteBatchCommitted(Document doc, string turnId, string groupName)
        {
            try { Model.Pushed(Key(doc), turnId, groupName); }
            catch (Exception) { /* an unknown key only means Undo stays off for it */ }
        }

        /// <summary>The legacy receipt's [Undo]: ONE Revit Undo of the newest entry, accepted only
        /// when everything Revit undid is BINA-owned (else Redo at once). The legacy recorder cannot
        /// tell inner transactions from one assimilated group, so it never undoes more than one
        /// entry. Call on Revit's thread; the task completes with the real outcome.</summary>
        internal static Task<Envelope> UndoNewestOursAsync(Document doc, CancellationToken ct)
        {
            lock (Gate)
            {
                if (!_attached)
                    return Task.FromResult(WriteEnvelopes.Failure(ErrorCodes.RevitBusy, "Undo is not attached to Revit in this session; use Revit's Undo list.", EnvelopePhase.Commit));
                if (_job != null)
                    return Task.FromResult(WriteEnvelopes.Failure(ErrorCodes.RevitBusy, "An undo is already in progress; wait for it to finish.", EnvelopePhase.Commit));
                return Start(new Job { DocKey = Key(doc), Doc = doc, OursOnly = true, Ct = ct }, new[] { "the newest BINA change" });
            }
        }

        private static Task<Envelope> Start(Job job, IReadOnlyList<string> names)
        {
            foreach (var n in names) job.Names.Enqueue(n);
            job.Total = names.Count;
            _job = job;
            return job.Done.Task;
        }

        // ------------------------------------------------------------------
        // Idling: one post per Idling
        // ------------------------------------------------------------------

        private static void OnIdling(object? sender, IdlingEventArgs e)
        {
            Job? job;
            lock (Gate) job = _job;
            if (job == null || sender is not UIApplication app) return;

            try
            {
                if (job.NeedsRedo)
                {
                    job.NeedsRedo = false;
                    var redo = RevitCommandId.LookupPostableCommandId(PostableCommand.Redo);
                    var posted = false;
                    if (app.CanPostCommand(redo))
                    {
                        Model.ExpectRedo(job.DocKey);
                        app.PostCommand(redo);
                        posted = true;
                    }
                    Finish(job, Failure(job, ErrorCodes.DiffDrift,
                        job.MismatchText + (posted ? " It was redone at once." : " Revit could not redo it automatically; use Redo in Revit.") +
                        " Undo stopped." + UndoModel.UseRevitUndoList));
                    return;
                }

                if (job.InFlight != null)
                {
                    if (DateTime.UtcNow > job.Deadline)
                    {
                        Model.ClearExpectation(job.DocKey);
                        Model.Disable(job.DocKey, "Revit did not report the posted Undo");
                        Finish(job, Failure(job, ErrorCodes.Timeout, "Revit did not report the posted Undo of '" + job.InFlight + "'; check Revit's Undo list."));
                    }
                    else
                    {
                        e.SetRaiseWithoutDelay();
                    }
                    return;
                }

                if (job.Names.Count == 0) { Finish(job, Success(job)); return; }
                if (job.Ct.IsCancellationRequested) { Finish(job, Failure(job, ErrorCodes.Timeout, "Undo was cancelled.")); return; }
                if (job.Blocked != null) { Finish(job, Failure(job, ErrorCodes.NotAllowed, job.Blocked + UndoModel.UseRevitUndoList)); return; }

                // Re-check before EVERY post: a drafter commit between two of our steps
                // must stop us, never be undone and redone.
                if (job.TurnId != null)
                {
                    if (!Model.CanUndoTurn(job.TurnId, out var turnDoc, out var why) || turnDoc != job.DocKey)
                    {
                        Finish(job, Failure(job, ErrorCodes.NotAllowed, why ?? "This turn can no longer be undone from here." + UndoModel.UseRevitUndoList));
                        return;
                    }
                    var newest = Model.PlanUndo(job.TurnId).FirstOrDefault();
                    if (newest != job.Names.Peek())
                    {
                        Finish(job, Failure(job, ErrorCodes.NotAllowed, "The newest BINA change is no longer '" + job.Names.Peek() + "'." + UndoModel.UseRevitUndoList));
                        return;
                    }
                }

                var doc = app.ActiveUIDocument?.Document;
                if (doc == null || Key(doc) != job.DocKey || (job.Doc != null && !SameDocument(doc, job.Doc)))
                {
                    Finish(job, Failure(job, ErrorCodes.DocChanged, "The active document is not the one these changes were made in; switch back to it, then try Undo again."));
                    return;
                }
                job.Doc ??= doc;

                var undo = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
                if (!app.CanPostCommand(undo))
                {
                    Finish(job, Failure(job, ErrorCodes.RevitBusy, "Revit cannot undo right now (a command or dialog is active); try again in a moment."));
                    return;
                }

                var name = job.Names.Peek();
                if (job.OursOnly) Model.ExpectUndoOfOurs(job.DocKey);
                else Model.ExpectUndo(job.DocKey, name);
                job.InFlight = name;
                job.Deadline = DateTime.UtcNow + StepTimeout;
                app.PostCommand(undo);
                e.SetRaiseWithoutDelay();
            }
            catch (Exception ex)
            {
                Model.ClearExpectation(job.DocKey);
                Finish(job, Failure(job, ErrorCodes.RuntimeError, "Undo failed in the add-in (" + ex.GetType().Name + ": " + ex.Message + ")."));
            }
        }

        // ------------------------------------------------------------------
        // DocumentChanged outside our callbacks
        // ------------------------------------------------------------------

        private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null || doc.IsLinked) return;
                if (InOurScope(doc)) return;   // our own preview / read / look / commit

                var key = Key(doc);
                var names = e.GetTransactionNames()?.ToList() ?? new List<string>();
                switch (e.Operation)
                {
                    case UndoOperation.TransactionCommitted:
                        Model.ForeignChange(key, names);
                        BlockJob(key, "The model changed while Undo was running ('" + string.Join(", ", names) + "').");
                        break;
                    case UndoOperation.TransactionUndone:
                        OnUndone(key, names);
                        break;
                    case UndoOperation.TransactionRedone:
                        if (Model.Redone(key, names) != UndoObservation.Expected)
                            BlockJob(key, "Revit's Redo was used while Undo was running.");
                        break;
                }
            }
            catch (Exception)
            {
                // A read-only notification handler must never throw into Revit.
            }
        }

        private static void OnUndone(string key, List<string> names)
        {
            var observed = Model.Undone(key, names);
            Job? job;
            lock (Gate) job = _job;
            if (job == null || job.DocKey != key) return;
            if (job.InFlight == null)
            {
                job.Blocked = "Revit's Undo was used directly while Undo was running.";
                return;
            }

            if (observed == UndoObservation.Expected)
            {
                job.Undone.Add(job.Names.Dequeue());
                job.InFlight = null;
                return;
            }
            job.MismatchText = "Revit undid '" + string.Join(", ", names) + "' instead of '" + job.InFlight + "'.";
            job.InFlight = null;
            job.NeedsRedo = true;
        }

        // ------------------------------------------------------------------

        private static Envelope Success(Job job) => new Envelope
        {
            Ok = true,
            Status = EnvelopeStatus.Done,
            Phase = EnvelopePhase.Commit,
            Summary = EnvelopeFactory.Shorten(
                job.OursOnly
                    ? "Undid the newest BINA change in Revit. If the batch made more than one, use Revit's Undo list for the rest."
                    : "Undid " + job.Undone.Count + " BINA change(s) in Revit: " + string.Join("; ", job.Undone) + ".",
                EnvelopeLimits.SummaryMaxChars),
            Count = job.Undone.Count,
            Verified = new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = job.Undone.Count },
        };

        private static Envelope Failure(Job job, string code, string message)
        {
            var done = job.Undone.Count;
            var text = done > 0
                ? message + " " + done + " of " + job.Total + " change(s) were undone before it stopped."
                : message + " Nothing was undone.";
            var e = WriteEnvelopes.Failure(code, text, EnvelopePhase.Commit);
            if (done > 0) e.Status = EnvelopeStatus.Partial;
            return e;
        }

        private static void Finish(Job job, Envelope result)
        {
            lock (Gate)
            {
                if (ReferenceEquals(_job, job)) _job = null;
            }
            job.Done.TrySetResult(result);
        }

        private static void BlockJob(string key, string reason)
        {
            Job? job;
            lock (Gate) job = _job;
            if (job != null && job.DocKey == key && job.Blocked == null) job.Blocked = reason;
        }

        private static void Disable(Document? doc, string reason)
        {
            try { if (doc != null) Model.Disable(Key(doc), reason); }
            catch (Exception) { /* notification handlers never throw */ }
        }

        private static void Forget(Document? doc)
        {
            try { if (doc != null) Model.Forget(Key(doc)); }
            catch (Exception) { /* notification handlers never throw */ }
        }

        private static bool InOurScope(Document doc)
        {
            try { return CallbackScope.Current(doc) != null; }
            catch (NotImplementedException) { return false; }   // W1's scope not merged yet
        }

        private static bool SameDocument(Document a, Document b)
        {
            try { return a.Equals(b); }
            catch (Exception) { return false; }
        }

        private static string Key(Document doc) => RevitDocIdentity.Of(doc).Key;
    }
}
