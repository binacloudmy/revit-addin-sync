using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;
// Autodesk.Revit.DB exports `Control` and `Binding` too — disambiguate to the
// WPF ones since this is a WPF view file.
using Control = System.Windows.Controls.Control;
using Binding = System.Windows.Data.Binding;

namespace RevitWebAppSync
{
    public partial class AIAssistantWindow : Window
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly AIService _aiService;
        private readonly ExternalEvent _externalEvent;
        private readonly CodeExecutionHandler _handler;
        private readonly BinaConfig _config;
        private readonly string _sessionId = Guid.NewGuid().ToString();

        private CancellationTokenSource _cts;
        // Separate CTS for the auto-fix / retry round-trip — the send-flow's
        // _cts is disposed by the time HandleExecutionFailureAsync runs.
        private CancellationTokenSource _retryCts;
        private int _totalTokens = 0;

        private List<CommandTemplate> _allCommands;
        private bool _commandsLoaded;
        // The saved command (if any) that triggered the current turn — used to
        // backfill its generated_code after a first successful run so future
        // re-runs skip the LLM.
        private CommandTemplate _lastRunCommand;
        private string _lastUserPrompt;
        private string _lastExecutedCode;
        private int _retryCount;
        private const int MaxRetries = 2;
        // Remembers the error that started the retry / error-card chain so we
        // can record (error, working_code) on the eventual success — fuels the
        // backend's error-pattern learning (FR-022).
        private string _errorBeingFixed;

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

        public AIAssistantWindow(UIDocument uidoc, ExternalEvent externalEvent, CodeExecutionHandler handler)
        {
            InitializeComponent();

            _uidoc = uidoc;
            _doc = uidoc.Document;
            _externalEvent = externalEvent;
            _handler = handler;

            _config = BinaConfig.Load();
            _aiService = new AIService(_config.ResolvedAIBaseUrl);

            CheckBackendConnection();
            EnforceLoginGate();

            // Forward Ctrl+Z and Ctrl+Y to Revit even when the Copilot window
            // has focus — WPF would otherwise swallow them since the chat is a
            // separate window. Uses the same channel as the Revert button.
            this.PreviewKeyDown += AIAssistantWindow_PreviewKeyDown;

            // Load saved commands eagerly (decoupled from the Expander) — fire and forget.
            _ = SafeLoadCommandsAsync();
        }

        private void AIAssistantWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Only act on Ctrl+Z and Ctrl+Y when the prompt input is empty —
            // typing into the input shouldn't trigger Revit-level undo/redo.
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (!string.IsNullOrEmpty(PromptInput?.Text)) return;
            if (e.Key == Key.Z)
            {
                DispatchRevert();
                e.Handled = true;
            }
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
        // Recently-used mention InsertText values, MRU-ordered (most recent first).
        // Capped at 10 entries (FR-006).
        private readonly List<string> _recentMentionTexts = new List<string>();
        private const int MaxRecentMentions = 10;

