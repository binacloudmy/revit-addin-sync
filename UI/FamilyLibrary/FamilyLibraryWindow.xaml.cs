using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot;

namespace RevitWebAppSync.UI.FamilyLibrary
{
    /// <summary>
    /// Browse the BINA cloud family library and pick one to load.
    ///
    /// The dialog only chooses. It closes with <see cref="SelectedFamily"/> set,
    /// and the command that opened it does the actual load inside Revit's API
    /// context — so nothing here has to know about Transactions, and the manual
    /// path ends up running the same loader the copilot's load_family tool uses.
    ///
    /// Cards are built in code rather than a DataTemplate: the greyed-out state,
    /// the placeholder tile and the thumbnail arriving later would each need a
    /// converter, and this matches how LibraryView and the JKR dashboard build
    /// their rows.
    /// </summary>
    public partial class FamilyLibraryWindow : Window
    {
        private const int PageSize = 24;

        // Long enough that a drafter's pauses don't each fire a query, short
        // enough that it still feels like it's reacting to typing.
        private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

        private readonly string _accessToken;
        private readonly int? _revitVersion;

        // Thumbnails are immutable for a given family and small (~1 KB), so once
        // fetched they stay for the life of the dialog. Paging back and forth
        // then costs nothing.
        private readonly Dictionary<string, BitmapImage> _thumbnailCache
            = new Dictionary<string, BitmapImage>();

        private readonly System.Windows.Threading.DispatcherTimer _searchTimer;

        // Cancels the in-flight page load when the user types again, changes
        // chip, or pages — without this a slow earlier request can land after a
        // newer one and repaint the grid with stale results.
        private CancellationTokenSource _loadCts;

        private string _activeCategory = "All";
        private int _page = 1;
        private int _totalPages;

        private Border _selectedCard;

        /// <summary>Set when the user confirms; null if they cancelled.</summary>
        public FamilyLibraryItem SelectedFamily { get; private set; }

        public FamilyLibraryWindow(string accessToken, int? revitVersion)
        {
            // Must precede InitializeComponent: the XAML resolves Cp.* the
            // moment it parses, and Revit never creates a WPF Application for
            // the dictionaries to live in.
            CopilotTheme.EnsureLoaded();
            InitializeComponent();

            _accessToken = accessToken;
            _revitVersion = revitVersion;

            _searchTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = SearchDebounce
            };
            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                _page = 1;
                _ = LoadPageAsync();
            };

            SubtitleText.Text = revitVersion.HasValue
                ? $"Revit {revitVersion.Value}"
                : "BINA cloud library";

