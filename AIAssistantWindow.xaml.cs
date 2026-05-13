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

        // Structured conversation memory (per-session) — sent with each request so
        // the agent can resolve pronouns ("those", "it", "the first one"). Holds
        // the PREVIOUS turn until SendCodeGenQuery updates them.
        private string _ctxPreviousPrompt;
        private string _ctxLastOperation;
        private string _ctxLastResult;

        // Plain-text running log of the conversation, for "Copy transcript".
        private readonly System.Text.StringBuilder _transcript = new System.Text.StringBuilder();

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

        // ===================== @mention autocomplete =====================

        private List<MentionItem> _mentionCatalog;
        private int _mentionTokenStart = -1;

        private void BuildMentionCatalogIfNeeded()
        {
            if (_mentionCatalog != null) return;
            var list = new List<MentionItem>();

            // Special / bulk mentions (no API needed).
            list.Add(new MentionItem("selected", "@selected", "current selection", "@selected -> the elements currently selected in Revit"));
            list.Add(new MentionItem("here", "@here", "elements in active view", "@here -> the elements visible in the active view"));
            foreach (var cat in new[] { "walls", "doors", "windows", "floors", "roofs", "ceilings", "rooms", "columns", "grids" })
                list.Add(new MentionItem("all_" + cat, "@all_" + cat, "all " + cat, $"@all_{cat} -> every {cat} element in the model"));

            try
            {
                foreach (var lvl in new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
                    list.Add(new MentionItem(lvl.Name, "@" + lvl.Name, "Level", $"@{lvl.Name} -> Level (id {lvl.Id.Value})"));

                foreach (var g in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Grids).WhereElementIsNotElementType())
                    list.Add(new MentionItem(g.Name, "@" + g.Name, "Grid", $"@{g.Name} -> Grid (id {g.Id.Value})"));

                foreach (var v in new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).OrderBy(v => v.Name).Take(200))
                    list.Add(new MentionItem(v.Name, "@" + v.Name, $"View · {v.ViewType}", $"@{v.Name} -> View, type {v.ViewType} (id {v.Id.Value})"));

                foreach (var r in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                            .Cast<Autodesk.Revit.DB.Architecture.Room>().Where(r => r.Area > 0).Take(300))
                {
                    var disp = (string.IsNullOrEmpty(r.Number) ? "" : r.Number + " ") + r.Name;
                    list.Add(new MentionItem(disp, "@" + disp, "Room", $"@{disp} -> Room (id {r.Id.Value})"));
                }

                foreach (var s in new FilteredElementCollector(_doc).OfClass(typeof(Autodesk.Revit.DB.MEPSystem)).Cast<Autodesk.Revit.DB.MEPSystem>().Take(100))
                    list.Add(new MentionItem(s.Name, "@" + s.Name, "System", $"@{s.Name} -> MEP system (id {s.Id.Value})"));
            }
            catch { /* keep whatever we collected */ }

            foreach (var c in new[] { "Walls", "Doors", "Windows", "Floors", "Roofs", "Ceilings", "Rooms", "Furniture", "Columns",
                                       "Structural Columns", "Structural Framing", "Pipes", "Ducts", "Plumbing Fixtures",
                                       "Mechanical Equipment", "Electrical Fixtures", "Lighting Fixtures" })
                list.Add(new MentionItem(c, "@" + c, "Category", $"@{c} -> Category"));

            _mentionCatalog = list;   // always assigned — never rebuilt
        }

        private void PromptInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                UpdateMentionPopup();
            }
            catch (Exception ex)
            {
                try { MentionPopup.IsOpen = false; } catch { }
                try { StatusText.Text = "@mention: " + ex.Message; StatusText.Foreground = BrushErr; } catch { }
            }
        }

        private void UpdateMentionPopup()
        {
            var text = PromptInput.Text ?? string.Empty;
            var caret = PromptInput.CaretIndex;
            if (caret <= 0 || caret > text.Length) { MentionPopup.IsOpen = false; return; }

            var at = text.LastIndexOf('@', caret - 1);
            if (at < 0) { MentionPopup.IsOpen = false; return; }

            var token = text.Substring(at + 1, caret - at - 1);
            if (token.IndexOf('\n') >= 0 || token.IndexOf('\r') >= 0) { MentionPopup.IsOpen = false; return; }

            BuildMentionCatalogIfNeeded();
            List<MentionItem> matches;
            if (token.Length == 0)
            {
                matches = _mentionCatalog.Take(30).ToList();
            }
            else
            {
                matches = _mentionCatalog
                    .Where(m => m.Display.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(m => m.Display.StartsWith(token, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(m => m.Display.Length)
                    .Take(30).ToList();
            }

            if (matches.Count == 0) { MentionPopup.IsOpen = false; return; }

            _mentionTokenStart = at;
            MentionList.ItemsSource = matches;
            MentionList.SelectedIndex = 0;
            MentionPopup.IsOpen = true;
        }

        private void PromptInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!MentionPopup.IsOpen) return;
            if (e.Key == Key.Down)
            {
                MentionList.SelectedIndex = Math.Min(MentionList.SelectedIndex + 1, MentionList.Items.Count - 1);
                MentionList.ScrollIntoView(MentionList.SelectedItem); e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                MentionList.SelectedIndex = Math.Max(MentionList.SelectedIndex - 1, 0);
                MentionList.ScrollIntoView(MentionList.SelectedItem); e.Handled = true;
            }
            else if ((e.Key == Key.Enter || e.Key == Key.Tab) && Keyboard.Modifiers != ModifierKeys.Control)
            {
                InsertSelectedMention(); e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                MentionPopup.IsOpen = false; e.Handled = true;
            }
        }

        private void MentionList_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (MentionList.SelectedItem is MentionItem) InsertSelectedMention();
        }

        private void InsertSelectedMention()
        {
            try
            {
                if (!(MentionList.SelectedItem is MentionItem m) || _mentionTokenStart < 0) { MentionPopup.IsOpen = false; return; }
                var text = PromptInput.Text ?? string.Empty;
                var caret = PromptInput.CaretIndex;
                if (_mentionTokenStart > caret || caret > text.Length) { MentionPopup.IsOpen = false; return; }

                var before = text.Substring(0, _mentionTokenStart);
                var after = text.Substring(caret);
                var inserted = m.InsertText + " ";
                MentionPopup.IsOpen = false;
                PromptInput.Text = before + inserted + after;
                PromptInput.CaretIndex = before.Length + inserted.Length;
                PromptInput.Focus();
            }
            catch { try { MentionPopup.IsOpen = false; } catch { } }
        }

        // Append a "## Referenced elements" block so the agent gets precise data
        // for any @mentions in the prompt. The chat shows the original prompt.
        private string AugmentWithReferences(string prompt)
        {
            if (string.IsNullOrEmpty(prompt) || prompt.IndexOf('@') < 0) return prompt;
            BuildMentionCatalogIfNeeded();
            var refs = new List<string>();
            foreach (var m in _mentionCatalog)
            {
                if (refs.Count >= 15) break;
                if (prompt.IndexOf(m.InsertText, StringComparison.OrdinalIgnoreCase) >= 0 && !refs.Contains(m.Resolved))
                    refs.Add(m.Resolved);
            }
            if (refs.Count == 0) return prompt;
            return prompt + "\n\n## Referenced elements\n" + string.Join("\n", refs.Select(r => "- " + r));
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
            // Already handling a request — ignore re-entry (e.g. clicking a
            // quick-action / saved command while one is in flight).
            if (_cts != null) return;

            if (string.IsNullOrEmpty(_config?.AccessToken))
            {
                AddError("Please log in first to use the AI Assistant.");
                return;
            }

            _cts = new CancellationTokenSource();
            SetInputEnabled(false);
            SetCancelVisible(true);
            StatusText.Text = "Thinking...";
            StatusText.Foreground = BrushDim;

            // Build the request context BEFORE updating the "previous turn" fields,
            // so it carries the prior turn for pronoun resolution.
            var requestContext = BuildRequestContext();

            AddMessage(prompt, isUser: true);
            PromptInput.Text = "";
            _lastUserPrompt = prompt;
            _retryCount = 0;
            UpdateSaveCommandButton();

            var requestInFlight = true;
            try
            {
                int? userId = _config.UserId > 0 ? _config.UserId : (int?)null;
                var routedPrompt = AugmentWithReferences(prompt);
                var route = await _aiService.RouteAsync(routedPrompt, requestContext, userId, _sessionId, templateId, _config.AccessToken, _cts.Token);
                requestInFlight = false;
                SetCancelVisible(false);

                if (route != null && !string.IsNullOrEmpty(route.Intent))
                    _ctxLastOperation = route.Intent;
                _ctxPreviousPrompt = prompt;

                if (route == null)
                {
                    AddError("No response from the backend.");
                    SetInputEnabled(true);
                    StatusText.Text = "Error"; StatusText.Foreground = BrushErr;
                    return;
                }

                if (route.TokensUsed.HasValue)
                {
                    _totalTokens += route.TokensUsed.Value;
                    TokensText.Text = $"Tokens: {_totalTokens}";
                }

                if (route.NeedsClarification || string.Equals(route.Intent, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                {
                    AddMessage(route.ClarifyingQuestion ?? route.Reply ?? "Could you give me a bit more detail?", isUser: false);
                    SetInputEnabled(true);
                    StatusText.Text = "Ready"; StatusText.Foreground = BrushOk;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(route.Reply))
                    AddMessage(route.Reply, isUser: false);

                int executable = 0;
                foreach (var action in route.Actions ?? new List<RouteAction>())
                {
                    // route.Reply already concatenates the action descriptions, so we
                    // don't re-print them — just show the code for executable actions.
                    string code = await ResolveActionCode(action, prompt);
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        AddCodeBlock(code);
                        AddRunDiscardRow(code);
                        _lastExecutedCode = code;   // so "Run again" knows what to re-run
                        executable++;
                    }
                }

                AddSuggestionRow(route.Suggestions);

                SetInputEnabled(true);
                if (executable > 0)
                {
                    StatusText.Text = "Review the code above, then click Run.";
                    StatusText.Foreground = BrushDim;
                }
                else
                {
                    StatusText.Text = "Ready"; StatusText.Foreground = BrushOk;
                }
            }
            catch (Exception ex)
            {
                AddError($"Error: {ex.Message}");
                SetInputEnabled(true);
                StatusText.Text = "Error"; StatusText.Foreground = BrushErr;
            }
            finally
            {
                if (requestInFlight) SetCancelVisible(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        // Turn a routed action into C# the executor can run, or null if it's
        // purely informational. open_view is synthesised locally; action types
        // the addin can't dispatch natively fall back to code generation.
        private async Task<string> ResolveActionCode(RouteAction action, string originalPrompt)
        {
            if (action == null) return null;
            switch (action.Type)
            {
                case "execute_code":
                    return action.Code;

                case "open_view":
                {
                    var name = GetParamString(action.Params, "view") ?? GetParamString(action.Params, "level")
                               ?? GetParamString(action.Params, "name") ?? GetParamString(action.Params, "target")
                               ?? GetParamString(action.Params, "view_name");
                    if (string.IsNullOrWhiteSpace(name)) goto default;
                    var n = name.Replace("\\", "").Replace("\"", "").Trim();
                    return
                        $"var __v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()\n" +
                        $"    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, \"{n}\", StringComparison.OrdinalIgnoreCase));\n" +
                        $"if (__v == null) __v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()\n" +
                        $"    .FirstOrDefault(v => !v.IsTemplate && v.Name != null && v.Name.IndexOf(\"{n}\", StringComparison.OrdinalIgnoreCase) >= 0);\n" +
                        $"if (__v != null) {{ OpenView(__v); ShowMessage(\"View opened\", __v.Name); }}\n" +
                        $"else ShowMessage(\"Not found\", \"No view matching '{n}'.\");";
                }

                case "none":
                    return null;

                default:
                    // select_elements / run_analysis / export / query / Unknown — code-gen fallback.
                    var gen = await _aiService.GenerateCodeAsync(
                        new Models.AIRequest
                        {
                            Prompt = originalPrompt,
                            Context = GetModelContext(),
                            UserId = _config.UserId > 0 ? _config.UserId : (int?)null,
                            SessionId = _sessionId
                        },
                        _config.AccessToken,
                        _cts?.Token ?? System.Threading.CancellationToken.None);
                    return (gen != null && gen.Success && !string.IsNullOrEmpty(gen.Code)) ? gen.Code : null;
            }
        }

        private static string GetParamString(Dictionary<string, object> p, string key)
        {
            if (p != null && p.TryGetValue(key, out var v) && v != null)
            {
                var s = v.ToString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            return null;
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
                        _ctxLastResult = result.Message ?? "Done";
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

        // Model context + conversation memory, sent with each /route request.
        private object BuildRequestContext()
        {
            var mc = GetModelContext();
            int selCount = 0;
            string activeView = null;
            try { selCount = _uidoc.Selection.GetElementIds().Count; } catch { }
            try { activeView = _doc.ActiveView?.Name; } catch { }
            return new
            {
                projectName = mc.ProjectName,
                levels = mc.Levels,
                categories = mc.Categories,
                activeViewName = mc.ActiveViewName ?? activeView,
                activeViewType = mc.ActiveViewType,
                selectedElementIds = mc.SelectedElementIds,
                selectedCount = selCount,
                phases = mc.Phases,
                revitVersion = mc.RevitVersion,
                // conversation memory (previous turn)
                last_prompt = _ctxPreviousPrompt,
                last_operation = _ctxLastOperation,
                last_result = TruncateForContext(_ctxLastResult, 2000)
            };
        }

        private static string TruncateForContext(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
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

        private void Log(string who, string text)
        {
            try { _transcript.AppendLine($"{who}: {text}").AppendLine(); } catch { }
        }

        private void CopyTranscriptButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var t = _transcript.ToString();
                if (string.IsNullOrWhiteSpace(t)) { AddSuccess("Nothing to copy yet."); return; }
                Clipboard.SetText(t);
                AddSuccess("Transcript copied to the clipboard.");
            }
            catch (Exception ex) { AddError("Couldn't copy: " + ex.Message); }
        }

        private void AddMessage(string text, bool isUser)
        {
            Log(isUser ? "You" : "Assistant", text);
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
            Log("Generated code", code);
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 4)
            };

            var stack = new StackPanel();

            var textBox = new TextBox
            {
                Text = code,
                Foreground = new SolidColorBrush(Color.FromRgb(156, 220, 254)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            // Collapsible: a tiny header that toggles the code's visibility.
            var toggle = new TextBlock
            {
                Text = "▾ code",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontSize = 10,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 4)
            };
            toggle.MouseLeftButtonUp += (s, e) =>
            {
                if (textBox.Visibility == System.Windows.Visibility.Visible)
                {
                    textBox.Visibility = System.Windows.Visibility.Collapsed;
                    toggle.Text = "▸ code (hidden)";
                }
                else
                {
                    textBox.Visibility = System.Windows.Visibility.Visible;
                    toggle.Text = "▾ code";
                }
            };

            stack.Children.Add(toggle);
            stack.Children.Add(textBox);
            border.Child = stack;
            ActiveChatHistory.Children.Add(border);
            ScrollToBottom();
        }

        // Renders the orchestrator's quick-action suggestions as a row of buttons.
        // Clicking one sends a mapped follow-up prompt (or shows a hint).
        private void AddSuggestionRow(List<RouteSuggestion> suggestions)
        {
            if (suggestions == null || suggestions.Count == 0) return;
            var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
            foreach (var s in suggestions)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Text)) continue;
                var btn = new Button
                {
                    Content = s.Text,
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                    Background = new SolidColorBrush(Color.FromRgb(45, 45, 48))
                };
                var action = s.Action;
                var text = s.Text;
                btn.Click += (sender, e) => OnSuggestionClicked(action, text);
                panel.Children.Add(btn);
            }
            if (panel.Children.Count > 0)
            {
                ActiveChatHistory.Children.Add(panel);
                ScrollToBottom();
            }
        }

        private void OnSuggestionClicked(string action, string text)
        {
            // A few actions get special handling; the rest map to a follow-up prompt.
            switch ((action ?? "").ToLowerInvariant())
            {
                case "undo":
                    AddSuccess("Press Ctrl+Z in Revit to undo the last change.");
                    return;
                case "rerun":
                    // Re-run the last code directly — no re-review, no re-asking the AI.
                    if (!string.IsNullOrWhiteSpace(_lastExecutedCode))
                    {
                        if (_cts != null) return;          // busy with another request
                        SetInputEnabled(false);
                        _retryCount = 0;
                        StatusText.Text = "Re-running...";
                        StatusText.Foreground = BrushDim;
                        ExecuteCode(_lastExecutedCode);
                    }
                    else if (!string.IsNullOrWhiteSpace(_lastUserPrompt))
                    {
                        _ = SendCodeGenQuery(_lastUserPrompt);
                    }
                    return;
                case "edit_params":
                    PromptInput.Text = "change the parameter of the selected elements: ";
                    PromptInput.CaretIndex = PromptInput.Text.Length;
                    PromptInput.Focus();
                    return;
            }

            string followUp;
            switch ((action ?? "").ToLowerInvariant())
            {
                case "view_3d":        followUp = "show me the default 3D view"; break;
                case "select_here":    followUp = "select all elements visible in the active view"; break;
                case "isolate":        followUp = "isolate the current selection in the active view"; break;
                case "analyze_view":   followUp = "check the active view for compliance issues"; break;
                case "open_created":   followUp = "open the view I just created"; break;
                case "export_report":  followUp = "export the results to Excel"; break;
                case "export_results": followUp = "export the results to Excel"; break;
                case "export_bcf":     followUp = "export the clashes to BCF"; break;
                case "open_export":    followUp = "open the exported file"; break;
                case "autofix":        followUp = "auto-fix the issues you just found"; break;
                case "refine":
                case "apply_a":
                    PromptInput.Text = text + ": ";
                    PromptInput.CaretIndex = PromptInput.Text.Length;
                    PromptInput.Focus();
                    return;
                default:               followUp = text; break;   // fall back to the button label
            }
            _ = SendCodeGenQuery(followUp);
        }

        // Shows a Run / Discard choice under a code block — nothing executes until
        // the user clicks Run (PRD FR-025).
        private void AddRunDiscardRow(string code)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 8)
            };

            var runBtn = new Button
            {
                Content = "▶  Run",
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212))
            };

            var discardBtn = new Button
            {
                Content = "Discard",
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48))
            };

            runBtn.Click += (s, e) =>
            {
                runBtn.IsEnabled = false;
                discardBtn.IsEnabled = false;
                _retryCount = 0;
                _lastExecutedCode = code;
                SetInputEnabled(false);
                StatusText.Text = "Executing in Revit...";
                StatusText.Foreground = BrushDim;
                ExecuteCode(code);
            };
            discardBtn.Click += (s, e) =>
            {
                runBtn.IsEnabled = false;
                discardBtn.IsEnabled = false;
                AddSuccess("Discarded — nothing was run.");
                SetInputEnabled(true);
                StatusText.Text = "Ready";
                StatusText.Foreground = BrushOk;
            };

            panel.Children.Add(runBtn);
            panel.Children.Add(discardBtn);
            ActiveChatHistory.Children.Add(panel);
            ScrollToBottom();
        }

        private void AddSuccess(string message)
        {
            Log("Result", message);
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
            Log("Error", message);
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
            Log("Note", message);
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

    /// <summary>One entry in the @mention autocomplete catalog.</summary>
    public class MentionItem
    {
        public string Display { get; }       // "Level 2"
        public string InsertText { get; }    // "@Level 2"
        public string TypeLabel { get; }     // "Level", "View · FloorPlan", "Category", ...
        public string Resolved { get; }      // "@Level 2 -> Level (id 12345)"  — sent to the agent

        public MentionItem(string display, string insertText, string typeLabel, string resolved)
        {
            Display = display ?? string.Empty;
            InsertText = insertText ?? string.Empty;
            TypeLabel = typeLabel ?? string.Empty;
            Resolved = resolved ?? string.Empty;
        }
    }
}
