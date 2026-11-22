using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-496: Undo v1. A model of the pane (rows + which elements fail the rule) drives the real JkrRecheck.Reconcile, so
    /// "Copilot fix, JKR Fix All, then Ctrl+Z" runs the real reopen decision. Revit itself (the undo event, the receipt chip) is not run.
    /// </summary>
    public class JkrCopilotUndoTests
    {
        private const string Doc = @"C:\m\a.rvt";

        private sealed class Row : ICopilotRecheckRow
        {
            public string Id { get; set; }
            public long RevitElementId { get; set; }
            public string Title { get; set; } = "t";
            public string Required { get; set; } = "";
            public string FixedVia { get; set; } = "";
            public bool Fixed;
            public bool FixVerified { get; set; }
            public RecheckState RecheckState => Fixed ? RecheckState.Fixed : RecheckState.Open;
        }

        /// <summary>A fake pane: rows, the elements whose rule fails now, and the by-scan answer for create-type rows.</summary>
        private sealed class Pane
        {
            public List<Row> Rows = new List<Row>();
            public HashSet<long> FailingElements = new HashSet<long>();
            public HashSet<string> FailingByScan = new HashSet<string>();
            public bool ScanCanRun = true;
            public int ScansRun, Rechecks;
            public List<int> Toasts = new List<int>();
            public List<string> Reopened = new List<string>();

            public JkrCopilotUndo<Row> Undo;

            public Pane() { Undo = new JkrCopilotUndo<Row>(Host()); }

            private JkrCopilotUndoHost<Row> Host() => new JkrCopilotUndoHost<Row>
            {
                Issue = id => Rows.FirstOrDefault(r => r.Id == id),
                RowElementIds = () => new HashSet<long>(Rows.Select(r => r.RevitElementId)),
                Recheck = ids =>
                {
                    Rechecks++;
                    // the fresh scan: a failing row for every failing element
                    var fresh = Rows.Where(r => FailingElements.Contains(r.RevitElementId))
                        .Select(r => new Row { Id = r.Id, RevitElementId = r.RevitElementId }).ToList();
                    var o = JkrRecheck.Reconcile(Rows, ids.ToList(), fresh, true);
                    Rows = o.Issues;
                    return Task.FromResult(o);
                },
                RecheckByScan = rows =>
                {
                    ScansRun++;
                    var d = new Dictionary<string, bool?>();
                    if (ScanCanRun) foreach (var r in rows) d[r.Id] = !FailingByScan.Contains(r.Id);
                    return Task.FromResult<IReadOnlyDictionary<string, bool?>>(d);
                },
                Reopen = r => { r.Fixed = false; r.FixedVia = ""; Reopened.Add(r.Id); },
                ToastUndone = Toasts.Add,
            };

            public Row Add(string id, long elem, bool fixedViaCopilot = false, long[] written = null)
            {
                var r = new Row { Id = id, RevitElementId = elem, Fixed = fixedViaCopilot, FixedVia = fixedViaCopilot ? "Copilot" : "" };
                Rows.Add(r);
                if (fixedViaCopilot) Undo.Ledger.Remember(Doc, id, elem, written ?? new[] { elem });
                return r;
            }
        }

        [Fact]
        public async Task Copilot_Undo_chip_completion_rechecks_every_Copilot_row_even_when_Revit_reported_no_ids()
        {
            // AI-508: the chip's own completion event carries no ids. Element row r1 and create-type row r2 both reopen.
            var p = new Pane();
            p.Add("r1", 100, true);
            p.Add("r2", 0, true, new long[0]);
            p.FailingElements.Add(100);
            p.FailingByScan.Add("r2");
            var n = await p.Undo.RunAsync(Doc, new long[0], allLedger: true);
            Assert.Equal(2, n);
            Assert.Equal(new[] { 2 }, p.Toasts);
            Assert.Equal(0, p.Undo.Ledger.Count);
            Assert.True(p.Undo.HasRows(Doc) == false);
        }

        [Fact]
        public void Recheck_waits_while_the_chip_undo_is_running_then_runs()
        {
            var running = true;
            var u = new JkrCopilotUndo<Row>(new JkrCopilotUndoHost<Row> { UndoInProgress = () => running });
            Assert.True(u.ShouldDefer());
            running = false;
            Assert.False(u.ShouldDefer());
            Assert.False(new JkrCopilotUndo<Row>(new JkrCopilotUndoHost<Row>()).ShouldDefer());   // no probe: never defer
        }

        [Fact]
        public async Task Chip_completion_after_the_DocumentChanged_run_does_not_toast_twice()
        {
            var p = new Pane();
            p.Add("r1", 100, true);
            p.FailingElements.Add(100);
            Assert.Equal(1, await p.Undo.RunAsync(Doc, new long[] { 100 }));
            Assert.Equal(0, await p.Undo.RunAsync(Doc, new long[0], allLedger: true));   // ledger already empty
            Assert.Equal(new[] { 1 }, p.Toasts);
        }

        [Fact]
        public async Task Chunked_pack_rows_all_reopen_when_the_ids_span_the_chunks()
        {
            var p = new Pane();
            var written = Enumerable.Range(1000, 120).Select(i => (long)i).ToArray();   // 3 chunks of 50/50/20
            p.Add("r1", 1000, true, written);
            p.FailingElements.Add(1000);
            Assert.Equal(1, await p.Undo.RunAsync(Doc, new long[] { 1119 }));            // only the last chunk's id reported
        }

        [Fact]
        public async Task Undo_of_the_Copilot_write_reopens_the_row_with_one_toast()
        {
            var p = new Pane();
            p.Add("r1", 100, true);
            p.FailingElements.Add(100);                    // Undo put the bad value back: the rule fails again
            var n = await p.Undo.RunAsync(Doc, new long[] { 100 });
            Assert.Equal(1, n);
            var row = Assert.Single(p.Rows);
            Assert.False(row.Fixed);                       // a fresh Open row replaced the Fixed one
            Assert.Equal("", row.FixedVia);
            Assert.Equal(new[] { 1 }, p.Toasts);
            Assert.Equal(0, p.Undo.Ledger.Count);          // no longer Fixed via Copilot: forgotten
        }

        [Fact]
        public async Task Undo_that_leaves_the_rule_passing_keeps_the_row_Fixed()
        {
            var p = new Pane();
            var r = p.Add("r1", 100, true);
            var n = await p.Undo.RunAsync(Doc, new long[] { 100 });   // rule still passes (something else was undone)
            Assert.Equal(0, n);
            Assert.True(r.Fixed);
            Assert.Equal("Copilot", r.FixedVia);
            Assert.Empty(p.Toasts);
            Assert.Equal(1, p.Undo.Ledger.Count);          // still remembered for the next Undo
        }

        [Fact]
        public async Task Undo_of_unrelated_elements_does_not_touch_the_Copilot_row_or_scan()
        {
            var p = new Pane();
            var r = p.Add("r1", 100, true);
            p.FailingElements.Add(100);                    // would fail if checked; the Undo did not touch it
            var n = await p.Undo.RunAsync(Doc, new long[] { 555 });
            Assert.Equal(0, n);
            Assert.Equal(0, p.Rechecks);
            Assert.True(r.Fixed);
        }

        [Fact]
        public async Task Write_to_a_related_element_still_reopens_the_row_through_the_remembered_write()
        {
            // Copilot changed element 101 (a type) to fix row r1 on element 100. Undo reports only 101.
            var p = new Pane();
            p.Add("r1", 100, true, new long[] { 101 });
            p.FailingElements.Add(100);
            var n = await p.Undo.RunAsync(Doc, new long[] { 101 });
            Assert.Equal(1, n);
            Assert.False(p.Rows.Single().Fixed);
        }

        [Fact]
        public async Task Copilot_fix_then_Fix_All_then_Ctrl_Z_only_reopens_what_the_rule_says()
        {
            // r1: Copilot fixed (element 100). r2: Fix All fixed it (element 200, not Copilot). Ctrl+Z undoes the Fix All group.
            var p = new Pane();
            var r1 = p.Add("r1", 100, true);
            var r2 = p.Add("r2", 200);
            r2.Fixed = true; r2.FixedVia = "";
            p.FailingElements.Add(200);                    // undone Fix All: 200 fails again; 100 is untouched and still passes
            var n = await p.Undo.RunAsync(Doc, new long[] { 200 });
            Assert.Equal(1, n);                            // r2 reopened by the panel's own re-check (AI-427), counted once
            Assert.True(p.Rows.Single(r => r.Id == "r1").Fixed);
            Assert.Equal("Copilot", p.Rows.Single(r => r.Id == "r1").FixedVia);   // Copilot row not corrupted
            Assert.False(p.Rows.Single(r => r.Id == "r2").Fixed);
            Assert.Equal(1, p.Undo.Ledger.Count);          // r1 still remembered; Fix All never entered the ledger
            Assert.Equal(new[] { 1 }, p.Toasts);
        }

        [Fact]
        public async Task Fix_All_over_the_same_element_then_Ctrl_Z_back_to_the_Copilot_value_keeps_the_row_Fixed()
        {
            // Fix All rewrote element 100 (also Copilot's); Ctrl+Z restores Copilot's value: the rule passes.
            var p = new Pane();
            var r1 = p.Add("r1", 100, true);
            var n = await p.Undo.RunAsync(Doc, new long[] { 100 });
            Assert.Equal(0, n);
            Assert.True(r1.Fixed);
            Assert.Equal("Copilot", r1.FixedVia);
        }

        [Fact]
        public async Task Create_type_row_is_checked_by_scan_even_when_the_Undo_reports_no_ids()
        {
            var p = new Pane();
            p.Add("v1", 0, true, new long[0]);            // a created view: no element id, nothing in the request
            p.FailingByScan.Add("v1");
            Assert.True(p.Undo.Interested(Doc, new long[0]));
            var n = await p.Undo.RunAsync(Doc, new long[0]);
            Assert.Equal(1, n);
            Assert.Equal(new[] { "v1" }, p.Reopened);
            Assert.Equal(new[] { 1 }, p.Toasts);
            Assert.Equal(0, p.Undo.Ledger.Count);
        }

        [Fact]
        public async Task Create_type_row_whose_rule_still_passes_stays_Fixed()
        {
            var p = new Pane();
            var v = p.Add("v1", 0, true, new long[0]);
            var n = await p.Undo.RunAsync(Doc, new long[0]);
            Assert.Equal(0, n);
            Assert.True(v.Fixed);
            Assert.Empty(p.Reopened);
        }

        [Fact]
        public async Task Create_type_row_stays_Fixed_when_the_scan_could_not_run()
        {
            var p = new Pane { ScanCanRun = false };
            var v = p.Add("v1", 0, true, new long[0]);
            p.FailingByScan.Add("v1");
            Assert.Equal(0, await p.Undo.RunAsync(Doc, new long[0]));
            Assert.True(v.Fixed);
        }

        [Fact]
        public async Task A_scan_is_not_run_when_nothing_is_remembered()
        {
            var p = new Pane();
            Assert.False(p.Undo.Interested(Doc, new long[0]));
            await p.Undo.RunAsync(Doc, new long[0]);
            Assert.Equal(0, p.ScansRun);
        }

        [Fact]
        public async Task Other_document_entries_are_ignored()
        {
            var p = new Pane();
            var r = p.Add("r1", 100, true, new long[] { 101 });   // the panel itself drops an Undo of another document
            p.FailingElements.Add(100);
            Assert.Equal(0, await p.Undo.RunAsync(@"C:\m\other.rvt", new long[] { 101 }));
            Assert.True(r.Fixed);
        }

        [Fact]
        public async Task Row_that_left_the_list_is_forgotten()
        {
            var p = new Pane();
            p.Add("r1", 100, true);
            p.Rows.Clear();
            await p.Undo.RunAsync(Doc, new long[] { 100 });
            Assert.Equal(0, p.Undo.Ledger.Count);
        }

        [Fact]
        public void Remembering_the_same_row_again_replaces_the_entry()
        {
            var l = new JkrCopilotFixLedger();
            l.Remember(Doc, "r1", 100, new long[] { 1 });
            l.Remember(Doc, "r1", 100, new long[] { 2 });
            Assert.Equal(1, l.Count);
            Assert.Empty(l.HitBy(Doc, new long[] { 1 }));
            Assert.Single(l.HitBy(Doc, new long[] { 2 }));
        }

        [Fact]
        public async Task Two_create_type_rows_share_one_scan_for_one_Undo()
        {
            var p = new Pane();
            p.Add("v1", 0, true, new long[0]);
            p.Add("v2", 0, true, new long[0]);
            p.FailingByScan.Add("v1");
            var n = await p.Undo.RunAsync(Doc, new long[0]);
            Assert.Equal(1, p.ScansRun);
            Assert.Equal(1, n);                            // v1 reopened, v2 passes and stays
            Assert.Equal(new[] { "v1" }, p.Reopened);
        }

        [Fact]
        public async Task Undo_of_unrelated_elements_runs_no_scan_even_with_a_create_type_row_Fixed()
        {
            var p = new Pane();
            p.Add("v1", 0, true, new long[0]);
            p.Add("r1", 100, true);
            p.FailingByScan.Add("v1");
            Assert.False(p.Undo.Interested(Doc, new long[] { 555 }));
            Assert.Equal(0, await p.Undo.RunAsync(Doc, new long[] { 555 }));
            Assert.Equal(0, p.ScansRun);
        }

        [Fact]
        public async Task Undo_that_touches_a_create_type_rows_written_ids_scans_once()
        {
            var p = new Pane();
            p.Add("v1", 0, true, new long[] { 900 });
            p.FailingByScan.Add("v1");
            Assert.Equal(1, await p.Undo.RunAsync(Doc, new long[] { 900 }));
            Assert.Equal(1, p.ScansRun);
        }

        [Fact]
        public async Task A_row_with_no_verdict_from_the_scan_stays_Fixed()
        {
            var p = new Pane { ScanCanRun = false };       // no verdict for any row
            var v = p.Add("v1", 0, true, new long[0]);
            Assert.Equal(0, await p.Undo.RunAsync(Doc, new long[0]));
            Assert.True(v.Fixed);
        }
    }
}
