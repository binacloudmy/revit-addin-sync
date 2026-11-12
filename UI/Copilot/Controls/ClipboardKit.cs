#nullable enable
// Put text on the clipboard WITHOUT holding Revit's UI thread. Clipboard.SetText retries while another program holds the
// clipboard (10 tries, 100 ms apart) and flushes it: the latency probe of 0.0.106 measured 3.7 s and 1.3 s freezes on Copy
// table. The text is built and set on a short-lived STA thread, so a locked clipboard delays that thread, not Revit.
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class ClipboardKit
    {
        public static Task<bool> CopyAsync(Func<string> text, string label, int count = 0)
        {
            var op = ClickTrace.Begin(label, count);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var ok = false;
                try
                {
                    op.HandedOff(); op.Started();
                    var t = text() ?? "";
                    Clipboard.SetDataObject(t, true);   // retries while another program holds the clipboard: here it only delays this thread
                    ok = true;
                }
                catch (Exception ex) { op.Note = "failed: " + ex.Message; }
                ClickTrace.End(op);
                done.TrySetResult(ok);
            }) { IsBackground = true, Name = "bina-clipboard" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return done.Task;
        }
    }
}
