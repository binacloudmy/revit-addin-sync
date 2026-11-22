using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-509 (Revit- and WPF-free, runs on the Mac harness): "Fix all elements via Copilot" on a check header.
    /// Eligibility (7 checks only), the count label, one request for all rows, chunking into parts, one confirm for every part,
    /// the one re-check after the last part, rows Fixed only if the rule re-passes, the check flipping to Pass and the headline %
    /// rising without a rescan, the tab counts, and Undo reopening every row of the pack.
    /// The IssueVm half (facts of rows, the adapter) is in JkrCopilotFixCheckRowTests (WPF, Windows CI).
    /// </summary>
    [Collection("ForcedConfirmStatic")]
    public class JkrCopilotFixCheckTests
    {
        private static readonly string[] EightChecks = { "Da.1", "Db.1", "Db.2", "Dc.1", "Dd.1", "A5.2", "C1.6.3", "C4.2.2" };

        // ── fixture: a failing spec check with N element rows, as the pane holds them ──

        private static SpecResult FailResult(string check, int n, string issueId)
        {
            var r = SpecChecks.Fail(check, "bad", 1000 + n, "Doors · T" + n);
            r.Source = SpecReport.Model;
            r.IssueId = issueId;
            return r;
        }

        private static SpecResult PassResult(string check) { var r = SpecChecks.Pass(check, "ok"); r.Source = SpecReport.Model; return r; }

        private static PanelItem Item(string id, string check, long elementId, bool copilot = true, Action<PanelItem> tweak = null)
        {
            var x = new PanelItem
            {
                Id = id, Row = check, Sec = "D", Sev = "Med", V = Verify.Ai, Title = Text.Same(id), El = "Doors · T" + elementId,
                CopilotFixable = copilot, ElementIds = new[] { elementId }, Status = ItemStatus.Open,
            };
            tweak?.Invoke(x);
            return x;
        }

        private sealed class Board
        {
            public string Check;
            public List<PanelItem> Items;
            public SpecReport Spec;
            public PanelCore Core;
        }

        /// <summary>The scan as the pane shows it: the check fails on n elements (a row each); every other AI check passes.</summary>
        private static Board Scan(string check, int n, Action<PanelItem> tweak = null)
        {
            var items = Enumerable.Range(1, n).Select(i => Item("r" + i, check, 1000 + i, true, tweak)).ToList();
            var results = new List<SpecResult>();
            foreach (var other in AiChecksExcept(check)) results.Add(PassResult(other));
            for (var i = 1; i <= n; i++) results.Add(FailResult(check, i, "r" + i));
            return Show(check, items, SpecReport.Build(results));
        }

        private static List<string> AiChecksExcept(string check) =>
            SpecCatalog.Checks.Where(c => c.Class == SpecCellClass.Ai && c.Id != check).Select(c => c.Id).ToList();

        private static Board Show(string check, List<PanelItem> items, SpecReport spec)
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            core.Replace(items, null);
            return new Board { Check = check, Items = items, Spec = spec, Core = core };
        }

        private static void ShowAgain(Board b)
        {
            b.Core.Replace(b.Items, null);
        }

        private static ChecklistCheck Header(Board b) => SpecChecklist.CheckRow(b.Spec.For(b.Check), b.Spec, b.Items.ToDictionary(i => i.Id), _ => true);

        // ── eligibility, label ──

        [Fact]
        public void The_header_offers_it_for_the_eight_checks_only()
        {
            foreach (var id in SpecCatalog.Checks.Select(c => c.Id))
            {
                var items = new List<PanelItem> { Item("a", id, 1), Item("b", id, 2) };
                var spec = SpecReport.Build(new[] { FailResult(id, 1, "a"), FailResult(id, 2, "b") });
                var row = SpecChecklist.CheckRow(spec.For(id), spec, items.ToDictionary(i => i.Id), _ => true);
                Assert.Equal(EightChecks.Contains(id), row.CanFixViaCopilot);
                if (!row.CanFixViaCopilot) Assert.Empty(row.CopilotFixIds);
            }
        }

        [Fact]
        public void Human_Fix_Pick_manual_ignored_and_not_ready_rows_are_left_out_and_with_none_left_there_is_no_button()
        {
            var items = new List<PanelItem>
            {
                Item("ok1", "Da.1", 1), Item("ok2", "Da.1", 2),
                Item("human", "Da.1", 3, true, x => x.V = Verify.Human),
                Item("fix", "Da.1", 4, true, x => x.Fix = FixKind.Fix),
                Item("pick", "Da.1", 5, true, x => x.Pick = true),
                Item("manual", "Da.1", 6, true, x => x.MarkedManual = true),
                Item("ignored", "Da.1", 7, true, x => x.Status = ItemStatus.Ignored),
                Item("notready", "Da.1", 8, false),     // CopilotFixable false: no launcher, no ids, a wall material ...
            };
            var spec = SpecReport.Build(items.Select((x, i) => FailResult("Da.1", i + 1, x.Id)).ToList());
            var row = SpecChecklist.CheckRow(spec.For("Da.1"), spec, items.ToDictionary(i => i.Id), _ => true);
            Assert.Equal(new[] { "ok1", "ok2" }, row.CopilotFixIds);
            Assert.Equal(2, row.CopilotFixElements);
            Assert.Equal("Fix all 2 elements via Copilot", row.CopilotFixLabel);

            foreach (var x in items) x.CopilotFixable = false;
            row = SpecChecklist.CheckRow(spec.For("Da.1"), spec, items.ToDictionary(i => i.Id), _ => true);
            Assert.False(row.CanFixViaCopilot);
            Assert.Equal("", row.CopilotFixLabel);
        }

        [Fact]
        public void The_label_counts_elements_in_both_languages_whether_they_sit_in_many_rows_or_one()
        {
            var b = Scan("Da.1", 46);
            var h = Header(b);
            Assert.True(h.CanFixViaCopilot);
            Assert.Equal(46, h.CopilotFixElements);
            Assert.Equal("Fix all 46 elements via Copilot", h.CopilotFixLabel);
            Assert.Equal("Fix all 1 element via Copilot", JkrCopilotFix.FixAllLabel(1));

            // one row that carries 46 ids (a spec row can cover 46 doors)
            var one = Item("only", "Da.1", 1, true, x => x.ElementIds = Enumerable.Range(1, 46).Select(i => (long)i).ToArray());
            var spec = SpecReport.Build(new[] { FailResult("Da.1", 1, "only") });
            var row = SpecChecklist.CheckRow(spec.For("Da.1"), spec, new Dictionary<string, PanelItem> { ["only"] = one }, _ => true);
            Assert.Equal(new[] { "only" }, row.CopilotFixIds);
        }

        [Fact]
        public void Fix_all_deterministic_keeps_skipping_these_rows_and_the_row_level_button_is_unchanged()
        {
            var b = Scan("Da.1", 5);
            Assert.Empty(b.Core.FixableOpen());
            Assert.All(b.Core.Issues, x => Assert.True(x.CopilotFixable));
            Assert.Empty(Header(b).FixIds);          // the deterministic "Fix n" does not take them
            Assert.False(Header(b).CanFix);
        }

        // ── the request, the prompt parts, the gate ──

        private static CopilotPromptFacts RowFacts(string check, int from, int count, string cat = "Doors", string required = "JKR code")
        {
            var ids = Enumerable.Range(from, count).Select(i => 5000L + i).ToList();
            return new CopilotPromptFacts { CheckId = check, Category = cat, Required = required, Actual = "x", ElementIds = ids, ElementLabels = ids.ToDictionary(i => i, i => "Doors · T" + i),
                Renames = check == "Dd.1" ? ids.ToDictionary(i => i, i => new CopilotRename { Old = "Door T" + i, New = "Window T" + i, Source = "the type name's first word" }) : new Dictionary<long, CopilotRename>() };
        }

        [Fact]
        public void The_request_of_a_check_carries_every_row_and_the_union_of_their_ids()
        {
            var r = CopilotFixRequest.ForCheck("Db.2", new[] { "a", "b", "a", "" }, new long[] { 3, 1, 3, 0, 2 }, "Title");
            Assert.True(r.IsCheckWide);
            Assert.Equal(new[] { "a", "b" }, r.IssueIds);
            Assert.Equal(new[] { "a", "b" }, r.Rows);
            Assert.Equal("a", r.IssueId);
            Assert.Equal(new long[] { 3, 1, 2 }, r.ElementIds);
            var single = new CopilotFixRequest("x", "Da.1", new long[] { 1 }, "t");
            Assert.False(single.IsCheckWide);
            Assert.Equal(new[] { "x" }, single.Rows);
        }

        [Fact]
        public void Rows_merge_into_one_set_of_facts_ids_distinct_and_in_order()
        {
            var merged = JkrCopilotPrompt.MergeFacts(new[] { RowFacts("Db.2", 1, 3, "Doors"), RowFacts("Db.2", 3, 3, "Windows") });
            Assert.Equal(new long[] { 5001, 5002, 5003, 5004, 5005 }, merged.ElementIds);
            Assert.Equal("Doors, Windows", merged.Category);
            Assert.Equal("JKR code", merged.Required);                       // the same on every row: kept
            Assert.Equal("Doors · T5004", merged.ElementLabels[5004]);
            var differs = JkrCopilotPrompt.MergeFacts(new[] { RowFacts("Db.2", 1, 1, required: "A"), RowFacts("Db.2", 2, 1, required: "B") });
            Assert.Equal("", differs.Required);                                // differing rows never quote one row for all
            Assert.Throws<ArgumentException>(() => JkrCopilotPrompt.MergeFacts(new CopilotPromptFacts[0]));
        }

        [Fact]
        public void A_hundred_and_twenty_ids_become_three_parts_with_disjoint_complete_confirmed_write_ids()
        {
            // 120 elements in many rows: Db.2 is an instance-level family, so it is chunked 50 / 50 / 20
            var rows = Enumerable.Range(0, 12).Select(k => RowFacts("Db.2", k * 10 + 1, 10)).ToList();
            var merged = JkrCopilotPrompt.MergeFacts(rows);
            Assert.Equal(120, merged.ElementIds.Count);
            {
                var parts = JkrCopilotPrompt.Build(merged);
                Assert.Equal(3, parts.Count);
                Assert.Contains("(part 1 of 3)", parts[0]);
                Assert.Contains("(part 3 of 3)", parts[2]);
            }
            var perPart = Enumerable.Range(0, 3).Select(p => JkrCopilotConfirmedWrite.ForPart(merged, p).Ids).ToList();
            Assert.Equal(new[] { 50, 50, 20 }, perPart.Select(x => x.Count));
            Assert.Equal(merged.ElementIds.OrderBy(x => x), perPart.SelectMany(x => x).OrderBy(x => x));
            Assert.Equal(120, perPart.SelectMany(x => x).Distinct().Count());
        }

        [Fact]
        public void A_type_level_check_is_one_prompt_with_one_id_per_type_whatever_the_row_count()
        {
            var ids = Enumerable.Range(1, 120).Select(i => 7000L + i).ToList();
            var facts = new CopilotPromptFacts { CheckId = "Da.1", Category = "Doors", ElementIds = ids, ElementLabels = ids.ToDictionary(i => i, i => "Doors · Type" + (i % 4)) };
            var parts = JkrCopilotPrompt.Build(facts);
            Assert.Single(parts);
            Assert.Equal(4, JkrCopilotPrompt.TargetIds(facts).Count);
        }

        [Theory]
        [InlineData("Da.1")] [InlineData("Db.1")] [InlineData("Db.2")] [InlineData("Dc.1")] [InlineData("Dd.1")] [InlineData("A5.2")] [InlineData("C1.6.3")] [InlineData("C4.2.2")]
        public void The_check_wide_prompt_passes_the_gate_engine_tools_only_confirmed_already_no_pass_fail_decision(string check)
        {
            var merged = JkrCopilotPrompt.MergeFacts(new[] { RowFacts(check, 1, 30), RowFacts(check, 31, 30) });
                foreach (var p in JkrCopilotPrompt.Build(merged))
                {
                    foreach (var old in new[] { "get_user_input", "create_views_for_levels", "rename_elements", "change_type", "dry_run", "set_parameter_by_filter", "fill_missing_parameter", "create_schedule" })
                        Assert.DoesNotContain(old, p);
                    Assert.False(Regex.IsMatch(p, @"\bset_parameter\b"), "old singular tool name");
                    Assert.Contains("set_parameters", p);
                    Assert.Contains("I already confirmed this change", p);
                    Assert.Contains("Do not decide pass or fail", p);
                    Assert.Contains("never use a filter", p);
                    Assert.Contains("Scope: change only", p);
                }
        }

        [Fact]
        public void The_confirm_names_the_check_the_total_and_that_it_is_one_confirmation_for_all_parts()
        {
            var merged = JkrCopilotPrompt.MergeFacts(Enumerable.Range(0, 12).Select(k => RowFacts("Db.2", k * 10 + 1, 10)).ToList());
            var en = JkrCopilotConfirm.Build(merged);
            Assert.Equal("Let Copilot change 120 elements for check Db.2?", en.Heading);
            Assert.Contains("Sent in 3 parts, with this one confirmation", en.Body);
            Assert.Contains("... and 70 more", en.Details);
        }

        // ── the launcher: one confirm, one chat, parts in order, one re-check ──

        private sealed class Session : ICopilotSession
        {
            public readonly List<string> Log = new List<string>();
            private int _sends, _n;
            public Task<string> OpenPaneAsync() { Log.Add("open"); return Task.FromResult<string>(null); }
            public void NewChat() => Log.Add("new");
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsPaneVisible => true;
            public bool IsSending => _sends > 0 && _n++ < 2;
            public CopilotCardState Cards => _sends > 0 && _n >= 2 ? CopilotCardState.Approved : CopilotCardState.None;
            public void Send(string text) { Log.Add("send"); _sends++; _n = 0; }
        }

        private static (JkrCopilotFixLauncher l, Session s, List<string> log) Launcher(string check, CopilotPromptFacts facts, Func<bool> confirm)
        {
            var s = new Session();
            var log = new List<string>();
            var l = new JkrCopilotFixLauncher(s, req => JkrCopilotPrompt.Build(facts), log.Add, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(5), settle: TimeSpan.FromMilliseconds(500),
                afterWrite: req => { log.Add("recheck"); return Task.CompletedTask; },
                confirm: req => { log.Add("confirm"); return confirm(); },
                confirmedWrite: (req, part) => { log.Add("cw" + part); return JkrCopilotConfirmedWrite.ForPart(facts, part); });
            return (l, s, log);
        }

        [Fact]
        public async Task One_confirm_one_new_chat_three_parts_in_order_and_one_recheck_after_the_last()
        {
            var merged = JkrCopilotPrompt.MergeFacts(Enumerable.Range(0, 12).Select(k => RowFacts("Db.2", k * 10 + 1, 10)).ToList());
            var (l, s, log) = Launcher("Db.2", merged, () => true);
            var req = CopilotFixRequest.ForCheck("Db.2", Enumerable.Range(0, 12).Select(k => "r" + k), merged.ElementIds, "t");
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(req));
            Assert.Equal(1, log.Count(x => x == "confirm"));
            Assert.Equal(new[] { "open", "new", "send", "send", "send" }, s.Log);
            Assert.Equal(1, log.Count(x => x == "recheck"));
            Assert.Equal("recheck", log.Last());
            Assert.Equal(new[] { "confirm", "cw0", "cw1", "cw2", "recheck" }, log);   // confirmed_write per part, no second confirm
        }

        [Fact]
        public async Task Declined_changes_nothing_no_chat_no_send_no_recheck()
        {
            var merged = JkrCopilotPrompt.MergeFacts(new[] { RowFacts("Db.2", 1, 60) });
            var (l, s, log) = Launcher("Db.2", merged, () => false);
            var req = CopilotFixRequest.ForCheck("Db.2", new[] { "r0" }, merged.ElementIds, "t");
            Assert.Equal(CopilotLaunchOutcome.NotConfirmed, await l.LaunchAsync(req));
            Assert.Empty(s.Log);
            Assert.Equal(new[] { "confirm" }, log);
            Assert.False(ConfirmedWriteSlot.IsArmed);
        }

        // ── the re-check: rows Fixed only if the rule re-passes; one re-check; ledger gets every row ──

        private sealed class Row : ICopilotRecheckRow, IRowElementIds
        {
            public string Id { get; set; }
            public long RevitElementId { get; set; }
            public IReadOnlyList<long> ElementIds { get; set; }
            public string Title { get; set; } = "t";
            public string Required { get; set; } = "D-<code>";
            public string FixedVia { get; set; } = "";
            public bool Fixed;
            public bool FixVerified { get; set; }
            public string Error = "";
            public RecheckState RecheckState => Fixed ? RecheckState.Fixed : RecheckState.Open;
        }

        private sealed class Fake
        {
            public List<Row> Rows;
            public HashSet<string> Failing = new HashSet<string>();
            public bool CannotCheck;
            public int Rechecks, ManyCalls, SingleCalls;
            public IReadOnlyCollection<long> LastIds;
            public readonly List<string> Toasts = new List<string>();
            public readonly List<(CopilotFixRequest, Row)> Remembered = new List<(CopilotFixRequest, Row)>();
            public JkrCopilotRecheckHost<Row> Host() => new JkrCopilotRecheckHost<Row>
            {
                Issue = id => Rows.FirstOrDefault(r => r.Id == id),
                Recheck = (ids, written) =>
                {
                    Rechecks++; LastIds = ids;
                    if (CannotCheck) return Task.FromResult<JkrRecheckOutcome<Row>>(null);
                    var o = new JkrRecheckOutcome<Row>();
                    foreach (var r in written) (Failing.Contains(r.Id) ? o.StillFailing : o.Verified).Add(r);
                    return Task.FromResult(o);
                },
                MarkFixed = (r, t) => { SingleCalls++; r.Fixed = true; r.FixedVia = "Copilot"; },
                MarkFixedMany = (rs, t) => { ManyCalls++; foreach (var r in rs) { r.Fixed = true; r.FixedVia = "Copilot"; } Toasts.Add(t); },
                MarkStillFailing = (rs, reason) => { foreach (var r in rs) r.Error = reason; },
                Remember = (req, r) => Remembered.Add((req, r)),
                Toast = Toasts.Add,
            };
        }

        private static Fake Pack(int n) => new Fake { Rows = Enumerable.Range(1, n).Select(i => new Row { Id = "r" + i, RevitElementId = 1000 + i, ElementIds = new long[] { 1000 + i } }).ToList() };
        private static CopilotFixRequest PackRequest(Fake f) => CopilotFixRequest.ForCheck("Da.1", f.Rows.Select(r => r.Id), f.Rows.SelectMany(r => r.ElementIds), "t");

        [Fact]
        public async Task All_rows_re_pass_all_are_Fixed_in_one_step_after_one_re_check_and_each_is_remembered()
        {
            var f = Pack(46);
            var re = new JkrCopilotRecheck<Row>(f.Host());
            Assert.Equal(CopilotRecheckResult.Fixed, await re.RunAsync(PackRequest(f)));
            Assert.Equal(1, f.Rechecks);
            Assert.Equal(46, f.LastIds.Count);
            Assert.Equal((1, 0), (f.ManyCalls, f.SingleCalls));
            Assert.All(f.Rows, r => { Assert.True(r.Fixed); Assert.True(r.FixVerified); Assert.Equal("Copilot", r.FixedVia); });
            Assert.Equal(46, f.Remembered.Select(x => x.Item2.Id).Distinct().Count());   // the ledger has every row of the pack
            Assert.Equal(new long[] { 1001 }, f.Remembered[0].Item1.ElementIds);          // ... each with its own ids
            Assert.Single(f.Toasts);
            Assert.Equal("Fixed via Copilot (46 rows re-passed the check)", f.Toasts[0]);
        }

        [Fact]
        public async Task A_failed_element_stays_Open_with_the_reason_and_the_others_are_Fixed()
        {
            var f = Pack(10); f.Failing.Add("r4");
            var re = new JkrCopilotRecheck<Row>(f.Host());
            Assert.Equal(CopilotRecheckResult.Fixed, await re.RunAsync(PackRequest(f)));
            var bad = f.Rows.Single(r => r.Id == "r4");
            Assert.False(bad.Fixed);
            Assert.Contains("still fails for this element", bad.Error);
            Assert.Contains("D-<code>", bad.Error);
            Assert.Equal(9, f.Rows.Count(r => r.Fixed));
            Assert.Equal(9, f.Remembered.Count);
            Assert.DoesNotContain(f.Remembered, x => x.Item2.Id == "r4");
            Assert.Contains("1 still Open", f.Toasts.Single());
            Assert.Equal(1, f.Rechecks);
        }

        [Fact]
        public async Task Nothing_re_passes_nothing_is_marked_and_the_toast_says_so()
        {
            var f = Pack(5); foreach (var r in f.Rows) f.Failing.Add(r.Id);
            Assert.Equal(CopilotRecheckResult.StillFailing, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(PackRequest(f)));
            Assert.Equal(0, f.ManyCalls);
            Assert.Empty(f.Remembered);
            Assert.All(f.Rows, r => Assert.False(r.Fixed));
            Assert.Contains("still fails for 5 rows", Assert.Single(f.Toasts));
        }

        [Fact]
        public async Task A_re_check_that_could_not_run_leaves_every_row_Open_and_says_press_Re_scan()
        {
            var f = Pack(5); f.CannotCheck = true;
            Assert.Equal(CopilotRecheckResult.NotChecked, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(PackRequest(f)));
            Assert.All(f.Rows, r => Assert.False(r.Fixed));
            Assert.Empty(f.Remembered);
            Assert.Contains("Re-scan", Assert.Single(f.Toasts));
        }

        [Fact]
        public async Task A_row_that_left_the_list_is_skipped_and_a_pack_with_no_row_left_is_RowGone()
        {
            var f = Pack(3);
            var req = CopilotFixRequest.ForCheck("Da.1", new[] { "r1", "gone", "r2" }, new long[] { 1001, 1002 }, "t");
            Assert.Equal(CopilotRecheckResult.Fixed, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(req));
            Assert.Equal(2, f.Rows.Count(r => r.Fixed));
            var none = CopilotFixRequest.ForCheck("Da.1", new[] { "x" }, new long[] { 1 }, "t");
            Assert.Equal(CopilotRecheckResult.RowGone, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(none));
        }

        [Fact]
        public async Task The_row_level_request_keeps_its_old_path()
        {
            var f = Pack(1);
            var single = new CopilotFixRequest("r1", "Da.1", new long[] { 1001 }, "t");
            Assert.Equal(CopilotRecheckResult.Fixed, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(single));
            Assert.Equal((0, 1), (f.ManyCalls, f.SingleCalls));
        }

        // ── a pack that stops early: the parts that did commit are still re-checked ──

        private sealed class Scripted : ICopilotSession
        {
            private readonly Func<int, CopilotCardState> _end;   // part index -> card state when its turn ends
            private int _sends, _n;
            public bool Visible = true;
            public int Sent => _sends;
            public Scripted(Func<int, CopilotCardState> end) { _end = end; }
            public Task<string> OpenPaneAsync() => Task.FromResult<string>(null);
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsPaneVisible => Visible;
            public bool IsSending => _sends > 0 && _n++ < 2;
            public CopilotCardState Cards => _sends > 0 && _n >= 2 ? _end(_sends - 1) : CopilotCardState.None;
            public void Send(string text) { _sends++; _n = 0; }
        }

        private static async Task<(CopilotLaunchOutcome o, Fake f, List<string> toasts, Scripted s)> Pack3(Func<int, CopilotCardState> end, Action<Scripted> tweak = null, bool startGraceOnly = false)
        {
            // 120 ids in 12 rows of 10: parts of 50 hold rows 1-5, 6-10 (id 51-100) and 11-12
            var merged = JkrCopilotPrompt.MergeFacts(Enumerable.Range(0, 12).Select(k => RowFacts("Db.2", k * 10 + 1, 10)).ToList());
            var f = new Fake { Rows = Enumerable.Range(0, 12).Select(k => new Row { Id = "r" + k, RevitElementId = 5001 + k * 10, ElementIds = Enumerable.Range(k * 10 + 1, 10).Select(i => 5000L + i).ToList() }).ToList() };
            var s = new Scripted(end); tweak?.Invoke(s);
            var toasts = new List<string>();
            var re = new JkrCopilotRecheck<Row>(f.Host());
            var req = CopilotFixRequest.ForCheck("Db.2", f.Rows.Select(r => r.Id), merged.ElementIds, "t");
            // the rows whose ids lie in parts 2 and 3 were never written: the rule still fails on them
            foreach (var r in f.Rows.Skip(5)) f.Failing.Add(r.Id);
            var l = new JkrCopilotFixLauncher(s, _ => JkrCopilotPrompt.Build(merged), toasts.Add, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(3), startGrace: TimeSpan.FromMilliseconds(500), settle: TimeSpan.FromMilliseconds(500),
                afterWrite: re.RunAsync, confirm: _ => true);
            var o = await l.LaunchAsync(req);
            return (o, f, toasts, s);
        }

        [Fact]
        public async Task Part_two_declined_part_one_was_written_so_the_pack_is_re_checked_once()
        {
            var (o, f, toasts, s) = await Pack3(p => p == 0 ? CopilotCardState.Approved : CopilotCardState.Declined);
            Assert.Equal(CopilotLaunchOutcome.Declined, o);
            Assert.Equal(2, s.Sent);                                           // part 3 never sent
            Assert.Equal(1, f.Rechecks);
            Assert.Equal(5, f.Rows.Count(r => r.Fixed));                       // part 1's rows are Fixed ...
            Assert.All(f.Rows.Skip(5), r => { Assert.False(r.Fixed); Assert.Contains("still fails", r.Error); });   // ... the others stay Open with a reason
            Assert.Contains(toasts, t => t.Contains("The rows that were fixed are re-checked."));
            Assert.Contains(toasts, t => t.Contains("You answered No on part 2"));
        }

        [Fact]
        public async Task Part_one_declined_nothing_was_written_so_there_is_no_re_check()
        {
            var (o, f, toasts, s) = await Pack3(p => CopilotCardState.Declined);
            Assert.Equal(CopilotLaunchOutcome.Declined, o);
            Assert.Equal(1, s.Sent);
            Assert.Equal(0, f.Rechecks);
            Assert.All(f.Rows, r => Assert.False(r.Fixed));
            Assert.DoesNotContain(toasts, t => t.Contains("re-checked"));
        }

        [Fact]
        public async Task Part_two_ends_with_no_write_the_earlier_part_is_re_checked()
        {
            var (o, f, toasts, _) = await Pack3(p => p == 0 ? CopilotCardState.Approved : CopilotCardState.None);
            // a part with no card after a committed one means "not needed": it stops, re-checks, and reports Sent
            Assert.Equal(1, f.Rechecks);
            Assert.Equal(5, f.Rows.Count(r => r.Fixed));
        }

        [Fact]
        public async Task A_turn_timeout_on_part_two_re_checks_part_one()
        {
            var (o, f, toasts, _) = await Pack3(p => p == 0 ? CopilotCardState.Approved : CopilotCardState.Open);
            Assert.Equal(CopilotLaunchOutcome.TurnTimeout, o);
            Assert.Equal(1, f.Rechecks);
            Assert.Equal(5, f.Rows.Count(r => r.Fixed));
            Assert.Contains(toasts, t => t.Contains("re-checked"));
        }

        [Fact]
        public async Task The_pane_closed_after_part_one_re_checks_part_one_and_with_no_part_written_it_does_not()
        {
            var merged = JkrCopilotPrompt.MergeFacts(Enumerable.Range(0, 12).Select(k => RowFacts("Db.2", k * 10 + 1, 10)).ToList());
            var closes = await Pack3(p => CopilotCardState.Approved, s => s.Visible = true);
            Assert.Equal(CopilotLaunchOutcome.Sent, closes.o);   // control: the full pack re-checks once
            Assert.Equal(1, closes.f.Rechecks);

            var f = new Fake { Rows = Enumerable.Range(0, 12).Select(k => new Row { Id = "r" + k, RevitElementId = 5001 + k * 10, ElementIds = new long[] { 5001 + k * 10 } }).ToList() };
            var s2 = new Scripted(p => CopilotCardState.Approved);
            var l = new JkrCopilotFixLauncher(s2, _ => JkrCopilotPrompt.Build(merged), _ => { }, _ => { s2.Visible = false; return Task.CompletedTask; },
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(3), settle: TimeSpan.FromMilliseconds(500),
                afterWrite: new JkrCopilotRecheck<Row>(f.Host()).RunAsync, confirm: _ => true);
            var o = await l.LaunchAsync(CopilotFixRequest.ForCheck("Db.2", f.Rows.Select(r => r.Id), merged.ElementIds, "t"));
            Assert.Equal(CopilotLaunchOutcome.PaneClosed, o);
            Assert.Equal(1, f.Rechecks);                                      // part 1 committed before the pane closed
        }

        [Fact]
        public void The_header_tooltip_warns_that_a_type_rename_changes_all_instances_in_both_languages()
        {
            Assert.Contains("Da.1 / Dc.1 / Dd.1: a type rename changes all instances", JkrCopilotFix.FixAllTooltip());
        }

        // ── Undo: every row of the pack reopens ──

        [Fact]
        public async Task Undo_reopens_every_row_of_the_pack_the_ledger_holds_them_all()
        {
            var f = Pack(46);
            var undo = new JkrCopilotUndo<Row>(new JkrCopilotUndoHost<Row>
            {
                Issue = id => f.Rows.FirstOrDefault(r => r.Id == id),
                RowElementIds = () => new HashSet<long>(f.Rows.Select(r => r.RevitElementId)),
                Recheck = ids =>
                {
                    // the Undo put the old values back: the rule fails again on every element, so every Copilot-fixed row reopens
                    var o = new JkrRecheckOutcome<Row>();
                    foreach (var r in f.Rows.Where(r => ids.Contains(r.RevitElementId) && r.Fixed)) { r.Fixed = false; r.FixedVia = ""; o.Reopened.Add(r); }
                    return Task.FromResult(o);
                },
                RecheckByScan = rows => Task.FromResult<IReadOnlyDictionary<string, bool?>>(new Dictionary<string, bool?>()),
                Reopen = r => { },
                ToastUndone = n => f.Toasts.Add("undone " + n),
            });
            var host = f.Host();
            host.Remember = (req, r) => undo.Ledger.Remember("doc", r.Id, r.RevitElementId, req.ElementIds);
            await new JkrCopilotRecheck<Row>(host).RunAsync(PackRequest(f));
            Assert.Equal(46, undo.Ledger.ForDoc("doc").Count);

            Assert.Equal(46, await undo.RunAsync("doc", new long[0], allLedger: true));   // the Copilot Undo chip finished
            Assert.All(f.Rows, r => { Assert.False(r.Fixed); Assert.Equal("", r.FixedVia); });
            Assert.Contains("undone 46", f.Toasts);
            Assert.Equal(0, undo.Ledger.ForDoc("doc").Count);
        }

        // ── visible progress: the check flips to Pass and the headline % rises, no rescan ──

        /// <summary>What the pane does after the AI-495 re-check: the spec report is rebuilt from the fresh model read, the rows that
        /// re-passed are marked Fixed (via Copilot) and stay in Selesai, the board is replaced.</summary>
        private static void AfterRecheck(Board b, int stillFailing)
        {
            var results = new List<SpecResult>();
            foreach (var other in AiChecksExcept(b.Check)) results.Add(PassResult(other));
            if (stillFailing == 0) results.Add(PassResult(b.Check));
            for (var i = 1; i <= stillFailing; i++) results.Add(FailResult(b.Check, i, "r" + i));
            b.Spec = SpecReport.Build(results);
            foreach (var x in b.Items.Skip(stillFailing))
            {
                x.Status = ItemStatus.Resolved; x.Passed = true; x.FixedViaCopilot = true;   // as ResultsAdapter reads a verified Copilot fix
            }
            ShowAgain(b);
        }

        private static int Tabs(PanelCore c) => c.TabCounts().Values.Sum();

        [Fact]
        public void A_fully_fixed_check_flips_to_Pass_and_the_headline_rises_by_exactly_the_checks_weight()
        {
            var b = Scan("Da.1", 46);
            var before = SpecChecklist.Score(b.Spec);
            var pctBefore = SpecChecklist.Pct(b.Spec);
            Assert.Equal("fail", Header(b).Tone);
            Assert.Equal(SpecState.Fail, b.Spec.For("Da.1").State);
            var tabsBefore = b.Core.TabCounts();
            var total = Tabs(b.Core);
            Assert.Equal(46, tabsBefore[PanelTab.Open]);

            AfterRecheck(b, stillFailing: 0);

            Assert.Equal(SpecState.Pass, b.Spec.For("Da.1").State);
            Assert.Equal("pass", Header(b).Tone);
            var after = SpecChecklist.Score(b.Spec);
            Assert.Equal(before.Passed + 1, after.Passed);                  // one check = one over the total
            Assert.Equal(before.Total, after.Total);
            Assert.True(SpecChecklist.Pct(b.Spec) > pctBefore);
            // the board and the tabs, in the same step
            var tabs = b.Core.TabCounts();
            Assert.Equal(0, tabs[PanelTab.Open]);
            Assert.Equal(46, tabs[PanelTab.Resolved]);                       // Copilot-fixed rows stay in Selesai (AI-507)
            Assert.Equal(total, Tabs(b.Core));                               // Open + Selesai + Manual + Ignored still sum to the total
            Assert.Equal(46, b.Core.ResolvedN);
        }

        [Fact]
        public void A_partial_fix_keeps_the_check_failing_and_the_headline_unchanged_but_the_tabs_move()
        {
            var b = Scan("Da.1", 46);
            var before = SpecChecklist.Score(b.Spec);
            var pctBefore = SpecChecklist.Pct(b.Spec);
            var total = Tabs(b.Core);

            AfterRecheck(b, stillFailing: 1);        // 45 re-passed, 1 still fails

            Assert.Equal(SpecState.Fail, b.Spec.For("Da.1").State);
            Assert.Equal("fail", Header(b).Tone);
            Assert.Equal(before, SpecChecklist.Score(b.Spec));
            Assert.Equal(pctBefore, SpecChecklist.Pct(b.Spec));
            var tabs = b.Core.TabCounts();
            Assert.Equal(1, tabs[PanelTab.Open]);
            Assert.Equal(45, tabs[PanelTab.Resolved]);
            Assert.Equal(total, Tabs(b.Core));
            Assert.True(Header(b).CanFixViaCopilot);                         // the one left can be tried again
            Assert.Equal("Fix all 1 element via Copilot", Header(b).CopilotFixLabel);
        }

        [Fact]
        public void The_board_percentage_without_a_spec_report_also_counts_the_passed_rows()
        {
            // the headline of a run with no spec report is Stats() over the rows: Passed rows are passing cells (also when they stay visible)
            var items = Enumerable.Range(1, 10).Select(i => Item("r" + i, "Da.1", 1000 + i)).ToList();
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            core.Replace(items, null);
            var p0 = core.HeadPct;
            foreach (var x in items) { x.Status = ItemStatus.Resolved; x.Passed = true; x.FixedViaCopilot = true; }
            core.Replace(items, null);
            Assert.True(core.HeadPct > p0);
            Assert.Equal(100, core.HeadPct);
            Assert.Equal(10, core.TabCounts()[PanelTab.Resolved]);
            Assert.Equal(10, Tabs(core));
        }
    }
}
