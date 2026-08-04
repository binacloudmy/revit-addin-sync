using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;          // RelayCommand (shared chrome)
using RevitWebAppSync.UI.SpacePlanning.Model;

namespace RevitWebAppSync.UI.SpacePlanning
{
    /// <summary>Which panel the pane is showing.</summary>
    public enum SpScreen
    {
        /// <summary>The brief form — where the flow starts.</summary>
        Brief,
        /// <summary>Waiting on /planning/suggest, or on Revit during a Build.</summary>
        Running,
        /// <summary>SOA + schemes + floor-plan preview.</summary>
        Plan,
        /// <summary>What a Build placed in the model.</summary>
        Result,
    }

    /// <summary>
    /// View-model for the standalone Space Planning pane.
    ///
    /// Lifted out of CopilotViewModel (which is 1,900+ lines of chat, tools, history
    /// and usage) so the feature owns its own state and can be opened from its own
    /// ribbon button. The planning members keep their ORIGINAL NAMES — PlanningView
    /// binds to them by name, so the screen moved across unchanged apart from the
    /// type of its DataContext.
    ///
    /// Two states the chat pane used to supply, which this now owns:
    ///   · the BRIEF — previously typed after "/massing" in the composer;
    ///   · RUNNING / RESULT — previously the Copilot's shared screens.
    ///
    /// Only ONE method here writes to the Revit document: <see cref="BuildMassingAsync"/>.
    /// Everything else is a network call or pixels.
    /// </summary>
    public class SpacePlanningViewModel : INotifyPropertyChanged
    {
        public SpacePlanningViewModel()
        {
            SelectSchemeCommand = new RelayCommand(p => { if (p is MassingScheme s) SelectedScheme = s; });
            SelectLevelCommand = new RelayCommand(p =>
            {
                if (p is int n) SelectedLevel = n;
                else if (p != null && int.TryParse(p.ToString(), out var parsed)) SelectedLevel = parsed;
            });
            BuildMassingCommand = new RelayCommand(_ => _ = BuildMassingAsync());
            SuggestCommand = new RelayCommand(_ => _ = BeginPlanningAsync(Brief));
            // Back from the plan returns to the brief WITHOUT clearing it, so a user
            // who wants to tweak one number doesn't retype the whole thing.
            BackHomeCommand = new RelayCommand(_ => Screen = SpScreen.Brief);
            // From the result card, back to the plan the build came from.
            BackToPlanCommand = new RelayCommand(_ =>
            {
                if (Planning != null) Screen = SpScreen.Plan;
            });
            CancelCommand = new RelayCommand(_ => CancelRun());
            NewPlanCommand = new RelayCommand(_ =>
            {
                Planning = null;
                SelectedScheme = null;
                BuildOutcome = null;
                Screen = SpScreen.Brief;
            });
        }

        // ══════════ Screen ══════════

        private SpScreen _screen = SpScreen.Brief;
        public SpScreen Screen
        {
            get => _screen;
            private set
            {
                if (_screen == value) return;
                _screen = value; Raise();
                Raise(nameof(IsBrief)); Raise(nameof(IsRunning));
                Raise(nameof(IsPlan)); Raise(nameof(IsResult));
            }
        }

        public bool IsBrief => Screen == SpScreen.Brief;
        public bool IsRunning => Screen == SpScreen.Running;
        public bool IsPlan => Screen == SpScreen.Plan;
        public bool IsResult => Screen == SpScreen.Result;

        // ══════════ The brief form ══════════

        private string _brief = "";
        /// <summary>The plain-language building brief. The backend parses it with a
        /// regex first and only calls a model when the regex cannot pin it down.</summary>
        public string Brief
        {
            get => _brief;
            set { _brief = value ?? ""; Raise(); Raise(nameof(CanSuggest)); }
        }

        /// <summary>Site area in m². Optional — the authoritative source is the
        /// property-line sketch in the model, so this is only sent when the user
        /// types it. Null is meaningful: the backend omits the chip entirely.</summary>
        private double? _siteAreaM2;
        public double? SiteAreaM2
        {
            get => _siteAreaM2;
            set { _siteAreaM2 = value; Raise(); }
        }

