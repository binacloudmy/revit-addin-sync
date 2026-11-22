using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FittingChangeNoteTests
    {
        private static readonly string[] None = new string[0];

        [Fact]
        public void NoRetypedCurves_NoNote()
        {
            Assert.Null(FittingChangeNote.Build(None, new[] { "Tee" }, new[] { "Elbow" }, None));
        }

        [Fact]
        public void RetypedButNoFittingChanged_NoNote()
        {
            Assert.Null(FittingChangeNote.Build(new[] { "Pipes" }, None, None, None));
        }

        [Fact]
        public void NamesCountsPerFittingType()
        {
            var note = FittingChangeNote.Build(new[] { "Pipes", "Pipes" },
                new[] { "Elbow - Generic", "Elbow - Generic", "Tee - Generic" }, None,
                new[] { "Elbow - Generic", "Tee - Generic", "Tee - Generic" });
            Assert.NotNull(note);
            Assert.Contains("2 Pipes", note);
            Assert.Contains("2 Elbow - Generic, 1 Tee - Generic replaced or added", note);
            Assert.Contains("2 Tee - Generic, 1 Elbow - Generic removed", note);
        }

        [Fact]
        public void ModifiedOnly_IsEnough_AndManyKindsFold()
        {
            var note = FittingChangeNote.Build(new[] { "Ducts" }, None,
                new[] { "A", "B", "C", "D", "E", "F" }, None);
            Assert.NotNull(note);
            Assert.Contains("2 other modified", note);
        }
    }
}
