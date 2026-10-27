#nullable enable
// W3 final round (R78, review I-1 and M-5). Every write envelope carries
// changes.touched_by_category, counted from the full touched set; a code call
// emits no modify rows with an empty field (the counts carry them); touched
// lists are capped at 50 rows with a handle for the rest; and the envelope
// is re-checked against the §2.1 invariants after the engine changed it.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class EnvelopeChangesTests
    {
        private static ChangesDto Touched(int added, int modified, int deleted, long start = 1000)
        {
            var t = new ChangesDto();
            for (var i = 0; i < added; i++) t.Added.Add(new ElementRef(start + i, null));
            for (var i = 0; i < modified; i++) t.Modified.Add(new FieldChangeDto { Id = start + 1000 + i, Field = "" });
            for (var i = 0; i < deleted; i++) t.Deleted.Add(new ElementRef(start + 2000 + i, null));
            return t;
        }

        [Fact]
        public void CountsTheFullTouchedSetPerCategory()
        {
            var t = Touched(added: 0, modified: 34, deleted: 7);
            var cats = new Dictionary<long, string>();
            foreach (var m in t.Modified) cats[m.Id] = "Walls";
            for (var i = 0; i < 7; i++) cats[t.Deleted[i].Id] = i < 2 ? "Sheets" : "Views";

            var by = EnvelopeChanges.ByCategory(t, cats);

            Assert.Equal(34, by.ModifiedByCategory["Walls"]);
            Assert.Equal(2, by.DeletedByCategory["Sheets"]);
            Assert.Equal(5, by.DeletedByCategory["Views"]);
            Assert.Empty(by.AddedByCategory);
            // An id with no known category is counted as Other.
            Assert.Equal(1, EnvelopeChanges.ByCategory(Touched(1, 0, 0), new Dictionary<long, string>()).AddedByCategory["Other"]);
        }

        [Fact]
        public void CodeRowsWithAnEmptyFieldAreDropped()
        {
            var t = Touched(0, 3, 0);
            t.Modified[1].Field = "Mark";
            t.Modified[1].Before = "A";
            t.Modified[1].After = "B";
            EnvelopeChanges.DropEmptyFieldRows(t);
            var row = Assert.Single(t.Modified);
            Assert.Equal("Mark", row.Field);
        }

        [Fact]
        public void LongTouchedListsAreCappedWithAHandle()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran the code.",
                Changes = new ChangeReportDto { Touched = Touched(added: 3000, modified: 60, deleted: 2) },
                Verified = new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = 3062 },
            };
            IReadOnlyList<ElementRef>? put = null;

            EnvelopeChanges.Cap(env, env.Changes.Touched.Added.Concat(env.Changes.Touched.Modified.Select(m => new ElementRef(m.Id, null))).ToList(),
                refs => { put = refs; return "h_0001abcd"; });

            Assert.Equal(EnvelopeLimits.MaxRows, env.Changes.Touched.Added.Count);
            Assert.Equal(EnvelopeLimits.MaxRows, env.Changes.Touched.Modified.Count);
            Assert.Equal(2, env.Changes.Touched.Deleted.Count);
            Assert.Equal("h_0001abcd", env.Handle);
            Assert.Equal(3060, put!.Count);
            Assert.Empty(EnvelopeChanges.Recheck(env));
        }

        [Fact]
        public void ShortListsKeepTheirRowsAndAnyExistingHandle()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview, Summary = "Preview.", Handle = "h_tool",
                Changes = new ChangeReportDto { Touched = Touched(2, 2, 0) },
            };
            var called = false;
            EnvelopeChanges.Cap(env, new List<ElementRef>(), refs => { called = true; return "h_x"; });
            Assert.False(called);
            Assert.Equal("h_tool", env.Handle);
            Assert.Equal(2, env.Changes!.Touched.Added.Count);
        }

        [Fact]
        public void RecheckRepairsWhatTheEngineCouldBreak()
        {
            var env = new Envelope { Ok = false, Status = EnvelopeStatus.Failed, Phase = EnvelopePhase.Commit, Summary = new string('x', 400) };
            var violations = EnvelopeChanges.Recheck(env);
            Assert.NotEmpty(violations);                       // what was wrong, for the log
            Assert.Empty(EnvelopeInvariants.Violations(env));  // and it is valid now
            Assert.NotNull(env.Error);
        }
    }
}
