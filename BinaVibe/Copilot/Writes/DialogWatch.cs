#nullable enable
// Applies DialogPolicy to Revit's DialogBoxShowing while one of our write
// callbacks runs (spec §7.10). The engine opens a Collect() scope around
// each call; dialogs answered or left to the drafter during it land in that
// call's envelope `dialogs`. An unknown dialog raises BusyDialogShown so the
// pane can show progress{revit_busy, text} (W5 subscribes); the call then
// waits for the drafter, since a running callback cannot be aborted.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public static class DialogWatch
    {
        private static readonly object Gate = new();
        private static bool _attached;

        [ThreadStatic] private static Collector? _current;

        /// <summary>A dialog with no safe answer is waiting for the drafter during a write
        /// (raised on Revit's thread; the text is ready for a progress line).</summary>
        public static event Action<string>? BusyDialogShown;

        public static void Attach(UIControlledApplication app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            lock (Gate)
            {
                if (_attached) return;
                app.DialogBoxShowing += OnShowing;
                _attached = true;
            }
        }

        /// <summary>Starts collecting the dialogs of one write call on this thread.</summary>
        public static Collector Collect()
        {
            var c = new Collector(_current);
            _current = c;
            return c;
        }

        public sealed class Collector : IDisposable
        {
            private readonly Collector? _outer;
            private readonly List<DialogDto> _seen = new();

            internal Collector(Collector? outer) { _outer = outer; }

            public IReadOnlyList<DialogDto> Snapshot() => _seen.ToList();

            internal void Add(DialogDto dialog) => _seen.Add(dialog);

            public void Dispose()
            {
                if (ReferenceEquals(_current, this)) _current = _outer;
            }
        }

        private static void OnShowing(object? sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                var collector = _current;
                var message = e is TaskDialogShowingEventArgs task ? task.Message
                    : e is MessageBoxShowingEventArgs box ? box.Message
                    : null;
                var decision = DialogPolicy.Decide(e.DialogId, message, inOurCallback: collector != null);
                if (decision.Action == DialogAction.Ignore || collector == null) return;

                if (decision.Action == DialogAction.Answer)
                {
                    bool answered;
                    try { answered = e.OverrideResult(decision.ResultCode); }
                    catch (Exception) { answered = false; }
                    if (answered)
                    {
                        collector.Add(new DialogDto { Id = e.DialogId ?? "", Text = decision.Text, AnsweredWith = decision.AnsweredWith });
                        return;
                    }
                    decision = DialogPolicy.Decide(null, message ?? decision.Text, inOurCallback: true);
                }

                collector.Add(new DialogDto { Id = e.DialogId ?? "", Text = decision.Text, AnsweredWith = null });
                BusyDialogShown?.Invoke(decision.Text);
            }
            catch (Exception)
            {
                // Never break Revit's dialog handling; the drafter can still answer it.
            }
        }
    }
}
