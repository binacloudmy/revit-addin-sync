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
                        foreach (var (fname, fcontent, fpath, fkind) in _files)
                        {
                            switch (fkind)
                            {
                                case RevitWebAppSync.UI.Copilot.Model.AttachmentKind.Dwg:
                                    files.Add(RevitWebAppSync.UI.Copilot.Model.FileAttachment.ForDrawing(fname, fpath));
                                    break;
                                case RevitWebAppSync.UI.Copilot.Model.AttachmentKind.Pdf:
                                    files.Add(RevitWebAppSync.UI.Copilot.Model.FileAttachment.ForDocument(fname, fpath));
                                    break;
                                default:
                                    files.Add(new RevitWebAppSync.UI.Copilot.Model.FileAttachment(fname, fcontent));
                                    break;
                            }
                        }
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
                    Filter = "Supported files|*.txt;*.csv;*.md;*.log;*.json;*.xml;*.dwg;*.dxf;*.pdf"
                           + "|Text files|*.txt;*.csv;*.md;*.log;*.json;*.xml"
                           + "|Drawings|*.dwg;*.dxf"
                           + "|Documents|*.pdf",
                    Title = "Attach file(s)",
                };
                if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
            };
            PlanBtn.Click += (_, __) =>
            {
                UsagePopup.IsOpen = !UsagePopup.IsOpen;
                // Opening the card is an explicit "what's my usage right now?", so
                // re-fetch rather than show whatever was last cached. Usage is
                // otherwise only refreshed after a prompt / on pane re-show, so an
                // idle pane — or a quota spent in another Revit session — would show
                // a stale percent. The card is bound to UsageChanged, so it updates
                // in place when the response lands.
                if (UsagePopup.IsOpen && _usageVm != null)
                    _ = _usageVm.RefreshUsageAndBadgeAsync();
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

        // ─── Footer plan button + usage popover ──────────────────────────────

        /// <summary>Raised by the popover's "Upgrade plan" button; host opens the upgrade sheet.</summary>
        public event System.Action UpgradeRequested;

        private CopilotViewModel _usageVm;

        /// <summary>Wire the footer plan-name button + popover to the VM's live usage
        /// snapshot. Re-renders on every UsageChanged.</summary>
        public void BindUsage(CopilotViewModel vm)
        {
            if (_usageVm != null) _usageVm.UsageChanged -= OnUsageChanged;
            _usageVm = vm;
            if (_usageVm != null) _usageVm.UsageChanged += OnUsageChanged;
            PlanBtn.Visibility = Visibility.Visible;
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
            if (pct >= Model.UsageState.WarnPct && !u.Unlimited)
            {
                PlanDot.Visibility = Visibility.Visible;
                PlanDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, Model.UsageState.MeterColorKey(pct));
            }
            else PlanDot.Visibility = Visibility.Collapsed;

            // Popover: plan + tier pill, % used, the fill bar (severity colour), and
            // either the reset line or the upgrade CTA.
            PopPlan.Text = u.PlanName;
            // Only assert a tier the BACKEND named. /credits/balance returns no "plan"
            // field today, so PlanName is the inferred "Free" — showing the pill
            // regardless would stamp FREE on a paying Pro customer's popover.
            var tier = u.PlanKnown ? u.TierBadge : "";
            PopTier.Text = tier;
            PopTierBadge.Visibility = string.IsNullOrEmpty(tier) ? Visibility.Collapsed : Visibility.Visible;

            PopPctUsed.Text = u.Unlimited ? "No limit" : pct + "% used";
            PopFillCol.Width = new System.Windows.GridLength(u.Unlimited ? 0 : pct, System.Windows.GridUnitType.Star);
            PopRestCol.Width = new System.Windows.GridLength(u.Unlimited ? 100 : 100 - pct, System.Windows.GridUnitType.Star);
            PopFill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, Model.UsageState.MeterColorKey(pct));

            // Reset line while there's headroom; upgrade CTA once we're in the warn
            // band. An uncapped wallet has nothing to upgrade TO and no reset date, so
            // it gets neither — telling an admin-override account to buy a plan (or
            // showing it a quota reset) would be nonsense.
            bool warn = pct >= Model.UsageState.WarnPct || u.AtLimit;
            string resets = u.ResetsLabel;
            bool showReset = !u.Unlimited && !warn && !string.IsNullOrEmpty(resets);
            PopResets.Text = resets;
            PopResetRow.Visibility = showReset ? Visibility.Visible : Visibility.Collapsed;
            PopUpgradeBtn.Visibility = (!u.Unlimited && !showReset) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─── Pasted screenshots (pending, sent with the next prompt) ─────────
        private const int MaxImages = 3;
        // Cap the long edge before base64-encoding: keeps the JSON payload sane
        // while leaving plenty of resolution for the model to read a screenshot.
        private const int MaxImageDim = 1568;
        private readonly System.Collections.Generic.List<System.Windows.Media.Imaging.BitmapSource> _images
            = new System.Collections.Generic.List<System.Windows.Media.Imaging.BitmapSource>();

        // ─── File attachments (pending, content injected into prompt text) ────
        // One map, one rule: Text extensions are read here (small, UTF-8, capped);
        // every other kind is read by the addin itself, so only the path travels
        // and no size cap applies.
        private static readonly System.Collections.Generic.Dictionary<string, Model.AttachmentKind> SupportedExtensions
            = new System.Collections.Generic.Dictionary<string, Model.AttachmentKind>(System.StringComparer.OrdinalIgnoreCase)
              {
                  [".txt"] = Model.AttachmentKind.Text,
                  [".csv"] = Model.AttachmentKind.Text,
                  [".md"] = Model.AttachmentKind.Text,
                  [".log"] = Model.AttachmentKind.Text,
                  [".json"] = Model.AttachmentKind.Text,
                  [".xml"] = Model.AttachmentKind.Text,
                  [".dwg"] = Model.AttachmentKind.Dwg,
                  [".dxf"] = Model.AttachmentKind.Dwg,
                  [".pdf"] = Model.AttachmentKind.Pdf,
              };
        private const long MaxFileBytes = 32 * 1024;
        // Path is non-null for binary kinds; Content is non-null for text only.
        private readonly System.Collections.Generic.List<(string Name, string Content, string Path, Model.AttachmentKind Kind)> _files
            = new System.Collections.Generic.List<(string, string, string, Model.AttachmentKind)>();

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
                if (!SupportedExtensions.TryGetValue(ext ?? "", out var kind)) continue;
                var info = new System.IO.FileInfo(path);
                if (!info.Exists) continue;

                if (kind != Model.AttachmentKind.Text)
                {
                    // Binary — never ReadAllText it, and the 32KB cap doesn't
                    // apply: nothing but the path leaves this method.
                    _files.Add((System.IO.Path.GetFileName(path), null, path, kind));
                    continue;
                }

                if (info.Length > MaxFileBytes) continue;
                var content = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                _files.Add((System.IO.Path.GetFileName(path), content, null, kind));
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

            foreach (var (name, content, path, kind) in _files)
            {
                var capturedName = name;
                System.Action remove = () =>
                {
                    _files.RemoveAll(f => f.Name == capturedName);
                    RebuildThumbStrip();
                };
                // Page count is unknown until the addin reads the PDF, which
                // happens on send — the chip shows the kind until then.
                var chip = kind == Model.AttachmentKind.Dwg ? AttachmentChip.ForDrawing(name, remove)
                         : kind == Model.AttachmentKind.Pdf ? AttachmentChip.ForDocument(name, 0, remove)
                         : AttachmentChip.ForFile(name, content, remove);
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