        private double? _setbackM;
        public double? SetbackM
        {
            get => _setbackM;
            set { _setbackM = value; Raise(); }
        }

        private double? _targetGfaM2;
        public double? TargetGfaM2
        {
            get => _targetGfaM2;
            set { _targetGfaM2 = value; Raise(); }
        }

        public bool CanSuggest => !string.IsNullOrWhiteSpace(Brief) && Screen != SpScreen.Running;

        private string _briefError;
        /// <summary>Inline failure text on the brief form. Set from the typed soft
        /// failure the service returns — the pane never throws a dialog at the user.</summary>
        public string BriefError
        {
            get => _briefError;
            private set { _briefError = value; Raise(); Raise(nameof(HasBriefError)); }
        }

        public bool HasBriefError => !string.IsNullOrWhiteSpace(BriefError);

        public RelayCommand SuggestCommand { get; }
        public RelayCommand BackHomeCommand { get; }
        public RelayCommand BackToPlanCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand NewPlanCommand { get; }

        // ══════════ Massing / space planning ══════════
        //
        // Flow: brief → Running → POST /planning/suggest → Plan screen.
        // The pane draws the returned schemes as pixels; the ONLY write to the
        // Revit document is BuildMassingAsync (place_massing_scheme).

        private SuggestResult _planning;
        /// <summary>The last /planning/suggest response driving the Plan screen.</summary>
        public SuggestResult Planning
        {
            get => _planning;
            private set
            {
                _planning = value; Raise();
                Raise(nameof(PlanningSchemes)); Raise(nameof(PlanningRejected));
                Raise(nameof(PlanningSoa)); Raise(nameof(HasRejected));
            }
        }

        public List<MassingScheme> PlanningSchemes => _planning?.Schemes ?? new List<MassingScheme>();
        public List<RejectedScheme> PlanningRejected => _planning?.Rejected ?? new List<RejectedScheme>();
        public Soa PlanningSoa => _planning?.Soa;
        public bool HasRejected => PlanningRejected.Count > 0;

        private MassingScheme _selectedScheme;
        /// <summary>The scheme the preview canvas draws and Build would place.</summary>
        public MassingScheme SelectedScheme
        {
            get => _selectedScheme;
            set
            {
                if (ReferenceEquals(_selectedScheme, value)) return;
                _selectedScheme = value;
                Raise(); Raise(nameof(CanBuildMassing)); Raise(nameof(SelectedSchemeLevels));
                // A 2-storey scheme followed by a 1-storey one must not leave the
                // toggle pointing at a level the new scheme doesn't have.
                var levels = SelectedSchemeLevels;
                if (levels.Count > 0 && !levels.Contains(SelectedLevel)) SelectedLevel = levels[0];
            }
        }

        public List<int> SelectedSchemeLevels => _selectedScheme?.Levels() ?? new List<int>();

        private int _selectedLevel = 1;
        /// <summary>Which storey the preview draws (the L1/L2 toggle).</summary>
        public int SelectedLevel
        {
            get => _selectedLevel;
            set { if (_selectedLevel == value) return; _selectedLevel = value; Raise(); }
        }

        private bool _isBuildingMassing;
        public bool IsBuildingMassing
        {
            get => _isBuildingMassing;
            private set { _isBuildingMassing = value; Raise(); Raise(nameof(CanBuildMassing)); }
        }

        public bool CanBuildMassing => _selectedScheme != null && !_isBuildingMassing;

        /// <summary>The brief that produced the current result — echoed on the Plan
        /// screen so the user can see what was interpreted.</summary>
        public string PlanningBrief { get; private set; }

        public RelayCommand SelectSchemeCommand { get; }
        public RelayCommand SelectLevelCommand { get; }
        public RelayCommand BuildMassingCommand { get; }

        // ══════════ Running-screen copy ══════════

        public string RunningTitle { get; private set; }
        public string RunningInfo { get; private set; }
        public string[] RunningSteps { get; private set; }

