#nullable enable
// Whether the copilot pane is shown, as several Revit and WPF signals report it
// (the dockable frame's visibility event, the panel's IsVisible, and a check of
// DockablePane.IsShown on the first Idling, which covers Revit starting with
// the pane hidden or restored). Each change is forwarded once; repeats are
// dropped so W2's SnapshotService.SetPaneOpen(true) (which also prewarms the
// model overview) does not run again for the same show. Revit-free.

using System;
using System.Diagnostics;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class PaneOpenState
    {
        private readonly object _gate = new object();
        private readonly Action<bool> _apply;
        private bool? _open;

        public PaneOpenState(Action<bool> apply)
        {
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }

        /// <summary>Null until the first report.</summary>
        public bool? Open { get { lock (_gate) return _open; } }

        /// <summary>Report the pane shown or hidden. True when it changed (and was forwarded).</summary>
        public bool Report(bool open)
        {
            lock (_gate)
            {
                if (_open == open) return false;
                _open = open;
                try { _apply(open); }
                catch (Exception ex) { Debug.WriteLine("[BINA] copilot pane open=" + open + ": " + ex.Message); }
            }
            return true;
        }
    }
}
