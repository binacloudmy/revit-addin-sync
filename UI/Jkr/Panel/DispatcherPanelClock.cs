using System;
using System.Windows.Threading;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary><see cref="IPanelClock"/> on the WPF dispatcher: callbacks run on
    /// the pane's UI thread, so PanelCore state changes are safe to bind.</summary>
    public sealed class DispatcherPanelClock : IPanelClock
    {
        private readonly Dispatcher _dispatcher;

        public DispatcherPanelClock(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var timer = new DispatcherTimer(delay, DispatcherPriority.Normal, (s, e) =>
            {
                ((DispatcherTimer)s).Stop();
                action();
            }, _dispatcher);
            timer.Start();
            return new Stopper(timer);
        }

        private sealed class Stopper : IDisposable
        {
            private DispatcherTimer _timer;
            public Stopper(DispatcherTimer timer) { _timer = timer; }
            public void Dispose() { _timer?.Stop(); _timer = null; }
        }
    }
}