        private void SetRunningCopy(string title, string info, string[] steps)
        {
            RunningTitle = title; RunningInfo = info; RunningSteps = steps;
            Raise(nameof(RunningTitle)); Raise(nameof(RunningInfo)); Raise(nameof(RunningSteps));
        }

        // ══════════ Result of a Build ══════════

        /// <summary>What one Build actually placed. Deliberately a small local type
        /// rather than the Copilot's ResultModel — this pane has no chat thread, no
        /// Save/Copy/Undo bar, and dragging that model in would re-couple the two.</summary>
        public sealed class MassingBuildOutcome
        {
            public bool Ok;
            public string Headline;
            public string GroupName;
            public string Output;              // rooms | masses | both
            public int MassCount;
            public int WallCount;
            public int LevelCount;
            public int SkippedCount;
            public string Category;
            public string Lod;
            public List<string> CreatedLevels = new List<string>();
            public string Error;

            // ── rooms mode ──
            public int RoomCount;
            public int SeparationLineCount;
            /// <summary>Rooms Revit created but could not close a boundary around.
            /// They look fine in plan and then schedule as nothing, so this is
            /// surfaced rather than left to be found later.</summary>
            public int UnenclosedCount;
            public int RoomFailureCount;
            public List<string> CreatedViews = new List<string>();
            /// <summary>The plan view Revit was switched to. Rooms draw nothing in an
            /// unrelated view, so a build with nowhere to look reads as a failure.</summary>
            public string OpenedView;
            public int TagCount;
            public int TagFailureCount;
            /// <summary>Rooms cannot be group members — Revit refuses. Deleting the
            /// group removes the separation lines and masses and LEAVES the rooms.</summary>
            public bool RoomsInGroup;
        }

        private MassingBuildOutcome _buildOutcome;
        public MassingBuildOutcome BuildOutcome
        {
            get => _buildOutcome;
            private set { _buildOutcome = value; Raise(); }
        }

        // ══════════ Actions ══════════

        private CancellationTokenSource _planningCts;

        private void CancelRun()
        {
            _planningCts?.Cancel();
            // A Build cannot be cancelled once Revit has the job — only a suggest can.
            if (!IsBuildingMassing)
                Screen = Planning != null ? SpScreen.Plan : SpScreen.Brief;
        }

        /// <summary>
        /// Ask the backend for a Schedule of Accommodation and candidate block
        /// schemes, then show the Plan screen.
        ///
        /// Never throws: SuggestPlanningAsync returns a typed soft failure, and a
        /// backend outage lands back on the brief form with the reason inline.
        /// </summary>
        public async Task BeginPlanningAsync(string brief)
        {
            if (string.IsNullOrWhiteSpace(brief)) return;

            PlanningBrief = brief;
            Raise(nameof(PlanningBrief));
            BriefError = null;

            SetRunningCopy(
                "Massing / Space Planning",
                "Reading the brief against JKR/KPM modules and UBBL — nothing is written to your model yet.",
                new[]
                {
                    "Parsing the building brief",
                    "Deriving the Schedule of Accommodation",
                    "Checking sanitary provision (UBBL)",
                    "Generating block schemes",
                    "Scoring against target GFA",
                });
            Screen = SpScreen.Running;

            // Cancel replaces any in-flight suggest — without this, hitting Cancel
            // returned to the form and then the late response yanked the user onto
            // the Plan screen anyway.
            _planningCts?.Cancel();
            var cts = _planningCts = new CancellationTokenSource();

            var cfg = BinaConfig.Load();
            SuggestResult result;
            try
            {
                var request = new SuggestRequest
                {
                    Brief = brief,
                    UserId = cfg?.UserId,
                    SiteAreaM2 = SiteAreaM2,
                    SetbackM = SetbackM,
                    TargetGfaM2 = TargetGfaM2,
                };
                result = await new AIService().SuggestPlanningAsync(
                    request, cfg?.AccessToken, cts.Token);
            }
            catch (Exception ex)
            {
                // Defence in depth — SuggestPlanningAsync already soft-fails.
                result = SuggestResult.Fail(ex.Message);
            }

            if (cts.IsCancellationRequested) return;   // user cancelled — stay put

            if (result == null || !result.Success || result.Soa == null)
            {
                BriefError = string.IsNullOrWhiteSpace(result?.Error)
                    ? "The planning service didn't respond."
                    : result.Error;
                Screen = SpScreen.Brief;
                return;
            }

            Planning = result;
            // Default to the first PASSING scheme; fall back to the first scheme so
            // a result where nothing meets GFA still previews something.
            SelectedScheme = result.Schemes.FirstOrDefault(s => s.MeetsGfa) ?? result.Schemes.FirstOrDefault();
            var levels = SelectedSchemeLevels;
            SelectedLevel = levels.Contains(1) ? 1 : (levels.Count > 0 ? levels[0] : 1);
            Screen = SpScreen.Plan;
        }

