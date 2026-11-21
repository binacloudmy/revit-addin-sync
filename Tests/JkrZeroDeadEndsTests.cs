using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-437: "zero dead ends". A seeded scan with at least one failing row for every JKR rule 1-26,
    /// shaped like the backend's ComplianceIssueDto (valid_options / ai_suggested_code /
    /// requires_user_choice / Confidence included), run through the real mapper and the pane's own
    /// models (ResultsScreenModel, DetailScreenModel, JkrConfirmModel, JkrFixerRow, JkrManualFix,
    /// JkrRecheck, ProjectChecklistModel). Revit-free: the writes and the re-checks are faked.
    ///
    /// Two questions:
    ///  1. Does every failing row expose a way to resolve it (fix / pick / AI + manual override /
    ///     needs-a-person review / Project Checklist step)? And none exposes nothing at all?
    ///  2. Fix all -> pickers -> AI-fixer rows -> checklist, in that order, does every row reach
    ///     Passed / Confirmed / Ticked, leaving nothing red with nothing to click?
    ///
    /// The numbering of rules 1-26 is the audit's own (the backend has no numbered table): the project
    /// rules are fixed by AI-436 (1, 21-24, 26); the element rules follow the order the backend emits
    /// them in jkr_deterministic.py. A rule with a REAL dead end is pinned in <see cref="KnownDeadEnds"/>
    /// so the suite stays green and a new dead end, or a fixed one, fails it loudly.
    /// </summary>
    public class JkrZeroDeadEndsTests
    {
        private readonly ITestOutputHelper _out;
        public JkrZeroDeadEndsTests(ITestOutputHelper output) { _out = output; }

        // ── the 26 rules ─────────────────────────────────────────────────────────────────────────

        private enum Shape
        {
            /// <summary>An element row with a ready-made fix (Fix all writes it).</summary>
            Ready,
            /// <summary>An element row with several valid codes: a picker (Fix all never writes it).</summary>
            Pick,
            /// <summary>An element row with no fix: the AI-fixer row (Ask AI + manual override).</summary>
            AiOnly,
            /// <summary>A project-level row with a Project Checklist step (rules 1, 21-24).</summary>
            Checklist,
            /// <summary>Rule 26: on the checklist with no button; follows the element-naming rows.</summary>
            ChecklistFollows,
            /// <summary>A project-level row with no fix, no element and no checklist item.</summary>
            ManualOnly,
        }

        private sealed class RuleCase
        {
            public int Number;
            public string Rule;
            public string Category;       // what the backend sends as "category"
            public string Domain;         // element | project_setup
            public Shape Shape;
            public string[] Options = Array.Empty<string>();
            public string FixAction = "";
            public string FixParam = "";
            public string FixValue = "";
            public string FixTarget = "instance";
            public string ManualValue = "";   // a value a person can type that the rule's own check accepts
            public string Actual = "(empty)";
            public string Required = "";
        }

        private const string JkrName = "jkrAR_wal-k_(kt) Dinding Konkrit";

        private static readonly RuleCase[] Rules =
        {
            new RuleCase { Number = 1,  Rule = "File naming convention", Category = "Project Naming", Domain = "project_setup", Shape = Shape.Checklist,
                           Actual = "Copy of model.rvt", Required = "jkr{Disiplin}{YY}_..." },
            new RuleCase { Number = 2,  Rule = "Element has a name", Category = "Walls", Domain = "element", Shape = Shape.Ready,
                           FixAction = "rename_type", FixValue = JkrName, FixTarget = "type", ManualValue = JkrName, Required = "jkr-prefixed type name" },
            new RuleCase { Number = 3,  Rule = "Naming convention", Category = "Doors", Domain = "element", Shape = Shape.Ready,
                           FixAction = "rename_type", FixValue = "jkrAR_dor-k_(ky) Pintu Kayu", FixTarget = "type", ManualValue = "jkrAR_dor-k_(ky) Pintu Kayu", Actual = "Single-Flush" },
            new RuleCase { Number = 4,  Rule = "Valid discipline code in element name", Category = "Floors", Domain = "element", Shape = Shape.Ready,
                           FixAction = "rename_type", FixValue = "jkrAR_flr-k_(kt) Lantai Konkrit", FixTarget = "type", ManualValue = "jkrAR_flr-k_(kt) Lantai Konkrit", Actual = "jkrXX_flr" },
            new RuleCase { Number = 5,  Rule = "Category code matches Revit category", Category = "Windows", Domain = "element", Shape = Shape.Ready,
                           FixAction = "rename_type", FixValue = "jkrAR_win-k_(ky) Tingkap Kayu", FixTarget = "type", ManualValue = "jkrAR_win-k_(ky) Tingkap Kayu", Actual = "jkrAR_dor-k" },
            new RuleCase { Number = 6,  Rule = "Valid subtype code", Category = "Walls", Domain = "element", Shape = Shape.Pick,
                           Options = new[] { "wal-k", "wal-b", "wal-p" }, Actual = "wal-x" },
            new RuleCase { Number = 7,  Rule = "Material code in name is valid", Category = "Walls", Domain = "element", Shape = Shape.AiOnly,
                           ManualValue = JkrName, Actual = "jkrAR_wal-k_(zz) Dinding" },
            new RuleCase { Number = 8,  Rule = "Discipline matches category", Category = "Columns", Domain = "element", Shape = Shape.Ready,
                           FixAction = "rename_type", FixValue = "jkrST_col-k_(kt) Tiang Konkrit", FixTarget = "type", ManualValue = "jkrST_col-k_(kt) Tiang Konkrit", Actual = "jkrAR_col-k" },
            new RuleCase { Number = 9,  Rule = "Missing type parameter: Tahap_Api_jkr_stt", Category = "Doors", Domain = "element", Shape = Shape.AiOnly,
                           ManualValue = "FRD60", Actual = "(missing)" },
            new RuleCase { Number = 10, Rule = "Empty instance parameter: Lokasi_jkr_sit", Category = "Doors", Domain = "element", Shape = Shape.AiOnly,
                           ManualValue = "Blok A", Actual = "(empty)" },
            new RuleCase { Number = 11, Rule = "Kod_Jenis_jkr_stt value valid", Category = "Walls", Domain = "element", Shape = Shape.Pick,
                           Options = new[] { "DGb", "DGk", "DGp" }, Actual = "(none)" },
            new RuleCase { Number = 12, Rule = "Sistem_jkr_stt value valid", Category = "Pipes", Domain = "element", Shape = Shape.Pick,
                           Options = new[] { "SA", "SB" }, Actual = "(none)" },
            new RuleCase { Number = 13, Rule = "Sub_Sistem_jkr_stt value valid", Category = "Pipes", Domain = "element", Shape = Shape.Pick,
                           Options = new[] { "SA1", "SA2", "SA3" }, Actual = "(none)" },
            new RuleCase { Number = 14, Rule = "Bahan_Utama_jkr_stt material code valid", Category = "Walls", Domain = "element", Shape = Shape.AiOnly,
                           ManualValue = "(kt) Konkrit", Actual = "(zz) Entah" },
            new RuleCase { Number = 15, Rule = "Bidang_Kejuruteraan_jkr_stt value valid", Category = "Ducts", Domain = "element", Shape = Shape.Ready,
                           FixAction = "set_parameter", FixParam = "Bidang_Kejuruteraan_jkr_stt", FixValue = "ME", FixTarget = "type", ManualValue = "ME", Actual = "Mech" },
            new RuleCase { Number = 16, Rule = "Name discipline matches Bidang_Kejuruteraan", Category = "Ducts", Domain = "element", Shape = Shape.Ready,
                           FixAction = "rename_type", FixValue = "jkrME_dct-k_(kt) Saluran", FixTarget = "type", ManualValue = "jkrME_dct-k_(kt) Saluran", Actual = "jkrAR_dct" },
            new RuleCase { Number = 17, Rule = "Material code in name matches Bahan_Utama_jkr_stt", Category = "Walls", Domain = "element", Shape = Shape.Ready,
                           FixAction = "rename_type", FixValue = JkrName, FixTarget = "type", ManualValue = JkrName, Actual = "jkrAR_wal-k_(ky) Dinding" },
            new RuleCase { Number = 18, Rule = "JKR code assigned", Category = "Walls", Domain = "element", Shape = Shape.Pick,
                           Options = new[] { "PKk", "PKg", "PKb" }, Actual = "(none)" },
            new RuleCase { Number = 19, Rule = "JKR code prefix matches category", Category = "Floors", Domain = "element", Shape = Shape.Ready,
                           FixAction = "set_jkr_code", FixParam = "Kod_Jenis_jkr_stt", FixValue = "LTj", FixTarget = "type", ManualValue = "LTj", Actual = "PKk" },
            new RuleCase { Number = 20, Rule = "Project Name populated", Category = "Project Information", Domain = "project_setup", Shape = Shape.ManualOnly,
                           Actual = "(empty)", Required = "Non-empty value from project brief" },
            new RuleCase { Number = 21, Rule = "LOD level meets phase requirements", Category = "Project Setup", Domain = "project_setup", Shape = Shape.Checklist,
                           Actual = "LOD 200", Required = "LOD 300" },
            new RuleCase { Number = 22, Rule = "BPEP exists", Category = "Project Setup", Domain = "project_setup", Shape = Shape.Checklist,
                           Actual = "Not confirmed", Required = "BPEP confirmed" },
            new RuleCase { Number = 23, Rule = "JKR BIM template used", Category = "Project Setup", Domain = "project_setup", Shape = Shape.Checklist,
                           Actual = "Architectural Template", Required = "JKR BIM template" },
            new RuleCase { Number = 24, Rule = "JKR shared parameters loaded", Category = "Project Setup", Domain = "project_setup", Shape = Shape.Checklist,
                           Actual = "Not loaded", Required = "JKR shared parameter file" },
            new RuleCase { Number = 25, Rule = "Project Base Point surveyed", Category = "Project Base Point", Domain = "project_setup", Shape = Shape.ManualOnly,
                           Actual = "E: 0.000  N: 0.000  Elev: 0.000", Required = "Survey coordinates per JUPEM reference" },
            new RuleCase { Number = 26, Rule = "Element naming adoption", Category = "Component Naming", Domain = "element", Shape = Shape.ChecklistFollows,
                           Actual = "66.7% (10/15)", Required = "100%" },
        };

        /// <summary>The rules whose failing row has no way to resolve it from the pane (rule number → why).
        /// Both are project-level checks the backend sends with element_id 0, no fix and no AI path, and the
        /// Project Checklist has no item for them: the row only shows "how to fix" text, Ignore and Mark as
        /// Manual. Delete an entry when the pane gives the rule a control; the test then asks you to.</summary>
        private static readonly Dictionary<int, string> KnownDeadEnds = new Dictionary<int, string>
        {
            [20] = "Project Information 'populated' row: element 0, no fix, not AI-fixable, not on the Project Checklist; only how-to-fix text, Ignore, Mark as Manual",
            [25] = "Project Base Point row: element 0, no fix, not AI-fixable, not on the Project Checklist; only how-to-fix text, Ignore, Mark as Manual",
        };

        // ── fixtures shaped like the real DTO ────────────────────────────────────────────────────

        private const long FirstElement = 301000;

        private static ComplianceIssueDto Dto(RuleCase r, int n, string status = "fail")
        {
            var project = r.Domain == "project_setup" || r.Shape == Shape.ChecklistFollows;
            var element = project ? 0 : FirstElement + r.Number * 10 + n;
            var dto = new ComplianceIssueDto
            {
                IssueId = JkrComplianceService.ComputeIssueId(r.Category, r.Rule, element),
                CheckId = project ? $"project.r{r.Number:00}" : $"element.r{r.Number:00}.{n}",
                Domain = r.Domain,
                ElementId = element,
                Category = r.Category,
                TypeName = project ? "" : r.Category + " type " + n,
                Status = status,
                Rule = r.Rule,
                Actual = r.Actual,
                ActualValue = r.Actual,
                RequiredValue = r.Required,
                Reason = $"Rule {r.Number} fails: {r.Rule}.",
                FixSuggestion = project ? "Fix it in the project settings." : "Fix it in Revit.",
                Confidence = "high",
                Locatable = element > 0,
                RevitCategory = project ? "" : r.Category,
                SpecDocNumber = "09",
                SpecSection = "5." + r.Number,
            };
            if (r.Shape == Shape.Ready)
            {
                dto.FixAction = r.FixAction;
                dto.FixParameterName = r.FixParam;
                dto.FixValue = r.FixValue;
                dto.FixOldValue = r.Actual;
                dto.FixTarget = r.FixTarget;
                dto.FixPriority = 10;
            }
            if (r.Shape == Shape.Pick)
            {
                dto.ValidOptions = r.Options.ToList();
                dto.AiSuggestedCode = n == 1 ? r.Options[0] : "";      // the AI picks for one element, not the other
                dto.AiSuggestionReason = n == 1 ? "closest to the element's material" : "";
                dto.RequiresUserChoice = true;
            }
            return dto;
        }

        private static ModelCheckResponse Scan(IEnumerable<ComplianceIssueDto> dtos)
        {
            var r = new ModelCheckResponse();
            foreach (var d in dtos) (d.ElementId == 0 ? r.BuildingRequirements : r.ElementIssues).Add(d);
            return r;
        }

        /// <summary>Every rule 1-26, two failing elements for the element rules (one for the project ones).</summary>
        private static List<ComplianceIssueDto> SeededScan()
        {
            var all = new List<ComplianceIssueDto>();
            foreach (var r in Rules)
            {
                all.Add(Dto(r, 1));
                if (r.Domain == "element" && r.Shape != Shape.ChecklistFollows) all.Add(Dto(r, 2));
            }
            // A check the backend could not judge: a person's review (AI-judged rows never end here).
            all.Add(Dto(Rules.Single(x => x.Number == 7), 3, "cannot_verify"));
            return all;
        }

        private static int NumberOf(IssueVm i) => Rules.First(r => r.Rule == i.RawRule).Number;

        // ── the pane's models over a list of issues ──────────────────────────────────────────────

        private sealed class Pane
        {
            public PanelCore Core;
            public ResultsScreenModel Results;
            public DetailScreenModel Detail;
            public ProjectChecklistModel Checklist => Results.ProjectChecklist;
            public IReadOnlyList<IssueVm> Issues;

            public static Pane Open(IReadOnlyList<IssueVm> issues)
            {
                var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
                var start = new StartScreenModel(core) { FileLabel = "jkrAR24.rvt" };
                core.SetLod("300");
                core.SetLang(PanelStrings.En);
                core.BeginExternalRun();
                core.FinishExternalRun();
                var results = new ResultsScreenModel(core, start);
                var p = new Pane { Core = core, Results = results, Issues = issues };
                p.Detail = new DetailScreenModel(core, results)
                {
                    RuleOf = id => p.Issues.FirstOrDefault(i => i.Id == id)?.RawRule ?? "",
                    CategoryOf = id => p.Issues.FirstOrDefault(i => i.Id == id)?.RevitCategory ?? "",
                    StepsOf = id => p.Issues.FirstOrDefault(i => i.Id == id)?.ManualFixSteps ?? new List<string>(),
                    IsLocatable = id => p.Issues.FirstOrDefault(i => i.Id == id)?.Locatable == true,
                };
                results.Load(issues, AuditSummary.Build(Enumerable.Empty<Bim010Cell>()));
                return p;
            }

            public DetailCard CardOf(string id)
            {
                Detail.Open(id);
                return Detail.Card;
            }

            public void Reload(IReadOnlyList<IssueVm> issues)
            {
                Issues = issues;
                Results.Load(issues, AuditSummary.Build(Enumerable.Empty<Bim010Cell>()));
            }
        }

        /// <summary>What a failing row offers, read from the detail card the pane would show.</summary>
        private sealed class Offer
        {
            public IssueVm Issue;
            public int Rule;
            public bool Fix, Pick, Fixer, Review, ChecklistStep, Steps, Locate, Ignore, MarkManual;
            /// <summary>A control that RESOLVES the row (not just sets it aside).</summary>
            public bool Resolves => Fix || Pick || Fixer || Review || ChecklistStep;
            /// <summary>Anything at all to click or read.</summary>
            public bool Any => Resolves || Steps || Locate || Ignore || MarkManual;
            public override string ToString() =>
                $"rule {Rule} {Issue.Title} [{Issue.Id}] fix={Fix} pick={Pick} aiFixer={Fixer} review={Review} checklist={ChecklistStep} steps={Steps} locate={Locate} ignore={Ignore} markManual={MarkManual}";
        }

        private static List<Offer> Offers(Pane pane, IEnumerable<IssueVm> issues)
        {
            var list = new List<Offer>();
            foreach (var i in issues)
            {
                var card = pane.CardOf(i.Id);
                Assert.NotNull(card);
                var item = pane.Core.Issues.Single(x => x.Id == i.Id);
                var number = NumberOf(i);
                var checklist = pane.Checklist.Items.FirstOrDefault(c => c.Rule == number);
                list.Add(new Offer
                {
                    Issue = i,
                    Rule = number,
                    Fix = card.CanFix,
                    Pick = card.IsFixerRow && item.Pick,
                    Fixer = card.IsFixerRow,
                    Review = card.IsManual,
                    // Rule 26's item has no button: it only tells the person what moves it.
                    ChecklistStep = checklist != null && checklist.HasAction && i.RevitElementId == 0,
                    Steps = card.Steps.Count > 0,
                    Locate = card.CanLocate,
                    Ignore = card.CanIgnore,
                    MarkManual = card.CanMarkManual,
                });
            }
            return list;
        }

        private static List<IssueVm> Map(IEnumerable<ComplianceIssueDto> dtos) => IssueMapper.MapAll(Scan(dtos));

        // ── 1. every failing row has a way out ───────────────────────────────────────────────────

        [Fact]
        public void The_seed_has_a_failing_row_for_every_rule_1_to_26_and_the_dtos_carry_the_new_fields()
        {
            var dtos = SeededScan();
            Assert.Equal(Enumerable.Range(1, 26), Rules.Select(r => r.Number));
            var issues = Map(dtos);
            foreach (var r in Rules)
                Assert.Contains(issues, i => i.RawRule == r.Rule && !i.NeedsPerson);

            // The new fields survive the mapper.
            var pick = issues.First(i => i.RawRule == "Kod_Jenis_jkr_stt value valid" && i.AiSuggestedCode.Length > 0);
            Assert.True(pick.IsPick);
            Assert.Equal(new[] { "DGb", "DGk", "DGp" }, pick.ValidOptions);
            Assert.Equal("DGb", pick.AiSuggestedCode);
            Assert.Equal("high", pick.Confidence);
            Assert.Contains(issues, i => i.NeedsPerson);   // the cannot_verify row
        }

        [Fact]
        public void No_failing_row_is_left_with_nothing_to_click()
        {
            var pane = Pane.Open(Map(SeededScan()));
            foreach (var o in Offers(pane, pane.Issues))
                Assert.True(o.Any, "a row with zero available actions: " + o);
        }

        [Fact]
        public void Every_failing_row_resolves_from_the_pane_except_the_pinned_dead_ends()
        {
            var pane = Pane.Open(Map(SeededScan()));
            var offers = Offers(pane, pane.Issues);
            foreach (var o in offers) _out.WriteLine(o.ToString());

            var stuck = offers.Where(o => !o.Resolves && o.Rule != 26).Select(o => o.Rule).Distinct().OrderBy(n => n).ToList();
            foreach (var n in stuck)
                _out.WriteLine($"DEAD END rule {n}: {(KnownDeadEnds.TryGetValue(n, out var why) ? why : "NOT PINNED")}");

            Assert.Equal(KnownDeadEnds.Keys.OrderBy(n => n), stuck);
        }

        [Fact]
        public void Each_shape_offers_the_control_it_is_meant_to()
        {
            var pane = Pane.Open(Map(SeededScan()));
            foreach (var o in Offers(pane, pane.Issues))
            {
                var r = Rules.First(x => x.Number == o.Rule);
                if (o.Issue.NeedsPerson) { Assert.True(o.Review, o.ToString()); continue; }
                switch (r.Shape)
                {
                    case Shape.Ready: Assert.True(o.Fix && !o.Pick, o.ToString()); break;
                    case Shape.Pick: Assert.True(o.Pick && o.Fixer && !o.Fix, o.ToString()); break;
                    case Shape.AiOnly: Assert.True(o.Fixer && !o.Pick && !o.Fix, o.ToString()); break;
                    case Shape.Checklist: Assert.True(o.ChecklistStep, o.ToString()); break;
                }
            }
        }

        [Fact]
        public void The_list_rows_agree_with_the_detail_cards()
        {
            var pane = Pane.Open(Map(SeededScan()));
            var offers = Offers(pane, pane.Issues).Where(o => !o.Issue.NeedsPerson).ToDictionary(o => o.Issue.Id);
            pane.Results.SelectTab(PanelTab.Open);
            foreach (var row in pane.Results.Rows)
            {
                var o = offers[row.Id];
                Assert.Equal(o.Fix, row.ShowFix);
                Assert.Equal(o.Fixer, row.IsAiFixer);
            }
        }

        [Fact]
        public void A_manual_value_each_element_rule_accepts_passes_the_rules_own_check()
        {
            // The AI-fixer row's manual override: for every row that has one, a legitimate value is accepted.
            foreach (var i in Map(SeededScan()).Where(x => x.AiFixCandidate && !x.IsPick))
            {
                var r = Rules.First(x => x.Number == NumberOf(i));
                var plan = JkrManualFix.Plan(i.RawRule, i.RevitCategory, r.ManualValue, i.ReferenceValues);
                Assert.True(plan.Ok, $"rule {r.Number}: {plan.Error}");
            }
            foreach (var i in Map(SeededScan()).Where(x => x.IsPick))
            {
                var plan = JkrManualFix.Plan(i.RawRule, i.RevitCategory, i.ValidOptions[0], i.ValidOptions);
                Assert.True(plan.Ok, plan.Error);
            }
        }

        // ── 2. Fix all -> pickers -> AI-fixer rows -> checklist ─────────────────────────────────

        /// <summary>The fake model and backend: which issue ids the model passes now. Writes add to it.</summary>
        private sealed class FakeModel
        {
            public readonly HashSet<string> Healed = new HashSet<string>();
            public readonly List<string> Writes = new List<string>();
            public readonly HashSet<string> WritesThatFail = new HashSet<string>();

            /// <summary>One write; true when it landed. The value is recorded, the row heals.</summary>
            public bool Write(IssueVm i)
            {
                Writes.Add($"{i.Id}:{i.FixAction}:{i.FixParameterName}={i.FixValue}");
                if (WritesThatFail.Contains(i.Id)) return false;
                Healed.Add(i.Id);
                return true;
            }

            /// <summary>The fresh scan after a write: a row per issue the model still fails.</summary>
            public List<IssueVm> Rescan(IEnumerable<ComplianceIssueDto> dtos) =>
                IssueMapper.MapAll(Scan(dtos.Where(d => !Healed.Contains(d.IssueId))));
        }

        /// <summary>The pane's write → re-check step for the rows it just wrote (JkrRecheck, AI-427).</summary>
        private static List<IssueVm> RecheckAfterWrite(List<IssueVm> current, List<IssueVm> written, FakeModel model, List<ComplianceIssueDto> dtos)
        {
            var fresh = model.Rescan(dtos);
            var outcome = JkrRecheck.Reconcile(current, written.Select(w => w.RevitElementId).ToList(), fresh, true, written);
            foreach (var v in outcome.Verified) { v.Status = IssueStatus.Fixed; v.FixVerified = true; v.FixError = ""; }
            foreach (var f in outcome.StillFailing) f.FixError = "Write did not stick";
            // Rows the re-check dropped (they pass now) leave the list; the rest stay as they were.
            return outcome.Issues;
        }

        private static bool IsEmptyRename(IssueVm i) =>
            string.Equals(i.FixAction, "rename_type", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(i.FixValue);

        private static bool OpenAi(IssueVm i) => i.IsAiOpen && i.Status == IssueStatus.Open;

        [Fact]
        public void Fix_all_then_pickers_then_AI_rows_then_the_checklist_leaves_no_row_stuck()
        {
            var dtos = SeededScan();
            var model = new FakeModel();
            var current = Map(dtos);

            // ── step A: Fix all: ready-made fixes only, one confirm, one write ──
            var fixAll = current.Where(i => OpenAi(i) && i.BulkFixable && !string.IsNullOrEmpty(i.FixAction) && !IsEmptyRename(i))
                                .OrderBy(i => i.FixPriority).ToList();
            Assert.NotEmpty(fixAll);
            Assert.DoesNotContain(fixAll, i => i.IsPick || i.AiFixCandidate);
            var fixConfirm = new JkrConfirmModel(JkrConfirmKind.Fix, fixAll.Select(JkrConfirmRow.FromIssue), fixAll.Count);
            Assert.True(fixConfirm.CanConfirm);
            Assert.False(fixConfirm.HasPick);
            Assert.True(fixConfirm.TryConfirm());
            foreach (var w in fixAll) Assert.True(model.Write(w));
            current = RecheckAfterWrite(current, fixAll, model, dtos);
            Assert.All(fixAll, w => Assert.True(w.Status == IssueStatus.Fixed && w.FixVerified));

            // ── step B: the pickers: one confirm carrying a picker per option set, each element its own write ──
            var picks = current.Where(i => OpenAi(i) && i.IsPick).ToList();
            Assert.NotEmpty(picks);
            var pickConfirm = new JkrConfirmModel(JkrConfirmKind.Fix, picks.Select(JkrConfirmRow.FromIssue), picks.Count);
            Assert.True(pickConfirm.HasPick);
            Assert.Equal(picks.Count, pickConfirm.PickCount);
            Assert.True(pickConfirm.CanConfirm);   // every picker starts on a valid code
            Assert.True(pickConfirm.TryConfirm());
            foreach (var pick in picks)
            {
                var chosen = pickConfirm.SelectedFor(pickConfirm.PickRows.First(r => r.IssueId == pick.Id));
                var plan = JkrManualFix.Plan(pick.RawRule, pick.RevitCategory, chosen, pick.ValidOptions);
                Assert.True(plan.Ok, plan.Error);
                pick.UseManualValue(plan);
                Assert.True(model.Write(pick));
                current = RecheckAfterWrite(current, new List<IssueVm> { pick }, model, dtos);
                Assert.Equal(IssueStatus.Fixed, pick.Status);
            }

            // ── step C: the AI-fixer rows: Ask AI, accept; or type a value yourself ──
            var aiRows = current.Where(i => OpenAi(i) && i.AiFixCandidate).ToList();
            Assert.NotEmpty(aiRows);
            int asked = 0;
            foreach (var row in aiRows)
            {
                var r = Rules.First(x => x.Number == NumberOf(row));
                var viaAi = asked++ % 2 == 0;   // the fixer answers for every other row; the rest take a typed value
                row.AiAsked = true;
                if (viaAi)
                {
                    row.AiProposal = new AiSuggestion { Action = row.RawRule.Contains("_jkr_") ? "set_parameter" : "rename_type",
                        ParameterName = JkrManualFix.ParameterOf(row.RawRule), Value = r.ManualValue, OldValue = row.Actual,
                        Target = "type", Reason = "closest valid value" };
                    var fixer = new JkrFixerRow(row.Id, row.RawRule, row.RevitCategory, FixerAiState.Suggested, row.AiProposal.Value, row.AiProposal.Reason, row.ReferenceValues);
                    Assert.True(fixer.CanAccept);
                    row.UseAiSuggestion();
                    Assert.True(row.FixProposedByAi);
                }
                else
                {
                    var fixer = new JkrFixerRow(row.Id, row.RawRule, row.RevitCategory, FixerAiState.NoSuggestion, options: row.ReferenceValues);
                    Assert.False(fixer.CanAccept);
                    fixer.ManualValue = r.ManualValue;
                    Assert.True(fixer.CanWriteManual, $"rule {r.Number}: {fixer.ManualError}");
                    row.UseManualValue(fixer.ManualPlan);
                }
                var confirm = new JkrConfirmModel(JkrConfirmKind.Fix, new[] { JkrConfirmRow.FromIssue(row) }, 1);
                Assert.True(confirm.CanConfirm);
                Assert.True(confirm.TryConfirm());
                Assert.True(model.Write(row));
                current = RecheckAfterWrite(current, new List<IssueVm> { row }, model, dtos);
                Assert.Equal(IssueStatus.Fixed, row.Status);
            }

            // ── step D: the rows that need a person: Mark as reviewed ──
            foreach (var h in current.Where(i => i.NeedsPerson)) h.Reviewed = true;

            // ── step E: the Project Checklist: act, then re-check; a pass is the only way an item flips ──
            var pane = Pane.Open(current);
            var cl = pane.Checklist;
            var requested = new List<int>();
            cl.ActionRequested += requested.Add;
            var focusedLod = false;
            cl.FocusLodRequested += () => focusedLod = true;
            var projectRows = current.Where(i => i.RevitElementId == 0 && !i.NeedsPerson).ToList();
            foreach (var rule in new[] { 1, 21, 22, 23, 24 })
            {
                Assert.True(projectRows.Any(i => NumberOf(i) == rule), $"rule {rule} row seeded");
                cl.ApplyRecheck(rule, new JkrProjectRecheckResponse
                {
                    RuleNumber = rule, Passed = false,
                    Checks = new List<JkrProjectRecheckCheck> { new JkrProjectRecheckCheck { Status = "fail", Reason = "still failing" } },
                });
                Assert.Equal(ProjectCheckState.Fail, cl.Item(rule).State);
                Assert.True(cl.Item(rule).ShowAction, $"rule {rule} shows its button while failing");
                cl.Act(rule);                                    // the person presses it: Save As / LOD box / Confirm BPEP / Load shared params
                cl.ApplyRecheck(rule, new JkrProjectRecheckResponse { RuleNumber = rule, Passed = true,
                    Checks = new List<JkrProjectRecheckCheck> { new JkrProjectRecheckCheck { Status = "pass" } } });
                Assert.Equal(ProjectCheckState.Pass, cl.Item(rule).State);
                projectRows.RemoveAll(i => NumberOf(i) == rule);   // the next scan no longer returns it
            }
            Assert.True(focusedLod);
            Assert.Equal(new[] { 1, 22, 23, 24 }, requested.OrderBy(n => n));

            // Rule 26: red while elements are unnamed, ticks when the element rows above have all resolved.
            cl.ApplyNamingAdoption("66.7% (10/15)");
            Assert.Equal(ProjectCheckState.Fail, cl.Item(26).State);
            Assert.False(cl.Item(26).HasAction);
            var namingRowsLeft = current.Count(i => OpenAi(i) && i.RevitElementId > 0 && new[] { 2, 3, 4, 5, 6, 7, 8, 16, 17 }.Contains(NumberOf(i)));
            Assert.Equal(0, namingRowsLeft);                       // everything that raises naming adoption is done
            cl.ApplyNamingAdoption("100% (15/15)");
            Assert.Equal(ProjectCheckState.Pass, cl.Item(26).State);
            projectRows.RemoveAll(i => NumberOf(i) == 26);

            // ── the end state: nothing red without a control, except the pinned dead ends ──
            Assert.Empty(current.Where(i => i.RevitElementId > 0 && OpenAi(i)));          // every element row resolved
            Assert.All(current.Where(i => i.NeedsPerson), i => Assert.True(i.Reviewed));  // every review ticked
            var left = current.Where(i => OpenAi(i) && i.RevitElementId == 0 && NumberOf(i) != 26 && !new[] { 1, 21, 22, 23, 24 }.Contains(NumberOf(i)))
                              .Select(NumberOf).Distinct().OrderBy(n => n).ToList();
            foreach (var n in left) _out.WriteLine($"still red after the whole sequence, rule {n}: {KnownDeadEnds[n]}");
            Assert.Equal(KnownDeadEnds.Keys.OrderBy(n => n), left);
            Assert.True(model.Writes.Count >= fixAll.Count + picks.Count + aiRows.Count);
        }

        [Fact]
        public void A_write_that_does_not_stick_leaves_the_row_open_with_the_reason_and_a_way_to_retry()
        {
            var dtos = SeededScan();
            var model = new FakeModel();
            var current = Map(dtos);
            var victim = current.First(i => OpenAi(i) && i.BulkFixable);
            model.WritesThatFail.Add(victim.Id);

            Assert.False(model.Write(victim));
            current = RecheckAfterWrite(current, new List<IssueVm> { victim }, model, dtos);
            Assert.Equal(IssueStatus.Open, victim.Status);
            Assert.True(victim.HasFixError);

            // The real pane blocklists the failed combination, so the next scan returns the row WITHOUT a ready-made fix.
            // It must still be a row with a control: the AI-fixer row (it has an element and a check id).
            var scope = "ai437-" + Guid.NewGuid().ToString("N");
            var before = IssueMapper.BlockScope;
            lock (typeof(IssueMapper))
            {
                IssueMapper.BlockScope = scope;
                try
                {
                    IssueMapper.BlockFix(victim.RevitElementId, victim.FixAction, victim.FixParameterName, "Revit refused the write");
                    var again = Map(dtos).Single(i => i.Id == victim.Id);
                    Assert.False(again.AutoFixable);
                    Assert.Equal("Revit refused the write", again.FixError);
                    Assert.True(again.AiFixCandidate);
                    var pane = Pane.Open(new List<IssueVm> { again });
                    var offer = Offers(pane, pane.Issues).Single();
                    Assert.True(offer.Fixer && offer.Resolves, offer.ToString());
                }
                finally { IssueMapper.ClearBlocklist(); IssueMapper.BlockScope = before; }
            }
        }

        [Fact]
        public void The_project_checklist_has_a_step_for_every_project_rule_with_a_button_and_rule_26_says_what_moves_it()
        {
            var cl = new ProjectChecklistModel();
            foreach (var rule in new[] { 1, 21, 22, 23, 24 })
            {
                Assert.True(cl.Item(rule).HasAction, $"rule {rule}");
                Assert.True(cl.Item(rule).CanRecheck, $"rule {rule}");
            }
            Assert.False(cl.Item(26).HasAction);
            Assert.NotEmpty(cl.Item(26).Guidance);
        }
    }
}