            Loaded += async (s, e) => await InitialLoadAsync();
        }

        // ── loading ────────────────────────────────────────────────────

        private async Task InitialLoadAsync()
        {
            await LoadCategoriesAsync().ConfigureAwait(true);
            await LoadPageAsync().ConfigureAwait(true);
        }

        private async Task LoadCategoriesAsync()
        {
            try
            {
                var categories = await FamilyLibraryApi
                    .GetCategoriesAsync(_accessToken, _revitVersion)
                    .ConfigureAwait(true);
                BuildChips(categories);
            }
            catch (Exception ex)
            {
                // Chips are navigation, not content — the grid can still work
                // without them, so this doesn't take the dialog down.
                System.Diagnostics.Debug.WriteLine($"[BINA] family chips failed: {ex.Message}");
            }
        }

        private async Task LoadPageAsync()
        {
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            SetStatus("Loading families…");
            CardsHost.Children.Clear();
            ClearSelection();

            try
            {
                var page = await FamilyLibraryApi.GetFamiliesAsync(
                        _accessToken,
                        search: SearchInput.Text,
                        category: _activeCategory,
                        revitVersion: _revitVersion,
                        page: _page,
                        limit: PageSize,
                        ct: cts.Token)
                    .ConfigureAwait(true);

                if (cts.IsCancellationRequested) return;

                _totalPages = page.Pages;
                RenderPage(page);
                _ = LoadThumbnailsAsync(page.Items, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Superseded by a newer request; that one owns the grid now.
                // The filter matters: HttpClient reports its own timeout as a
                // TaskCanceledException too, and swallowing that would leave
                // the dialog sitting on "Loading families…" forever with no
                // error and nothing to retry. Those fall through below.
            }
            catch (FamilyLibraryException ex)
            {
                SetStatus(ex.Message);
            }
            catch (Exception ex)
            {
                SetStatus($"Could not reach the family library.\n{ex.Message}");
            }
        }

        private void RenderPage(FamilyLibraryPage page)
        {
            if (page.Items.Count == 0)
            {
                SetStatus(string.IsNullOrWhiteSpace(SearchInput.Text)
                    ? "No families in this category."
                    : $"Nothing matches “{SearchInput.Text.Trim()}”.");
                CountText.Text = "";
                UpdatePagingButtons();
                return;
            }

            SetStatus(null);
            foreach (var item in page.Items)
                CardsHost.Children.Add(BuildCard(item));

            var first = (page.Page - 1) * page.Limit + 1;
            var last = first + page.Items.Count - 1;
            CountText.Text = $"{first}–{last} of {page.Total} families";
            UpdatePagingButtons();
        }

        /// <summary>
        /// Fetch the pictures after the cards are already on screen, one at a
        /// time, so the grid appears immediately and fills in rather than
        /// blocking on two dozen round trips.
        /// </summary>
        private async Task LoadThumbnailsAsync(List<FamilyLibraryItem> items, CancellationToken ct)
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) return;
                if (!item.HasThumbnail || string.IsNullOrEmpty(item.LibraryId)) continue;
                if (_thumbnailCache.ContainsKey(item.LibraryId))
                {
                    ApplyThumbnail(item.LibraryId, _thumbnailCache[item.LibraryId]);
                    continue;
                }

                try
                {
                    var bytes = await FamilyLibraryApi
                        .GetThumbnailAsync(_accessToken, item.LibraryId, ct)
                        .ConfigureAwait(true);
                    if (bytes == null || ct.IsCancellationRequested) continue;

                    var image = DecodePng(bytes);
                    if (image == null) continue;

                    _thumbnailCache[item.LibraryId] = image;
                    ApplyThumbnail(item.LibraryId, image);
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    // A missing picture is cosmetic — the placeholder stands in.
                }
            }
        }

        /// <summary>
        /// Decode eagerly and freeze, so the image is safe to hand to the UI
        /// from any thread and the stream can be released immediately.
        /// </summary>
        private static BitmapImage DecodePng(byte[] bytes)
        {
            try
            {
                var img = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                }
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        private void ApplyThumbnail(string libraryId, BitmapImage image)
        {
            foreach (var child in CardsHost.Children.OfType<Border>())
            {
                if (!(child.Tag is FamilyLibraryItem item)) continue;
                if (item.LibraryId != libraryId) continue;
                if (child.FindName("Thumb") is Image target)
                {
                    target.Source = image;
                    if (child.FindName("ThumbPlaceholder") is UIElement ph)
                        ph.Visibility = Visibility.Collapsed;
                }
                return;
            }
        }

        // ── card + chip construction ───────────────────────────────────

        private void BuildChips(List<FamilyLibraryCategory> categories)
        {
            ChipsHost.Children.Clear();
            foreach (var category in categories)
            {
                var chip = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Content = $"{category.Category}  {category.Count}",
                    Style = (Style)FindResource("Cp.Chip"),
                    Margin = new Thickness(0, 0, 6, 0),
                    IsChecked = category.Category == _activeCategory,
                    Tag = category.Category,
                };
                chip.Checked += Chip_Checked;
                // Clicking the active chip again shouldn't leave the grid with
                // no filter selected.
                chip.Unchecked += (s, e) =>
                {
                    if ((string)((System.Windows.Controls.Primitives.ToggleButton)s).Tag == _activeCategory)
                        ((System.Windows.Controls.Primitives.ToggleButton)s).IsChecked = true;
                };
                ChipsHost.Children.Add(chip);
            }
        }

        private void Chip_Checked(object sender, RoutedEventArgs e)
        {
            var chip = (System.Windows.Controls.Primitives.ToggleButton)sender;
            var category = (string)chip.Tag;
            if (category == _activeCategory) return;

            _activeCategory = category;
            foreach (var other in ChipsHost.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
                if (!ReferenceEquals(other, chip)) other.IsChecked = false;

            _page = 1;
            _ = LoadPageAsync();
        }

        private Border BuildCard(FamilyLibraryItem item)
        {
            // A family authored in a newer Revit than the one running cannot be
            // loaded, so it's shown but not selectable.
            var loadable = !(_revitVersion.HasValue && item.RevitVersion.HasValue
                             && item.RevitVersion.Value > _revitVersion.Value);

            var card = new Border
            {
                Width = 188,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("Cp.Line"),
                Background = (Brush)FindResource("Cp.Bg"),
                Cursor = loadable ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.No,
                Opacity = loadable ? 1.0 : 0.45,
                Tag = item,
            };

            var stack = new StackPanel();

            // ── picture (or placeholder while it loads / if none exists)
            var thumbHost = new Grid { Height = 108, Margin = new Thickness(0, 0, 0, 8) };
            thumbHost.Children.Add(new Border
            {
                Background = (Brush)FindResource("Cp.Hover"),
                CornerRadius = new CornerRadius(5),
            });

            var placeholder = new TextBlock
            {
                Text = item.HasThumbnail ? "…" : "No preview",
                FontSize = 11,
                Foreground = (Brush)FindResource("Cp.Faint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RegisterCardPart(card, "ThumbPlaceholder", placeholder);
            thumbHost.Children.Add(placeholder);

            var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6) };
            RegisterCardPart(card, "Thumb", image);
            thumbHost.Children.Add(image);
            stack.Children.Add(thumbHost);

            // ── name
            stack.Children.Add(new TextBlock
            {
                Text = item.FamilyName,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Cp.Ink"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.FamilyName,
            });

            // ── "Doors · 2.4 MB"
            stack.Children.Add(new TextBlock
            {
                Text = item.SubtitleLabel,
                FontSize = 11,
                Foreground = (Brush)FindResource("Cp.Muted"),
                Margin = new Thickness(0, 2, 0, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // ── version badge
            if (!string.IsNullOrEmpty(item.VersionLabel))
            {
                stack.Children.Add(new Border
                {
                    Background = (Brush)FindResource(loadable ? "Cp.Hover" : "Cp.AmberBg"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = loadable
                            ? item.VersionLabel
                            : $"Requires {item.VersionLabel}",
                        FontSize = 10.5,
                        Foreground = (Brush)FindResource(loadable ? "Cp.Muted" : "Cp.AmberFg"),
                    },
                });
            }

            card.Child = stack;

            if (loadable)
            {
                card.MouseLeftButtonUp += (s, e) => SelectCard(card, item);
            }
            else
            {
                card.ToolTip =
                    $"Authored in Revit {item.RevitVersion}. " +
                    $"This Revit is {_revitVersion}, which cannot open it.";
            }

            return card;
        }

        /// <summary>
        /// Cards are built in code, so their parts aren't in the XAML name
        /// scope that FindName searches. Registering them gives the thumbnail
        /// pass a way back to the right Image once its bytes arrive.
        /// </summary>
        private static void RegisterCardPart(Border card, string name, FrameworkElement part)
        {
            if (card.FindName(name) != null) return;
            NameScope.SetNameScope(card, NameScope.GetNameScope(card) ?? new NameScope());
            card.RegisterName(name, part);
        }

        private void SelectCard(Border card, FamilyLibraryItem item)
        {
            ClearSelection();
            _selectedCard = card;
            card.BorderBrush = (Brush)FindResource("Cp.Accent");
            card.BorderThickness = new Thickness(2);
            SelectedFamily = item;
            LoadButton.IsEnabled = true;
        }

        private void ClearSelection()
        {
            if (_selectedCard != null)
            {
                _selectedCard.BorderBrush = (Brush)FindResource("Cp.Line");
                _selectedCard.BorderThickness = new Thickness(1);
                _selectedCard = null;
            }
            SelectedFamily = null;
            LoadButton.IsEnabled = false;
        }

        // ── state helpers ──────────────────────────────────────────────

        private void SetStatus(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                StatusText.Visibility = Visibility.Collapsed;
                GridScroller.Visibility = Visibility.Visible;
                return;
            }
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
            GridScroller.Visibility = Visibility.Collapsed;
        }

        private void UpdatePagingButtons()
        {
            PrevButton.IsEnabled = _page > 1;
            NextButton.IsEnabled = _page < _totalPages;
        }

        // ── events ─────────────────────────────────────────────────────

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_page <= 1) return;
            _page--;
            _ = LoadPageAsync();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_page >= _totalPages) return;
            _page++;
            _ = LoadPageAsync();
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFamily == null) return;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
