using System;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Ports
{
    /// <summary>RevitThread.RunAsync with a stopwatch: the click's line says how long the work waited for Revit's turn and how
    /// long it ran. A failure is logged and swallowed (a button never throws into the pane).</summary>
    internal static class TracedRevit
    {
        public static Task<T> Run<T>(string label, int count, Func<UIApplication, T> body, T fallback)
        {
            var op = ClickTrace.Begin(label, count);
            Task<T> task;
            try
            {
                task = BinaVibe.Copilot.Writes.RevitThread.RunAsync<T>(app =>
                {
                    op.Started();
                    try { return body(app); }
                    catch (Exception ex) { op.Note = "failed: " + ex.Message; return fallback; }
                }, default);
                op.HandedOff();
            }
            catch (Exception ex)
            {
                op.HandedOff(); op.Started();
                ClickTrace.End(op, "not queued: " + ex.Message);
                return Task.FromResult(fallback);
            }
            return task.ContinueWith(t =>
            {
                ClickTrace.End(op, t.IsFaulted ? "revit busy: " + t.Exception?.GetBaseException().Message : "");
                return t.IsFaulted ? fallback : t.Result;
            }, TaskScheduler.Default);
        }
    }
}
