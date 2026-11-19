#nullable enable
// r13 (0.0.148), live fdda8df4 (hide tags) and fb713fc2 (section box) on 0.0.147 (frames R2-01b, R7-18): the server's read-back after a
// write ("Checked: section box on, name ..., box bottom -300 mm ...") was drawn inside the Notes box as one more remark. An info check that
// starts with "Checked:" and has no answers gets its own compact row (a small green tick and the line) above Notes; warnings stay amber,
// every other check is drawn as before.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class VerifiedRow0148Tests
    {
        private const string SectionBox = "Checked: section box on, name Section Box - PTBR_Aras 01, box bottom -300 mm from PTBR_Aras 01, box top +3700 mm from PTBR_Aras 01, box x 51152 mm, box y 35083 mm.";
        private const string HideTags = "Checked: Door Tags hidden 45, Multi-Category Tags hidden 7, Plumbing Fixture Tags hidden 21, Room Tags hidden 39, Window Tags hidden 39.";

        [Fact]
        public void TheServersReadBack_IsItsOwnRow()
        {
            Assert.True(ChecksCardText.IsVerified("info", SectionBox, 0));
            Assert.True(ChecksCardText.IsVerified("info", HideTags, 0));
            Assert.True(ChecksCardText.IsVerified(" INFO ", "  checked: rows 46, fields ok", 0));
        }

        [Fact]
        public void EveryOtherCheck_IsDrawnAsBefore()
        {
            Assert.False(ChecksCardText.IsVerified("warning", SectionBox, 0));   // a doubt that starts with "Checked:" stays amber
            Assert.False(ChecksCardText.IsVerified("error", SectionBox, 0));
            Assert.False(ChecksCardText.IsVerified("", SectionBox, 0));
            Assert.False(ChecksCardText.IsVerified(null, SectionBox, 0));
            Assert.False(ChecksCardText.IsVerified("info", SectionBox, 1));       // a check with answers keeps its buttons
            Assert.False(ChecksCardText.IsVerified("info", "Not tags, left visible: 141 Text Notes, 26 Spot Elevations.", 0));
            Assert.False(ChecksCardText.IsVerified("info", "Hanya Aras 01 yang dikotakkan; aras lain tidak dimasukkan.", 0));
            Assert.False(ChecksCardText.IsVerified("info", "I checked: the walls", 0));   // only at the start
            Assert.False(ChecksCardText.IsVerified("info", "", 0));
            Assert.False(ChecksCardText.IsVerified("info", null, 0));
        }

        [Fact]
        public void TheSectionBoxTurn_SplitsIntoOneReadBackRow_AndTwoNotes()
        {
            var result = new TurnResultDto
            {
                Checks = new List<ResultCheckDto>
                {
                    new ResultCheckDto { Severity = "info", Text = SectionBox },
                    new ResultCheckDto { Severity = "info", Text = "Bawah kotak -300 mm dari PTBR_Aras 01, atas +3700 mm (di bawah PTBR_Beam Aras 02 pada 4550 mm)." },
                    new ResultCheckDto { Severity = "info", Text = "Hanya Aras 01 yang dikotakkan; aras lain tidak dimasukkan." },
                    new ResultCheckDto { Severity = "warning", Text = "Template jkrAR_07d locks V/G, so tags were hidden by elements." },
                },
            };
            var items = CheckItems.From(result);
            var verified = items.Where(c => ChecksCardText.IsVerified(c.Severity, c.Text, c.Actions.Count)).ToList();
            var notes = items.Where(c => ChecksCardText.IsNote(c.Severity) && !ChecksCardText.IsVerified(c.Severity, c.Text, c.Actions.Count)).ToList();
            var doubts = items.Where(c => !ChecksCardText.IsNote(c.Severity)).ToList();
            Assert.Equal(new[] { SectionBox }, verified.Select(c => c.Text));
            Assert.Equal(2, notes.Count);
            Assert.Single(doubts);
        }

        [Fact]
        public void TheCard_DrawsTheReadBackBetweenTheDoubtsAndNotes_AndKeepsItOutOfNotes()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var src = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/ChecksCard.cs")).Replace("\r\n", "\n");
            var doubts = src.IndexOf("AddChecks(root, card, ms, actions, notes: false);", System.StringComparison.Ordinal);
            var verified = src.IndexOf("AddVerified(root, card, actions);", System.StringComparison.Ordinal);
            var notes = src.IndexOf("AddChecks(root, card, ms, actions, notes: true);", System.StringComparison.Ordinal);
            Assert.True(doubts >= 0 && doubts < verified && verified < notes);
            // Filtered out before the empty check, so a Notes box holding only the read-back is never drawn empty.
            Assert.Contains(".Where(c => !ChecksCardText.IsVerified(c.Severity, c.Text, c.Actions.Count)).ToList();", src);
            Assert.Contains("Icon(TickIcon, \"Cp.Green\", 2, 12)", src);
        }
    }
}
