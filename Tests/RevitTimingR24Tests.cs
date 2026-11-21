#nullable enable
// r24 (slow schedule 616d168b: one run_revit_code round trip took 255.8 s against ~10 s normally; the add-in reported only the total).
// Every envelope of a request that queued Revit work carries data.revit_timing: the time waiting for Revit's turn (queued_s) split
// from the time running on Revit's thread (run_s). No contract change: data is the envelope's free-form object. A wait past 30 s writes
// one add-in log line naming what Revit was busy with.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class RevitTimingR24Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
        }

        private static Envelope Env(string phase, object? data = null) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = phase, Summary = "ran",
            Data = data == null ? null : CopilotJson.ToElement(data),
        };

        private static ToolCallDto Call(string tool) => new ToolCallDto { CallId = "c1", IdempotencyKey = "k1", Tool = tool };

        /// <summary>A read executor whose "Revit job" waited and ran as given, as RevitThread reports it.</summary>
        private sealed class TimedReads : IReadExecutor
        {
            public long Queued, Run;
            public bool Jobs = true;
            public object? Data;
            public Task<Envelope[]> RunAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct)
            {
                if (Jobs) RevitTiming.Current!.Ran(Queued, Run);
                return Task.FromResult(calls.Select(_ => Env(EnvelopePhase.Read, Data)).ToArray());
            }
        }

        /// <summary>A write runner with a preview job and a commit job, the card's wait between them not counted.</summary>
        private sealed class TimedWrites : IWriteBatchRunner
        {
            public async Task<Envelope[]> RunAsync(WriteBatchRequest req, CancellationToken ct)
            {
                RevitTiming.Current!.Ran(200, 1000);       // preview (dry run)
                await Task.Delay(10).ConfigureAwait(false); // the card
                RevitTiming.Current!.Ran(254000, 1800);    // commit, queued behind a busy Revit
                return req.Calls.Select(_ => Env(EnvelopePhase.Commit, new { schedule = "Duct Schedule" })).ToArray();
            }
        }

        private static JsonElement Timing(Envelope e) => e.Data!.Value.GetProperty(RevitTiming.Key);

        [Fact]
        public async Task ARead_CarriesQueuedAndRunSeconds_InItsDataObject_KeepingItsOwnKeys()
        {
            var host = new CopilotToolHost { ReadExecutor = new TimedReads { Queued = 1500, Run = 9800, Data = new { rows = 3 } } };
            var envs = await host.RunReadsAsync("t", new[] { Call("find_elements") }, new SnapshotModel(), CancellationToken.None);
            var t = Timing(envs[0]);
            Assert.Equal(1.5, t.GetProperty("queued_s").GetDouble());
            Assert.Equal(9.8, t.GetProperty("run_s").GetDouble());
            Assert.Equal(1, t.GetProperty("jobs").GetInt32());
            Assert.Equal(3, envs[0].Data!.Value.GetProperty("rows").GetInt32());
            Assert.False(t.TryGetProperty("busy", out _));
            Assert.Empty(EnvelopeInvariants.Violations(envs[0]));
        }

        [Fact]
        public async Task AWrite_SumsPreviewAndCommitJobs_AndNamesTheLongestWait()
        {
            var host = new CopilotToolHost { WriteRunner = new TimedWrites() };
            var req = new WriteBatchRequest { TurnId = "t", RequestId = "r", Calls = new[] { Call("run_revit_code") }, Snapshot = new SnapshotModel() };
            var envs = await host.RunWritesAsync(req, CancellationToken.None);
            var t = Timing(envs[0]);
            Assert.Equal(254.2, t.GetProperty("queued_s").GetDouble());
            Assert.Equal(2.8, t.GetProperty("run_s").GetDouble());
            Assert.Equal(254.0, t.GetProperty("max_queued_s").GetDouble());
            Assert.Equal(2, t.GetProperty("jobs").GetInt32());
            Assert.Equal("Duct Schedule", envs[0].Data!.Value.GetProperty("schedule").GetString());
        }

        [Fact]
        public async Task ARequestWithNoRevitJob_IsLeftAsItWas()
        {
            var host = new CopilotToolHost { ReadExecutor = new TimedReads { Jobs = false } };
            var envs = await host.RunReadsAsync("t", new[] { Call("inspect") }, new SnapshotModel(), CancellationToken.None);
            Assert.Null(envs[0].Data);
        }

        [Fact]
        public async Task EachRequestHasItsOwnTiming()
        {
            var host = new CopilotToolHost { ReadExecutor = new TimedReads { Queued = 100, Run = 100 } };
            await host.RunReadsAsync("t", new[] { Call("inspect") }, new SnapshotModel(), CancellationToken.None);
            var envs = await host.RunReadsAsync("t", new[] { Call("inspect") }, new SnapshotModel(), CancellationToken.None);
            Assert.Equal(1, Timing(envs[0]).GetProperty("jobs").GetInt32());
            Assert.Null(RevitTiming.Current);   // the caller's flow never holds a request's timing
        }

        [Fact]
        public void With_NullDataBecomesAnObject_AnArrayIsUntouched_AnOldTimingIsReplaced()
        {
            var timing = new JsonObject { ["queued_s"] = 1.0 };
            var fromNull = RevitTiming.With(null, timing)!.Value;
            Assert.Equal(JsonValueKind.Object, fromNull.ValueKind);
            Assert.Equal(1.0, fromNull.GetProperty(RevitTiming.Key).GetProperty("queued_s").GetDouble());

            var array = CopilotJson.ToElement(new[] { 1, 2 });
            Assert.Equal(array.GetRawText(), RevitTiming.With(array, new JsonObject())!.Value.GetRawText());

            var old = CopilotJson.ToElement(new Dictionary<string, object> { [RevitTiming.Key] = new { queued_s = 9 }, ["x"] = 1 });
            var again = RevitTiming.With(old, new JsonObject { ["queued_s"] = 2.0 })!.Value;
            Assert.Equal(2.0, again.GetProperty(RevitTiming.Key).GetProperty("queued_s").GetDouble());
            Assert.Equal(1, again.GetProperty("x").GetInt32());
        }

        [Fact]
        public void NotRunJobsAndTheBusyCategory_AreReported()
        {
            var t = new RevitTiming();
            t.NotRun(61000);
            t.NoteBusy(RevitTiming.ModalDialog);
            t.NoteBusy(RevitTiming.NoIdle);   // the first category is kept
            var o = t.ToJson();
            Assert.Equal(61.0, (double)o["queued_s"]!);
            Assert.Equal(0, (int)o["jobs"]!);
            Assert.Equal(1, (int)o["not_run"]!);
            Assert.Equal("modal_dialog", (string)o["busy"]!);
            var env = Env(EnvelopePhase.Read);
            t.Stamp(new[] { env });
            Assert.True(env.Data!.Value.TryGetProperty(RevitTiming.Key, out _));
        }

        // Privacy (audit of 13ff5913): dialog messages, window titles and status-bar text can hold paths and user names. None reaches the envelope.
        private const string Path_ = @"C:\Users\aminah.binti\Documents\MRSM3-run.rvt";

        [Theory]
        [InlineData(Path_, null, null, null, "modal_dialog")]
        [InlineData(null, null, null, "Envelope[]", "copilot_job:Envelope[]")]
        [InlineData(null, null, "id 'TaskDialog_Save', Save changes to " + Path_ + "?", null, "task_dialog")]
        [InlineData(null, "Opening " + Path_, null, null, "status_bar_busy")]
        [InlineData(null, null, null, null, "no_idle")]
        public void TheEnvelopeGetsOnlyACategory_NeverTheWords(string? modal, string? status, string? dialog, string? running, string expected)
        {
            var category = RevitTiming.BusyCategory(modal, status, dialog, running);
            Assert.Equal(expected, category);
            Assert.Equal("copilot_job:CUsersxscript", RevitTiming.BusyCategory(null, null, null, @"C:\Users\x <script>"));   // only letters, digits, _ and []
            Assert.True(RevitTiming.IsCategory(category));
            var t = new RevitTiming();
            t.Ran(31000, 10);
            t.NoteBusy(category);
            var env = Env(EnvelopePhase.Read);
            t.Stamp(new[] { env });
            var json = CopilotJson.Serialize(env);
            Assert.DoesNotContain("aminah", json);
            Assert.DoesNotContain("Users", json);
            Assert.DoesNotContain(".rvt", json);
            Assert.DoesNotContain("\\", json);
        }

        [Theory]
        [InlineData(Path_)]
        [InlineData("modal dialog open 'Save " + Path_ + "'")]
        [InlineData("copilot_job:" + Path_)]
        [InlineData("copilot_job:")]
        [InlineData("")]
        public void FreeTextIsDropped_ByNoteBusy(string text)
        {
            var t = new RevitTiming();
            t.Ran(31000, 10);
            t.NoteBusy(text);
            Assert.Null(t.Busy);
            Assert.False(t.ToJson().ContainsKey("busy"));
        }

        [Fact]
        public void TheBusyWords_AreBuiltInTheLocalLogLineOnly_AndTheThreadNotesACategory()
        {
            var s = Src("BinaVibe/Copilot/Writes/RevitThread.cs");
            Assert.Contains("head.NoteBusy(RevitTiming.BusyCategory(modal, status, dialog, running));", s);
            Assert.DoesNotContain("NoteBusy(RevitTiming.BusyWith(", s);
            Assert.DoesNotContain("NoteBusy(RevitTiming.SlowWaitLine(", s);
        }

        [Fact]
        public async Task AMultiCallRequest_CarriesTheTotalsOnItsFirstEnvelopeOnly()
        {
            var host = new CopilotToolHost { ReadExecutor = new TimedReads { Queued = 4000, Run = 2000, Data = new { rows = 1 } } };
            var envs = await host.RunReadsAsync("t", new[] { Call("find_elements"), Call("inspect"), Call("look") }, new SnapshotModel(), CancellationToken.None);
            Assert.True(envs[0].Data!.Value.TryGetProperty(RevitTiming.Key, out _));
            Assert.False(envs[1].Data!.Value.TryGetProperty(RevitTiming.Key, out _));
            Assert.False(envs[2].Data!.Value.TryGetProperty(RevitTiming.Key, out _));
        }

        [Fact]
        public void AnArrayFirstEnvelope_PassesTheTotalsToTheNextObjectOne()
        {
            var t = new RevitTiming();
            t.Ran(100, 100);
            var a = Env(EnvelopePhase.Read, new[] { 1 });
            var b = Env(EnvelopePhase.Read, new { x = 1 });
            var c = Env(EnvelopePhase.Read, new { x = 2 });
            t.Stamp(new[] { a, b, c });
            Assert.Equal("[1]", a.Data!.Value.GetRawText());
            Assert.True(b.Data!.Value.TryGetProperty(RevitTiming.Key, out _));
            Assert.False(c.Data!.Value.TryGetProperty(RevitTiming.Key, out _));
        }

        [Fact]
        public void TheSlowWaitLine_NamesTheBestKnownCause()
        {
            Assert.Equal(30, RevitTiming.SlowWait.TotalSeconds);
            var line = RevitTiming.SlowWaitLine("CommitOutcome", 31.7, false, "Calculating", "Ready", null, null);
            Assert.Equal("copilot request waiting 31 s for Revit (job CommitOutcome): modal dialog open 'Calculating'", line);
            Assert.StartsWith("a copilot job still running (Envelope[])", RevitTiming.BusyWith(false, null, null, null, "Envelope[]"));
            Assert.Equal("status bar 'Click to enter duct start point' (active command or calculation)",
                RevitTiming.BusyWith(false, null, "Click to enter duct start point", null, null));
            Assert.StartsWith("dialog shown while waiting id 'Dialog_X'", RevitTiming.BusyWith(true, null, null, "id 'Dialog_X'", null));
            Assert.Contains("cause not detectable", RevitTiming.BusyWith(false, null, null, null, null));
        }

        [Fact]
        public void RevitThread_TellsTheRequest_BeforeCompletingTheJob_AndWatchesForSlowWaits()
        {
            var s = Src("BinaVibe/Copilot/Writes/RevitThread.cs");
            Assert.Contains("private readonly RevitTiming? _timing = RevitTiming.Current;", s);
            var run = s.Substring(s.IndexOf("public void Run(UIApplication app)", System.StringComparison.Ordinal));
            Assert.True(run.IndexOf("_timing?.Ran(waited, ran.ElapsedMilliseconds)", System.StringComparison.Ordinal)
                        < run.IndexOf("Done.TrySetResult(result)", System.StringComparison.Ordinal));
            Assert.Contains("_timing?.NotRun(_queued.ElapsedMilliseconds);   // before the task completes", s);
            var watch = s.Substring(s.IndexOf("private static void Watch()", System.StringComparison.Ordinal));
            Assert.Contains("NoteSlowWait();", watch.Substring(0, 200));
            Assert.Contains("head.TakeSlowNote()", s);
            var host = Src("BinaVibe/Copilot/CopilotToolHost.cs");
            Assert.Contains("var timing = BinaVibe.Copilot.Writes.RevitTiming.Begin();", host);
            Assert.Contains("timing.Stamp(results);", host);
        }
    }
}
