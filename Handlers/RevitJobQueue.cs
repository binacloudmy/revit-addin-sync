using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Thrown into a job's task when Revit refuses to schedule the ExternalEvent
    /// (Denied / TimedOut), e.g. while another command or a modal dialog is active.
    /// </summary>
    public sealed class RevitBusyException : Exception
    {
        public RevitBusyException()
            : base("Revit is busy. Close any open dialog or command and try again.") { }
    }

    /// <summary>
    /// Revit-free core of <see cref="RevitDispatcher"/>. Panes enqueue work and get
    /// a Task; Revit runs the work inside an ExternalEvent, the only place the model
    /// may be read or written. TContext is UIApplication in the add-in and any
    /// stand-in in tests (the test host cannot load RevitAPI).
    ///
    /// Rules:
    /// - Jobs run in enqueue order. One failing job faults only its own task.
    /// - A job queued while a drain is running waits for the next drain, so one
    ///   Execute call can never loop forever.
    /// - Tasks complete with RunContinuationsAsynchronously: awaiting code resumes
    ///   after Execute returns, never inside Revit's API context.
    /// - Jobs must return plain data, never Revit objects (Element, Document...),
    ///   because the caller uses the result outside the API context.
    /// </summary>
    public sealed class RevitJobQueue<TContext>
    {
        private interface IJob
        {
            bool IsCompleted { get; }
            void Run(TContext context);
            void Fail(Exception ex);
            void Cancel();
        }

        private sealed class Job<T> : IJob
        {
            private readonly Func<TContext, T> _work;
            public readonly TaskCompletionSource<T> Tcs =
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Job(Func<TContext, T> work) { _work = work; }

            public bool IsCompleted => Tcs.Task.IsCompleted;

            public void Run(TContext context)
            {
                try { Tcs.TrySetResult(_work(context)); }
                catch (Exception ex) { Tcs.TrySetException(ex); }
            }

            public void Fail(Exception ex) => Tcs.TrySetException(ex);
            public void Cancel() => Tcs.TrySetCanceled();
        }

        private readonly object _gate = new object();
        private readonly Queue<IJob> _jobs = new Queue<IJob>();
        private readonly Func<bool> _raise;

        /// <param name="raise">Asks Revit for an Execute pass. Returns false when Revit
        /// refused (ExternalEventRequest.Denied or TimedOut).</param>
        public RevitJobQueue(Func<bool> raise)
        {
            _raise = raise ?? throw new ArgumentNullException(nameof(raise));
        }

        public int PendingCount
        {
            get { lock (_gate) return _jobs.Count; }
        }

        public Task<T> Enqueue<T>(Func<TContext, T> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            var job = new Job<T>(work);
            lock (_gate) _jobs.Enqueue(job);

            bool accepted;
            try { accepted = _raise(); }
            catch (Exception ex) { job.Fail(ex); return job.Tcs.Task; }

            // A refused job stays in the queue but is already completed, so Drain skips it.
            if (!accepted) job.Fail(new RevitBusyException());
            return job.Tcs.Task;
        }

        public Task Enqueue(Action<TContext> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            return Enqueue<bool>(ctx => { work(ctx); return true; });
        }

        /// <summary>Runs the jobs that were queued when the drain started. Called from
        /// IExternalEventHandler.Execute. Returns how many jobs actually ran.</summary>
        public int Drain(TContext context)
        {
            List<IJob> batch;
            lock (_gate)
            {
                batch = new List<IJob>(_jobs);
                _jobs.Clear();
            }

            var ran = 0;
            foreach (var job in batch)
            {
                if (job.IsCompleted) continue; // refused or cancelled
                job.Run(context);
                ran++;
            }
            return ran;
        }

        /// <summary>Cancels every job that has not run yet (add-in shutdown).</summary>
        public void CancelAll()
        {
            List<IJob> batch;
            lock (_gate)
            {
                batch = new List<IJob>(_jobs);
                _jobs.Clear();
            }
            foreach (var job in batch) job.Cancel();
        }
    }
}
