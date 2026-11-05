#nullable enable
// room_has (AI-269 reliability 1.6): the kind loads (and needs values), the verdicts come from a room -> count map,
// a door counts for the rooms on both sides, unplaced and not-enclosed rooms get no outcome, an unreadable run is
// not_verified. Revit-free: the Revit side only fills RoomHasCounts and RoomFacts.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Check;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RoomHasTests
    {
        private static string RuleJson(string values = "[\"Doors\"]") =>
            CheckFixtures.OneRule("{\"kind\":\"room_has\",\"values\":" + values + "}", "{\"categories\":[\"Rooms\"],\"link\":\"host\"}");

        private static Rule LoadRule(string json) => RuleSetLoader.Load(CheckFixtures.Parse(json)).RuleSet.Rules.Single();

        [Fact]
        public void TheKind_IsKnown_AndLoadsWithValues_WithoutItBeingUnsupported()
        {
            Assert.Contains(AssertionKinds.RoomHas, AssertionKinds.All);
            var loaded = RuleSetLoader.Load(CheckFixtures.Parse(RuleJson()));
            Assert.Empty(loaded.Unsupported);                                             // an old add-in would list it here
            var rule = loaded.RuleSet.Rules.Single();
            Assert.Equal(AssertionKinds.RoomHas, rule.Assertion.Kind);
            Assert.Equal(new[] { "Doors" }, rule.Assertion.Values);
        }

        [Fact]
        public void TheKind_WithoutValues_IsAProblem()
        {
            var ex = Assert.Throws<RuleSetInvalidException>(() => RuleSetLoader.Load(CheckFixtures.Parse(CheckFixtures.OneRule("{\"kind\":\"room_has\"}", "{\"categories\":[\"Rooms\"]}"))));
            Assert.Contains("kind room_has needs values", ex.Message);
            var empty = Assert.Throws<RuleSetInvalidException>(() => RuleSetLoader.Load(CheckFixtures.Parse(RuleJson("[]"))));
            Assert.Contains("values must hold at least one value", empty.Message);
        }

        private static RoomFacts Room(long id, string label, bool placed = true, double area = 12) =>
            new RoomFacts { Ref = new ElementRef(id, null), Id = id, Placed = placed, AreaM2 = area, Label = label };

        // four placed rooms (A: door and window, B: door only, C: window only, D: neither) and one unplaced room (E)
        private static readonly RoomFacts[] Rooms =
        {
            Room(1, "1.01 Pejabat"), Room(2, "1.02 Stor"), Room(3, "1.03 Bilik Fail"), Room(4, "1.04 Tandas"), Room(5, "1.05 Tiada Lokasi", placed: false, area: 0),
        };

        private static RoomHasCounts Doors()
        {
            var c = new RoomHasCounts();
            c.Add(1, 2);          // a door between A and B: counts for both
            c.Add(null, 1);       // an outer door of A
            c.Add(2, null);
            return c;
        }

        private static RoomHasCounts Windows()
        {
            var c = new RoomHasCounts();
            c.Add(1, null);       // a window in A
            c.Add(3, null);       // a window in C
            return c;
        }

        [Fact]
        public void Doors_TwoRoomsPass_TwoFail_TheUnplacedRoomHasNoOutcome()
        {
            var run = new RuleRun(LoadRule(RuleJson()));
            RoomHasEvaluation.Run(run, Rooms, Doors(), new[] { "Doors" }, null);

            Assert.Equal(4, run.Results.Count);                                             // E (unplaced) gets none
            Assert.DoesNotContain(run.Results, r => r.Ref.Id == 5);
            Assert.Equal(2, run.Results.Count(r => r.Outcome.Kind == OutcomeKind.Pass));
            var fails = run.Results.Where(r => r.Outcome.Kind == OutcomeKind.Fail).ToList();
            Assert.Equal(new long[] { 3, 4 }, fails.Select(f => f.Ref.Id).OrderBy(i => i).ToArray());
            Assert.Equal("No Doors in room 1.03 Bilik Fail.", fails.Single(f => f.Ref.Id == 3).Outcome.Reason);
            Assert.Equal("0", fails[0].Outcome.Value);
            Assert.Equal("2", run.Results.Single(r => r.Ref.Id == 1).Outcome.Value);        // the A-B door and A's outer door
        }

        [Fact]
        public void Windows_TwoRoomsPass_TwoFail()
        {
            var run = new RuleRun(LoadRule(RuleJson("[\"Windows\"]")));
            RoomHasEvaluation.Run(run, Rooms, Windows(), new[] { "Windows" }, null);
            Assert.Equal(2, run.Results.Count(r => r.Outcome.Kind == OutcomeKind.Pass));
            Assert.Equal(new long[] { 2, 4 }, run.Results.Where(r => r.Outcome.Kind == OutcomeKind.Fail).Select(r => r.Ref.Id).OrderBy(i => i).ToArray());
        }

        [Fact]
        public void ADoorBetweenTwoRooms_CountsForBoth_ButOnceWhenBothSidesAreTheSameRoom()
        {
            var c = new RoomHasCounts();
            c.Add(10, 20);
            Assert.Equal(1, c.For(10));
            Assert.Equal(1, c.For(20));
            Assert.Equal(0, c.For(30));
            c.Add(10, 10);                                                                  // both sides one room: one door
            Assert.Equal(2, c.For(10));
            c.Add(null, null);
            Assert.Equal(0, c.For(0));
        }

        [Fact]
        public void ANotEnclosedRoom_PlacedWithNoArea_HasNoOutcomeEither()
        {
            var run = new RuleRun(LoadRule(RuleJson()));
            RoomHasEvaluation.Run(run, new[] { Room(7, "2.01 Terbuka", placed: true, area: 0) }, Doors(), new[] { "Doors" }, null);
            Assert.Empty(run.Results);                                                      // room_enclosed counts it; here it would count twice
        }

        [Fact]
        public void Unreadable_IsNotVerified_NeverAPass()
        {
            var run = new RuleRun(LoadRule(RuleJson()));
            RoomHasEvaluation.Run(run, Rooms, null, new[] { "Doors" }, "the Doors could not be read: InvalidOperationException: phase");
            Assert.Equal(4, run.Results.Count);
            Assert.All(run.Results, r =>
            {
                Assert.Equal(OutcomeKind.NotVerified, r.Outcome.Kind);
                Assert.Equal(OutcomeCodes.Unreadable, r.Outcome.Code);
                Assert.Contains("phase", r.Outcome.Reason);
            });
            var noCounts = new RuleRun(LoadRule(RuleJson()));
            RoomHasEvaluation.Run(noCounts, Rooms, null, new[] { "Doors", "Windows" }, null);   // no map and no reason: still not a pass
            Assert.All(noCounts.Results, r => Assert.Equal(OutcomeKind.NotVerified, r.Outcome.Kind));
            Assert.Contains("Doors or Windows", noCounts.Results[0].Outcome.Reason);
        }

        [Fact]
        public void TheReport_ListsTheFailingRooms_WithAFailHandleForAll()
        {
            var set = RuleSetLoader.Load(CheckFixtures.Parse(RuleJson())).RuleSet;
            var run = new RuleRun(set.Rules.Single());
            RoomHasEvaluation.Run(run, Rooms, Doors(), new[] { "Doors" }, null);
            var handles = new Handles();
            var result = CheckReport.Build(set, new[] { run }, handles);
            var rule = Assert.Single(result.Rules);
            Assert.Equal(2, rule.Pass);
            Assert.Equal(2, rule.Fail);
            Assert.Equal(0, rule.NotVerified);
            Assert.Equal(2, rule.Top.Count);
            Assert.NotNull(rule.FailHandle);
            Assert.Equal(new long[] { 3, 4 }, handles.Puts.Single().OrderBy(i => i).ToArray());
        }

        private sealed class Handles : IHandleStore
        {
            public List<long[]> Puts { get; } = new();
            public string Put(IReadOnlyList<ElementRef> refs, string label) { Puts.Add(refs.Select(r => r.Id).ToArray()); return "h_" + Puts.Count.ToString("x8"); }
            public IReadOnlyList<ElementRef>? Get(string handle) => null;
        }
    }
}
