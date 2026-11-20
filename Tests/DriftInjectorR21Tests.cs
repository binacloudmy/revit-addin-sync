#nullable enable
// r21: the Debug-only drift injector (DriftInjector) that proves the Force card live. Arming by file, one-shot delete, the
// same element at every commit of the armed turn, the forced commit covered by the card's approval, and the source check
// that every use of it is inside #if DEBUG (a Release build has none). Revit-free.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class DriftInjectorSourceR21Tests
    {
        private static string Root([CallerFilePath] string thisFile = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

        [Fact]
        public void InjectorFileIsWhollyInsideIfDebug()
        {
            var lines = File.ReadAllLines(Path.Combine(Root(), "BinaVibe", "Copilot", "Writes", "DriftInjector.cs"));
            Assert.Equal("#if DEBUG", lines.First().Trim());
            Assert.Equal("#endif", lines.Last(l => l.Trim().Length > 0).Trim());
            Assert.Single(lines, l => l.TrimStart().StartsWith("#if", StringComparison.Ordinal));
            Assert.Single(lines, l => l.TrimStart().StartsWith("#endif", StringComparison.Ordinal));
        }

        [Fact]
        public void EveryUseOutsideTheInjectorIsInsideIfDebug()
        {
            var sources = Directory.EnumerateFiles(Root(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                            !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                            !f.Contains(Path.DirectorySeparatorChar + "Tests" + Path.DirectorySeparatorChar) &&
                            !f.EndsWith("DriftInjector.cs", StringComparison.Ordinal));
            var uses = 0;
            foreach (var f in sources)
            {
                var debug = new Stack<bool>();
                foreach (var raw in File.ReadLines(f))
                {
                    var l = raw.Trim();
                    if (l.StartsWith("#if", StringComparison.Ordinal)) { debug.Push(l == "#if DEBUG"); continue; }
                    if (l.StartsWith("#else", StringComparison.Ordinal) || l.StartsWith("#elif", StringComparison.Ordinal)) { if (debug.Count > 0) { debug.Pop(); debug.Push(false); } continue; }
                    if (l.StartsWith("#endif", StringComparison.Ordinal)) { if (debug.Count > 0) debug.Pop(); continue; }
                    if (!Regex.IsMatch(raw, @"\bDriftInjector\b|inject-connected-drift")) continue;
                    if (l.StartsWith("//", StringComparison.Ordinal)) continue;
                    uses++;
                    Assert.True(debug.Contains(true), f + ": a DriftInjector use outside #if DEBUG: " + l);
                }
            }
            Assert.True(uses >= 3, "the engine's injector call sites were not found");
        }
    }

#if DEBUG
    [Collection("DriftInjector")]
    public class DriftInjectorR21Tests : IDisposable
    {
        private const long Pipe = 1705297, Fitting = 1705300, OtherFitting = 1705310, Accessory = 1705305, Unconnected = 1709999;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bina-inject-" + Guid.NewGuid().ToString("N"));
        private readonly Func<string> _oldPath = DriftInjector.FlagPath;
        private string Flag => Path.Combine(_dir, DriftInjector.FlagFileName);
        private static readonly IReadOnlyList<string> Calls = new[] { "set_parameters|{\"Diameter\":2}" };
        private static readonly long[] Targets = { Pipe };

        private static readonly Dictionary<long, string> Connected = new()
        {
            [OtherFitting] = "Pipe Fittings", [Fitting] = "Pipe Fittings", [Accessory] = "Pipe Accessories", [Pipe] = "Pipes",
        };

        public DriftInjectorR21Tests()
        {
            Directory.CreateDirectory(_dir);
            DriftInjector.FlagPath = () => Flag;
            DriftInjector.Reset();
        }

        public void Dispose()
        {
            DriftInjector.FlagPath = _oldPath;
            DriftInjector.Reset();
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private static BatchShape Preview(params long[] modified)
        {
            var s = new BatchShape();
            s.Modified.UnionWith(modified);
            s.Touched.Add(Pipe);
            return s;
        }

        private void Arm() => File.WriteAllText(Flag, "1");

        private DriftExtra? Commit(string turn, bool forced, BatchShape? preview = null, bool match = true, bool measured = true, IReadOnlyList<string>? calls = null) =>
            DriftInjector.At(turn, calls ?? Calls, forced, match, measured, preview ?? Preview(Pipe, Fitting, OtherFitting, Accessory, Unconnected),
                Connected, Targets, out _);

        [Fact]
        public void NoFlagNothingFires()
        {
            Assert.Null(Commit("t1", forced: false));
            Assert.Null(DriftInjector.ArmedId("t1"));
        }

        [Fact]
        public void FlagFiresOnceOnTheLowestConnectedFittingThePreviewChangedAndIsDeleted()
        {
            Arm();
            var hit = DriftInjector.At("t1", Calls, false, true, true, Preview(Pipe, OtherFitting, Fitting, Accessory, Unconnected), Connected, Targets, out var log);
            Assert.NotNull(hit);
            Assert.Equal(Fitting, hit!.Id);              // the lowest id; the pipe (target), the accessory and the unconnected id are never picked
            Assert.Equal("Pipe Fittings", hit.Category);
            Assert.Equal(ConnectedDrift.Changed, hit.Kind);
            Assert.False(File.Exists(Flag));             // one-shot
            Assert.Contains("flag deleted, fired on id 1705300", log);
        }

        [Fact]
        public void SameTurnFiresAgainOnRetryAndForcedCommitThenClears()
        {
            Arm();
            Assert.Equal(Fitting, Commit("t1", false)!.Id);    // attempt 0
            Assert.Equal(Fitting, Commit("t1", false)!.Id);    // the runner's retry commit
            Assert.Equal(Fitting, Commit("t1", true)!.Id);     // "Force the change"
            Assert.Null(DriftInjector.ArmedId("t1"));
            Assert.Null(Commit("t1", true));                   // cleared: a second forced commit is real
            Assert.Null(Commit("t1", false));                  // and the flag is gone
        }

        [Fact]
        public void AnotherTurnIsNotInjected()
        {
            Arm();
            Assert.NotNull(Commit("t1", false));
            Assert.Null(Commit("t2", false));
            Assert.Null(Commit("t2", true));
            Assert.Null(Commit("t1", true));                   // the next turn dropped the old arming
        }

        [Fact]
        public void OtherCallsInTheArmedTurnAreNotInjected()
        {
            Arm();
            Assert.NotNull(Commit("t1", false));
            Assert.Null(Commit("t1", false, calls: new[] { "set_parameters|{\"Diameter\":4}" }));
            Assert.Equal(Fitting, DriftInjector.ArmedId("t1"));
        }

        [Fact]
        public void ForcedRequestNeverArms()
        {
            Arm();
            Assert.Null(Commit("t1", forced: true));
            Assert.True(File.Exists(Flag));
        }

        [Fact]
        public void NoCandidateOrRealDriftOrUnmeasuredLeavesTheFlag()
        {
            Arm();
            Assert.Null(DriftInjector.At("t1", Calls, false, true, true, Preview(Pipe, Accessory, Unconnected), Connected, Targets, out var log));
            Assert.Contains("no connected fitting", log);
            Assert.Null(Commit("t1", false, match: false));
            Assert.Null(Commit("t1", false, measured: false));
            Assert.True(File.Exists(Flag));
            Assert.Null(DriftInjector.ArmedId("t1"));
        }

        [Fact]
        public void ARealDriftInTheArmedTurnIsJudgedAsReal()
        {
            Arm();
            Assert.NotNull(Commit("t1", false));
            Assert.Null(Commit("t1", false, match: false));
            Assert.Equal(Fitting, DriftInjector.ArmedId("t1"));
        }

        [Fact]
        public void ATargetIsNeverPicked()
        {
            var connected = new Dictionary<long, string> { [Pipe] = "Pipe Fittings" };
            Assert.Null(DriftInjector.Candidate(Preview(Pipe), connected, Targets));
        }

        [Fact]
        public void TheInjectedVerdictIsConnectedOnlyAndTheCardApprovalCoversTheForcedCommit()
        {
            Arm();
            var first = DriftInjector.Apply(new ConnectedVerdict(), Commit("t1", false)!);
            Assert.True(first.ConnectedOnly);
            var data = ConnectedDrift.Data(first).GetProperty("drift");
            Assert.True(data.GetProperty("connected_only").GetBoolean());
            Assert.Equal("connected", data.GetProperty("force").GetString());
            Assert.Equal(Fitting, data.GetProperty("affected")[0].GetProperty("ids")[0].GetInt64());

            var approval = ForceApproval.From(Calls, first);
            var forced = DriftInjector.Apply(new ConnectedVerdict(), Commit("t1", true)!);
            Assert.True(approval.Covers(forced));
            Assert.Contains("changed 1 Pipe Fittings (ids 1705300)", ConnectedDrift.ForcedWarning(forced));
        }

        [Fact]
        public void ApplyKeepsARealOutsideElementUnconnected()
        {
            var real = new ConnectedVerdict { Outside = "element 1709999 (Walls) is not connected to the changed element(s)" };
            Assert.False(DriftInjector.Apply(real, new DriftExtra { Id = Fitting, Category = "Pipe Fittings", Kind = ConnectedDrift.Changed }).ConnectedOnly);
        }
    }
#endif
}
