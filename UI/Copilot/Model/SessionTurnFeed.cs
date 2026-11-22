#nullable enable
// Feeds the turn client's events and outcome into a ChatSession (AI-305).
//
// CopilotTurnClient drives one stream leg and calls an ICopilotTurnHandler for
// progress, reply text, errors and tool runs; the leg's end comes back as a
// TurnOutcome (an ask, turn_end, or a failure). This pair is the whole bridge
// from that to the reducer, so a recorded stream can be replayed into Turn
// state exactly as the pane's router would see it. CopilotTurnRouter does not
// use it yet (it still keeps its own private turn state); AI-306 / AI-310
// switch the router over and delete the duplicate.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Runs a tool request in Revit (the pane's PaneToolRunner): one envelope per call, in call order.</summary>
    public delegate Task<Envelope[]> ToolRunner(ToolRequestDto request, CancellationToken ct);

    /// <summary>The ICopilotTurnHandler of ONE turn leg: its events can only reach that turn.</summary>
    public sealed class SessionTurnHandler : ICopilotTurnHandler
    {
        private readonly ChatSession _session;
        private readonly string _turnId;
        private readonly ToolRunner _run;
        private readonly Func<SnapshotModel> _snapshot;
        private readonly Func<string?> _budget;

        /// <param name="budget">The developer budget line after a tool run, or null (TurnBudget.Describe).</param>
        public SessionTurnHandler(ChatSession session, string turnId, ToolRunner run, Func<SnapshotModel>? snapshot = null, Func<string?>? budget = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _turnId = turnId;
            _run = run ?? throw new ArgumentNullException(nameof(run));
            _snapshot = snapshot ?? (() => new SnapshotModel { Stale = true });
            _budget = budget ?? (() => null);
        }

        public void OnProgress(ProgressDto progress) => _session.OnProgress(_turnId, progress);
        public void OnReplyDelta(ReplyDeltaDto delta) => _session.OnReplyDelta(_turnId, delta);
        public void OnError(StreamErrorDto error) => _session.OnError(_turnId, error);
        public SnapshotModel CurrentSnapshot() => _snapshot();

        public async Task<Envelope[]> RunToolsAsync(string turnId, ToolRequestDto request, CancellationToken ct)
        {
            _session.OnToolRequest(_turnId, request);
            var envelopes = await _run(request, ct).ConfigureAwait(false) ?? Array.Empty<Envelope>();
            _session.OnToolResults(_turnId, request, envelopes, _budget());
            return envelopes;
        }
    }

    public static class SessionOutcome
    {
        /// <summary>Apply how a leg ended: an ask parks the turn, turn_end finishes it, anything else is a failure.</summary>
        public static void Apply(ChatSession session, string turnId, TurnOutcome outcome)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            switch (outcome.Kind)
            {
                case TurnOutcomeKind.Ask:
                    session.OnAsk(turnId, outcome.Ask);
                    break;
                case TurnOutcomeKind.Ended:
                    session.OnEnd(turnId, outcome.End, outcome.Error?.Message);
                    break;
                case TurnOutcomeKind.Refused:
                    session.OnFailure(turnId, RouteFailure.Refused, outcome.Message);
                    break;
                case TurnOutcomeKind.UpdateRequired:
                    session.OnFailure(turnId, RouteFailure.UpdateRequired, outcome.Message);
                    break;
                case TurnOutcomeKind.TurnActive:
                    session.OnFailure(turnId, RouteFailure.TurnActive, outcome.Message);
                    break;
                case TurnOutcomeKind.Unauthorized:
                    session.OnFailure(turnId, RouteFailure.SignIn, outcome.Message);
                    break;
                default:
                    session.OnFailure(turnId,
                        outcome.HttpStatus == null && !outcome.ResultsUndelivered ? RouteFailure.Unreachable : RouteFailure.Other,
                        outcome.Message);
                    break;
            }
        }
    }
}
