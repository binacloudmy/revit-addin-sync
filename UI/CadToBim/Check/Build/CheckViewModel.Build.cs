// AI-642 step 1: Build (screens 8-9) and Review (10, 12) on the Check view model. One request at a
// time through the existing CadToBimBuildHandler + ExternalEvent; the report lands in State.Run and
// every bound property below is derived from that one state.

using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.CadToBim;

namespace RevitWebAppSync.UI.CadToBim.Check
{
    public sealed partial class CheckViewModel
    {
        private IBuildRequestSink _sink;
        private IBuildEventRaiser _raiser;

        /// <summary>Wire the builder (the pane host: App's handler + ExternalEvent). Without it Build only raises BuildRequested.</summary>
        public void UseBuilder(IBuildRequestSink sink, IBuildEventRaiser raiser)
        {
            _sink = sink;
            _raiser = raiser;
        }

        // ── bound surface ──
        public RunState Run => State.Run;
        public string BuildButtonText => State.Built ? BuildText.BuiltBlock : BuildLabel;
        /// <summary>The one line under Build when it is blocked for a reason other than "built".</summary>
        public string BuildBlockLine => State.Built || State.Building ? null : State.BuildBlock;
        /// <summary>The view was zoomed to the plan when the build started ("Follow in view"; live follow is AI-628).</summary>
        public string FollowText => State.Run == null ? "" : "Zoomed to " + State.Run.Level;
        public string ProgressText => State.Run == null ? "" : BuildText.Progress(State.Run);
        public string DoneText => State.Run?.Report == null ? "" : BuildText.Done(State.Run.Report, State.Run.Level);
        public bool RunFailed => State.Run?.Stage == RunStage.Failed;
        public string RunError => State.Run?.Error;
        public string UndoLine => State.Run == null ? "" : BuildText.UndoLine(State.Run);
        public bool RoomsBuilt => (State.Run?.Report?.Rooms ?? 0) > 0;
        public List<BannerLine> Banners => State.Run?.Report == null ? new List<BannerLine>()
            : BuildText.Banners(State.Run.Report).Select(b => new BannerLine { Lead = b.Lead, Bold = b.Bold, Post = b.Post, Tone = b.Tone }).ToList();
        public string AlsoLine => BuildText.Also(State);
        public string TotalsLine => State.Run?.Report == null ? "" : BuildText.Totals(State.Run.Report);
        /// <summary>Review's "From the build" rows: skipped openings by reason, then Revit failures by kind (errors first).</summary>
        public List<IssueRow> IssueRows => State.Run?.Report == null ? new List<IssueRow>()
            : State.Run.Report.Skipped.GroupBy(s => s.Reason).Select(g => IssueRow.From(g.Key, g.ToList()))
                .Concat(State.Run.Report.Failures.Groups.OrderByDescending(g => g.Error).ThenByDescending(g => g.Count).Select(IssueRow.From)).ToList();
        public List<AcceptRow> AcceptRows => State.Run?.Report == null ? new List<AcceptRow>() : BuildText.AcceptRows(State.Run.Report);
        /// <summary>Stepper: 0 = Check, 1 = Build, 2 = Review.</summary>
        public int Phase => State.Screen switch
        {
            CheckScreen.Building or CheckScreen.Built => 1,
            CheckScreen.Review or CheckScreen.Accept => 2,
            _ => 0,
        };

        public string StepCheck => Phase == 0 ? "on" : "done";
        public string StepBuild => Phase == 1 ? "on" : Phase > 1 ? "done" : "off";
        public string StepReview => Phase == 2 ? "on" : "off";
        public string StepCheckText => (Phase > 0 ? "✓ " : "") + "1 Check";
        public string StepBuildText => (Phase > 1 ? "✓ " : "") + "2 Build";
        public bool IsBuilt => State.Built;

        /// <summary>A run finished (the pane host logs it: the source of Tests/data/cad2bim/aswara-run.real.json).</summary>
        public event System.Action<BuildReport> RunFinished;

        // ── commands ──

        private void StartBuild()
        {
            BuildRequest request = CheckBuild.Request(State, _path);
            if (request == null || _sink == null || _raiser == null) return;   // harness / tests without a builder: BuildRequested only
            if (_sink.Request != null) return;                                  // the legacy pane's build (or ours) is still in flight
            State.Run = new RunState { Level = State.BuildLevel, Plan = State.MainPlan.Plan, Stage = RunStage.Building };
            State.Screen = CheckScreen.Building;
            _sink.Request = request;
            _sink.Completed -= OnBuilt;
            _sink.Completed += OnBuilt;
            _view?.ZoomTo(State.MainPlan.Plan.Box);   // "Follow in view": the view shows the plan being built
            if (!_raiser.Raise())
            {
                _sink.Completed -= OnBuilt;
                _sink.Request = null;
                State.Run.Stage = RunStage.Failed;
                State.Run.Error = "Revit is busy. Nothing was added; try Build again.";
                State.Screen = CheckScreen.Built;
            }
            Changed(rows: true);
        }

        /// <summary>The handler's Completed (Revit's main thread, inside the ExternalEvent).</summary>
        private void OnBuilt(BuildReport report)
        {
            _sink.Completed -= OnBuilt;
            if (State.Run == null || State.Run.Stage != RunStage.Building) return;   // a newer open replaced the state
            State.Run.Report = report;
            State.Run.Stage = report.Ok ? RunStage.Done : RunStage.Failed;
            State.Run.Error = report.Ok ? null : report.Error;
            State.Screen = CheckScreen.Built;
            if (report.Ok) _view?.Watch(report.WallIds.Values);
            Changed(rows: true);
            RunFinished?.Invoke(report);
        }

        /// <summary>The bridge saw every wall of the run leave the model (Ctrl+Z), or come back (redo).</summary>
        private void OnRunPresent(bool present)
        {
            if (State.Run == null || State.Run.Stage != RunStage.Done) return;
            State.Run.Undone = !present;
            if (!present && State.Screen != CheckScreen.Rows) State.Screen = CheckScreen.Rows;   // back to Check, Build allowed again
            Changed(rows: true);
        }

        public void Show(IssueRow row)
        {
            if (row == null || !row.CanShow) return;
            if (row.Ids.Count > 0) _view?.Show(row.Ids);
            else _view?.ZoomTo(row.Box.Value);
        }
    }

    public sealed class BannerLine
    {
        public string Lead { get; set; }
        public string Bold { get; set; }
        public string Post { get; set; }
        public string Tone { get; set; }
    }
}
