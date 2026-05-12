using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        private string _lastUserPrompt;
        private string _lastExecutedCode;
        private int _retryCount;
        private const int MaxRetries = 2;

        private static readonly Regex _placeholderRe = new Regex(@"\{(\w+)\}", RegexOptions.Compiled);

        private static readonly SolidColorBrush BrushOk = new SolidColorBrush(Color.FromRgb(0, 200, 83));
        private static readonly SolidColorBrush BrushErr = new SolidColorBrush(Color.FromRgb(255, 82, 82));
        private static readonly SolidColorBrush BrushDim = new SolidColorBrush(Color.FromRgb(136, 136, 136));

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

            // Load saved commands eagerly (decoupled from the Expander) — fire and forget.
            _ = SafeLoadCommandsAsync();
        }

        private async Task SafeLoadCommandsAsync()
        {
            try
            {
                if (_commandsLoaded) return;
                _commandsLoaded = true;
                await LoadCommandsAsync();
            }
            catch (Exception ex)
            {
                _commandsLoaded = false;
                try { if (CommandsHint != null) CommandsHint.Text = "Couldn't load commands: " + ex.Message; } catch { }
            }
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
            _lastUserPrompt = prompt;
            _retryCount = 0;
            UpdateSaveCommandButton();

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

                    _lastExecutedCode = response.Code;
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

        // --- Saved Commands (browse, run, save, edit, delete) ---

        private async void CommandsExpander_Expanded(object sender, RoutedEventArgs e)
        {
            // Defensive: an unhandled exception in an async void handler bubbles
            // to the Revit UI thread and can take the whole app down.
            try
            {
                if (_commandsLoaded) return;
                _commandsLoaded = true;
                await LoadCommandsAsync();
            }
            catch (Exception ex)
            {
                _commandsLoaded = false;
                try { CommandsHint.Text = "Couldn't load commands: " + ex.Message; } catch { }
            }
        }

        private async Task LoadCommandsAsync()
        {
            try { CommandsHint.Text = "Loading commands..."; } catch { }
            List<CommandTemplate> commands;
            try
            {
                commands = await _aiService.GetCommandsAsync(UserIdOrNull, _config?.OrgId, _config?.AccessToken);
            }
            catch (Exception ex)
            {
                _allCommands = new List<CommandTemplate>();
                try { CommandsHint.Text = "Couldn't reach the backend: " + ex.Message; } catch { }
                return;
            }

            _allCommands = commands ?? new List<CommandTemplate>();
            ApplyCommandFilter(CommandSearchBox?.Text);

            CommandsHint.Text = _allCommands.Count == 0
                ? "No commands available. (Is the backend reachable?)"
                : $"{_allCommands.Count} command(s). Double-click to run.";
        }

        private int? UserIdOrNull => _config?.UserId > 0 ? _config.UserId : (int?)null;

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
            try
            {
                if (!(CommandsList.SelectedItem is CommandTemplate cmd)) return;
                await RunCommand(cmd);
            }
            catch (Exception ex)
            {
                try { CommandsHint.Text = "Couldn't run that command: " + ex.Message; } catch { }
            }
        }

        private async Task RunCommand(CommandTemplate cmd)
        {
            string prompt = cmd.PromptTemplate;

            if (cmd.HasVariables)
            {
                var dialog = new CommandRunWindow(cmd) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                prompt = RenderTemplate(cmd.PromptTemplate, dialog.Values);
            }

            CommandsExpander.IsExpanded = false;
            await SendCodeGenQuery(prompt, cmd.Id);
        }

        private static string RenderTemplate(string template, IDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(template) || values == null) return template;
            return _placeholderRe.Replace(template, m =>
                values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : m.Value);
        }

        private async void SaveAsCommandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_lastUserPrompt))
                {
                    CommandsHint.Text = "Send a prompt first, then you can save it as a command.";
                    return;
                }
                var dialog = new CommandSaveWindow(_lastUserPrompt, UserIdOrNull, _config?.OrgId) { Owner = this };
                if (dialog.ShowDialog() != true) return;

                var created = await _aiService.SaveCommandAsync(dialog.Result, _config?.AccessToken);
                CommandsHint.Text = created != null ? $"Saved \"{created.Name}\"." : "Could not save the command.";
                if (created != null) { _commandsLoaded = false; CommandsExpander.IsExpanded = true; await LoadCommandsAsync(); }
            }
            catch (Exception ex)
            {
                try { CommandsHint.Text = "Couldn't save: " + ex.Message; } catch { }
            }
        }

        private async void EditCommandMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(CommandsList.SelectedItem is CommandTemplate cmd)) return;
                if (cmd.OwnerUserId != UserIdOrNull) { CommandsHint.Text = "You can only edit your own commands."; return; }

                var dialog = new CommandSaveWindow(cmd.PromptTemplate, UserIdOrNull, _config?.OrgId, editing: cmd) { Owner = this };
                if (dialog.ShowDialog() != true) return;

                var updated = await _aiService.UpdateCommandAsync(dialog.EditingTemplateId, dialog.Result, _config?.AccessToken);
                CommandsHint.Text = updated != null ? $"Updated \"{updated.Name}\"." : "Could not update the command.";
                if (updated != null) { _commandsLoaded = false; await LoadCommandsAsync(); }
            }
            catch (Exception ex)
            {
                try { CommandsHint.Text = "Couldn't edit: " + ex.Message; } catch { }
            }
        }

        private async void DeleteCommandMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(CommandsList.SelectedItem is CommandTemplate cmd)) return;
                if (cmd.OwnerUserId != UserIdOrNull) { CommandsHint.Text = "You can only delete your own commands."; return; }

                var confirm = MessageBox.Show($"Delete the command \"{cmd.Name}\"?", "Delete command",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                var ok = await _aiService.DeleteCommandAsync(cmd.Id, UserIdOrNull, _config?.AccessToken);
                CommandsHint.Text = ok ? $"Deleted \"{cmd.Name}\"." : "Could not delete the command.";
                if (ok) { _commandsLoaded = false; await LoadCommandsAsync(); }
            }
            catch (Exception ex)
            {
                try { CommandsHint.Text = "Couldn't delete: " + ex.Message; } catch { }
            }
        }

        private async void RunCommandMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CommandsList.SelectedItem is CommandTemplate cmd)
                    await RunCommand(cmd);
            }
            catch (Exception ex)
            {
                try { CommandsHint.Text = "Couldn't run that command: " + ex.Message; } catch { }
            }
        }

        private void UpdateSaveCommandButton()
        {
            if (SaveAsCommandButton != null)
                SaveAsCommandButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastUserPrompt);
        }

        private void ExecuteCode(string code)
        {
            _lastExecutedCode = code;
            _handler.CodeToExecute = code;
            _handler.OnCompleted = (result) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (result.Success)
                    {
                        AddSuccess(result.Message ?? "Executed successfully");
                        StatusText.Text = "Ready";
                        StatusText.Foreground = BrushOk;
                        _retryCount = 0;
                        SetInputEnabled(true);
                    }
                    else
                    {
                        _ = HandleExecutionFailureAsync(result.Error ?? "Execution failed");
                    }
                });
            };

            _externalEvent.Raise();
        }

        // On compile/exec failure, feed the error back to the AI and re-run the
        // fixed code, up to MaxRetries times.
        private async Task HandleExecutionFailureAsync(string error)
        {
            try
            {
                if (_retryCount >= MaxRetries || string.IsNullOrEmpty(_config?.AccessToken))
                {
                    AddError(error);
                    StatusText.Text = "Execution failed";
                    StatusText.Foreground = BrushErr;
                    _retryCount = 0;
                    SetInputEnabled(true);
                    return;
                }

                _retryCount++;
                AddWarning($"That didn't work. Auto-fixing (attempt {_retryCount}/{MaxRetries})...");
                StatusText.Text = "Auto-fixing the code...";
                StatusText.Foreground = BrushDim;

                var resp = await _aiService.RetryCodeAsync(
                    _lastUserPrompt, _lastExecutedCode, error, _retryCount,
                    UserIdOrNull, _sessionId, _config?.AccessToken);

                if (resp != null && resp.Success && !string.IsNullOrEmpty(resp.Code))
                {
                    AddMessage(resp.Explanation ?? "Trying a corrected version...", isUser: false);
                    AddCodeBlock(resp.Code);
                    StatusText.Text = "Executing in Revit...";
                    StatusText.Foreground = BrushDim;
                    ExecuteCode(resp.Code);   // recurses; _retryCount caps it
                }
                else
                {
                    AddError(resp?.Error ?? error);
                    StatusText.Text = "Execution failed";
                    StatusText.Foreground = BrushErr;
                    _retryCount = 0;
                    SetInputEnabled(true);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    AddError("Auto-fix failed: " + ex.Message);
                    StatusText.Text = "Error";
                    StatusText.Foreground = BrushErr;
                    _retryCount = 0;
                    SetInputEnabled(true);
                }
                catch { }
            }
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
