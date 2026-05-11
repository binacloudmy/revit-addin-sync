using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace RevitWebAppSync
{
    public partial class AIAssistantWindow : Window
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly AIService _aiService;
        private readonly JkrSpecService _jkrService;
        private readonly ExternalEvent _externalEvent;
        private readonly CodeExecutionHandler _handler;
        private readonly BinaConfig _config;
        private readonly string _sessionId = Guid.NewGuid().ToString();

        private CancellationTokenSource _cts;
        private int _totalTokens = 0;

        private List<CommandTemplate> _allCommands;
        private bool _commandsLoaded;

        // Track which tab is active
        private bool IsJkrMode => ModeTabs.SelectedIndex == 1;

        // Current chat panel based on active tab
        private StackPanel ActiveChatHistory => IsJkrMode ? JkrChatHistory : CopilotChatHistory;
        private ScrollViewer ActiveScrollViewer => IsJkrMode ? JkrScrollViewer : CopilotScrollViewer;

        public AIAssistantWindow(UIDocument uidoc, ExternalEvent externalEvent, CodeExecutionHandler handler)
        {
            InitializeComponent();

            _uidoc = uidoc;
            _doc = uidoc.Document;
            _externalEvent = externalEvent;
            _handler = handler;

            _config = BinaConfig.Load();
            _aiService = new AIService(_config.ResolvedAIBaseUrl);
            _jkrService = new JkrSpecService(_config.ResolvedAIBaseUrl);

            CheckBackendConnection();
            EnforceLoginGate();
        }

        private void EnforceLoginGate()
        {
            if (string.IsNullOrEmpty(_config?.AccessToken))
            {
                SetInputEnabled(false);
                AddError("Please log in first to use the AI Assistant.");
            }
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

        private void ModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Update placeholder hint when switching tabs
            if (PromptInput == null) return;

            PromptInput.ToolTip = IsJkrMode
                ? "Ask about JKR BIM specifications (Ctrl+Enter to send)"
                : "Enter your prompt (Ctrl+Enter to send)";
        }

        private void PromptInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SendButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var prompt = PromptInput.Text?.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            if (IsJkrMode)
            {
                await SendJkrQuery(prompt);
            }
            else
            {
                await SendCodeGenQuery(prompt);
            }
        }

        #region Revit Copilot (Code Gen)

        private async Task SendCodeGenQuery(string prompt, string templateId = null)
        {
            if (string.IsNullOrEmpty(_config?.AccessToken))
            {
                AddError("Please log in first to use the AI Assistant.");
                return;
            }

            _cts = new CancellationTokenSource();
            SetInputEnabled(false);
            SetCancelVisible(true);
            StatusText.Text = "Generating code...";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));

            AddMessage(prompt, isUser: true);
            PromptInput.Text = "";

            var requestInFlight = true;
            try
            {
                var request = new Models.AIRequest
                {
                    Prompt = prompt,
                    Context = GetModelContext(),
                    UserId = _config.UserId > 0 ? _config.UserId : (int?)null,
                    SessionId = _sessionId,
                    TemplateId = templateId
                };

                var response = await _aiService.GenerateCodeAsync(request, _config.AccessToken, _cts.Token);
                requestInFlight = false;
                SetCancelVisible(false);

                if (response.Success && !string.IsNullOrEmpty(response.Code))
                {
                    AddMessage(response.Explanation ?? "Executing code...", isUser: false);
                    AddCodeBlock(response.Code);

                    if (response.TokensUsed.HasValue)
                    {
                        _totalTokens += response.TokensUsed.Value;
                        TokensText.Text = $"Tokens: {_totalTokens}";
                    }

                    if (response.Warnings?.Count > 0)
                    {
                        AddWarning(string.Join("\n", response.Warnings));
                    }

                    StatusText.Text = "Executing in Revit...";
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
            finally
            {
                if (requestInFlight) SetCancelVisible(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void SetCancelVisible(bool visible)
        {
            CancelButton.Visibility = visible
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        // --- Saved Commands (browse + run) ---

        private async void CommandsExpander_Expanded(object sender, RoutedEventArgs e)
        {
            if (_commandsLoaded) return;
            _commandsLoaded = true;
            await LoadCommandsAsync();
        }

        private async Task LoadCommandsAsync()
        {
            CommandsHint.Text = "Loading commands...";
            int? userId = _config?.UserId > 0 ? _config.UserId : (int?)null;
            var commands = await _aiService.GetCommandsAsync(userId, null, _config?.AccessToken);

            _allCommands = commands ?? new List<CommandTemplate>();
            ApplyCommandFilter(CommandSearchBox.Text);

            CommandsHint.Text = _allCommands.Count == 0
                ? "No commands available. (Is the backend reachable?)"
                : $"{_allCommands.Count} command(s). Double-click to run.";
        }

        private void CommandSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_allCommands == null) return;
            ApplyCommandFilter(CommandSearchBox.Text);
        }

        private void ApplyCommandFilter(string filter)
        {
            IEnumerable<CommandTemplate> view = _allCommands ?? new List<CommandTemplate>();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var f = filter.Trim();
                view = view.Where(c =>
                    (c.Name?.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (c.Category?.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (c.Description?.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0));
            }
            CommandsList.ItemsSource = view.ToList();
        }

        private async void CommandsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(CommandsList.SelectedItem is CommandTemplate cmd)) return;

            if (cmd.HasVariables)
            {
                // Phase 2 fallback: drop the raw template into the input so the user
                // can fill the {placeholders} by hand. The variable form arrives in phase 3.
                PromptInput.Text = cmd.PromptTemplate;
                PromptInput.Focus();
                PromptInput.CaretIndex = PromptInput.Text.Length;
                CommandsHint.Text = $"\"{cmd.Name}\" has variables — fill the {{...}} placeholders, then Send.";
                CommandsExpander.IsExpanded = false;
                return;
            }

            CommandsExpander.IsExpanded = false;
            await SendCodeGenQuery(cmd.PromptTemplate, cmd.Id);
        }

        private void ExecuteCode(string code)
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
                    }
                    else
                    {
                        AddError(result.Error ?? "Execution failed");
                        StatusText.Text = "Execution failed";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
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
                    "Ceilings", "Rooms", "Furniture", "Columns",
                    "Structural Columns", "Structural Framing"
                };
            }
            catch
            {
                // Ignore errors in context gathering
            }

            return context;
        }

        #endregion

        #region JKR Guide

        private async Task SendJkrQuery(string question)
        {
            SetInputEnabled(false);
            StatusText.Text = "Searching JKR specs...";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));

            AddMessage(question, isUser: true);
            PromptInput.Text = "";

            try
            {
                var response = await _jkrService.AskAsync(question);

                if (!string.IsNullOrEmpty(response?.Content))
                {
                    AddMessage(response.Content, isUser: false);
                    StatusText.Text = "Ready";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 83));
                }
                else
                {
                    AddError("No response from JKR Specialist agent.");
                    StatusText.Text = "Error";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
                }
            }
            catch (Exception ex)
            {
                AddError($"Error: {ex.Message}");
                StatusText.Text = "Error";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
            }

            SetInputEnabled(true);
        }

        #endregion

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

            if (isUser)
            {
                var textBlock = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 350
                };
                border.Child = textBlock;
            }
            else
            {
                // Render markdown for bot responses
                border.Child = Helpers.MarkdownRenderer.Render(text, 350);
            }

            ActiveChatHistory.Children.Add(border);
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
            ActiveChatHistory.Children.Add(border);
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

            ActiveChatHistory.Children.Add(textBlock);
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

            ActiveChatHistory.Children.Add(textBlock);
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

            ActiveChatHistory.Children.Add(textBlock);
            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            ActiveScrollViewer.ScrollToEnd();
        }

        private void SetInputEnabled(bool enabled)
        {
            PromptInput.IsEnabled = enabled;
            SendButton.IsEnabled = enabled;
        }

        #endregion
    }
}
