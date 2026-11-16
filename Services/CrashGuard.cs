#nullable enable
// AI-327: an exception from the add-in must never take Revit down with the
// drafter's unsaved work.
//
// Revit's WPF panes (the copilot pane among them) run on Revit's own UI
// dispatcher. An exception that escapes one of our event handlers or `async
// void` continuations reaches Dispatcher.UnhandledException, and unhandled
// there it can end the Revit process. The guard marks it handled when OUR
// code is on its stack (CrashGuardPolicy), logs it to crash-guard.log (Send
// diagnostics collects and scrubs *.log) and sends the exception type to
// telemetry. Exceptions with none of our frames are left to Revit, as before.
//
// AppDomain.UnhandledException (a background thread) cannot be stopped; it is
// logged so the next crash leaves evidence. An unobserved faulted Task of ours
// is marked observed (it never ends the process on .NET 8+, but can on .NET
// Framework under ThrowUnobservedTaskExceptions).

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace RevitWebAppSync.Services
{
    public static class CrashGuard
    {
        private const int MaxTrackedPerSession = 20;
        private const long MaxLogBytes = 512 * 1024;

        private static readonly object Gate = new object();
        private static readonly Assembly Ours = typeof(CrashGuard).Assembly;
        private static bool _installed;
        private static int _tracked;

        /// <summary>Subscribe once, on Revit's UI thread, at startup. Never throws.</summary>
        public static void Install(Dispatcher? ui)
        {
            lock (Gate)
            {
                if (_installed) return;
                _installed = true;
            }
            try { if (ui != null) ui.UnhandledException += OnDispatcherException; }
            catch (Exception ex) { Debug.WriteLine("[BINA] crash guard (dispatcher): " + ex.Message); }
            try { AppDomain.CurrentDomain.UnhandledException += OnDomainException; }
            catch (Exception ex) { Debug.WriteLine("[BINA] crash guard (domain): " + ex.Message); }
            try { TaskScheduler.UnobservedTaskException += OnUnobservedTask; }
            catch (Exception ex) { Debug.WriteLine("[BINA] crash guard (tasks): " + ex.Message); }
        }

        private static void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.Handled || !CrashGuardPolicy.ShouldHandle(e.Exception, Ours)) return;
                e.Handled = true;
                Report("dispatcher_handled", e.Exception, flush: false);
            }
            catch (Exception) { /* the guard itself must never throw */ }
        }

        private static void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                // Cannot be stopped: leave the evidence before the process ends.
                Report(CrashGuardPolicy.ShouldHandle(ex, Ours) ? "fatal_ours" : "fatal_other", ex, flush: e.IsTerminating);
            }
            catch (Exception) { }
        }

        private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                if (!CrashGuardPolicy.ShouldHandle(e.Exception, Ours)) return;
                e.SetObserved();
                Report("task_observed", e.Exception, flush: false);
            }
            catch (Exception) { }
        }

        private static void Report(string stage, Exception? ex, bool flush)
        {
            Debug.WriteLine("[BINA] crash guard " + stage + ": " + ex);
            WriteLog(stage, ex);
            // Throttled: a handler that throws on every layout pass must not flood telemetry.
            if (Interlocked.Increment(ref _tracked) <= MaxTrackedPerSession || flush)
                TelemetryService.Track("crash_guard", stage, new { error_class = ex?.GetType().Name ?? "unknown" });
            if (flush) TelemetryService.FlushBlocking(TimeSpan.FromSeconds(2));
        }

        /// <summary>%LocalAppData%\Bina\RevitSync\crash-guard.log, restarted past 512 KB.</summary>
        private static void WriteLog(string stage, Exception? ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "crash-guard.log");
                var text = new StringBuilder()
                    .Append(DateTime.UtcNow.ToString("o")).Append(' ').Append(stage).AppendLine()
                    .AppendLine(ex?.ToString() ?? "(no exception object)")
                    .AppendLine()
                    .ToString();
                lock (Gate)
                {
                    if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes) File.Delete(path);
                    File.AppendAllText(path, text);
                }
            }
            catch (Exception) { /* logging is best effort */ }
        }
    }
}