        /// <summary>
        /// Drop a ready-made result straight onto the Plan screen, no backend call.
        /// For the UiHarness preview (and any future screenshot test) so the whole
        /// screen can be built and iterated without a live backend.
        /// </summary>
        public void ShowPlanningPreview(SuggestResult result, string brief = null)
        {
            if (result == null) return;
            PlanningBrief = brief;
            Raise(nameof(PlanningBrief));
            Planning = result;
            SelectedScheme = result.Schemes.FirstOrDefault(s => s.MeetsGfa) ?? result.Schemes.FirstOrDefault();
            var levels = SelectedSchemeLevels;
            SelectedLevel = levels.Contains(1) ? 1 : (levels.Count > 0 ? levels[0] : 1);
            Screen = SpScreen.Plan;
        }

        /// <summary>
        /// Build — place the selected scheme into the model. Runs the geometry on
        /// Revit's main thread through the MCP job pump (never from this async
        /// continuation) and lands on the Result screen.
        /// </summary>
        public async Task BuildMassingAsync()
        {
            var scheme = SelectedScheme;
            if (scheme == null || IsBuildingMassing) return;

            IsBuildingMassing = true;
            var optionName = MassingArgs.OptionName(scheme);
            SetRunningCopy(
                "Placing massing scheme",
                "One transaction, one undo step. Everything lands in a named Model Group you can delete in one action.",
                new[]
                {
                    "Resolving levels",
                    "Creating conceptual masses",
                    "Grouping the scheme",
                    "Committing the transaction",
                });
            Screen = SpScreen.Running;

            try
            {
                // Build to the height the SOA's volume was computed from, so the
                // placed geometry and the reported isipadu describe one building.
                // Walls ON. Room-separation lines alone draw as thin dashes, which
                // a drafter reads as "nothing was built"; walls are what make a plan
                // look like a plan. NOTE this crosses LOD 100 (space boundaries) into
                // LOD 200 (building elements) — see the plan doc, it is a decision the
                // SV owns, not a detail.
                var args = MassingArgs.Build(
                    scheme, makeWalls: true, optionName: optionName,
                    storeyHeightMm: MassingArgs.StoreyHeightMmFor(Planning));
                var json = await RunLocalToolAsync("place_massing_scheme", args);
                BuildOutcome = ReadBuildOutcome(optionName, json);
            }
            catch (Exception ex)
            {
                BuildOutcome = new MassingBuildOutcome
                {
                    Ok = false,
                    Headline = "Build failed",
                    Error = ex.Message,
                    GroupName = optionName,
                };
            }
            finally
            {
                IsBuildingMassing = false;
                Screen = SpScreen.Result;
            }
        }

