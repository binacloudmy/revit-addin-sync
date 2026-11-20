#nullable enable
// The force path for a drifted commit (tester feedback 20 Nov, item 4; live c1bd5c9a, e09b72b3): a duct resized to 300 x 200
// committed with Revit removing 2 connected fittings the preview had not removed. ShapeComparer.JudgeConnected says whether
// every difference lies within the elements connected to the targets; only then may a forced batch keep it. ForceScope
// decides which errors a forced batch may let Revit resolve. The runner carries the drafter's click (tool_request force)
// into the engine and the measured drift into the diff_drift envelope.
//
// Revit-free: compiles in Tests.csproj.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using BinaVibe.Copilot.Writes;
using BinaVibe.DocState;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ConnectedDriftTests
    {
        private const long Duct = 1382112;
        private static readonly Dictionary<long, string> Fittings = new() { [1543386] = "Duct Fittings", [1543387] = "Duct Fittings", [1543390] = "Ducts" };

        private static BatchShape Shape(IEnumerable<long>? modified = null, IEnumerable<long>? deleted = null, IEnumerable<AddedShape>? added = null)
        {
            var s = new BatchShape();
            if (modified != null) s.Modified.UnionWith(modified);
            if (deleted != null) s.Deleted.UnionWith(deleted);
            if (added != null) s.Added.AddRange(added);
            s.Touched.Add(Duct);
            return s;
        }

        private static AddedShape Fitting(long id, double x) => new AddedShape
        {
            Id = id, Category = "Duct Fittings", TypeId = 88, LevelId = 2, BboxMm = new[] { x, 0.0, 0.0, x + 300.0, 300.0, 200.0 },
        };

        [Fact]
        public void TwoConnectedFittingsRemoved_IsConnectedOnly_AndListedByCategory()
        {
            // e09b72b3: the preview changed the duct only; the commit also removed fittings 1543386 and 1543387.
            var preview = Shape(modified: new[] { Duct });
            var commit = Shape(modified: new[] { Duct }, deleted: new long[] { 1543386, 1543387 });

            Assert.False(ShapeComparer.Compare(Array.Empty<WritePlan>(), preview, commit).IsMatch);   // the default still refuses
            var verdict = ShapeComparer.JudgeConnected(preview, commit, Fittings, new Dictionary<long, string>());

            Assert.True(verdict.ConnectedOnly, verdict.Outside);
            Assert.Equal(new long[] { 1543386, 1543387 }, verdict.Extras.Select(e => e.Id).ToArray());
            Assert.All(verdict.Extras, e => { Assert.Equal("Duct Fittings", e.Category); Assert.Equal(ConnectedDrift.Removed, e.Kind); });

            var warning = ConnectedDrift.ForcedWarning(verdict);
            Assert.Contains("removed 2 Duct Fittings (ids 1543386, 1543387)", warning);
            Assert.Contains("One Undo restores all of it", warning);
        }

        [Fact]
        public void TheTargetsOwnValuesMayDifferToo_WhenItsTargetsArePassed()
        {
            // Revit removing the fittings may also change the resized duct's length and connectors at the commit.
            var preview = Shape(modified: new[] { Duct });
            preview.Values[Duct] = "w=300|h=200|len=2390";
            var commit = Shape(modified: new[] { Duct }, deleted: new long[] { 1543386, 1543387 });
            commit.Values[Duct] = "w=300|h=200|len=2600";

            Assert.False(ShapeComparer.JudgeConnected(preview, commit, Fittings, new Dictionary<long, string>()).ConnectedOnly);
            var verdict = ShapeComparer.JudgeConnected(preview, commit, Fittings, new Dictionary<long, string>(), new[] { Duct });
            Assert.True(verdict.ConnectedOnly, verdict.Outside);
            Assert.Contains(verdict.Extras, e => e.Id == Duct && e.Kind == ConnectedDrift.Changed);
        }

        [Fact]
        public void AnElementOutsideTheConnectedSet_IsNeverConnectedOnly()
        {
            var preview = Shape(modified: new[] { Duct });
            var commit = Shape(modified: new[] { Duct, 777000L }, deleted: new long[] { 1543386 });
            commit.CategoryOf[777000] = "Walls";

            var verdict = ShapeComparer.JudgeConnected(preview, commit, Fittings, new Dictionary<long, string>());

            Assert.False(verdict.ConnectedOnly);
            Assert.Contains("777000", verdict.Outside);
            Assert.Contains("Walls", verdict.Outside);
        }

        [Fact]
        public void ANewFittingConnectedAfterTheCommit_IsAllowed_AnUnconnectedAdditionIsNot()
        {
            var preview = Shape(modified: new[] { Duct });
            var commit = Shape(modified: new[] { Duct }, deleted: new long[] { 1543386 }, added: new[] { Fitting(1600001, 0) });

            var connected = ShapeComparer.JudgeConnected(preview, commit, Fittings, new Dictionary<long, string> { [1600001] = "Duct Fittings" });
            Assert.True(connected.ConnectedOnly, connected.Outside);
            Assert.Contains(connected.Extras, e => e.Id == 1600001 && e.Kind == ConnectedDrift.Added);

            var loose = ShapeComparer.JudgeConnected(preview, commit, Fittings, new Dictionary<long, string>());
            Assert.False(loose.ConnectedOnly);
            Assert.Contains("added Duct Fittings", loose.Outside);
        }

        [Fact]
        public void AMeasuredMatchOrAnEstimate_IsNotConnectedOnly()
        {
            var same = Shape(modified: new[] { Duct });
            Assert.False(ShapeComparer.JudgeConnected(same, Shape(modified: new[] { Duct }), Fittings, Fittings).ConnectedOnly);   // nothing to force
            Assert.False(ShapeComparer.JudgeConnected(null, same, Fittings, Fittings).ConnectedOnly);                           // estimate-only card
        }

        [Fact]
        public void DriftData_OffersForceOnlyWhenConnectedOnly()
        {
            var preview = Shape(modified: new[] { Duct });
            var connected = ShapeComparer.JudgeConnected(preview, Shape(modified: new[] { Duct }, deleted: new long[] { 1543386, 1543387 }), Fittings, new Dictionary<long, string>());
            var data = ConnectedDrift.Data(connected).GetProperty("drift");
            Assert.True(data.GetProperty("connected_only").GetBoolean());
            Assert.Equal("connected", data.GetProperty("force").GetString());
            var group = data.GetProperty("affected")[0];
            Assert.Equal("Duct Fittings", group.GetProperty("category").GetString());
            Assert.Equal("removed", group.GetProperty("kind").GetString());
            Assert.Equal(2, group.GetProperty("count").GetInt32());

            var outside = Shape(modified: new[] { Duct }, deleted: new long[] { 42 });
            var refused = ConnectedDrift.Data(ShapeComparer.JudgeConnected(preview, outside, Fittings, new Dictionary<long, string>())).GetProperty("drift");
            Assert.False(refused.GetProperty("connected_only").GetBoolean());
            Assert.False(refused.TryGetProperty("force", out _));
            Assert.True(refused.TryGetProperty("outside", out _));
        }

        [Fact]
        public void ForceScope_ResolvesOnlyErrorsWithAPermittedFixOnConnectedElements_AndStopsAfterThreeRounds()
        {
            Assert.Null(ForceScope.Current);
            using (var scope = ForceScope.Begin(new long[] { Duct, 1543386 }))
            {
                Assert.Same(scope, ForceScope.Current);
                Assert.True(scope.Allows(new long[] { Duct, 1543386 }, hasResolutions: true, permitted: true));
                Assert.False(scope.Allows(new long[] { Duct, 99 }, true, true));        // an element outside the set
                Assert.False(scope.Allows(new long[] { Duct }, hasResolutions: false, permitted: true));   // no fix: the drafter must decide
                Assert.False(scope.Allows(new long[] { Duct }, true, permitted: false));
                Assert.False(scope.Allows(Array.Empty<long>(), true, true));            // names nothing: never resolved
                scope.NoteRound(); scope.NoteRound(); scope.NoteRound();
                Assert.False(scope.Allows(new long[] { Duct }, true, true));            // a fix that keeps failing cannot loop
                scope.Record("Duct is too small", "Delete Element(s)");
                Assert.Equal("Duct is too small — Delete Element(s)", scope.Resolved.Single());
            }
            Assert.Null(ForceScope.Current);
        }

        // ---- the runner carries the click and the measured drift --------------------------------------------------

        private sealed class Engine : IWriteEngine
        {
            public readonly List<WriteBatch> Batches = new();
            public Task<PreviewOutcome> PreviewAsync(WriteBatch batch, CancellationToken ct)
            {
                Batches.Add(batch);
                return Task.FromResult(new PreviewOutcome
                {
                    Reads = new Envelope?[batch.Calls.Count],
                    Failures = new Envelope?[batch.Calls.Count],
                    Plans = batch.Calls.Select(c => new WritePlan { Tool = c.Tool, CanonicalArgs = c.Args.GetRawText(), TargetIds = new[] { Duct } }).ToArray(),
                    Envelopes = batch.Calls.Select(_ => new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview, Summary = "Preview.", Count = 1 }).ToArray(),
                    Shape = new BatchShape { Modified = { Duct } },
                    Revision = 1,
                    GroupName = "BINA: Width",
                });
            }

            public Task<CommitOutcome> CommitAsync(WriteBatch batch, PreviewOutcome preview, string planHash, CancellationToken ct)
            {
                Batches.Add(batch);
                var data = ConnectedDrift.Data(ShapeComparer.JudgeConnected(preview.Shape, new BatchShape { Modified = { Duct }, Deleted = { 1543386, 1543387 } }, Fittings, new Dictionary<long, string>()));
                return Task.FromResult(new CommitOutcome { Kind = CommitKind.Drift, Reason = "The commit deleted 2 elements the preview did not delete (ids 1543386, 1543387).", DriftData = data });
            }
        }

        private sealed class Docs : IDocIdentityProvider
        {
            public Task<DocIdentity?> ActiveAsync(CancellationToken ct) => Task.FromResult<DocIdentity?>(new DocIdentity("project", "SnowdonHVAC", "hash", "n1"));
        }

        private static WriteBatchRequest Request(string? force) => new WriteBatchRequest
        {
            TurnId = "t1",
            RequestId = "req_03",
            Snapshot = new SnapshotModel { Doc = new SnapshotDoc { Kind = "project", Title = "SnowdonHVAC", PathHash = "hash", SessionNonce = "n1", Mode = "standalone" } },
            Calls = new[]
            {
                new ToolCallDto { CallId = "c1", IdempotencyKey = "t1:c1" + force, Tool = ToolNames.SetParameters, Force = force,
                                  Args = JsonDocument.Parse("{\"param\":\"Width\",\"value\":\"11.81\",\"target\":{\"ids\":[1382112]}}").RootElement.Clone() },
            },
        };

        [Fact]
        public async Task Runner_PassesTheClickedForce_AndPutsTheMeasuredDriftOnTheEnvelope()
        {
            var engine = new Engine();
            var runner = new WriteBatchRunner(null, engine, new Docs(), new OperationLedgerCommits(new OperationLedger()));

            var plain = await runner.RunAsync(Request(null), CancellationToken.None);
            Assert.All(engine.Batches, b => Assert.False(b.ForceConnected));
            Assert.Equal(ErrorCodes.DiffDrift, plain[0].Error!.Code);
            var drift = plain[0].Data!.Value.GetProperty("drift");
            Assert.Equal("connected", drift.GetProperty("force").GetString());
            Assert.Empty(EnvelopeInvariants.Violations(plain[0]));

            engine.Batches.Clear();
            await runner.RunAsync(Request(ConnectedDrift.ForceConnected), CancellationToken.None);
            Assert.NotEmpty(engine.Batches);
            Assert.All(engine.Batches, b => Assert.True(b.ForceConnected));
        }

        [Fact]
        public void ForceOnTheWire_IsOnTheRequest_NeverOnACall()
        {
            var dto = JsonSerializer.Deserialize<ToolRequestDto>("{\"request_id\":\"r\",\"kind\":\"write\",\"force\":\"connected\",\"calls\":[{\"call_id\":\"c\",\"idempotency_key\":\"k\",\"tool\":\"set_parameters\",\"args\":{},\"force\":\"connected\"}]}")!;
            Assert.Equal("connected", dto.Force);
            Assert.Null(dto.Calls[0].Force);   // a per-call value on the wire is ignored: only the router copies the request's
            Assert.DoesNotContain("force", JsonSerializer.Serialize(dto.Calls[0]));
        }
    }
}
