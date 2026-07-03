using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Bottom prompt bar. Hosts a MentionInput; Enter or the send button fires SubmitCommand
    /// with the composed text (mentions are parsed inline by the editor). While Busy (a reply
    /// is streaming) the send button becomes a Stop button that fires CancelCommand instead.
    /// </summary>
    public partial class PromptBar : UserControl
    {
        // Up arrow (send) vs. square (stop), drawn in the 24×24 icon viewbox.
        private static readonly Geometry SendGeom = Geometry.Parse("M12,4 L19,11.5 L14.4,11.5 L14.4,19 L9.6,19 L9.6,11.5 L5,11.5 Z");
        private static readonly Geometry StopGeom = Geometry.Parse("M6,6 H18 V18 H6 Z");

        public PromptBar()
        {
            InitializeComponent();
            SendBtn.Click += (_, __) =>
            {
                // Busy → the button is a Stop: cancel the in-flight reply instead
                // of submitting a new prompt.
                if (Busy)
                {
                    if (CancelCommand != null && CancelCommand.CanExecute(null))
                        CancelCommand.Execute(null);
                    return;
                }
                Input.TriggerSubmit();
            };
            Input.Submitted += (text, mentions) =>
            {
                // Enter while a reply streams must not queue another prompt.
                if (Busy) return;
                // With screenshots and/or files attached, submit a composed payload
                // (text + base64 PNGs + file attachments) and clear the strip; plain
                // text otherwise so the other PromptBar hosts (Result/Library
                // follow-ups) see no change. File CONTENTS are carried separately —
                // not concatenated into the text — so the chat bubble and history
                // show the user's message, not the file dump (the backend route text
                // re-embeds them in CopilotViewModel.BuildRouteText).
                object payload = text;
                if (_images.Count > 0 || _files.Count > 0)
                {
                    var pp = new RevitWebAppSync.UI.Copilot.Model.PromptPayload { Text = text };
                    if (_images.Count > 0)
                    {
                        var encoded = new System.Collections.Generic.List<string>();
                        foreach (var img in _images)
                        {
                            var b64 = EncodePng(img);
                            if (!string.IsNullOrEmpty(b64)) encoded.Add(b64);
                        }
                        if (encoded.Count > 0) pp.ImagesBase64 = encoded;
                        _images.Clear();
                    }
                    if (_files.Count > 0)
                    {
                        var files = new System.Collections.Generic.List<RevitWebAppSync.UI.Copilot.Model.FileAttachment>();
                        foreach (var (fname, fcontent) in _files)
                            files.Add(new RevitWebAppSync.UI.Copilot.Model.FileAttachment(fname, fcontent));
                        pp.Files = files;
                        _files.Clear();
                    }
                    payload = pp;
                    RebuildThumbStrip();
                }
                if (SubmitCommand != null && SubmitCommand.CanExecute(payload))
                    SubmitCommand.Execute(payload);
            };
            Input.ImagePasted += AddImage;
            Input.FileDropped += AddFiles;
            // Send button visual state: gray circle while empty, accent gradient
            // once there's text (or while a reply streams and it acts as Stop).
            Input.Editor.TextChanged += (_, __) => UpdateSendVisual();
            UpdateSendVisual();
            // @ button: append an @ (with a leading space when needed) and focus
            // the editor — the mention picker opens from the editor's own logic.
            AtBtn.Click += (_, __) =>
            {
                var t = Input.Editor.Text ?? "";
                Input.Editor.Text = t.Length > 0 && !char.IsWhiteSpace(t[t.Length - 1]) ? t + " @" : t + "@";
                Input.Editor.CaretIndex = Input.Editor.Text.Length;
                Input.Editor.Focus();
            };
            AttachBtn.Click += (_, __) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Multiselect = true,
                    Filter = "Text files|*.txt;*.csv;*.md;*.log;*.json;*.xml",
                    Title = "Attach file(s)",
                };
                if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
            };
        }

        // ─── Pasted screenshots (pending, sent with the next prompt) ─────────
        private const int MaxImages = 3;
        // Cap the long edge before base64-encoding: keeps the JSON payload sane
        // while leaving plenty of resolution for the model to read a screenshot.
        private const int MaxImageDim = 1568;
        private readonly System.Collections.Generic.List<System.Windows.Media.Imaging.BitmapSource> _images
            = new System.Collections.Generic.List<System.Windows.Media.Imaging.BitmapSource>();

        // ─── File attachments (pending, content injected into prompt text) ────
        private static readonly System.Collections.Generic.HashSet<string> SupportedExtensions
            = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
              { ".txt", ".csv", ".md", ".log", ".json", ".xml" };
        private const long MaxFileBytes = 32 * 1024;
        private readonly System.Collections.Generic.List<(string Name, string Content)> _files
            = new System.Collections.Generic.List<(string, string)>();

        private void AddImage(System.Windows.Media.Imaging.BitmapSource img)
        {
            if (img == null || _images.Count >= MaxImages) return;
            _images.Add(img);
            RebuildThumbStrip();
        }

        private void RemoveImage(System.Windows.Media.Imaging.BitmapSource img)
        {
            _images.Remove(img);
            RebuildThumbStrip();
        }

        private void AddFiles(string[] paths)
        {
            foreach (var path in paths)
            {
                var ext = System.IO.Path.GetExtension(path);
                if (!SupportedExtensions.Contains(ext)) continue;
                var info = new System.IO.FileInfo(path);
                if (!info.Exists || info.Length > MaxFileBytes) continue;
                var content = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                _files.Add((System.IO.Path.GetFileName(path), content));
            }
            RebuildThumbStrip();
        }

        private void RebuildThumbStrip()
        {
            ThumbStrip.Children.Clear();
            ThumbStrip.Visibility = (_images.Count > 0 || _files.Count > 0)
                ? Visibility.Visible : Visibility.Collapsed;

            foreach (var img in _images)
            {
                var captured = img;
                var chip = AttachmentChip.ForImage(img, () => RemoveImage(captured));
                chip.Margin = new Thickness(0, 0, 6, 0);
                ThumbStrip.Children.Add(chip);
            }

            foreach (var (name, content) in _files)
            {
                var capturedName = name;
                var chip = AttachmentChip.ForFile(name, content, () =>
                {
                    _files.RemoveAll(f => f.Name == capturedName);
                    RebuildThumbStrip();
                });
                chip.Margin = new Thickness(0, 0, 6, 0);
                ThumbStrip.Children.Add(chip);
            }
        }

        /// <summary>PNG-encode a pasted bitmap as base64, downscaling so the long
        /// edge ≤ MaxImageDim (screenshots from 4K monitors would otherwise bloat
        /// the request body). Returns null on encode failure.</summary>
        private static string EncodePng(System.Windows.Media.Imaging.BitmapSource src)
        {
            try
            {
                System.Windows.Media.Imaging.BitmapSource frame = src;
                double longEdge = System.Math.Max(src.PixelWidth, src.PixelHeight);
                if (longEdge > MaxImageDim)
                {
                    double scale = MaxImageDim / longEdge;
                    frame = new System.Windows.Media.Imaging.TransformedBitmap(
                        src, new ScaleTransform(scale, scale));
                }
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                using (var ms = new System.IO.MemoryStream())
                {
                    enc.Save(ms);
                    return System.Convert.ToBase64String(ms.ToArray());
                }
            }
            catch { return null; }
        }

        public static readonly DependencyProperty SubmitCommandProperty = DependencyProperty.Register(
            nameof(SubmitCommand), typeof(ICommand), typeof(PromptBar), new PropertyMetadata(null));
        public ICommand SubmitCommand { get => (ICommand)GetValue(SubmitCommandProperty); set => SetValue(SubmitCommandProperty, value); }

        // True while a reply is streaming — flips the send button to a Stop button.
        public static readonly DependencyProperty BusyProperty = DependencyProperty.Register(
            nameof(Busy), typeof(bool), typeof(PromptBar), new PropertyMetadata(false, OnBusyChanged));
        public bool Busy { get => (bool)GetValue(BusyProperty); set => SetValue(BusyProperty, value); }

        // Fired when the user clicks Stop (or presses the button while Busy).
        public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(
            nameof(CancelCommand), typeof(ICommand), typeof(PromptBar), new PropertyMetadata(null));
        public ICommand CancelCommand { get => (ICommand)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }

        private static void OnBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var pb = (PromptBar)d;
            bool busy = (bool)e.NewValue;
            if (pb.SendIcon != null) pb.SendIcon.Data = busy ? StopGeom : SendGeom;
            if (pb.SendBtn != null) pb.SendBtn.ToolTip = busy ? "Stop" : "Send";
            pb.UpdateSendVisual();
        }

        // Idle (no text): transparent circle + faint arrow. Armed (text present)
        // or Busy (stop): accent-gradient circle + white glyph.
        private void UpdateSendVisual()
        {
            if (SendBtn == null || SendIcon == null || Input?.Editor == null) return;
            bool armed = Busy || !string.IsNullOrWhiteSpace(Input.Editor.Text);
            if (armed)
            {
                SendBtn.Background = TryFindResource("Cp.AccentGrad") as System.Windows.Media.Brush ?? Brushes.RoyalBlue;
                SendIcon.Fill = TryFindResource("Cp.AccentContrast") as System.Windows.Media.Brush ?? Brushes.White;
            }
            else
            {
                SendBtn.Background = Brushes.Transparent;
                SendIcon.Fill = TryFindResource("Cp.Faint") as System.Windows.Media.Brush ?? Brushes.Gray;
            }
        }

        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
            nameof(Placeholder), typeof(string), typeof(PromptBar),
            new PropertyMetadata("Ask Copilot…", OnPlaceholderChanged));
        public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

        private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var pb = (PromptBar)d;
            if (pb.Input != null) pb.Input.PlaceholderText = (string)e.NewValue;
        }
    }
}
