using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Lock screen every docked pane sits behind while a forced update is
    /// pending. Pane hosts call <see cref="Wrap"/> on their content; the cover
    /// is collapsed while UpdateGate is Open and replaces the pane otherwise.
    /// Code-only (no XAML) so it builds on every TFM and needs no pack URI.
    /// </summary>
    public sealed class UpdateGateOverlay
    {
        private static readonly Brush CoverBrush = Frozen(Color.FromRgb(0xF3, 0xF4, 0xF6));
        private static readonly Brush CardBrush = Frozen(Colors.White);
        private static readonly Brush CardBorderBrush = Frozen(Color.FromRgb(0xE5, 0xE7, 0xEB));
        private static readonly Brush PrimaryBrush = Frozen(Color.FromRgb(0x0F, 0x6C, 0xBD));
        private static readonly Brush TitleBrush = Frozen(Color.FromRgb(0x11, 0x18, 0x27));
        private static readonly Brush BodyBrush = Frozen(Color.FromRgb(0x4B, 0x55, 0x63));
        private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xB9, 0x1C, 0x1C));
        private static readonly Brush SuccessBrush = Frozen(Color.FromRgb(0x10, 0x7C, 0x10));
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI");
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private const string LockGlyph = "";
        private const string CheckGlyph = "";

        private readonly FrameworkElement _content;
        private readonly Grid _root;
        private readonly Border _cover;
        private readonly StackPanel _card;

        // UI-thread only.
        private bool _subscribed;
        private bool _downloading;
        private string _error;
        private double _fraction;
        private string _status;

        /// <summary>Put <paramref name="content"/> behind the forced-update lock screen.</summary>
        public static FrameworkElement Wrap(FrameworkElement content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            return new UpdateGateOverlay(content)._root;
        }

        private UpdateGateOverlay(FrameworkElement content)
        {
            _content = content;
            _card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            _cover = new Border
            {
                Background = CoverBrush,
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new Border
                    {
                        Background = CardBrush,
                        BorderBrush = CardBorderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(24),
                        Margin = new Thickness(16),
                        MaxWidth = 360,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = _card,
                    },
                },
            };
            Panel.SetZIndex(_cover, 1);

            _root = new Grid();
            _root.Children.Add(content);
            _root.Children.Add(_cover);
            _root.Loaded += OnLoaded;
            _root.Unloaded += OnUnloaded;

            Render();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed)
            {
                UpdateGate.Changed += OnGateChanged;
                _subscribed = true;
            }
            Render();   // the gate may have moved while we were unloaded
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) return;
            UpdateGate.Changed -= OnGateChanged;
            _subscribed = false;
        }

        // Raised on whatever thread UpdateService is on.
        private void OnGateChanged(UpdateGateState state) => Post(Render);

        private void Post(Action action)
        {
            try { _root.Dispatcher.BeginInvoke(action); } catch { /* dispatcher shut down with Revit */ }
        }

        private void Render()
        {
            var state = UpdateGate.State;
            var open = state == UpdateGateState.Open;

            _content.IsEnabled = open;
            _content.IsHitTestVisible = open;
            _cover.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            _card.Children.Clear();
            if (open) return;

            var version = UpdateGate.PendingVersion ?? UpdateService.Pending?.Version;
            var name = string.IsNullOrEmpty(version) ? "the new version" : version;

            if (state == UpdateGateState.RestartRequired)
            {
                AddIcon(CheckGlyph, SuccessBrush);
                AddTitle(name + " is ready");
                AddBody("Save your work and restart Revit to finish the update.");
                return;
            }

            if (_downloading)
            {
                AddIcon(LockGlyph, PrimaryBrush);
                AddTitle("Downloading " + name);
                _card.Children.Add(new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = _fraction,
                    IsIndeterminate = _fraction <= 0,
                    Height = 6,
                    Margin = new Thickness(0, 12, 0, 8),
                    Foreground = PrimaryBrush,
                });
                AddBody(string.IsNullOrEmpty(_status) ? "Starting download…" : _status);
                return;
            }

            AddIcon(LockGlyph, PrimaryBrush);
            AddTitle("Update required");
            AddBody("BINA Sync " + (string.IsNullOrEmpty(version) ? "" : version + " ")
                    + "must be installed before you can use it.");

            if (_error != null)
            {
                AddBody(_error, ErrorBrush);
                AddButton("Try again");
            }
            else if (UpdateService.Pending == null)
            {
                // Locked from the remembered answer; the feed hasn't answered yet.
                // UpdateService re-sets the gate when it does, which re-renders us.
                AddBody("Checking for the update…");
            }
            else
            {
                AddButton("Update now");
            }
        }

        private void OnUpdateClicked(object sender, RoutedEventArgs e)
        {
            if (_downloading) return;
            _downloading = true;
            _error = null;
            _fraction = 0;
            _status = null;
            Render();

            Task task;
            try { task = UpdateService.StageAsync(new DispatcherProgress(this)); }
            catch (Exception ex) { task = FromException(ex); }

            task.ContinueWith(t => Post(() =>
            {
                _downloading = false;
                if (t.IsFaulted)
                {
                    var root = t.Exception.GetBaseException();
                    _error = "The download didn't finish: " + root.Message;
                }
                else if (t.IsCanceled)
                {
                    _error = "The download was cancelled.";
                }
                // On success UpdateService moves the gate to RestartRequired itself.
                Render();
            }), TaskScheduler.Default);
        }

        private static Task FromException(Exception ex)
        {
            var tcs = new TaskCompletionSource<bool>();
            tcs.SetException(ex);
            return tcs.Task;
        }

        /// <summary>Marshals StageAsync progress to the pane's dispatcher.</summary>
        private sealed class DispatcherProgress : IProgress<(double Fraction, string Status)>
        {
            private readonly UpdateGateOverlay _owner;
            public DispatcherProgress(UpdateGateOverlay owner) { _owner = owner; }

            public void Report((double Fraction, string Status) value)
            {
                _owner.Post(() =>
                {
                    if (!_owner._downloading) return;
                    _owner._fraction = Math.Max(0, Math.Min(1, value.Fraction));
                    _owner._status = value.Status;
                    _owner.Render();
                });
            }
        }

        private void AddIcon(string glyph, Brush brush)
        {
            _card.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = 32,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        private void AddTitle(string text)
        {
            _card.Children.Add(new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = TitleBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        private void AddBody(string text, Brush brush = null)
        {
            _card.Children.Add(new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 13,
                Foreground = brush ?? BodyBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        private void AddButton(string text)
        {
            var label = new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            // Own template: the default Button chrome swaps Background on hover,
            // so draw a flat border that stays blue.
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, PrimaryBrush);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.PaddingProperty, new Thickness(20, 8, 20, 8));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            var button = new Button
            {
                Content = label,
                Template = template,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            };
            button.Click += OnUpdateClicked;
            _card.Children.Add(button);
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
