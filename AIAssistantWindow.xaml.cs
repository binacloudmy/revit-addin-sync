using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace RevitWebAppSync
{
    public partial class AIAssistantWindow : Window
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly AIService _aiService;
        private readonly MentionService _mentionService;
        private readonly CommandLibraryService _commandLibrary;
        private readonly ExternalEvent _externalEvent;
        private readonly CodeExecutionHandler _handler;
        private readonly ExternalEvent _previewExternalEvent;
        private readonly PreviewExecutionHandler _previewHandler;

        private int _totalTokens = 0;
        private string _pendingCode = null;
        private ExecutionPreview _currentPreview = null;
        private bool _isDrawerOpen = false;

        // Mention autocomplete state
        private int _mentionStartIndex = -1;
        private bool _isMentionMode = false;

        // Last successful execution (for save command)
        private string _lastPrompt = null;
        private string _lastCode = null;
        private string _lastExplanation = null;
        private string _currentCategoryFilter = "All";

        public AIAssistantWindow(UIDocument uidoc, ExternalEvent externalEvent, CodeExecutionHandler handler)
        {
            InitializeComponent();

            _uidoc = uidoc;
            _doc = uidoc.Document;
            _externalEvent = externalEvent;
            _handler = handler;

            // Get preview handlers from App
            _previewExternalEvent = App.PreviewExternalEvent;
            _previewHandler = App.PreviewHandler;

            // Use default ngrok URL from AIService
            _aiService = new AIService();

            // Initialize mention service for @mentions
            _mentionService = new MentionService(_doc, _uidoc);

            // Initialize command library
            _commandLibrary = new CommandLibraryService();
            LoadCommandLibrary();

            // Check backend connection on load
            CheckBackendConnection();
        }

        private void LoadCommandLibrary(string categoryFilter = "All")
        {
            _currentCategoryFilter = categoryFilter;
            List<SavedCommand> commands;

            if (categoryFilter == "All")
            {
                commands = _commandLibrary.GetAllCommands();
            }
            else
            {
                commands = _commandLibrary.GetCommandsByCategory(categoryFilter);
            }

            CommandsList.ItemsSource = commands;
            CommandCountText.Text = $"({commands.Count} commands)";
        }

        private async void CheckBackendConnection()
        {
            StatusText.Text = "Connecting to backend...";
            var isHealthy = await _aiService.HealthCheckAsync();

            if (isHealthy)
            {
                StatusText.Text = "Connected";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 83));
            }
            else
            {
                StatusText.Text = "Backend not available";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
            }
        }

        private void PromptInput_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Enter to send
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SendButton_Click(sender, e);
                e.Handled = true;
            }
        }

        #region Mention Autocomplete

        private void PromptInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = PromptInput.Text;
            var caretIndex = PromptInput.CaretIndex;

            // Check if we should enter mention mode
            if (caretIndex > 0)
            {
                // Find the last @ before caret
                var lastAtIndex = text.LastIndexOf('@', caretIndex - 1);

                if (lastAtIndex >= 0)
                {
                    // Check if there's a space between @ and caret (not in mention mode)
                    var textAfterAt = text.Substring(lastAtIndex + 1, caretIndex - lastAtIndex - 1);

                    // If no space after @, we're in mention mode
                    if (!textAfterAt.Contains(" ") || textAfterAt.Split(' ').Length <= 2)
                    {
                        _isMentionMode = true;
                        _mentionStartIndex = lastAtIndex;

                        // Filter items based on text after @
                        var searchText = textAfterAt.TrimStart();
                        var items = _mentionService.FilterItems(searchText);

                        if (items.Count > 0)
                        {
                            MentionListBox.ItemsSource = items;
                            MentionListBox.SelectedIndex = 0;
                            MentionPopup.IsOpen = true;
                            return;
                        }
                    }
                }
            }

            // Close popup if not in mention mode
            CloseMentionPopup();
        }

        private void PromptInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!MentionPopup.IsOpen) return;

            switch (e.Key)
            {
                case Key.Down:
                    // Navigate down in list
                    if (MentionListBox.SelectedIndex < MentionListBox.Items.Count - 1)
                    {
                        MentionListBox.SelectedIndex++;
                        MentionListBox.ScrollIntoView(MentionListBox.SelectedItem);
                    }
                    e.Handled = true;
                    break;

                case Key.Up:
                    // Navigate up in list
                    if (MentionListBox.SelectedIndex > 0)
                    {
                        MentionListBox.SelectedIndex--;
                        MentionListBox.ScrollIntoView(MentionListBox.SelectedItem);
                    }
                    e.Handled = true;
                    break;

                case Key.Enter:
                case Key.Tab:
                    // Insert selected mention
                    if (MentionListBox.SelectedItem is MentionItem selectedItem)
                    {
                        InsertMention(selectedItem);
                        e.Handled = true;
                    }
                    break;

                case Key.Escape:
                    // Close popup
                    CloseMentionPopup();
                    e.Handled = true;
                    break;
            }
        }

        private void MentionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Keep focus on text box
            PromptInput.Focus();
        }

        private void MentionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (MentionListBox.SelectedItem is MentionItem selectedItem)
            {
                InsertMention(selectedItem);
            }
        }

        private void InsertMention(MentionItem item)
        {
            if (_mentionStartIndex < 0) return;

            var text = PromptInput.Text;
            var caretIndex = PromptInput.CaretIndex;

            // Build the mention text
            var mentionText = item.Name.Contains(" ") ? $"@\"{item.Name}\"" : $"@{item.Name}";

            // Replace text from @ to caret with the mention
            var newText = text.Substring(0, _mentionStartIndex) + mentionText;
            if (caretIndex < text.Length)
            {
                newText += text.Substring(caretIndex);
            }

            PromptInput.Text = newText;
            PromptInput.CaretIndex = _mentionStartIndex + mentionText.Length;

            CloseMentionPopup();
            PromptInput.Focus();
        }

        private void CloseMentionPopup()
        {
            MentionPopup.IsOpen = false;
            _isMentionMode = false;
            _mentionStartIndex = -1;
        }

        #endregion

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var prompt = PromptInput.Text?.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            // Close mention popup if open
            CloseMentionPopup();

            // Close drawer if open
            if (_isDrawerOpen) CloseDrawer();

            // Disable input while processing
            SetInputEnabled(false);
            StatusText.Text = "Generating code...";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));

            // Add user message to chat
            AddMessage(prompt, isUser: true);
            PromptInput.Text = "";

            // Hide save button from previous execution
            SaveCommandBtn.Visibility = Visibility.Collapsed;
            _lastPrompt = prompt;
            _lastCode = null;
            _lastExplanation = null;

            try
            {
                // Resolve @mentions in the prompt
                var mentionContext = _mentionService.ResolveMentions(prompt);

                // Get model context from current Revit state
                var context = GetModelContext();

                // Call AI service with mention context
                var response = await _aiService.GenerateCodeAsync(prompt, context, mentionContext);

                if (response.Success && !string.IsNullOrEmpty(response.Code))
                {
                    // Store for potential save
                    _lastExplanation = response.Explanation;

                    // Add AI response to chat
                    AddMessage(response.Explanation ?? "Processing request...", isUser: false);

                    // Update tokens
                    if (response.TokensUsed.HasValue)
                    {
                        _totalTokens += response.TokensUsed.Value;
                        TokensText.Text = $"Tokens: {_totalTokens}";
                    }

                    // Show warnings if any
                    if (response.Warnings?.Count > 0)
                    {
                        AddWarning(string.Join("\n", response.Warnings));
                    }

                    // Execute code directly (no preview)
                    StatusText.Text = "Executing...";
                    ExecuteCode(response.Code);
                }
                else
                {
                    AddError(response.Error ?? "Unknown error occurred");
                    SetInputEnabled(true);
                    StatusText.Text = "Error";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
                }
            }
            catch (Exception ex)
            {
                AddError($"Error: {ex.Message}");
                SetInputEnabled(true);
                StatusText.Text = "Error";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
            }
        }

        #region Side Drawer

        private void PreviewAndShowDrawer(string code, string explanation)
        {
            _pendingCode = code;

            _previewHandler.CodeToPreview = code;
            _previewHandler.Explanation = explanation;
            _previewHandler.OnCompleted = (preview) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (!preview.Success)
                    {
                        AddError(preview.Error ?? "Preview failed");
                        StatusText.Text = "Preview failed";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
                        SetInputEnabled(true);
                        return;
                    }

                    _currentPreview = preview;
                    PopulateDrawer(preview);
                    OpenDrawer();
                    StatusText.Text = "Review changes";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                });
            };

            _previewExternalEvent.Raise();
        }

        private void PopulateDrawer(ExecutionPreview preview)
        {
            // Set explanation
            DrawerExplanation.Text = preview.Explanation ?? preview.ExecutionMessage ?? "";

            // Set risk badge
            SetRiskBadge(preview.Risk);

            // Set summary
            DrawerSummary.Text = preview.Summary;

            // Clear and populate badges
            SummaryBadges.Children.Clear();
            if (preview.DeletedElements.Count > 0)
                AddBadge($"{preview.DeletedElements.Count} deleted", "#F44336");
            if (preview.ModifiedElements.Count > 0)
                AddBadge($"{preview.ModifiedElements.Count} modified", "#FF9800");
            if (preview.CreatedElements.Count > 0)
                AddBadge($"{preview.CreatedElements.Count} created", "#4CAF50");
            if (preview.SelectedElements.Count > 0)
                AddBadge($"{preview.SelectedElements.Count} selected", "#2196F3");

            // Clear and populate changes list
            DrawerChangesList.Children.Clear();

            // Add deleted elements
            foreach (var elem in preview.DeletedElements.Take(20))
                AddChangeItem(elem, "#F44336");

            // Add modified elements
            foreach (var elem in preview.ModifiedElements.Take(20))
                AddChangeItem(elem, "#FF9800", showParams: true);

            // Add created elements
            foreach (var elem in preview.CreatedElements.Take(20))
                AddChangeItem(elem, "#4CAF50");

            // Add selected elements
            foreach (var elem in preview.SelectedElements.Take(20))
                AddChangeItem(elem, "#2196F3");

            // Show highlight button if there are elements
            HighlightBtn.Visibility = preview.TotalAffected > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Update approve button style based on risk
            if (preview.Risk == RiskLevel.High)
            {
                ApproveBtn.Style = (Style)FindResource("DangerButton");
                ApproveBtn.Content = "Delete Elements";
            }
            else
            {
                ApproveBtn.Style = (Style)FindResource("ApproveButton");
                ApproveBtn.Content = preview.IsReadOnly ? "Continue" : "Apply Changes";
            }
        }

        private void SetRiskBadge(RiskLevel risk)
        {
            switch (risk)
            {
                case RiskLevel.High:
                    RiskBadge.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    RiskText.Text = "HIGH";
                    break;
                case RiskLevel.Medium:
                    RiskBadge.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                    RiskText.Text = "MEDIUM";
                    break;
                case RiskLevel.Low:
                    RiskBadge.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    RiskText.Text = "LOW";
                    break;
                default:
                    RiskBadge.Background = new SolidColorBrush(Color.FromRgb(33, 150, 243));
                    RiskText.Text = "SAFE";
                    break;
            }
        }

        private void AddBadge(string text, string hexColor)
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 0)
            };
            border.Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(color),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            SummaryBadges.Children.Add(border);
        }

        private void AddChangeItem(ElementChange elem, string hexColor, bool showParams = false)
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 4),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)),
                BorderThickness = new Thickness(0, 0, 3, 0)
            };

            var stack = new StackPanel();

            // Main row
            var mainRow = new StackPanel { Orientation = Orientation.Horizontal };
            mainRow.Children.Add(new TextBlock
            {
                Text = elem.Category,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                FontSize = 10,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            mainRow.Children.Add(new TextBlock
            {
                Text = elem.ElementName,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            });
            stack.Children.Add(mainRow);

            // Parameter changes
            if (showParams && elem.ParameterChanges?.Count > 0)
            {
                foreach (var pc in elem.ParameterChanges.Take(2))
                {
                    var paramRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                    paramRow.Children.Add(new TextBlock
                    {
                        Text = $"{pc.ParameterName}: ",
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                        FontSize = 10
                    });
                    paramRow.Children.Add(new TextBlock
                    {
                        Text = pc.BeforeValue,
                        Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                        FontSize = 10,
                        TextDecorations = TextDecorations.Strikethrough
                    });
                    paramRow.Children.Add(new TextBlock
                    {
                        Text = " -> ",
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                        FontSize = 10
                    });
                    paramRow.Children.Add(new TextBlock
                    {
                        Text = pc.AfterValue,
                        Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold
                    });
                    stack.Children.Add(paramRow);
                }
            }

            border.Child = stack;
            DrawerChangesList.Children.Add(border);
        }

        private void OpenDrawer()
        {
            _isDrawerOpen = true;
            var storyboard = (Storyboard)FindResource("OpenDrawer");
            storyboard.Begin(this);
        }

        private void CloseDrawer()
        {
            _isDrawerOpen = false;
            var storyboard = (Storyboard)FindResource("CloseDrawer");
            storyboard.Begin(this);
        }

        private void HighlightButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPreview == null) return;

            try
            {
                var elementIds = _currentPreview.Changes
                    .Where(c => c.ChangeType != ChangeType.Deleted)
                    .Select(c => new ElementId(c.ElementId))
                    .ToList();

                if (elementIds.Count > 0)
                {
                    _uidoc.Selection.SetElementIds(elementIds);
                    try { _uidoc.ShowElements(elementIds); } catch { }
                }
            }
            catch { }
        }

        private void ApproveButton_Click(object sender, RoutedEventArgs e)
        {
            CloseDrawer();
            StatusText.Text = "Applying changes...";
            ExecuteCode(_pendingCode);
            _pendingCode = null;
            _currentPreview = null;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseDrawer();
            AddInfo("Changes cancelled");
            StatusText.Text = "Cancelled";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            SetInputEnabled(true);
            _pendingCode = null;
            _currentPreview = null;
        }

        #endregion

        /// <summary>
        /// Execute code directly (after preview approval)
        /// </summary>
        private void ExecuteCode(string code, bool fromSavedCommand = false)
        {
            _handler.CodeToExecute = code;
            _handler.OnCompleted = (result) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (result.Success)
                    {
                        AddSuccess(result.Message ?? "Executed successfully");
                        StatusText.Text = "Ready";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 83));

                        // Show save button only for AI-generated commands (not saved ones)
                        if (!fromSavedCommand && !string.IsNullOrEmpty(_lastPrompt))
                        {
                            _lastCode = code;
                            SaveCommandBtn.Visibility = Visibility.Visible;
                        }
                    }
                    else
                    {
                        AddError(result.Error ?? "Execution failed");
                        StatusText.Text = "Execution failed";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
                        SaveCommandBtn.Visibility = Visibility.Collapsed;
                    }

                    SetInputEnabled(true);
                });
            };

            _externalEvent.Raise();
        }

        private ModelContext GetModelContext()
        {
            var context = new ModelContext
            {
                ProjectName = _doc.Title,
                RevitVersion = _uidoc.Application.Application.VersionNumber,
                Levels = new List<string>(),
                Categories = new List<string>(),
                Phases = new List<string>(),
                SelectedElementIds = new List<int>()
            };

            try
            {
                // Get levels
                var levels = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => l.Name)
                    .ToList();
                context.Levels = levels;

                // Get active view info
                var activeView = _doc.ActiveView;
                if (activeView != null)
                {
                    context.ActiveViewName = activeView.Name;
                    context.ActiveViewType = activeView.ViewType.ToString();
                }

                // Get selected elements
                var selection = _uidoc.Selection.GetElementIds();
                context.SelectedElementIds = selection.Select(id => (int)id.Value).ToList();

                // Get phases
                var phases = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Phase))
                    .Cast<Phase>()
                    .Select(p => p.Name)
                    .ToList();
                context.Phases = phases;

                // Common categories available in the model
                context.Categories = new List<string>
                {
                    "Walls", "Doors", "Windows", "Floors", "Roofs",
                    "Ceilings", "Rooms", "Furniture", "Columns",
                    "Structural Columns", "Structural Framing"
                };
            }
            catch
            {
                // Ignore errors in context gathering - proceed with partial context
            }

            return context;
        }

        #region Chat UI Helpers

        private void AddMessage(string text, bool isUser)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(isUser
                    ? Color.FromRgb(0, 120, 212)
                    : Color.FromRgb(45, 45, 48)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(isUser ? 50 : 0, 4, isUser ? 0 : 50, 4),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };

            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300
            };

            border.Child = textBlock;
            ChatHistory.Children.Add(border);
            ScrollToBottom();
        }

        private void AddCodeBlock(string code)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 4)
            };

            var textBox = new TextBox
            {
                Text = code,
                Foreground = new SolidColorBrush(Color.FromRgb(156, 220, 254)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11
            };

            border.Child = textBox;
            ChatHistory.Children.Add(border);
            ScrollToBottom();
        }

        private void AddSuccess(string message)
        {
            var textBlock = new TextBlock
            {
                Text = $"[OK] {message}",
                Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 83)),
                Margin = new Thickness(0, 4, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };

            ChatHistory.Children.Add(textBlock);
            ScrollToBottom();
        }

        private void AddError(string message)
        {
            var textBlock = new TextBlock
            {
                Text = $"[Error] {message}",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82)),
                Margin = new Thickness(0, 4, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };

            ChatHistory.Children.Add(textBlock);
            ScrollToBottom();
        }

        private void AddWarning(string message)
        {
            var textBlock = new TextBlock
            {
                Text = $"[Warning] {message}",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                Margin = new Thickness(0, 4, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };

            ChatHistory.Children.Add(textBlock);
            ScrollToBottom();
        }

        private void AddInfo(string message)
        {
            var textBlock = new TextBlock
            {
                Text = $"[Info] {message}",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                Margin = new Thickness(0, 4, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };

            ChatHistory.Children.Add(textBlock);
            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            ChatScrollViewer.ScrollToEnd();
        }

        private void SetInputEnabled(bool enabled)
        {
            PromptInput.IsEnabled = enabled;
            SendButton.IsEnabled = enabled;

            // Also enable/disable quick action buttons
            foreach (var child in QuickActionsPanel.Children)
            {
                if (child is Button btn)
                {
                    btn.IsEnabled = enabled;
                }
            }
        }

        #endregion

        #region Command Library

        private void FilterCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string category)
            {
                LoadCommandLibrary(category);
            }
        }

        private void Command_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is SavedCommand command)
            {
                // Close mention popup if open
                CloseMentionPopup();

                // Disable input while executing
                SetInputEnabled(false);
                StatusText.Text = "Executing command...";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));

                // Add to chat
                AddMessage($"Running: {command.Name}", isUser: true);

                // Record usage
                _commandLibrary.RecordUsage(command.Id);
                LoadCommandLibrary(_currentCategoryFilter);

                // Execute the saved code directly
                ExecuteCode(command.Code, fromSavedCommand: true);
            }
        }

        private void SaveCommand_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastCode) || string.IsNullOrEmpty(_lastPrompt))
            {
                return;
            }

            // Show save dialog
            var dialog = new SaveCommandDialog(_lastPrompt, _lastExplanation);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true)
            {
                // Save the command
                var command = _commandLibrary.SaveCommand(
                    name: dialog.CommandName,
                    prompt: _lastPrompt,
                    code: _lastCode,
                    description: _lastExplanation ?? dialog.CommandName,
                    category: dialog.Category,
                    icon: dialog.Icon
                );

                // Refresh library
                LoadCommandLibrary(_currentCategoryFilter);

                // Hide save button
                SaveCommandBtn.Visibility = Visibility.Collapsed;

                AddSuccess($"Command '{command.Name}' saved to library");
            }
        }

        #endregion
    }
}