        /// <summary>Read place_massing_scheme's result payload into the outcome the
        /// Result screen renders. Every field is optional on purpose — an older addin
        /// build returns a subset, and a missing count must read as 0, not crash.</summary>
        private static MassingBuildOutcome ReadBuildOutcome(
            string optionName, System.Text.Json.JsonElement? json)
        {
            var outcome = new MassingBuildOutcome { Ok = true, GroupName = optionName };
            if (json.HasValue && json.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var o = json.Value;
                // mass_count is the current name; floor_count is what the mutator used
                // to return and still emits, so read whichever is present.
                if (o.TryGetProperty("mass_count", out var m) && m.TryGetInt32(out var mi)) outcome.MassCount = mi;
                else if (o.TryGetProperty("floor_count", out var f) && f.TryGetInt32(out var fi)) outcome.MassCount = fi;
                if (o.TryGetProperty("wall_count", out var w) && w.TryGetInt32(out var wi)) outcome.WallCount = wi;
                if (o.TryGetProperty("level_count", out var l) && l.TryGetInt32(out var li)) outcome.LevelCount = li;
                if (o.TryGetProperty("skipped_count", out var s) && s.TryGetInt32(out var si)) outcome.SkippedCount = si;
                if (o.TryGetProperty("option_name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                    outcome.GroupName = n.GetString() ?? optionName;
                if (o.TryGetProperty("category", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                    outcome.Category = c.GetString();
                if (o.TryGetProperty("lod", out var lod) && lod.ValueKind == System.Text.Json.JsonValueKind.String)
                    outcome.Lod = lod.GetString();
                if (o.TryGetProperty("created_levels", out var cl) && cl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var item in cl.EnumerateArray())
                        if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                            outcome.CreatedLevels.Add(item.GetString());
                if (o.TryGetProperty("ok", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.False)
                    outcome.Ok = false;

                if (o.TryGetProperty("output", out var outp) && outp.ValueKind == System.Text.Json.JsonValueKind.String)
                    outcome.Output = outp.GetString();
                if (o.TryGetProperty("room_count", out var rc) && rc.TryGetInt32(out var rci)) outcome.RoomCount = rci;
                if (o.TryGetProperty("separation_line_count", out var sc2) && sc2.TryGetInt32(out var sci)) outcome.SeparationLineCount = sci;
                if (o.TryGetProperty("unenclosed_room_count", out var uc) && uc.TryGetInt32(out var uci)) outcome.UnenclosedCount = uci;
                if (o.TryGetProperty("room_failure_count", out var fc) && fc.TryGetInt32(out var fci)) outcome.RoomFailureCount = fci;
                if (o.TryGetProperty("rooms_in_group", out var rg) && rg.ValueKind == System.Text.Json.JsonValueKind.True)
                    outcome.RoomsInGroup = true;
                if (o.TryGetProperty("opened_view", out var ov) && ov.ValueKind == System.Text.Json.JsonValueKind.String)
                    outcome.OpenedView = ov.GetString();
                if (o.TryGetProperty("tag_count", out var tc) && tc.TryGetInt32(out var tci)) outcome.TagCount = tci;
                if (o.TryGetProperty("tag_failure_count", out var tf) && tf.TryGetInt32(out var tfi)) outcome.TagFailureCount = tfi;
                if (o.TryGetProperty("created_views", out var cv) && cv.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var item in cv.EnumerateArray())
                        if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                            outcome.CreatedViews.Add(item.GetString());
            }

            // Lead with whichever thing was actually placed.
            if (!outcome.Ok) outcome.Headline = "Build failed";
            else if (outcome.RoomCount > 0)
                outcome.Headline = $"Placed {outcome.RoomCount} room{(outcome.RoomCount == 1 ? "" : "s")}";
            else
                outcome.Headline = $"Placed {outcome.MassCount} mass{(outcome.MassCount == 1 ? "" : "es")}";
            return outcome;
        }

        /// <summary>Run one addin tool in-process on the Revit thread (no backend
        /// round-trip) and return its result as JSON. Throws with the tool's own
        /// message on failure so the caller can show it to the drafter.</summary>
        private static async Task<System.Text.Json.JsonElement?> RunLocalToolAsync(
            string tool, Dictionary<string, object> args)
        {
            var job = new BinaVibe.Mcp.McpJob
            {
                Tool = tool,
                Args = System.Text.Json.JsonSerializer.SerializeToElement(args ?? new Dictionary<string, object>()),
            };
            BinaVibe.Mcp.McpJobPump.Enqueue(job);
            await job.Done.Task;
            if (job.Error != null) throw new InvalidOperationException(job.Error);
            if (job.Result == null) return null;
            var json = System.Text.Json.JsonSerializer.Serialize(job.Result);
            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
        }

        // ══════════ INotifyPropertyChanged ══════════

        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
