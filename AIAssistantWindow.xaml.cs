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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

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

        private int _totalTokens = 0;

        // Mention autocomplete state
        private int _mentionStartIndex = -1;
        private bool _isMentionMode = false;

        // Last successful execution (for save command)
        private string _lastPrompt = null;
        private string _lastCode = null;
        private string _lastExplanation = null;

        public AIAssistantWindow(UIDocument uidoc, ExternalEvent externalEvent, CodeExecutionHandler handler)
        {
            InitializeComponent();

            _uidoc = uidoc;
            _doc = uidoc.Document;
            _externalEvent = externalEvent;
            _handler = handler;

            _aiService = new AIService();
            _mentionService = new MentionService(_doc, _uidoc);
            _commandLibrary = new CommandLibraryService();

            LoadCommandLibrary();
            CheckBackendConnection();
        }

        #region Backend Connection

        private async void CheckBackendConnection()
        {
            SetStatus("Connecting...", "#FFA000");
            var isHealthy = await _aiService.HealthCheckAsync();

            if (isHealthy)
            {
                SetStatus("Ready", "#2E7D32");
            }
            else
            {
                SetStatus("Offline", "#D32F2F");
            }
        }

        private void SetStatus(string text, string color)
        {
            StatusBadge.Text = text;
            StatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

            var parent = StatusBadge.Parent as Border;
            if (parent != null)
            {
                if (color == "#2E7D32")
                    parent.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8F5E9"));
                else if (color == "#D32F2F")
                    parent.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEBEE"));
                else
                    parent.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF3E0"));
            }
        }

        #endregion

        #region Tab Navigation

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (ChatTab == null || CommandsTab == null) return;

            if (ChatTab.IsChecked == true)
            {
                ChatPanel.Visibility = Visibility.Visible;
                CommandsPanel.Visibility = Visibility.Collapsed;
                ChatSubHeader.Visibility = Visibility.Visible;
            }
            else
            {
                ChatPanel.Visibility = Visibility.Collapsed;
                CommandsPanel.Visibility = Visibility.Visible;
                ChatSubHeader.Visibility = Visibility.Collapsed;
            }
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Show chat history
            MessageBox.Show("Chat history feature coming soon!", "History", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void NewChat_Click(object sender, RoutedEventArgs e)
        {
            ChatHistory.Children.Clear();
            EmptyState.Visibility = Visibility.Visible;
            SaveCommandBtn.Visibility = Visibility.Collapsed;
            _lastPrompt = null;
            _lastCode = null;
            _lastExplanation = null;
        }

        #endregion

        #region Input Handling

        private void PromptInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                SendButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var prompt = PromptInput.Text?.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            CloseMentionPopup();
            EmptyState.Visibility = Visibility.Collapsed;
            SaveCommandBtn.Visibility = Visibility.Collapsed;

            _lastPrompt = prompt;
            _lastCode = null;
            _lastExplanation = null;

            SetInputEnabled(false);
            SetStatus("Generating...", "#FFA000");

            AddUserMessage(prompt);
            PromptInput.Text = "";
            UpdatePlaceholder();

            try
            {
                var mentionContext = _mentionService.ResolveMentions(prompt);
                var context = GetModelContext();
                var response = await _aiService.GenerateCodeAsync(prompt, context, mentionContext);

                if (response.Success && !string.IsNullOrEmpty(response.Code))
                {
                    _lastExplanation = response.Explanation;
                    _lastCode = response.Code;

                    if (response.TokensUsed.HasValue)
                    {
                        _totalTokens += response.TokensUsed.Value;
                    }

                    SetStatus("Executing...", "#FFA000");
                    ExecuteCode(response.Code, response.Explanation);
                }
                else
                {
                    AddErrorMessage(response.Error ?? "Unknown error occurred");
                    SetInputEnabled(true);
                    SetStatus("Error", "#D32F2F");
                }
            }
            catch (Exception ex)
            {
                AddErrorMessage($"Error: {ex.Message}");
                SetInputEnabled(true);
                SetStatus("Error", "#D32F2F");
            }
        }

        private void ExecuteCode(string code, string explanation, bool fromSavedCommand = false)
        {
            _handler.CodeToExecute = code;
            _handler.OnCompleted = (result) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (result.Success)
                    {
                        AddAIResponse(explanation ?? "Command executed successfully.", result.Message, true);
                        SetStatus("Ready", "#2E7D32");

                        if (!fromSavedCommand && !string.IsNullOrEmpty(_lastPrompt))
                        {
                            SaveCommandBtn.Visibility = Visibility.Visible;
                        }
                    }
                    else
                    {
                        AddAIResponse(explanation ?? "Execution failed.", result.Error, false);
                        SetStatus("Error", "#D32F2F");
                        SaveCommandBtn.Visibility = Visibility.Collapsed;
                    }

                    SetInputEnabled(true);
                });
            };

            _externalEvent.Raise();
        }

        private void SetInputEnabled(bool enabled)
        {
            PromptInput.IsEnabled = enabled;
            SendButton.IsEnabled = enabled;
        }

        private void UpdatePlaceholder()
        {
            InputPlaceholder.Visibility = string.IsNullOrEmpty(PromptInput.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        #endregion

        #region Chat Messages

        private void AddUserMessage(string text)
        {
            var border = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0066CC")),
                CornerRadius = new CornerRadius(12, 12, 4, 12),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(50, 8, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 300
            };

            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            };

            border.Child = textBlock;
            ChatHistory.Children.Add(border);
            ScrollToBottom();
        }

        private void AddAIResponse(string explanation, string result, bool success)
        {
            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#22C55E" : "#EF4444")),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 8, 50, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 320
            };

            var stack = new StackPanel();

            // Explanation
            if (!string.IsNullOrEmpty(explanation))
            {
                var explanationText = new TextBlock
                {
                    Text = explanation,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1A1A")),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                stack.Children.Add(explanationText);
            }

            // Result
            if (!string.IsNullOrEmpty(result))
            {
                var resultBorder = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#F0FDF4" : "#FEF2F2")),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8, 10, 8)
                };

                var resultText = new TextBlock
                {
                    Text = result,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#166534" : "#991B1B"))
                };

                resultBorder.Child = resultText;
                stack.Children.Add(resultBorder);
            }

            card.Child = stack;
            ChatHistory.Children.Add(card);
            ScrollToBottom();
        }

        private void AddErrorMessage(string message)
        {
            AddAIResponse(null, message, false);
        }

        private void ScrollToBottom()
        {
            ChatScrollViewer.ScrollToEnd();
        }

        #endregion

        #region Command Library

        private void LoadCommandLibrary(string searchText = null)
        {
            var commands = string.IsNullOrWhiteSpace(searchText)
                ? _commandLibrary.GetAllCommands()
                : _commandLibrary.SearchCommands(searchText);

            CommandsList.ItemsSource = commands;
        }

        private void CommandSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            LoadCommandLibrary(CommandSearchBox.Text);
        }

        private void Command_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is SavedCommand command)
            {
                // Switch to chat tab
                ChatTab.IsChecked = true;
                EmptyState.Visibility = Visibility.Collapsed;

                SetInputEnabled(false);
                SetStatus("Executing...", "#FFA000");

                AddUserMessage($"Running: {command.Name}");

                _commandLibrary.RecordUsage(command.Id);
                LoadCommandLibrary(CommandSearchBox.Text);

                ExecuteCode(command.Code, command.Description, fromSavedCommand: true);
            }
        }

        private void SaveCommand_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastCode) || string.IsNullOrEmpty(_lastPrompt))
            {
                return;
            }

            var dialog = new SaveCommandDialog(_lastPrompt, _lastExplanation);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true)
            {
                var command = _commandLibrary.SaveCommand(
                    name: dialog.CommandName,
                    prompt: _lastPrompt,
                    code: _lastCode,
                    description: _lastExplanation ?? dialog.CommandName,
                    category: dialog.Category,
                    icon: dialog.Icon
                );

                LoadCommandLibrary();
                SaveCommandBtn.Visibility = Visibility.Collapsed;

                AddAIResponse($"Command '{command.Name}' saved to library!", null, true);
            }
        }

        #endregion

        #region Mention Autocomplete

        private void PromptInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePlaceholder();

            var text = PromptInput.Text;
            var caretIndex = PromptInput.CaretIndex;

            if (caretIndex > 0)
            {
                var lastAtIndex = text.LastIndexOf('@', caretIndex - 1);

                if (lastAtIndex >= 0)
                {
                    var textAfterAt = text.Substring(lastAtIndex + 1, caretIndex - lastAtIndex - 1);

                    if (!textAfterAt.Contains(" ") || textAfterAt.Split(' ').Length <= 2)
                    {
                        _isMentionMode = true;
                        _mentionStartIndex = lastAtIndex;

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

            CloseMentionPopup();
        }

        private void PromptInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!MentionPopup.IsOpen) return;

            switch (e.Key)
            {
                case Key.Down:
                    if (MentionListBox.SelectedIndex < MentionListBox.Items.Count - 1)
                    {
                        MentionListBox.SelectedIndex++;
                        MentionListBox.ScrollIntoView(MentionListBox.SelectedItem);
                    }
                    e.Handled = true;
                    break;

                case Key.Up:
                    if (MentionListBox.SelectedIndex > 0)
                    {
                        MentionListBox.SelectedIndex--;
                        MentionListBox.ScrollIntoView(MentionListBox.SelectedItem);
                    }
                    e.Handled = true;
                    break;

                case Key.Enter:
                case Key.Tab:
                    if (MentionListBox.SelectedItem is MentionItem selectedItem)
                    {
                        InsertMention(selectedItem);
                        e.Handled = true;
                    }
                    break;

                case Key.Escape:
                    CloseMentionPopup();
                    e.Handled = true;
                    break;
            }
        }

        private void MentionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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

            var mentionText = item.Name.Contains(" ") ? $"@\"{item.Name}\"" : $"@{item.Name}";

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

        #region Helpers

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
                var levels = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => l.Name)
                    .ToList();
                context.Levels = levels;

                var activeView = _doc.ActiveView;
                if (activeView != null)
                {
                    context.ActiveViewName = activeView.Name;
                    context.ActiveViewType = activeView.ViewType.ToString();
                }

                var selection = _uidoc.Selection.GetElementIds();
                context.SelectedElementIds = selection.Select(id => (int)id.Value).ToList();

                var phases = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Phase))
                    .Cast<Phase>()
                    .Select(p => p.Name)
                    .ToList();
                context.Phases = phases;

                context.Categories = new List<string>
                {
                    "Walls", "Doors", "Windows", "Floors", "Roofs",
                    "Ceilings", "Rooms", "Furniture", "Columns"
                };
            }
            catch { }

            return context;
        }

        #endregion
    }
}
