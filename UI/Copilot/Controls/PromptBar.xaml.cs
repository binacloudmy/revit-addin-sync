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
            Input.ToolPicked += OnToolPicked;
            Input.Submitted += (text, mentions) =>
            {
                // Enter while a reply streams must not queue another prompt.
                if (Busy) return;
                // A pending slash command takes the turn: raise it (with any typed
                // args) and skip the normal text/attachment submit path. UI-only.
                if (_pendingTool != null)
                {
                    var tool = _pendingTool;
                    ClearPendingTool();
                    SlashToolSubmitted?.Invoke(tool, text);
                    return;
                }
                // With screenshots and/or files attached, submit a composed payload
                // (text + base64 PNGs + file attachments) and clear the strip; plain
                // text otherwise so the other PromptBar hosts (Result/Library
                // follow-ups) see no change. File CONTENTS are carried separately —
                // not concatenated into the text — so the chat bubble and history
                // show the user's message, not the file dump (the backend route text
                // re-embeds them in CopilotViewModel.BuildRouteText).
                // An attachment still being read by the backend would silently
                // reach the agent as nothing at all. Hold the turn instead, and
                // put the typed text back (MentionInput clears the editor right
                // after this handler returns, so the restore is queued behind it).
                if (_files.Exists(f => f.Pending))
                {
                    AttachmentFailed?.Invoke("Still reading the attachment — send again in a moment.");
                    Dispatcher.BeginInvoke((System.Action)(() =>
                    {
                        Input.Editor.Text = text;
                        Input.Editor.CaretIndex = Input.Editor.Text.Length;
                    }));
                    return;
                }
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
                        foreach (var f in _files)
                            files.Add(new RevitWebAppSync.UI.Copilot.Model.FileAttachment(f.Name, f.Content));
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
                    Filter = "Text & drawings|*.txt;*.csv;*.md;*.log;*.json;*.xml;*.pdf;*.dwg;*.dxf"
                           + "|Text files|*.txt;*.csv;*.md;*.log;*.json;*.xml"
                           + "|Drawings & documents|*.pdf;*.dwg;*.dxf",
                    Title = "Attach file(s)",
                };
                if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
            };
            PlanBtn.Click += (_, __) =>
            {
                UsagePopup.IsOpen = !UsagePopup.IsOpen;
                UsageMeterClicked?.Invoke();
            };
            PopUpgradeBtn.Click += (_, __) =>
            {
                UsagePopup.IsOpen = false;
                UpgradeRequested?.Invoke();
            };
        }

        // ─── Slash command (pending, sent as the next turn) ──────────────────
        /// <summary>Raised when a message is sent with a slash command active —
        /// the picked tool plus any typed args. UI-only for now.</summary>
        public event System.Action<Model.SlashTool, string> SlashToolSubmitted;

        private Model.SlashTool _pendingTool;

        /// <summary>Host the "/" palette overlay for this composer (ChatView owns the
        /// in-panel layer; the editor drives it). See MentionInput.AttachSlashPalette.</summary>
        public void AttachSlashPalette(CommandPalette palette, System.Action<bool> setVisible)
            => Input.AttachSlashPalette(palette, setVisible);

        /// <summary>Close the palette from the host (scrim click-outside).</summary>
        public void CloseSlashPalette() => Input.CloseSlashExternal();

        /// <summary>Drop a starter prompt into the composer for the user to edit —
        /// does NOT send. If the user has already typed something, leave it alone
        /// (just focus) rather than overwrite. Caret goes to the end so they can
        /// type over the placeholders immediately.</summary>
        public void InsertStarterPrompt(string text)
        {
            if (Input?.Editor == null) return;
            if (!string.IsNullOrWhiteSpace(Input.Editor.Text)) { Input.Editor.Focus(); return; }
            Input.Editor.Text = text ?? "";
            Input.Editor.CaretIndex = Input.Editor.Text.Length;
            Input.Editor.Focus();
        }

        private void OnToolPicked(Model.SlashTool tool)
        {
            _pendingTool = tool;
            Input.AllowEmptySubmit = true;   // a bare "/tool" turn can be sent
            RebuildCommandStrip();
            UpdateSendVisual();
            Input.Editor.Focus();
        }

        private void ClearPendingTool()
        {
            _pendingTool = null;
            Input.AllowEmptySubmit = false;
            RebuildCommandStrip();
            UpdateSendVisual();
        }

        private void RebuildCommandStrip()
        {
            CommandStrip.Children.Clear();
            if (_pendingTool == null) { CommandStrip.Visibility = Visibility.Collapsed; return; }
            CommandStrip.Children.Add(CommandChip.Build(_pendingTool, ClearPendingTool));
            CommandStrip.Visibility = Visibility.Visible;
        }

        // ─── Footer usage meter ───────────────────────────────────────────────
        /// <summary>Raised when the meter row is clicked (popover opens itself).</summary>
        public event System.Action UsageMeterClicked;

        /// <summary>Raised by the popover's "Upgrade plan" button; host opens the upgrade sheet.</summary>
        public event System.Action UpgradeRequested;

        private CopilotViewModel _usageVm;

        /// <summary>Wire the footer plan-name button + usage popover to the VM's
        /// live usage snapshot. The full-width meter is gone; this renders the
        /// plan label, the severity dot (amber ≥80, red ≥95, hidden below 80) and
        /// the popover's bar / %. Re-renders on every UsageChanged.</summary>
        public void BindUsage(CopilotViewModel vm)
        {
            if (_usageVm != null) _usageVm.UsageChanged -= OnUsageChanged;
            _usageVm = vm;
            if (_usageVm != null) _usageVm.UsageChanged += OnUsageChanged;
            RenderUsage(vm?.Usage);
        }

        private void OnUsageChanged()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((System.Action)OnUsageChanged); return; }
            RenderUsage(_usageVm?.Usage);
        }

        private void RenderUsage(Model.UsageState u)
        {
            u = u ?? new Model.UsageState();
            int pct = System.Math.Max(0, System.Math.Min(100, u.Pct));

            // Footer plan-name button: live label + severity dot.
            PlanLabel.Text = u.PlanName;
            if (pct >= 80)
            {
                PlanDot.Visibility = Visibility.Visible;
                PlanDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, pct >= 95 ? "Cp.Red" : "Cp.Amber");
            }
            else PlanDot.Visibility = Visibility.Collapsed;

            // Usage popover: plan, % used, and the fill bar (severity colour).
            PopPlan.Text = u.PlanName;
            PopPctUsed.Text = pct + "%";
            PopFillCol.Width = new System.Windows.GridLength(pct, System.Windows.GridUnitType.Star);
            PopRestCol.Width = new System.Windows.GridLength(100 - pct, System.Windows.GridUnitType.Star);
            PopFill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, Model.UsageState.MeterColorKey(pct));
        }

        // ─── Pasted screenshots (pending, sent with the next prompt) ─────────
        private const int MaxImages = 3;
        // Cap the long edge before base64-encoding: keeps the JSON payload sane
        // while leaving plenty of resolution for the model to read a screenshot.
        private const int MaxImageDim = 1568;
        private readonly System.Collections.Generic.List<System.Windows.Media.Imaging.BitmapSource> _images
            = new System.Collections.Generic.List<System.Windows.Media.Imaging.BitmapSource>();

        // ─── File attachments (pending, content injected into prompt text) ────
        // Text formats are read right here. PDFs and drawings are binary, so the
        // bytes go to the backend (/agents/revit-ai/attachments/extract) and come
        // back as a digest that lands in the SAME slot — everything downstream
        // (BuildRouteText, the chat bubble, run history) is unchanged.
        private static readonly System.Collections.Generic.HashSet<string> TextExtensions
            = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
              { ".txt", ".csv", ".md", ".log", ".json", ".xml" };
        private static readonly System.Collections.Generic.HashSet<string> DocumentExtensions
            = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
              { ".pdf", ".dwg", ".dxf" };
        private const long MaxTextFileBytes = 32 * 1024;
        // Matches MAX_UPLOAD_BYTES in app/services/attachments/extract_service.py —
        // rejecting here saves a doomed 25 MB round-trip.
        private const long MaxDocumentFileBytes = 25 * 1024 * 1024;

        /// <summary>One attachment queued for the next turn. <see cref="Pending"/>
        /// marks a document whose backend extraction is still in flight.</summary>
        private class PendingFile
        {
            public string Name;
            public string Content;
            public string Info;      // chip sub-label: "42 ln", "12 pg", "reading…"
            public bool Pending;
        }

        private readonly System.Collections.Generic.List<PendingFile> _files
            = new System.Collections.Generic.List<PendingFile>();

        /// <summary>Raised when an attachment could not be read (unsupported,
        /// too big, backend/sidecar down). The host shows it in the thread — a
        /// silently dropped drawing reads to the user as a successful attach.</summary>
        public event System.Action<string> AttachmentFailed;

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

        private async void AddFiles(string[] paths)
        {
            foreach (var path in paths)
            {
                var ext = System.IO.Path.GetExtension(path);
                var name = System.IO.Path.GetFileName(path);
                var fileInfo = new System.IO.FileInfo(path);
                if (!fileInfo.Exists)
                {
                    AttachmentFailed?.Invoke($"{name}: file not found.");
                    continue;
                }

                if (TextExtensions.Contains(ext))
                {
                    if (fileInfo.Length > MaxTextFileBytes)
                    {
                        AttachmentFailed?.Invoke($"{name} is larger than {MaxTextFileBytes / 1024} KB — attach a smaller excerpt.");
                        continue;
                    }
                    var content = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                    _files.Add(new PendingFile { Name = name, Content = content, Info = LineInfo(content) });
                    RebuildThumbStrip();
                }
                else if (DocumentExtensions.Contains(ext))
                {
                    if (fileInfo.Length > MaxDocumentFileBytes)
                    {
                        AttachmentFailed?.Invoke($"{name} is larger than {MaxDocumentFileBytes / (1024 * 1024)} MB.");
                        continue;
                    }
                    await AddDocumentAsync(path, name);
                }
                else
                {
                    AttachmentFailed?.Invoke($"{name}: unsupported file type.");
                }
            }
            RebuildThumbStrip();
        }

        /// <summary>Ship a PDF/DWG/DXF to the backend for extraction. The chip
        /// appears immediately as "reading…" so the strip never looks frozen
        /// while a 20 MB drawing uploads.</summary>
        private async System.Threading.Tasks.Task AddDocumentAsync(string path, string name)
        {
            var entry = new PendingFile { Name = name, Info = "reading…", Pending = true };
            _files.Add(entry);
            RebuildThumbStrip();

            try
            {
                var cfg = BinaConfig.Load();
                if (cfg == null || !cfg.IsLoggedIn())
                    throw new RevitWebAppSync.Services.AIService.AttachmentExtractException(
                        "sign in to BINA Cloud first (ribbon → BINA Cloud → Login)");

                var bytes = System.IO.File.ReadAllBytes(path);
                var result = await new RevitWebAppSync.Services.AIService()
                    .ExtractAttachmentAsync(bytes, name, cfg.AccessToken);

                entry.Content = result?.Digest ?? "";
                entry.Info = DocumentInfo(result);
                entry.Pending = false;

                // Rendered sheet images ride the existing screenshot channel.
                if (result?.Images != null)
                {
                    foreach (var b64 in result.Images)
                    {
                        if (_images.Count >= MaxImages) break;
                        var bitmap = DecodePng(b64);
                        if (bitmap != null) _images.Add(bitmap);
                    }
                }
                if (!string.IsNullOrEmpty(result?.Warning))
                    AttachmentFailed?.Invoke($"{name}: {result.Warning}");
            }
            catch (System.Exception ex)
            {
                _files.Remove(entry);
                AttachmentFailed?.Invoke($"Could not read {name} — {ex.Message}");
            }
            RebuildThumbStrip();
        }

        private static string LineInfo(string content) =>
            (string.IsNullOrEmpty(content) ? 0 : content.Split('\n').Length) + " ln";

        private static string DocumentInfo(RevitWebAppSync.Services.AIService.AttachmentExtract r)
        {
            if (r == null) return "";
            if (r.Pages.HasValue) return r.Pages.Value + " pg";
            return string.IsNullOrEmpty(r.Kind) ? "" : r.Kind.ToUpperInvariant();
        }

        /// <summary>Inverse of <see cref="EncodePng"/> for backend-rendered pages.
        /// Returns null on a corrupt payload rather than killing the attach.</summary>
        private static System.Windows.Media.Imaging.BitmapSource DecodePng(string base64)
        {
            try
            {
                var bytes = System.Convert.FromBase64String(base64 ?? "");
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    var decoder = System.Windows.Media.Imaging.BitmapFrame.Create(
                        ms,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    return decoder;
                }
            }
            catch { return null; }
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

            foreach (var file in _files)
            {
                var captured = file;
                // A file still uploading gets no remove button — cancelling it
                // mid-flight would leave the response with nowhere to land.
                var chip = AttachmentChip.ForDocument(file.Name, file.Info, captured.Pending ? (System.Action)null : () =>
                {
                    _files.Remove(captured);
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
            bool armed = Busy || _pendingTool != null || !string.IsNullOrWhiteSpace(Input.Editor.Text);
            if (armed)
            {
                SendBtn.Background = TryFindResource("Cp.AccentGrad") as System.Windows.Media.Brush ?? Brushes.RoyalBlue;
                // Always white — NOT Cp.AccentContrast (which is near-black in dark theme,
                // giving a black glyph on the blue button). A send/stop glyph reads best
                // white on the accent gradient in both themes.
                SendIcon.Fill = Brushes.White;
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
