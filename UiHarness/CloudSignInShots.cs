using System;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BinaVibe.Auth;
using RevitWebAppSync.UI;

namespace UiHarness
{
    /// <summary>
    /// Renders CloudSignInWindow to PNG at each step of "Login to CDE", driven
    /// by CloudSignInStubHandler — no backend, no Revit, no visible window.
    /// Invoked by `UiHarness --shot-signin &lt;dir&gt;`.
    ///
    /// Each state is reached by filling the boxes and clicking the real buttons,
    /// so a broken step shows up as a wrong picture. The outcome of every walk
    /// is written to signin-outcomes.txt beside the images.
    /// </summary>
    internal static class CloudSignInShots
    {
        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "signin-outcomes.txt");
            File.WriteAllText(log, "");

            Walk(dir, log, "signin-empty.png", prefill: null, steps: w => { });

            Walk(dir, log, "signin-missing-password.png", prefill: "drafter@bina.cloud", steps: w => Submit(w));

            Walk(dir, log, "signin-refused.png", prefill: "drafter@bina.cloud", steps: w =>
            {
                Password(w, "wrong");
                Submit(w);
            });

            Walk(dir, log, "signin-code-step.png", prefill: "2fa@bina.cloud", steps: w =>
            {
                Password(w, "correct horse");
                Submit(w);
            });

            Walk(dir, log, "signin-code-refused.png", prefill: "2fa@bina.cloud", steps: w =>
            {
                Password(w, "correct horse");
                Submit(w);
                Code(w, "000000");
                Submit(w);
            });

            Walk(dir, log, "signin-code-resent.png", prefill: "2fa@bina.cloud", steps: w =>
            {
                Password(w, "correct horse");
                Submit(w);
                Click(w, "ResendLink");
            });

            // The two ways through: straight in, and in after the emailed code.
            // The window closes itself, so there is nothing left to picture —
            // the outcome line is the evidence.
            Walk(dir, log, null, prefill: "drafter@bina.cloud", steps: w =>
            {
                Password(w, "correct horse");
                Submit(w);
            });

            Walk(dir, log, null, prefill: "2fa@bina.cloud", steps: w =>
            {
                Password(w, "correct horse");
                Submit(w);
                Code(w, CloudSignInStubHandler.GoodCode);
                Submit(w);
            });

            // "Use a different account" must drop the challenge and ask again.
            Walk(dir, log, "signin-back-to-credentials.png", prefill: "2fa@bina.cloud", steps: w =>
            {
                Password(w, "correct horse");
                Submit(w);
                Click(w, "BackLink");
            });
        }

        private static void Walk(string dir, string log, string file, string prefill, Action<CloudSignInWindow> steps)
        {
            var signIn = new BinaPasswordSignIn(
                "https://harness.invalid", new HttpClient(new CloudSignInStubHandler()));
            int failures = 0;
            bool closed = false;

            var win = new CloudSignInWindow(signIn, prefill, _ => failures++)
            {
                WindowStyle = WindowStyle.None,
                Left = -4000,
                Top = -4000
            };
            win.Closed += (_, __) => closed = true;

            win.Show();
            Settle(200);

            try { steps(win); }
            catch (Exception ex) { File.AppendAllText(log, (file ?? "(no image)") + ": step failed — " + ex + "\n"); }

            Settle(300);
            if (file != null && !closed)
                Save((FrameworkElement)win.Content, Path.Combine(dir, file));

            File.AppendAllText(log,
                $"{file ?? "(no image)"}: prefill={prefill ?? "-"} closed={closed} " +
                $"signedIn={win.Tokens != null} email={win.Email ?? "-"} failuresReported={failures} " +
                $"heading=\"{Text(win, "HeadingText")}\" error=\"{VisibleText(win, "ErrorText")}\"\n");

            if (!closed) win.Close();
        }

        private static void Password(Window w, string value) =>
            ((PasswordBox)w.FindName("PasswordInput")).Password = value;

        private static void Code(Window w, string value) =>
            ((TextBox)w.FindName("CodeBox")).Text = value;

        /// <summary>Clicks the primary button and waits out the stubbed request.</summary>
        private static void Submit(Window w)
        {
            ((Button)w.FindName("SubmitButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(900);
        }

        /// <summary>Clicks a named hyperlink in the code step and waits out the request.</summary>
        private static void Click(Window w, string linkName)
        {
            var link = w.FindName(linkName) as System.Windows.Documents.Hyperlink
                ?? throw new InvalidOperationException("no link named " + linkName);
            link.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
            Settle(900);
        }

        private static string Text(Window w, string name) => (w.FindName(name) as TextBlock)?.Text ?? "";

        private static string VisibleText(Window w, string name)
        {
            var block = w.FindName(name) as TextBlock;
            return block != null && block.Visibility == Visibility.Visible ? block.Text : "";
        }

        private static void Save(FrameworkElement el, string path)
        {
            el.UpdateLayout();

            var m = el.Margin;
            int w = (int)Math.Ceiling(el.ActualWidth + m.Left + m.Right);
            int h = (int)Math.Ceiling(el.ActualHeight + m.Top + m.Bottom);
            if (w <= 0 || h <= 0) { w = 440; h = 360; }

            // The window's own background is not part of its content element.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(el), null, new Rect(m.Left, m.Top, el.ActualWidth, el.ActualHeight));
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        private static void Settle(int ms)
        {
            var frame = new DispatcherFrame();
            var t = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, __) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
