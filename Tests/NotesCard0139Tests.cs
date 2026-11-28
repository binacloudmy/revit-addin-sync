#nullable enable
// UX review Q2 (18 Nov): the amber "Check N things" card was used for plain information ("Both values are instance parameters on 1 door ..."). The contract
// already carries result_check.severity = error | warning | info: info is drawn calm, under "Notes"; error and warning are untouched.
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class NotesCard0139Tests
    {
        [Fact]
        public void InfoIsANote_ErrorAndWarningAreNot()
        {
            Assert.True(ChecksCardText.IsNote("info"));
            Assert.True(ChecksCardText.IsNote(" INFO "));
            Assert.False(ChecksCardText.IsNote("warning"));
            Assert.False(ChecksCardText.IsNote("error"));
            Assert.False(ChecksCardText.IsNote(""));
            Assert.False(ChecksCardText.IsNote(null));
            Assert.False(ChecksCardText.IsNote("note"));   // a severity the pane does not know keeps the amber card
        }

        [Fact]
        public void TheNotesTitle_IsPlain_InBothLanguages()
        {
            Assert.Equal("Notes", ChecksCardText.NotesTitle(ms: false));
            Assert.Equal("Nota", ChecksCardText.NotesTitle(ms: true));
        }

        [Fact]
        public void TheResultsChecks_KeepTheirSeverity_ThroughCheckItems()
        {
            var result = new TurnResultDto
            {
                Checks = new System.Collections.Generic.List<ResultCheckDto>
                {
                    new ResultCheckDto { Severity = "info", Text = "Both values are instance parameters on 1 door." },
                    new ResultCheckDto { Severity = "warning", Text = "Value types look odd." },
                },
            };
            var items = CheckItems.From(result);
            Assert.Equal(new[] { "info", "warning" }, items.Select(c => c.Severity));
            Assert.Equal(new[] { true, false }, items.Select(c => ChecksCardText.IsNote(c.Severity)));
        }

        [Fact]
        public void TheCard_DrawsNotesCalm_AndTheDoubtsAmber()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var src = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/ChecksCard.cs"));
            Assert.Contains("AddChecks(root, card, ms, actions, notes: false);", src);
            Assert.Contains("AddChecks(root, card, ms, actions, notes: true);", src);
            Assert.Contains("CardKit.Card(notes ? \"Cp.Line\" : \"Cp.AmberLn\"", src);
            Assert.Contains("CardKit.Header(notes ? \"Cp.Sunken\" : \"Cp.AmberBg\"", src);
            Assert.Contains("title.Text = notes ? ChecksCardText.NotesTitle(ms) : ChecksCardText.Title(open, ms);", src);
        }
    }
}