        // Common category names → BuiltInCategory enum names. Used both for the
        // @all_X catalog entries and for chip-click selection codegen.
        private static readonly Dictionary<string, string> _categoryNameToBic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "walls", "OST_Walls" },
            { "doors", "OST_Doors" },
            { "windows", "OST_Windows" },
            { "floors", "OST_Floors" },
            { "roofs", "OST_Roofs" },
            { "ceilings", "OST_Ceilings" },
            { "rooms", "OST_Rooms" },
            { "columns", "OST_Columns" },
            { "grids", "OST_Grids" },
            { "furniture", "OST_Furniture" },
            { "structural columns", "OST_StructuralColumns" },
            { "structural framing", "OST_StructuralFraming" },
            { "pipes", "OST_PipeCurves" },
            { "ducts", "OST_DuctCurves" },
            { "plumbing fixtures", "OST_PlumbingFixtures" },
            { "mechanical equipment", "OST_MechanicalEquipment" },
            { "electrical fixtures", "OST_ElectricalFixtures" },
            { "lighting fixtures", "OST_LightingFixtures" },
        };

        private static string BicNameFor(string categoryName)
            => categoryName != null && _categoryNameToBic.TryGetValue(categoryName, out var bic) ? bic : null;

        private void BuildMentionCatalogIfNeeded()
        {
            if (_mentionCatalog != null) return;
            var list = new List<MentionItem>();

            // Special / bulk mentions (no API needed).
            list.Add(new MentionItem("selected", "@selected", "current selection", "@selected -> the elements currently selected in Revit"));
            list.Add(new MentionItem("here", "@here", "elements in active view", "@here -> the elements visible in the active view"));
            foreach (var cat in new[] { "walls", "doors", "windows", "floors", "roofs", "ceilings", "rooms", "columns", "grids" })
                list.Add(new MentionItem("all_" + cat, "@all_" + cat, "all " + cat,
                    $"@all_{cat} -> every {cat} element in the model",
                    bicName: BicNameFor(cat)));

            try
            {
                foreach (var lvl in new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
                    list.Add(new MentionItem(lvl.Name, "@" + lvl.Name, "Level",
                        $"@{lvl.Name} -> Level (id {lvl.Id.Value})",
                        elementId: lvl.Id.Value));

                foreach (var g in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Grids).WhereElementIsNotElementType())
                    list.Add(new MentionItem(g.Name, "@" + g.Name, "Grid",
                        $"@{g.Name} -> Grid (id {g.Id.Value})",
                        elementId: g.Id.Value));

                foreach (var v in new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).OrderBy(v => v.Name).Take(200))
                    list.Add(new MentionItem(v.Name, "@" + v.Name, $"View · {v.ViewType}",
                        $"@{v.Name} -> View, type {v.ViewType} (id {v.Id.Value})",
                        elementId: v.Id.Value));

                foreach (var r in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                            .Cast<Autodesk.Revit.DB.Architecture.Room>().Where(r => r.Area > 0).Take(300))
                {
                    var disp = (string.IsNullOrEmpty(r.Number) ? "" : r.Number + " ") + r.Name;
                    list.Add(new MentionItem(disp, "@" + disp, "Room",
                        $"@{disp} -> Room (id {r.Id.Value})",
                        elementId: r.Id.Value));
                }

                foreach (var s in new FilteredElementCollector(_doc).OfClass(typeof(Autodesk.Revit.DB.MEPSystem)).Cast<Autodesk.Revit.DB.MEPSystem>().Take(100))
                    list.Add(new MentionItem(s.Name, "@" + s.Name, "System",
                        $"@{s.Name} -> MEP system (id {s.Id.Value})",
                        elementId: s.Id.Value));
            }
            catch { /* keep whatever we collected */ }

            foreach (var c in new[] { "Walls", "Doors", "Windows", "Floors", "Roofs", "Ceilings", "Rooms", "Furniture", "Columns",
                                       "Structural Columns", "Structural Framing", "Pipes", "Ducts", "Plumbing Fixtures",
                                       "Mechanical Equipment", "Electrical Fixtures", "Lighting Fixtures" })
                list.Add(new MentionItem(c, "@" + c, "Category", $"@{c} -> Category",
                    bicName: BicNameFor(c)));

            _mentionCatalog = list;   // always assigned — never rebuilt
        }

        // ─────────────────────────────────────────────────────────────
        // Clickable @mention badges (FR-008 / FR-032). Parse a chat
        // message for catalog @mentions and render each one as a coloured,
        // clickable Border inline. Click → select the referenced element(s)
        // in Revit via the same external-event channel as code execution.
        // ─────────────────────────────────────────────────────────────

        // Greedy: replace every catalog @mention in `text` with a chip and the
        // rest with plain Runs, populating `target.Inlines`. Falls back to a
        // single plain Run if the catalog isn't built yet.
        private void RenderTextWithMentions(TextBlock target, string text, Brush plainFg)
        {
            if (target == null) return;
            target.Inlines.Clear();
            if (string.IsNullOrEmpty(text))
                return;

            BuildMentionCatalogIfNeeded();
            // Sort by InsertText length DESC so longer matches win (e.g. "@Aras 02"
            // beats "@Aras").
            var sorted = _mentionCatalog?
                .Where(m => !string.IsNullOrEmpty(m.InsertText))
                .OrderByDescending(m => m.InsertText.Length)
                .ToList() ?? new List<MentionItem>();

            var buf = new System.Text.StringBuilder();
            void FlushBuf()
            {
                if (buf.Length == 0) return;
                target.Inlines.Add(new Run(buf.ToString()) { Foreground = plainFg });
                buf.Clear();
            }

            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '@')
                {
                    MentionItem hit = null;
                    foreach (var m in sorted)
                    {
                        var it = m.InsertText;
                        if (i + it.Length > text.Length) continue;
                        if (string.Compare(text, i, it, 0, it.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                        int after = i + it.Length;
                        if (after == text.Length || IsMentionBoundaryChar(text[after]))
                        {
                            hit = m;
                            break;
                        }
                    }
                    if (hit != null)
                    {
                        FlushBuf();
                        target.Inlines.Add(new InlineUIContainer(CreateMentionChip(hit))
                        {
                            BaselineAlignment = BaselineAlignment.Center
                        });
                        i += hit.InsertText.Length;
                        continue;
                    }
                }
                buf.Append(text[i]);
                i++;
            }
            FlushBuf();
        }

        private static bool IsMentionBoundaryChar(char c)
            => !char.IsLetterOrDigit(c) && c != '_';

        // Visual chip for one resolved @mention. Tooltip shows the type +
        // element id (if any); click dispatches a selection in Revit.
        private Border CreateMentionChip(MentionItem m)
        {
            var (bg, fg) = ChipColorsFor(m.TypeLabel);
            var label = new TextBlock
            {
                Text = m.InsertText,
                Foreground = fg,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(0)
            };
            var border = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(1, 0, 1, 0),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0),
                ToolTip = m.TypeLabel + (m.ElementId.HasValue ? "  ·  id " + m.ElementId.Value : "")
                                       + "\nClick to select in Revit"
            };
            border.Child = label;
            // Hover effect — slightly brighter background.
            var baseBg = bg;
            border.MouseEnter += (s, e) => border.Background = BrightenBrush(baseBg, 0.15);
            border.MouseLeave += (s, e) => border.Background = baseBg;
            border.MouseLeftButtonUp += (s, e) => OnMentionChipClicked(m);
            return border;
        }

        private static (SolidColorBrush bg, SolidColorBrush fg) ChipColorsFor(string typeLabel)
        {
            var t = (typeLabel ?? "").ToLowerInvariant();
            if (t == "level")                  return (Rgb(30, 92, 138), Rgb(168, 216, 255));
            if (t.StartsWith("view"))          return (Rgb(94, 61, 142),  Rgb(217, 188, 255));
            if (t == "grid")                   return (Rgb(122, 70, 18),  Rgb(255, 207, 161));
            if (t == "room")                   return (Rgb(31, 106, 60),  Rgb(168, 255, 200));
            if (t == "system")                 return (Rgb(122, 30, 92),  Rgb(255, 168, 216));
            if (t == "category")               return (Rgb(122, 108, 18), Rgb(255, 232, 161));
            if (t.StartsWith("all "))          return (Rgb(31, 90, 106),  Rgb(168, 224, 255));
            if (t == "current selection")      return (Rgb(63, 63, 70),   Rgb(204, 204, 204));
            if (t == "elements in active view")return (Rgb(63, 63, 70),   Rgb(204, 204, 204));
            return (Rgb(63, 63, 70), Rgb(204, 204, 204));
        }

        private static SolidColorBrush Rgb(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

        private static SolidColorBrush BrightenBrush(SolidColorBrush src, double amount)
        {
            if (src == null) return null;
            var c = src.Color;
            byte Up(byte v) => (byte)Math.Min(255, v + (int)((255 - v) * amount));
            return new SolidColorBrush(Color.FromRgb(Up(c.R), Up(c.G), Up(c.B)));
        }

        private void OnMentionChipClicked(MentionItem m)
        {
            if (m == null) return;
            var code = BuildChipClickCode(m);
            if (string.IsNullOrWhiteSpace(code))
            {
                AddSuccess("Tip: type '" + m.InsertText + "' in a request — this kind of mention isn't directly selectable from a click.");
                return;
            }
            StatusText.Text = "Selecting in Revit…";
            StatusText.Foreground = BrushDim;
            SetInputEnabled(false);
            _handler.CodeToExecute = code;
            _handler.OnCompleted = (result) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (result.Success) AddSuccess(result.Message ?? "Done.");
                    else AddError(result.Error ?? "Selection failed.");
                    SetInputEnabled(true);
                    StatusText.Text = result.Success ? "Ready" : "Error";
                    StatusText.Foreground = result.Success ? BrushOk : BrushErr;
                });
            };
            _externalEvent.Raise();
        }

        // Synthesise C# that picks the referenced element(s) in Revit based
        // on the mention's TypeLabel + (ElementId or BicName).
        private static string BuildChipClickCode(MentionItem m)
        {
            var t = (m.TypeLabel ?? "").ToLowerInvariant();
            string name = (m.Display ?? "").Replace("\"", "");

            // No-op for current selection.
            if (t == "current selection") return null;

            // @here → everything in the active view.
            if (t == "elements in active view")
                return
                    "var __ids = new FilteredElementCollector(doc, doc.ActiveView.Id)" +
                    ".WhereElementIsNotElementType().ToElementIds().ToList(); " +
                    "uidoc.Selection.SetElementIds(__ids); " +
                    "ShowMessage(\"Selected\", __ids.Count + \" element(s) in the active view\");";

            // @all_X → all elements of category X across the model.
            if (t.StartsWith("all ") && !string.IsNullOrEmpty(m.BicName))
            {
                string catWord = t.Substring(4);
                return
                    $"var __ids = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.{m.BicName})" +
                    ".WhereElementIsNotElementType().ToElementIds().ToList(); " +
                    "uidoc.Selection.SetElementIds(__ids); " +
                    $"ShowMessage(\"Selected\", __ids.Count + \" {catWord}\");";
            }

            // @Level X → open that level's default floor plan.
            if (t == "level" && m.ElementId.HasValue)
                return
                    $"var __levelId = new ElementId({m.ElementId.Value}L); " +
                    "var __plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()" +
                    ".FirstOrDefault(v => v.GenLevel != null && v.GenLevel.Id == __levelId && !v.IsTemplate); " +
                    $"if (__plan != null) {{ OpenView(__plan); ShowMessage(\"Opened\", __plan.Name); }} " +
                    $"else ShowMessage(\"Not found\", \"No floor plan for {name}\");";

            // @View X → open that view.
            if (t.StartsWith("view") && m.ElementId.HasValue)
                return
                    $"var __v = doc.GetElement(new ElementId({m.ElementId.Value}L)) as View; " +
                    "if (__v != null) { OpenView(__v); ShowMessage(\"Opened\", __v.Name); } " +
                    "else ShowMessage(\"Not found\", \"View no longer in the document\");";

            // @Grid / @Room / @System X → select that single element.
            if ((t == "grid" || t == "room" || t == "system") && m.ElementId.HasValue)
                return
                    $"var __el = doc.GetElement(new ElementId({m.ElementId.Value}L)); " +
                    "if (__el != null) { " +
                        "uidoc.Selection.SetElementIds(new List<ElementId>{ __el.Id }); " +
                        $"ShowMessage(\"Selected\", __el.Name ?? \"{name}\"); " +
                    "} else { " +
                        "ShowMessage(\"Not found\", \"Element no longer in the document\"); " +
                    "}";

            // @Walls / @Doors / @Rooms (Category) → select all in the active view.
            if (t == "category" && !string.IsNullOrEmpty(m.BicName))
                return
                    $"var __ids = new FilteredElementCollector(doc, doc.ActiveView.Id).OfCategory(BuiltInCategory.{m.BicName})" +
                    ".WhereElementIsNotElementType().ToElementIds().ToList(); " +
                    "uidoc.Selection.SetElementIds(__ids); " +
                    $"ShowMessage(\"Selected\", __ids.Count + \" {name.ToLowerInvariant()} in the active view\");";

            return null;
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

            // Tiered ranking for popup matches:
            //   tier 0 — recently-used (FR-006, only when token is empty)
            //   tier 1 — items related to the current Revit context (FR-005:
            //            the active view name and the active level take priority)
            //   tier 2 — exact-prefix matches of the typed token
            //   tier 3 — substring matches
            //   tier 4 — everything else
            //
            // Within a tier, shorter Display string wins so common names rise.
            var activeViewName = SafeGet(() => _uidoc?.ActiveView?.Name);
            var activeLevelName = SafeGet(() =>
            {
                var v = _uidoc?.ActiveView;
                if (v is ViewPlan plan && plan.GenLevel != null) return plan.GenLevel.Name;
                return null;
            });

            int Tier(MentionItem m)
            {
                if (token.Length == 0 && _recentMentionTexts.Contains(m.InsertText)) return 0;
                if (!string.IsNullOrEmpty(activeViewName) && string.Equals(m.Display, activeViewName, StringComparison.OrdinalIgnoreCase)) return 1;
                if (!string.IsNullOrEmpty(activeLevelName) && string.Equals(m.Display, activeLevelName, StringComparison.OrdinalIgnoreCase)) return 1;
                if (token.Length > 0 && m.Display.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return 2;
                if (token.Length > 0 && m.Display.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return 3;
                return 4;
            }

            List<MentionItem> matches;
            if (token.Length == 0)
            {
                matches = _mentionCatalog
                    .OrderBy(Tier)
                    .ThenBy(m => _recentMentionTexts.IndexOf(m.InsertText) is int i && i >= 0 ? i : int.MaxValue)
                    .ThenBy(m => m.Display.Length)
                    .Take(30).ToList();
            }
            else
            {
                matches = _mentionCatalog
                    .Where(m => m.Display.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(Tier)
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
                MarkMentionUsed(m.InsertText);
            }
            catch { try { MentionPopup.IsOpen = false; } catch { } }
        }

        // Recently-used tracking (FR-006). Moves the just-used InsertText to
        // the front of the MRU list, trims to MaxRecentMentions.
        private void MarkMentionUsed(string insertText)
        {
            if (string.IsNullOrEmpty(insertText)) return;
            _recentMentionTexts.Remove(insertText);
            _recentMentionTexts.Insert(0, insertText);
            if (_recentMentionTexts.Count > MaxRecentMentions)
                _recentMentionTexts.RemoveRange(MaxRecentMentions, _recentMentionTexts.Count - MaxRecentMentions);
        }

        // Wrap a getter in try/catch — handy for Revit API reads that throw
        // when no document / no active view.
        private static T SafeGet<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
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

            await SendCodeGenQuery(prompt);
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
            // Remember which saved command (if any) drove this turn, so we can
            // backfill its generated_code on a successful run.
            _lastRunCommand = !string.IsNullOrEmpty(templateId) && _allCommands != null
                ? _allCommands.FirstOrDefault(c => c.Id == templateId)
                : null;
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
                int autoRunFired = 0;
                foreach (var action in route.Actions ?? new List<RouteAction>())
                {
                    // route.Reply already concatenates the action descriptions, so we
                    // don't re-print them — just show the code for executable actions.
                    string code = await ResolveActionCode(action, prompt);
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    // open_view is addin-synthesised, non-destructive (no model
                    // edit, just navigation) — auto-run for the same UX as a
                    // chip click. Everything else still gates behind Run/Discard.
                    bool autoRunSafe = string.Equals(action.Type, "open_view", StringComparison.OrdinalIgnoreCase);

                    AddCodeBlock(code);
                    _lastExecutedCode = code;   // so "Run again" knows what to re-run

                    if (autoRunSafe)
                    {
                        ExecuteCode(code);
                        autoRunFired++;
                    }
                    else
                    {
                        AddRunDiscardRow(code);
                    }
                    executable++;
                }

                AddSuggestionRow(route.Suggestions);

                SetInputEnabled(true);
                if (autoRunFired > 0 && executable == autoRunFired)
                {
                    StatusText.Text = "Done."; StatusText.Foreground = BrushOk;
                }
                else if (executable > 0)
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
                    // The intent router sometimes leaves the raw "@mention" token in the
                    // param (e.g. "@Aras 02" or "@View_Level 2"). Revit view names don't
                    // include the "@" or the type prefix, so strip them before searching.
                    var n = name.Trim().TrimStart('@').Trim();
                    n = Regex.Replace(n, @"^(Level|View|Grid|Room|Type|Category|System)_", "", RegexOptions.IgnoreCase);
                    n = n.Replace("\\", "").Replace("\"", "").Trim();
                    return
                        $"var __v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()\n" +
                        $"    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, \"{n}\", StringComparison.OrdinalIgnoreCase));\n" +
                        $"if (__v == null) __v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()\n" +
                        $"    .FirstOrDefault(v => !v.IsTemplate && v.Name != null && v.Name.IndexOf(\"{n}\", StringComparison.OrdinalIgnoreCase) >= 0);\n" +
                        $"if (__v != null) {{ OpenView(__v); ShowMessage(\"View opened\", __v.Name); }}\n" +
                        $"else ShowMessage(\"Not found\", \"No view matching '{n}'.\");";
                }

                case "run_analysis":
                {
                    var op = (GetParamString(action.Params, "operation") ?? "").ToLowerInvariant();
                    if (op.Contains("cost"))
                    {
                        AddDashboardCard(
                            "💰 Cost estimate",
                            "Open the Cost dashboard to run the estimate against the model elements.",
                            "Open Cost dashboard", "cost");
                        return null;
                    }
                    if (op.Contains("clash") || op.Contains("quantit") || op.Contains("qto") || op.Contains("takeoff"))
                        goto default;   // no in-app entry point yet — let code-gen try
                    // jkr_compliance / fire_compliance / anything else compliance-ish
                    AddDashboardCard(
                        "✓ Compliance check",
                        "Open the JKR / UBBL Compliance dashboard to run the check on this model.",
                        "Open Compliance dashboard", "jkr");
                    return null;
                }

                case "select_elements":
                {
                    var code = BuildNativeSelectionCode(action);
                    if (!string.IsNullOrEmpty(code)) return code;
                    goto default;   // complex predicates fall through to the LLM
                }

                case "export":
                {
                    var code = BuildNativeExportCode(action);
                    if (!string.IsNullOrEmpty(code)) return code;
                    goto default;   // PDF / IFC / other formats fall through
                }

                case "none":
                    return null;

                default:
                    // query / Unknown / fallback — let the LLM generate code.
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

        // ─────────────────────────────────────────────────────────────
        // Native dispatchers (#27). The intent router already extracts
        // the actionable bits — for simple SELECT / EXPORT cases we can
        // synthesise the C# locally and skip the LLM round-trip.
        // Complex queries (predicates, custom filters) still go to the
        // agent via the default fallback.
        // ─────────────────────────────────────────────────────────────

        private static string BicNameForFuzzy(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var n = name.Trim().Replace("@", "").ToLowerInvariant();
            if (_categoryNameToBic.TryGetValue(n, out var direct)) return direct;
            if (n.EndsWith("s") && _categoryNameToBic.TryGetValue(n.Substring(0, n.Length - 1), out var sing)) return sing;
            if (!n.EndsWith("s") && _categoryNameToBic.TryGetValue(n + "s", out var plur)) return plur;
            return null;
        }

        // Mine the action's params + mentions for a category name and an
        // optional level name. Returns (categoryWord, bicEnumName, levelName).
        private static (string cat, string bic, string level) ExtractTargetFromAction(RouteAction action)
        {
            string cat = GetParamString(action.Params, "category");
            string level = GetParamString(action.Params, "level");

            if (action.Mentions != null)
            {
                foreach (var m in action.Mentions)
                {
                    var mt = (m?.Type ?? "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(cat) && (mt == "category" || mt == "all"))
                        cat = m.Name;
                    if (string.IsNullOrEmpty(level) && mt == "level")
                        level = m.Name;
                }
            }

            string bic = cat != null ? BicNameForFuzzy(cat) : null;
            return (cat, bic, level);
        }

        private string BuildNativeSelectionCode(RouteAction action)
        {
            if (action == null) return null;
            var (cat, bic, level) = ExtractTargetFromAction(action);
            if (string.IsNullOrEmpty(bic)) return null;   // can't map a category → fall back

            // Bail out if there's a predicate in params we don't natively handle
            // (thickness, area, etc.) — the LLM is better at those.
            if (action.Params != null)
            {
                foreach (var k in action.Params.Keys)
                {
                    if (string.Equals(k, "category", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(k, "level", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(k, "scope", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(k, "format", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(k, "name", StringComparison.OrdinalIgnoreCase)) continue;
                    // Anything else (value/threshold/param/etc.) → bail to LLM.
                    return null;
                }
            }

            string safeCat = (cat ?? "elements").Replace("\"", "");
            string safeLevel = (level ?? "").Replace("\"", "");

            var sb = new StringBuilder();
            sb.AppendLine("var __items = new FilteredElementCollector(doc)");
            sb.AppendLine($"    .OfCategory(BuiltInCategory.{bic}).WhereElementIsNotElementType()");
            sb.AppendLine("    .Cast<Element>().ToList();");
            if (!string.IsNullOrEmpty(safeLevel))
            {
                sb.AppendLine($"var __lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()");
                sb.AppendLine($"    .FirstOrDefault(l => string.Equals(l.Name, \"{safeLevel}\", StringComparison.OrdinalIgnoreCase));");
                sb.AppendLine("if (__lvl != null) __items = __items.Where(e => e.LevelId == __lvl.Id).ToList();");
            }
            sb.AppendLine("var __ids = __items.Select(e => e.Id).ToList();");
            sb.AppendLine("uidoc.Selection.SetElementIds(__ids);");
            string suffix = string.IsNullOrEmpty(safeLevel) ? "" : (" on " + safeLevel);
            sb.AppendLine($"ShowMessage(\"Selected\", __ids.Count + \" {safeCat}{suffix}\");");
            return sb.ToString();
        }

        private string BuildNativeExportCode(RouteAction action)
        {
            if (action == null) return null;

            string fmt = (GetParamString(action.Params, "format") ?? "").ToLowerInvariant();
            // Only Excel is fully native today. PDF / IFC / DWG / BCF → fall back to LLM.
            if (fmt.Length == 0 || fmt.IndexOf("excel", StringComparison.OrdinalIgnoreCase) < 0
                                  && fmt.IndexOf("xlsx", StringComparison.OrdinalIgnoreCase) < 0
                                  && fmt.IndexOf("xls", StringComparison.OrdinalIgnoreCase) < 0
                                  && fmt.IndexOf("csv", StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            var (cat, bic, level) = ExtractTargetFromAction(action);
            if (string.IsNullOrEmpty(bic)) return null;

            string filename = GetParamString(action.Params, "filename") ?? GetParamString(action.Params, "name");
            if (string.IsNullOrEmpty(filename))
                filename = (cat ?? "export").Replace(" ", "_").ToLowerInvariant() + "_schedule";
            filename = filename.Replace("\"", "").Replace("\\", "").Replace("/", "");

            string safeCat = (cat ?? "elements").Replace("\"", "");
            string safeLevel = (level ?? "").Replace("\"", "");

            var sb = new StringBuilder();
            sb.AppendLine("var __items = new FilteredElementCollector(doc)");
            sb.AppendLine($"    .OfCategory(BuiltInCategory.{bic}).WhereElementIsNotElementType()");
            sb.AppendLine("    .Cast<Element>().ToList();");
            if (!string.IsNullOrEmpty(safeLevel))
            {
                sb.AppendLine($"var __lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()");
                sb.AppendLine($"    .FirstOrDefault(l => string.Equals(l.Name, \"{safeLevel}\", StringComparison.OrdinalIgnoreCase));");
                sb.AppendLine("if (__lvl != null) __items = __items.Where(e => e.LevelId == __lvl.Id).ToList();");
            }
            sb.AppendLine("var __headers = new List<string> { \"Id\", \"Name\", \"Family\", \"Type\", \"Level\" };");
            sb.AppendLine("var __rows = new List<List<string>>();");
            sb.AppendLine("foreach (var __el in __items)");
            sb.AppendLine("{");
            sb.AppendLine("    string __family = __el is FamilyInstance __fi && __fi.Symbol != null ? __fi.Symbol.Family.Name : \"\";");
            sb.AppendLine("    string __type = __el.GetTypeId() != ElementId.InvalidElementId ? (doc.GetElement(__el.GetTypeId())?.Name ?? \"\") : \"\";");
            sb.AppendLine("    string __levelName = __el.LevelId != ElementId.InvalidElementId ? (doc.GetElement(__el.LevelId)?.Name ?? \"\") : \"\";");
            sb.AppendLine("    __rows.Add(new List<string> { __el.Id.Value.ToString(), __el.Name ?? \"\", __family, __type, __levelName });");
            sb.AppendLine("}");
            sb.AppendLine($"var __desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);");
            sb.AppendLine($"var __path = Path.Combine(__desktop, \"{filename}.xlsx\");");
            sb.AppendLine("WriteExcel(__path, __headers, __rows);");
            sb.AppendLine($"ShowMessage(\"Exported\", __rows.Count + \" {safeCat} written to \" + __path);");
            return sb.ToString();
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
            // Cancels whichever round-trip is in flight — the initial send
            // (_cts) or an auto-fix / regenerate retry (_retryCts).
            try { _cts?.Cancel(); } catch { }
            try { _retryCts?.Cancel(); } catch { }
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

            // If the command has a saved C# snapshot, execute it directly and
            // skip the /route + /generate round-trip. The prompt still appears
            // in the chat so the user knows what just ran.
            if (cmd.HasSavedCode)
            {
                _lastUserPrompt = prompt;
                AddMessage(prompt, isUser: true);
                AddMessage($"Running saved snapshot of **{cmd.Name}** (skipping AI).", isUser: false);
                AddCodeBlock(cmd.GeneratedCode);
                AddRunDiscardRow(cmd.GeneratedCode);
                StatusText.Text = "Review the code above, then click Run.";
                StatusText.Foreground = BrushDim;
                UpdateSaveCommandButton();
                return;
            }

            await SendCodeGenQuery(prompt, cmd.Id);
        }

        private static string RenderTemplate(string template, IDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(template) || values == null) return template;
            return _placeholderRe.Replace(template, m =>
                values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : m.Value);
        }

        private async void ExportCommandsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var bundle = await _aiService.ExportCommandsAsync(UserIdOrNull, _config?.OrgId, _config?.AccessToken);
                if (bundle == null || bundle.Commands == null || bundle.Commands.Count == 0)
                {
                    CommandsHint.Text = "No commands to export.";
                    return;
                }
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = $"bina-commands-{DateTime.Now:yyyyMMdd-HHmm}.json",
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    Title = "Export Copilot commands"
                };
                if (dlg.ShowDialog(this) != true) return;
                System.IO.File.WriteAllText(dlg.FileName,
                    Newtonsoft.Json.JsonConvert.SerializeObject(bundle, Newtonsoft.Json.Formatting.Indented));
                CommandsHint.Text = $"Exported {bundle.Commands.Count} commands to {System.IO.Path.GetFileName(dlg.FileName)}.";
            }
            catch (Exception ex)
            {
                CommandsHint.Text = "Export failed: " + ex.Message;
            }
        }

        private async void ImportCommandsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    Title = "Import Copilot commands",
                    Multiselect = false
                };
                if (dlg.ShowDialog(this) != true) return;
                var json = System.IO.File.ReadAllText(dlg.FileName);
                var bundle = Newtonsoft.Json.JsonConvert.DeserializeObject<CommandBundle>(json);
                if (bundle == null || bundle.Commands == null || bundle.Commands.Count == 0)
                {
                    CommandsHint.Text = "That file doesn't contain any commands.";
                    return;
                }
                var result = await _aiService.ImportCommandsAsync(
                    bundle, UserIdOrNull, _config?.OrgId, skipDuplicates: true, _config?.AccessToken);
                if (!result.HasValue)
                {
                    CommandsHint.Text = "Import failed — see backend logs.";
                    return;
                }
                CommandsHint.Text = $"Imported {result.Value.imported}, skipped {result.Value.skipped} (already exist), out of {result.Value.total}.";
                _commandsLoaded = false;
                await LoadCommandsAsync();
            }
            catch (Exception ex)
            {
                CommandsHint.Text = "Import failed: " + ex.Message;
            }
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
            _handler.Action = "execute";
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
                        if (LooksLikeModelChange(code))
                            AddRevertRow();
                        AddSaveAsCommandRow(_lastUserPrompt, code);
                        _ = TryBackfillCommandCodeAsync(code);
                        _ = TryRecordFixAsync(code);
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

        // After a successful run, if the run was driven by one of MY saved
        // commands AND that command doesn't have a code snapshot yet, push the
        // code back onto the command so subsequent runs skip the LLM.
        //
        // Scoping rules:
        // - Only commands owned by THIS user (scope='user', owner=me).
        // - Public seed commands stay null on purpose — they're shared, and
        //   the first user's generated code shouldn't pin everyone else.
        // - Team commands also skipped — modifying them affects others.
        // - Doesn't overwrite an existing snapshot (deliberate manual saves win).
        private async Task TryBackfillCommandCodeAsync(string executedCode)
        {
            try
            {
                var cmd = _lastRunCommand;
                _lastRunCommand = null;  // one-shot — clear regardless of outcome
                if (cmd == null) return;
                if (string.IsNullOrWhiteSpace(executedCode)) return;
                if (!string.Equals(cmd.Scope, "user", StringComparison.OrdinalIgnoreCase)) return;
                if (!cmd.OwnerUserId.HasValue || cmd.OwnerUserId.Value != UserIdOrNull) return;
                if (cmd.HasSavedCode) return;            // don't overwrite a manual snapshot
                if (string.IsNullOrEmpty(_config?.AccessToken)) return;

                var updated = await _aiService.UpdateCommandCodeAsync(
                    cmd.Id, executedCode, UserIdOrNull, _config.AccessToken);
                if (updated != null)
                {
                    // Quietly update local cache so the next list refresh and
                    // future re-runs see the snapshot. No chat noise — backfill
                    // is supposed to be invisible.
                    cmd.GeneratedCode = updated.GeneratedCode;
                    var idx = _allCommands?.FindIndex(c => c.Id == cmd.Id) ?? -1;
                    if (idx >= 0) _allCommands[idx] = updated;
                }
            }
            catch
            {
                // Backfill failures shouldn't bother the user. Worst case: the
                // command stays prompt-only, next run still works via the LLM.
            }
        }

        // When a run succeeded AFTER an error (auto-retry chain or error-card
        // fix click), tell the backend so the (error, working_code) pair gets
        // recorded for future explain-error lookups (FR-022). One-shot —
        // clears the remembered error regardless of outcome.
        private async Task TryRecordFixAsync(string workingCode)
        {
            try
            {
                var err = _errorBeingFixed;
                _errorBeingFixed = null;
                if (string.IsNullOrWhiteSpace(err) || string.IsNullOrWhiteSpace(workingCode)) return;
                if (string.IsNullOrEmpty(_config?.AccessToken)) return;
                await _aiService.RecordFixAsync(err, workingCode, UserIdOrNull, _sessionId, _config.AccessToken);
            }
            catch
            {
                // Best-effort — never bother the user.
            }
        }

        // Heuristic: did this code likely write to the model? Used to decide
        // whether to surface the "Revert last change" button. Read-only
        // queries (counts, lists) don't show the button — pressing it would
        // either no-op (empty transaction) or undo whatever the user did
        // manually before, which is confusing.
        private static readonly string[] _writeCallSignatures = new[]
        {
            "Transaction(",
            ".Set(", ".SetParameter",
            ".ChangeTypeId(",
            "doc.Delete(", "doc.Create.", "Document.Create.",
            ".NewRoom", ".NewSection",
            ".Pin(", ".UnPin(",
            "ElementTransformUtils.",
            "JoinGeometryUtils.",
            "Level.Create", "ViewPlan.Create", "ViewSection.Create", "View3D.Create",
            "SketchPlane.Create",
            ".SetGraphicsOverrides(",
            ".SetElementOverrides(",
            ".HideElements(", ".UnhideElements(", ".IsolateElement",
            ".SetCategoryHidden", "SetTemporary",
        };

        private static bool LooksLikeModelChange(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            foreach (var s in _writeCallSignatures)
                if (code.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        // After a successful model-changing run, offer a one-click "↶ Revert"
        // that posts a Revit Undo (FR-023). Pairs with the error-card's
        // code-fix flow — if the auto-fix took, the user can still bail out.
        private void AddRevertRow()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 8)
            };
            var btn = new Button
            {
                Content = "↶  Revert last change",
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                FontSize = 11,
                ToolTip = "Tells Revit to undo the most recent change (equivalent to pressing Ctrl+Z)."
            };
            btn.Click += (s, e) =>
            {
                btn.IsEnabled = false;
                DispatchRevert();
            };
            panel.Children.Add(btn);
            CopilotChatHistory.Children.Add(panel);
            ScrollToBottom();
        }

        // Offer to snapshot the just-run code as a reusable Saved Command
        // (#21). Stores both the prompt template AND the generated C# — next
        // time the command runs, it executes the snapshot directly and skips
        // /generate.
        private void AddSaveAsCommandRow(string prompt, string code)
        {
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(code)) return;
            if (string.IsNullOrEmpty(_config?.AccessToken)) return;   // requires login

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 8)
            };
            var btn = new Button
            {
                Content = "💾  Save this run as a command",
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                FontSize = 11,
                ToolTip = "Saves the prompt + the generated C# so you can re-run this exact operation without going through the AI."
            };
            string promptCopy = prompt;
            string codeCopy = code;
            btn.Click += async (s, e) =>
            {
                btn.IsEnabled = false;
                try
                {
                    var dialog = new CommandSaveWindow(
                        promptCopy, UserIdOrNull, _config?.OrgId,
                        editing: null, savedCode: codeCopy) { Owner = this };
                    if (dialog.ShowDialog() != true) return;
                    var created = await _aiService.SaveCommandAsync(dialog.Result, _config?.AccessToken);
                    if (created != null)
                    {
                        AddSuccess($"Saved as command: {created.Name}.");
                        _commandsLoaded = false;
                        await LoadCommandsAsync();
                    }
                    else
                    {
                        AddError("Couldn't save the command.");
                    }
                }
                catch (Exception ex)
                {
                    AddError("Save failed: " + ex.Message);
                }
            };
            panel.Children.Add(btn);
            CopilotChatHistory.Children.Add(panel);
            ScrollToBottom();
        }

        private void DispatchRevert()
        {
            StatusText.Text = "Reverting…";
            StatusText.Foreground = BrushDim;
            SetInputEnabled(false);
            _handler.Action = "undo";
            _handler.CodeToExecute = null;
            _handler.OnCompleted = (result) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (result.Success) AddSuccess(result.Message ?? "Reverted.");
                    else AddError(result.Error ?? "Couldn't revert.");
                    StatusText.Text = result.Success ? "Ready" : "Error";
                    StatusText.Foreground = result.Success ? BrushOk : BrushErr;
                    SetInputEnabled(true);
                    // PostCommand(Undo) activates Revit's main window to run the
                    // command, which sends this modeless window behind it. Bring
                    // the Copilot back once Revit has processed the posted undo.
                    BringToFrontAfterDelay(500);
                });
            };
            _externalEvent.Raise();
        }

        // Re-activates the Copilot window after `ms` — used after PostCommand-
        // style actions (Undo) where Revit grabs focus to run a posted command
        // and the modeless add-in window drops behind it. The brief Topmost
        // toggle is the reliable way to force foreground from a background app
        // (Activate() alone often just flashes the taskbar).
        private void BringToFrontAfterDelay(int ms)
        {
            try
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(ms)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    try
                    {
                        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                        // Gentle raise — no Topmost toggle (that flickers). If
                        // Windows' foreground lock blocks the raise, the worst
                        // case is the user clicks the window once; that's
                        // better than a visible flash on every revert.
                        if (!IsActive) Activate();
                    }
                    catch { /* window may have been closed */ }
                };
                timer.Start();
            }
            catch { /* non-fatal — worst case the user clicks the window */ }
        }

        // On compile/exec failure, feed the error back to the AI and re-run the
        // fixed code, up to MaxRetries times.
        private async Task HandleExecutionFailureAsync(string error)
        {
            try
            {
                if (_retryCount >= MaxRetries || string.IsNullOrEmpty(_config?.AccessToken))
                {
                    await ShowErrorExplainerAsync(error);
                    return;
                }

                _retryCount++;
                if (string.IsNullOrEmpty(_errorBeingFixed))
                    _errorBeingFixed = error;
                AddWarning($"That didn't work. Auto-fixing (attempt {_retryCount}/{MaxRetries})... (you can Cancel)");
                StatusText.Text = "Auto-fixing the code...";
                StatusText.Foreground = BrushDim;

                // Keep a live Cancel affordance during the retry round-trip so a
                // slow LLM tick never leaves the user trapped staring at a dimmed
                // window. Fresh CTS — the send-flow's _cts is already disposed.
                _retryCts?.Dispose();
                _retryCts = new CancellationTokenSource();
                SetCancelVisible(true);

                AIResponse resp;
                try
                {
                    resp = await _aiService.RetryCodeAsync(
                        _lastUserPrompt, _lastExecutedCode, error, _retryCount,
                        UserIdOrNull, _sessionId, _config?.AccessToken, _retryCts.Token);
                }
                catch (OperationCanceledException)
                {
                    SetCancelVisible(false);
                    AddWarning("Auto-fix cancelled.");
                    StatusText.Text = "Ready"; StatusText.Foreground = BrushOk;
                    _retryCount = 0;
                    _errorBeingFixed = null;
                    SetInputEnabled(true);
                    return;
                }
                finally
                {
                    SetCancelVisible(false);
                }

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
                    await ShowErrorExplainerAsync(resp?.Error ?? error);
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

        // After auto-fix is exhausted (or unavailable), ask the backend to
        // explain the failure in plain English and offer a few next steps,
        // rendered as a card. Falls back to the raw error if the call fails.
        private async Task ShowErrorExplainerAsync(string error)
        {
            _retryCount = 0;
            SetInputEnabled(true);
            StatusText.Text = "Execution failed";
            StatusText.Foreground = BrushErr;

            Models.ErrorExplanation expl = null;
            try
            {
                expl = await _aiService.ExplainErrorAsync(
                    error, _lastExecutedCode, _lastUserPrompt, GetModelContext(),
                    UserIdOrNull, _sessionId, _config?.AccessToken);
            }
            catch { /* fall through to raw error */ }

            if (expl == null || string.IsNullOrWhiteSpace(expl.Explanation))
            {
                AddError(error);
                return;
            }
            AddErrorExplainerCard(expl, error);
        }

        private void AddErrorExplainerCard(Models.ErrorExplanation expl, string rawError)
        {
            Log("Error", $"{expl.Explanation}" + (string.IsNullOrWhiteSpace(expl.RootCause) ? "" : $" (root cause: {expl.RootCause})"));

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(43, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 50, 50)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 4, 0, 8)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "⚠  That didn't work",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 130, 130)),
                Margin = new Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(new TextBlock
            {
                Text = expl.Explanation,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            if (!string.IsNullOrWhiteSpace(expl.RootCause) &&
                !string.Equals(expl.RootCause.Trim(), expl.Explanation.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "Why: " + expl.RootCause,
                    Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }

            if (expl.Fixes != null && expl.Fixes.Count > 0)
            {
                var fixPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                foreach (var fx in expl.Fixes)
                {
                    if (fx == null || string.IsNullOrWhiteSpace(fx.Label)) continue;
                    var btn = new Button
                    {
                        Content = fx.Recommended ? "★ " + fx.Label : fx.Label,
                        ToolTip = fx.Description,
                        Padding = new Thickness(12, 5, 12, 5),
                        Margin = new Thickness(0, 0, 6, 6),
                        Cursor = Cursors.Hand,
                        FontSize = 11,
                        Foreground = fx.Recommended ? Brushes.White : new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                        BorderThickness = new Thickness(fx.Recommended ? 0 : 1),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                        Background = fx.Recommended
                            ? new SolidColorBrush(Color.FromRgb(0, 120, 212))
                            : new SolidColorBrush(Color.FromRgb(45, 45, 48))
                    };
                    var capturedFix = fx;
                    btn.Click += async (s, e) =>
                    {
                        foreach (var child in fixPanel.Children)
                            if (child is Button b) b.IsEnabled = false;
                        await OnErrorFixClickedAsync(capturedFix, rawError);
                    };
                    fixPanel.Children.Add(btn);
                }
                if (fixPanel.Children.Count > 0) stack.Children.Add(fixPanel);
            }

            // Collapsible raw error.
            var rawToggle = new Button
            {
                Content = "▸ raw error",
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                Cursor = Cursors.Hand,
                FontSize = 10,
                Padding = new Thickness(0)
            };
            var rawBox = new TextBox
            {
                Text = rawError ?? "",
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = new SolidColorBrush(Color.FromRgb(200, 120, 120)),
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = System.Windows.Visibility.Collapsed
            };
            rawToggle.Click += (s, e) =>
            {
                bool show = rawBox.Visibility != System.Windows.Visibility.Visible;
                rawBox.Visibility = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                rawToggle.Content = (show ? "▾ raw error" : "▸ raw error");
            };
            stack.Children.Add(rawToggle);
            stack.Children.Add(rawBox);

            border.Child = stack;
            CopilotChatHistory.Children.Add(border);
            ScrollToBottom();
            SetPreview_Error(expl);
        }

        private async Task OnErrorFixClickedAsync(Models.ErrorFix fix, string rawError)
        {
            if (fix == null) return;
            if (!fix.CodeFix)
            {
                // Manual step — just surface the instruction.
                AddMessage(string.IsNullOrWhiteSpace(fix.Description) ? fix.Label : fix.Description, isUser: false);
                return;
            }

            // Code fix — ask the backend to regenerate, then let the user review & Run.
            AddWarning("Regenerating the code…");
            StatusText.Text = "Regenerating…";
            StatusText.Foreground = BrushDim;
            SetInputEnabled(false);
            // Remember the error so a subsequent successful run can record the
            // fix pattern (FR-022).
            _errorBeingFixed = rawError;
            try
            {
                var resp = await _aiService.RetryCodeAsync(
                    _lastUserPrompt, _lastExecutedCode, rawError ?? "", 1,
                    UserIdOrNull, _sessionId, _config?.AccessToken);
                if (resp != null && resp.Success && !string.IsNullOrEmpty(resp.Code))
                {
                    AddMessage(resp.Explanation ?? "Here's a corrected version — review it and click Run.", isUser: false);
                    AddCodeBlock(resp.Code);
                    AddRunDiscardRow(resp.Code);
                    _lastExecutedCode = resp.Code;
                    StatusText.Text = "Review the code above, then click Run.";
                    StatusText.Foreground = BrushDim;
                }
                else
                {
                    AddError(resp?.Error ?? "Couldn't regenerate the code.");
                    StatusText.Text = "Error";
                    StatusText.Foreground = BrushErr;
                }
            }
            catch (Exception ex)
            {
                AddError("Regeneration failed: " + ex.Message);
                StatusText.Text = "Error";
                StatusText.Foreground = BrushErr;
            }
            finally
            {
                SetInputEnabled(true);
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

        #region Chat UI Helpers

        private void Log(string who, string text)
        {
            try { _transcript.AppendLine($"{who}: {text}").AppendLine(); } catch { }
        }

        private void CopyTranscriptButton_Click(object sender, RoutedEventArgs e)
        {
            var t = _transcript.ToString();
            if (string.IsNullOrWhiteSpace(t)) { AddSuccess("Nothing to copy yet."); return; }

            // Clipboard.SetText is fragile in Revit's host process — Win32 clipboard
            // contention (clipboard history, screen readers, AV) intermittently throws
            // CLIPBRD_E_CANT_OPEN even when SetClipboardData succeeded underneath.
            // After every attempt (succeed or throw), VERIFY against the clipboard:
            // if it now has our text (with line-ending normalisation, since Windows
            // converts \n to \r\n), declare success regardless of what SetDataObject
            // reported.
            Exception lastEx = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Clipboard.SetDataObject(t, copy: true); }
                catch (Exception ex) { lastEx = ex; }

                if (ClipboardHasOurText(t))
                {
                    AddSuccess("Transcript copied to the clipboard.");
                    return;
                }
                System.Threading.Thread.Sleep(50);
            }
            AddError("Couldn't copy: " + (lastEx?.Message ?? "clipboard not accessible"));
        }

        // Returns true when the clipboard currently holds (a close match to) the
        // text we just tried to copy. Tolerates Windows line-ending normalisation
        // — \n becomes \r\n on round-trip through the clipboard.
        private static bool ClipboardHasOurText(string expected)
        {
            try
            {
                if (!Clipboard.ContainsText()) return false;
                var actual = Clipboard.GetText();
                if (string.IsNullOrEmpty(actual)) return false;
                return NormalizeLineEndings(actual) == NormalizeLineEndings(expected);
            }
            catch { return false; }
        }

        private static string NormalizeLineEndings(string s)
            => s == null ? null : s.Replace("\r\n", "\n").Replace("\r", "\n");

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
                // User messages get inline @mention chips — each catalog mention
                // becomes a coloured, clickable badge.
                var textBlock = new TextBlock
                {
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 350
                };
                RenderTextWithMentions(textBlock, text, Brushes.White);
                border.Child = textBlock;
            }
            else
            {
                // Render markdown for bot responses
                border.Child = Helpers.MarkdownRenderer.Render(text, 350);
            }

            CopilotChatHistory.Children.Add(border);
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
            CopilotChatHistory.Children.Add(border);
            ScrollToBottom();
            SetPreview_Code(code);
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
                CopilotChatHistory.Children.Add(panel);
                ScrollToBottom();
            }
        }

        private void OnSuggestionClicked(string action, string text)
        {
            // A few actions get special handling; the rest map to a follow-up prompt.
            switch ((action ?? "").ToLowerInvariant())
            {
                case "undo":
                    DispatchRevert();
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
            CopilotChatHistory.Children.Add(panel);
            ScrollToBottom();
        }

        // ─────────────────────────────────────────────────────────────
        // Rich tabular result rendering (FR-034). When a successful
        // result looks like a multi-row, multi-column table, render it
        // as a sortable DataGrid instead of monospace text — same logic
        // is reused in the preview panel.
        // ─────────────────────────────────────────────────────────────

        // Regex column separator: 2+ whitespace OR tab OR optional-space-pipe-optional-space.
        private static readonly Regex _columnSplitter = new Regex(@"\s{2,}|\t|\s*\|\s*", RegexOptions.Compiled);

        private struct TabularResult
        {
            public string Title;
            public List<string> Headers;
            public List<List<string>> Rows;
        }

        private static TabularResult? TryParseTabularResult(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;

            var lines = message.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Select(l => (l ?? "").TrimEnd())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (lines.Count < 2) return null;

            // ── Title peeling ──
            // Two shapes the agent uses:
            //   "Label: col1   col2   col3"   → label becomes title, rest becomes header row
            //   "Label:" (or "Label: description:") with no embedded columns → whole first line is the title
            string title = null;
            var first = lines[0];
            int colon = first.IndexOf(':');
            if (colon > 0 && colon < first.Length - 1)
            {
                var afterColon = first.Substring(colon + 1).Trim();
                if (_columnSplitter.IsMatch(afterColon))
                {
                    title = first.Substring(0, colon).Trim();
                    lines[0] = afterColon;
                }
                else if (first.TrimEnd().EndsWith(":"))
                {
                    title = first.TrimEnd().TrimEnd(':').Trim();
                    lines.RemoveAt(0);
                }
            }
            else if (first.TrimEnd().EndsWith(":"))
            {
                title = first.TrimEnd().TrimEnd(':').Trim();
                lines.RemoveAt(0);
            }
            if (lines.Count < 1) return null;

            // ── Path A: multi-space / tab / pipe column separator ──
            var split = lines
                .Select(l => _columnSplitter.Split(l).Where(s => !string.IsNullOrEmpty(s)).ToList())
                .ToList();
            var modal = split.GroupBy(r => r.Count).OrderByDescending(g => g.Count()).First();
            if (modal.Key >= 2
                && modal.Count() >= (split.Count + 1) / 2
                && split[0].Count == modal.Key)
            {
                var rows = split.Skip(1).Where(r => r.Count == modal.Key).ToList();
                if (rows.Count >= 1)
                    return new TabularResult { Title = title, Headers = split[0], Rows = rows };
            }

            // ── Path B: "key: value" pairs (2-column key/value table) ──
            // Only accept when EVERY remaining line splits cleanly into exactly
            // two parts AND the key side is short enough to look like a label.
            var kv = lines
                .Select(l => l.Split(new[] { ": " }, 2, StringSplitOptions.None).ToList())
                .ToList();
            if (lines.Count >= 2
                && kv.All(r => r.Count == 2 && r[0].Length > 0 && r[0].Length <= 40 && !string.IsNullOrWhiteSpace(r[1])))
            {
                var (keyHeader, valueHeader) = SniffKvHeaders(title);
                return new TabularResult
                {
                    Title = title,
                    Headers = new List<string> { keyHeader, valueHeader },
                    Rows = kv
                };
            }

            return null;
        }

        // Try to derive useful column headers for a 2-column key/value table
        // from the title's natural language. E.g. "Door counts per level"
        // becomes ("Level", "Door counts"). Falls back to ("Item", "Value").
        private static (string keyHeader, string valueHeader) SniffKvHeaders(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return ("Item", "Value");
            // Strip everything before the last ":" so titles like
            // "Door Counts: Door counts per level" reduce to "Door counts per level".
            var t = title.Contains(":") ? title.Substring(title.LastIndexOf(':') + 1).Trim() : title.Trim();
            var m = Regex.Match(t, @"^(.+?)\s+(?:per|by)\s+(.+?)\s*$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var x = m.Groups[1].Value.Trim();
                var y = m.Groups[2].Value.Trim();
                return (CapitalizeFirst(y), CapitalizeFirst(x));
            }
            return ("Item", "Value");
        }

        private static string CapitalizeFirst(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        // Builds a small dark-themed DataGrid from the parsed table. maxHeight
        // is enforced so the grid doesn't take over the chat scrollviewer.
        private static FrameworkElement BuildTableElement(TabularResult t, double maxHeight, bool monospaceCells = false)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

            if (!string.IsNullOrWhiteSpace(t.Title))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "[OK] " + t.Title,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 83)),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }

            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(37, 37, 38))));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
            headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(63, 63, 70))));
            headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(55, 55, 60)),
                RowBackground = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                FontSize = 11,
                ColumnHeaderHeight = 26,
                RowHeight = 22,
                MaxHeight = maxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ColumnHeaderStyle = headerStyle
            };
            if (monospaceCells)
                grid.FontFamily = new FontFamily("Consolas");

            for (int i = 0; i < t.Headers.Count; i++)
            {
                var col = new DataGridTextColumn
                {
                    Header = t.Headers[i],
                    Binding = new Binding("[" + i + "]"),
                    MinWidth = 60,
                    Width = DataGridLength.Auto
                };
                grid.Columns.Add(col);
            }
            grid.ItemsSource = t.Rows;

            // A small footer hint with the row count.
            stack.Children.Add(grid);
            stack.Children.Add(new TextBlock
            {
                Text = $"{t.Rows.Count} row(s)",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                FontSize = 10,
                Margin = new Thickness(2, 2, 0, 0)
            });
            return stack;
        }

        // True when the code ran fine but the *message* says the operation
        // couldn't complete for a model-state reason (no family loaded, nothing
        // matched, etc.). We render those as a yellow warning instead of a
        // green [OK] — '[OK] Error: No door family loaded' reads wrong.
        private static bool LooksLikeSoftFailureMessage(string m)
        {
            if (string.IsNullOrWhiteSpace(m)) return false;
            var s = m.TrimStart().ToLowerInvariant();
            return s.StartsWith("error")
                || s.StartsWith("can't") || s.StartsWith("cannot")
                || s.StartsWith("couldn't") || s.StartsWith("could not")
                || s.StartsWith("no ")        // "No single-flush door family loaded."
                || s.StartsWith("not found")
                || s.StartsWith("nothing ")   // "Nothing to update."
                || s.Contains("not loaded")
                || s.Contains("no view matching")
                || s.Contains("not found in the");
        }

        private void AddSuccess(string message)
        {
            Log("Result", message);

            // Graceful "couldn't do it because the model lacks X" messages get
            // warning styling, not a misleading green [OK].
            if (LooksLikeSoftFailureMessage(message))
            {
                AddWarning(message);
                SetPreview_Result(message, isError: false);
                return;
            }

            FrameworkElement content = null;
            try
            {
                var parsed = TryParseTabularResult(message);
                if (parsed.HasValue)
                    content = BuildTableElement(parsed.Value, maxHeight: 260);
            }
            catch { content = null; }

            if (content == null)
            {
                content = new TextBlock
                {
                    Text = $"[OK] {message}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 83)),
                    Margin = new Thickness(0, 4, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                };
            }

            CopilotChatHistory.Children.Add(content);
            ScrollToBottom();
            SetPreview_Result(message, isError: false);
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

            CopilotChatHistory.Children.Add(textBlock);
            ScrollToBottom();
            SetPreview_Result(message, isError: true);
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

            CopilotChatHistory.Children.Add(textBlock);
            ScrollToBottom();
        }

        // A chat card that links to one of the existing dockable dashboards
        // (JKR/UBBL compliance or cost). The Copilot recognises the intent;
        // the analysis itself still runs in the dedicated panel.
        private void AddDashboardCard(string title, string body, string buttonLabel, string paneKind)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 2, 0, 8)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(new TextBlock
            {
                Text = body,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var btn = new Button
            {
                Content = buttonLabel,
                Padding = new Thickness(14, 6, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212))
            };
            btn.Click += (s, e) => OpenDashboardPane(paneKind);
            stack.Children.Add(btn);
            border.Child = stack;
            CopilotChatHistory.Children.Add(border);
            Log("Bina", $"{title} — {body}");
            ScrollToBottom();
            SetPreview_Dashboard(title, body, buttonLabel, paneKind);
        }

        private void OpenDashboardPane(string kind)
        {
            try
            {
                var uiApp = _uidoc?.Application;
                if (uiApp == null) { AddError("No active Revit session — open a project first."); return; }

                DockablePaneId paneId;
                string label;
                if (kind == "cost") { paneId = CostDashboardHost.PaneId; label = "Cost"; }
                else { paneId = JkrComplianceDashboardHost.PaneId; label = "JKR / UBBL Compliance"; }

                var pane = uiApp.GetDockablePane(paneId);
                if (pane == null) { AddError($"The {label} panel isn't registered — restart Revit and try again."); return; }
                if (!pane.IsShown()) pane.Show();

                if (kind == "cost")
                {
                    try
                    {
                        App.CostDashboardHost?.DashboardPanel?.SetRevitApp(uiApp);
                        App.CostDashboardHost?.DashboardPanel?.RefreshData();
                    }
                    catch { /* panel may still be initialising */ }
                }
                else
                {
                    try { App.JkrComplianceDashboardHost?.DashboardPanel?.SetRevitApp(uiApp); } catch { }
                }
                AddSuccess($"Opened the {label} dashboard — run the check from there.");
            }
            catch (Exception ex)
            {
                AddError($"Couldn't open the dashboard: {ex.Message}");
            }
        }

        private void ScrollToBottom()
        {
            CopilotScrollViewer.ScrollToEnd();
        }

        // ─────────────────────────────────────────────────────────────
        // Split-view preview panel (FR-031). The chat keeps the full
        // running history; the preview mirrors the LATEST artefact in
        // expanded form so the user can read code, results or compliance
        // outcomes without scrolling. Read-only — interactions still
        // happen in the chat.
        // ─────────────────────────────────────────────────────────────

        private void TogglePreviewButton_Click(object sender, RoutedEventArgs e) => HidePreviewPanel();
        private void ShowPreviewButton_Click(object sender, RoutedEventArgs e) => ShowPreviewPanel();

        private GridLength _savedPreviewWidth = new GridLength(300, GridUnitType.Pixel);

        private void HidePreviewPanel()
        {
            try
            {
                _savedPreviewWidth = PreviewColumn.Width;
                PreviewColumn.Width = new GridLength(0);
                PreviewPanel.Visibility = System.Windows.Visibility.Collapsed;
                PreviewSplitter.Visibility = System.Windows.Visibility.Collapsed;
                ShowPreviewButton.Visibility = System.Windows.Visibility.Visible;
            }
            catch { /* layout may not be ready */ }
        }

        private void ShowPreviewPanel()
        {
            try
            {
                if (_savedPreviewWidth.Value <= 0)
                    _savedPreviewWidth = new GridLength(300, GridUnitType.Pixel);
                PreviewColumn.Width = _savedPreviewWidth;
                PreviewPanel.Visibility = System.Windows.Visibility.Visible;
                PreviewSplitter.Visibility = System.Windows.Visibility.Visible;
                ShowPreviewButton.Visibility = System.Windows.Visibility.Collapsed;
            }
            catch { /* layout may not be ready */ }
        }

        private void ResetPreview(string header)
        {
            try
            {
                PreviewContent.Children.Clear();
                PreviewHeader.Text = header ?? "Preview";
            }
            catch { }
        }

        private static SolidColorBrush PrevBgBrush() => new SolidColorBrush(Color.FromRgb(45, 45, 48));
        private static SolidColorBrush PrevBorderBrush() => new SolidColorBrush(Color.FromRgb(63, 63, 70));
        private static SolidColorBrush PrevFgBrush() => new SolidColorBrush(Color.FromRgb(220, 220, 220));
        private static SolidColorBrush PrevDimBrush() => new SolidColorBrush(Color.FromRgb(170, 170, 170));

        private void SetPreview_Code(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            ResetPreview("Latest code");
            var box = new TextBox
            {
                Text = code,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 220, 255)),
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderThickness = new Thickness(1),
                BorderBrush = PrevBorderBrush(),
                Padding = new Thickness(8),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            PreviewContent.Children.Add(box);
        }

        private void SetPreview_Result(string message, bool isError)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            ResetPreview(isError ? "Last error" : "Last result");

            // Tabular result → render as DataGrid in the preview too, with a
            // taller MaxHeight since the preview pane is dedicated to it.
            if (!isError)
            {
                FrameworkElement table = null;
                try
                {
                    var parsed = TryParseTabularResult(message);
                    if (parsed.HasValue)
                        table = BuildTableElement(parsed.Value, maxHeight: 480);
                }
                catch { table = null; }

                if (table != null)
                {
                    PreviewContent.Children.Add(table);
                    return;
                }
            }

            var border = new Border
            {
                Background = isError
                    ? new SolidColorBrush(Color.FromRgb(43, 30, 30))
                    : new SolidColorBrush(Color.FromRgb(28, 40, 32)),
                BorderBrush = isError
                    ? new SolidColorBrush(Color.FromRgb(120, 50, 50))
                    : new SolidColorBrush(Color.FromRgb(40, 100, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8)
            };
            // Use a TextBox in NoWrap mode so tabular results stay aligned (the
            // monospace render is what makes non-grid results readable).
            var box = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = isError
                    ? new SolidColorBrush(Color.FromRgb(255, 130, 130))
                    : new SolidColorBrush(Color.FromRgb(120, 220, 150)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            border.Child = box;
            PreviewContent.Children.Add(border);
        }

        private void SetPreview_Dashboard(string title, string body, string buttonLabel, string paneKind)
        {
            ResetPreview("Action card");
            var border = new Border
            {
                Background = PrevBgBrush(),
                BorderBrush = PrevBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(new TextBlock
            {
                Text = body,
                Foreground = PrevDimBrush(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var btn = new Button
            {
                Content = buttonLabel,
                Padding = new Thickness(12, 5, 12, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212))
            };
            btn.Click += (s, e) => OpenDashboardPane(paneKind);
            stack.Children.Add(btn);
            border.Child = stack;
            PreviewContent.Children.Add(border);
        }

        private void SetPreview_Error(Models.ErrorExplanation expl)
        {
            if (expl == null) return;
            ResetPreview("Last error");
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(43, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 50, 50)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "⚠  That didn't work",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 130, 130)),
                Margin = new Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(new TextBlock
            {
                Text = expl.Explanation ?? "",
                Foreground = PrevFgBrush(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            if (!string.IsNullOrWhiteSpace(expl.RootCause)
                && !string.Equals(expl.RootCause.Trim(), (expl.Explanation ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "Why: " + expl.RootCause,
                    Foreground = PrevDimBrush(),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 6)
                });
            }
            if (expl.Fixes != null && expl.Fixes.Count > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "Suggested next steps:",
                    Foreground = PrevDimBrush(),
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 2)
                });
                foreach (var fx in expl.Fixes)
                {
                    if (fx == null || string.IsNullOrWhiteSpace(fx.Label)) continue;
                    stack.Children.Add(new TextBlock
                    {
                        Text = (fx.Recommended ? "★ " : "•  ") + fx.Label,
                        Foreground = PrevFgBrush(),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(8, 1, 0, 1)
                    });
                }
            }
            border.Child = stack;
            PreviewContent.Children.Add(border);
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
        public long? ElementId { get; }      // single-element mentions only (Level / View / Grid / Room / System)
        public string BicName { get; }       // BuiltInCategory enum name for Category / all_X mentions

        public MentionItem(string display, string insertText, string typeLabel, string resolved,
                           long? elementId = null, string bicName = null)
        {
            Display = display ?? string.Empty;
            InsertText = insertText ?? string.Empty;
            TypeLabel = typeLabel ?? string.Empty;
            Resolved = resolved ?? string.Empty;
            ElementId = elementId;
            BicName = bicName;
        }
    }
}
