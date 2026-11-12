#nullable enable
// Live test of 0.0.107: after opening a floor plan the pane Home still named the previous view for minutes. Home now refreshes on
// every view / selection / document change, 300 ms after the last one.
using System;
using System.Collections.Generic;
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class HomeScope0109Tests
    {
        private sealed class FakeTimer
        {
            public readonly List<(TimeSpan Delay, Action Action, bool[] Cancelled)> Scheduled = new();
            public IDisposable Schedule(TimeSpan delay, Action action)
            {
                var cancelled = new bool[1];
                Scheduled.Add((delay, action, cancelled));
                return new Handle(cancelled);
            }
            public void RunDue() { foreach (var s in Scheduled.ToArray()) if (!s.Cancelled[0]) { s.Cancelled[0] = true; s.Action(); } }
            private sealed class Handle : IDisposable { private readonly bool[] _c; public Handle(bool[] c) { _c = c; } public void Dispose() { _c[0] = true; } }
        }

        [Fact]
        public void ManyTriggers_FireOnce_AfterTheLastOne()
        {
            var timer = new FakeTimer();
            var fired = 0;
            var d = new Debounce(TimeSpan.FromMilliseconds(300), () => fired++, timer.Schedule);
            for (var i = 0; i < 40; i++) d.Trigger();   // a drag selection
            Assert.True(d.IsPending);
            Assert.Equal(0, fired);
            timer.RunDue();
            Assert.Equal(1, fired);
            Assert.False(d.IsPending);
            Assert.All(timer.Scheduled, s => Assert.Equal(TimeSpan.FromMilliseconds(300), s.Delay));
        }

        [Fact]
        public void ATriggerAfterAFire_FiresAgain()
        {
            var timer = new FakeTimer();
            var fired = 0;
            var d = new Debounce(TimeSpan.FromMilliseconds(300), () => fired++, timer.Schedule);
            d.Trigger(); timer.RunDue();
            d.Trigger(); timer.RunDue();
            Assert.Equal(2, fired);
        }

        [Fact]
        public void ACancel_DropsAWaitingFire_EvenIfTheTimerStillTicks()
        {
            var timer = new FakeTimer();
            var fired = 0;
            var d = new Debounce(TimeSpan.FromMilliseconds(300), () => fired++, timer.Schedule);
            d.Trigger();
            var stale = timer.Scheduled[0].Action;
            d.Cancel();
            stale();           // a timer that already queued its tick
            timer.RunDue();
            Assert.Equal(0, fired);
            Assert.False(d.IsPending);
        }

        [Fact]
        public void AnOldTimersTick_DoesNotFireWhileANewerTriggerWaits()
        {
            var timer = new FakeTimer();
            var fired = 0;
            var d = new Debounce(TimeSpan.FromMilliseconds(300), () => fired++, timer.Schedule);
            d.Trigger();
            var first = timer.Scheduled[0].Action;
            d.Trigger();
            first();           // the first timer's tick arrives late
            Assert.Equal(0, fired);
            timer.RunDue();
            Assert.Equal(1, fired);
        }

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheSnapshotService_RaisesScopeChanged_OnViewSelectionAndDocumentEvents()
        {
            var s = Src("BinaVibe/Copilot/Read/SnapshotService.cs");
            Assert.Contains("public static event Action? ScopeChanged;", s);
            foreach (var handler in new[] { "OnViewActivated", "OnSelectionChanged", "OnDocumentOpened", "OnDocumentClosed" })
            {
                var from = s.IndexOf("private static void " + handler, StringComparison.Ordinal);
                Assert.True(from > 0, handler);
                var body = s.Substring(from, Math.Min(500, s.Length - from));
                Assert.Contains("RaiseScopeChanged();", body);
            }
        }

        [Fact]
        public void TheChatView_FollowsRevit_WhileItIsShown()
        {
            var chat = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("HookScope(); Rebuild();", chat);
            Assert.Contains("UnhookScope();", chat);
            var home = Src("UI/Copilot/Screens/ChatView.Home.cs");
            Assert.Contains("SnapshotService.ScopeChanged += OnScopeChanged;", home);
            Assert.Contains("SnapshotService.ScopeChanged -= OnScopeChanged;", home);
            Assert.Contains("TimeSpan.FromMilliseconds(300)", home);
            Assert.Contains("_scopeAgain", home);                 // a change during a read means one more read
            Assert.Contains("Vm.Thread.Count > 0", home);          // only while Home is what is showing
        }
    }
}
