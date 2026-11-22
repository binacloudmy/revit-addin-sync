#nullable enable
// The live "Working" card (design board Running.dc.html): the pure rules behind it.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RunningCardTests
    {
        private static ProgressStep Step(string id, string phase, string label, StepState state = StepState.Done, string outcome = "", string note = "") =>
            new ProgressStep { StepId = id, Phase = phase, Label = label, State = state, Outcome = outcome, Note = note };

        private static ToolCallDto Call(string tool, string args) =>
            new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = tool, Args = JsonDocument.Parse(args).RootElement.Clone() };

        // ---- the five segments ------------------------------------------------------------

        [Theory]
        [InlineData(0, 0)]
        [InlineData(19.9, 0)]
        [InlineData(20, 1)]
        [InlineData(45, 2)]
        [InlineData(79, 3)]
        [InlineData(80, 4)]
        [InlineData(100, 5)]
        [InlineData(250, 5)]
        [InlineData(-5, 0)]
        public void Segments_LightFloorOfPercentOver20(double pct, int done)
        {
            var seg = RunningCard.Segments(pct, running: true);
            Assert.Equal(5, seg.Count);
            Assert.Equal(done, seg.Count(s => s == SegmentState.Done));
        }

        [Fact]
        public void Segments_TheNextOnePulsesWhileRunning_AndNothingPulsesOnceFull()
        {
            var seg = RunningCard.Segments(45, running: true);
            Assert.Equal(new[] { SegmentState.Done, SegmentState.Done, SegmentState.Current, SegmentState.Pending, SegmentState.Pending }, seg);
            Assert.DoesNotContain(SegmentState.Current, RunningCard.Segments(100, running: true));
            Assert.DoesNotContain(SegmentState.Current, RunningCard.Segments(45, running: false));
        }

        // ---- rows -----------------------------------------------------------------------

        [Fact]
        public void Rows_DropTheDeveloperLine_TheThinkingRows_AndTheWritePreviewRow()
        {
            var rows = RunningCard.Rows(new[]
            {
                Step("thinking", "", "Thinking…", StepState.Running),
                Step("p:2", "thinking", "Thinking", StepState.Done),
                Step("budget", "budget", "Tools 1/60", StepState.Done),
                Step("write:r", WritePreviewText.Phase, "What I will write", StepState.Running),
                Step("call:1", "tool", "Finding elements", StepState.Done, outcome: "62 elements"),
            });
            var one = Assert.Single(rows);
            Assert.Equal("Finding elements", one.Text);
            Assert.Equal("62 elements", one.Outcome);
        }

        [Fact]
        public void Rows_FoldConsecutiveIdenticalLabelsIntoOne()
        {
            var rows = RunningCard.Rows(new[]
            {
                Step("call:1", "tool", "Reading the model overview"),
                Step("call:2", "tool", "Reading the model overview"),
                Step("call:3", "tool", "Reading the model overview", StepState.Running, note: "Counting categories"),
                Step("call:4", "tool", "Finding elements"),
                Step("call:5", "tool", "Reading the model overview"),
            });
            Assert.Equal(3, rows.Count);
            Assert.Equal(3, rows[0].Times);
            Assert.Equal(StepState.Running, rows[0].State);
            Assert.Equal("×3", rows[0].Outcome);
            Assert.Equal("Counting categories", rows[0].Note);
            Assert.Equal("Finding elements", rows[1].Text);
            Assert.Equal(1, rows[2].Times);          // not consecutive with the first run: its own row
        }

        [Fact]
        public void Rows_AFoldKeepsTheRealOutcome_AndAnErrorShowsOnTheRow()
        {
            var rows = RunningCard.Rows(new[]
            {
                Step("call:1", "tool", "Finding elements", outcome: "10 elements"),
                Step("call:2", "tool", "finding elements", StepState.Error),
            });
            var one = Assert.Single(rows);
            Assert.Equal("10 elements", one.Outcome);
            Assert.Equal(StepState.Error, one.State);
        }

        [Fact]
        public void Rows_OnlyARunningRowCarriesItsNote()
        {
            var rows = RunningCard.Rows(new[]
            {
                Step("call:1", "tool", "Finding elements", StepState.Done, note: "old note"),
                Step("call:2", "tool", "Checking the rules", StepState.Running, note: "Running the check rules"),
            });
            Assert.Equal("", rows[0].Note);
            Assert.Equal("Running the check rules", rows[1].Note);
        }

        [Fact]
        public void Rows_NullAndEmptyAreFine()
        {
            Assert.Empty(RunningCard.Rows(null));
            Assert.Empty(RunningCard.Rows(new List<ProgressStep>()));
        }

        // ---- label runs ----------------------------------------------------------------

        [Fact]
        public void LabelParts_BackticksMakeMonoRuns()
        {
            var parts = RunningCard.LabelParts("Found the `Adrino LED 601/10/840` row");
            Assert.Equal(new[] { ("Found the ", false), ("Adrino LED 601/10/840", true), (" row", false) }, parts);
            Assert.Equal(new[] { ("plain", false) }, RunningCard.LabelParts("plain"));
            Assert.Empty(RunningCard.LabelParts(""));
            Assert.Empty(RunningCard.LabelParts(null));
        }

        [Fact]
        public void LabelParts_AnOddBacktickIsDroppedNotHalfMono()
        {
            Assert.Equal(new[] { ("Found the Adrino row", false) }, RunningCard.LabelParts("Found the `Adrino row"));
        }

        // ---- outcome and note ------------------------------------------------------------

        [Theory]
        [InlineData(ToolNames.FindElements, 62, false, "62 elements")]
        [InlineData(ToolNames.FindElements, 1, false, "1 element")]
        [InlineData(ToolNames.FindElements, 2127, false, "2,127 elements")]
        [InlineData(ToolNames.FindElements, 62, true, "62 elemen")]
        [InlineData(ToolNames.Check, 7, false, "7 rules")]
        [InlineData(ToolNames.SetParameters, 3, false, "3 values")]
        [InlineData(ToolNames.ModelOverview, 12, false, "12")]
        public void OutcomeText_CountWithTheNounOfTheTool(string tool, int count, bool ms, string expected) =>
            Assert.Equal(expected, RunningCard.OutcomeText(tool, count, ms));

        [Fact]
        public void OutcomeText_NothingCountedSaysNothing()
        {
            Assert.Equal("", RunningCard.OutcomeText(ToolNames.FindElements, null, false));
            Assert.Equal("", RunningCard.OutcomeText(ToolNames.FindElements, 0, false));
        }

        [Fact]
        public void ActiveNote_NamesTheViewWhenKnown()
        {
            Assert.Equal("Looking through the model in {3D}", RunningCard.ActiveNote(ToolNames.FindElements, "3D", false));
            Assert.Equal("Looking through the model", RunningCard.ActiveNote(ToolNames.FindElements, "", false));
            Assert.Equal("Menyaring model dalam {3D}", RunningCard.ActiveNote(ToolNames.FindElements, "3D", true));
            Assert.Equal("", RunningCard.ActiveNote("something_else", "3D", false));
        }

        // ---- Stop row helper --------------------------------------------------------------

        [Fact]
        public void StopHelper_PromisesNothingChangedWhileTheValuesAreShownAndNotWritten()
        {
            var steps = new[] { Step("write:r", WritePreviewText.Phase, "What I will write", StepState.Running) };
            Assert.Equal(WritePreviewText.NothingWritten(false), RunningCard.StopHelper(steps, false));
            Assert.Equal(WritePreviewText.NothingWritten(true), RunningCard.StopHelper(steps, true));
        }

        [Fact]
        public void StopHelper_SaysNothingOnceTheWriteBegan_AndASaysOnlyReadingForAReadTurn()
        {
            var wrote = new[] { Step("write:r", WritePreviewText.Phase, "What I will write", StepState.Done) };
            Assert.Equal("", RunningCard.StopHelper(wrote, false));
            var previewing = new[] { Step("preview:r", "preview", "Previewing the change in Revit…", StepState.Running) };
            Assert.Equal("", RunningCard.StopHelper(previewing, false));
            var reading = new[] { Step("call:1", "tool", "Finding elements", StepState.Running) };
            Assert.Equal("Only reading. Stopping changes nothing.", RunningCard.StopHelper(reading, false));
        }

        // ---- write preview records ---------------------------------------------------------

        [Fact]
        public void Records_OnePerSetParametersCall_ValueAsTyped()
        {
            var recs = WritePreviewText.Records(new[]
            {
                Call(ToolNames.SetParameters, "{\"param\":\"Model\",\"value\":\"Adrino LED 601/10/840\"}"),
                Call(ToolNames.SetParameters, "{\"param\":\"Count\",\"value\":3}"),
            }, ms: false);
            Assert.Equal(new[] { new PreviewRecord("Model", "Adrino LED 601/10/840", false), new PreviewRecord("Count", "3", false) }, recs);
        }

        [Fact]
        public void Records_AMissingOrBlankValueIsTheDerivedRow()
        {
            var recs = WritePreviewText.Records(new[]
            {
                Call(ToolNames.SetParameters, "{\"param\":\"Type Comments\"}"),
                Call(ToolNames.SetParameters, "{\"param\":\"URL\",\"value\":\"  \"}"),
                Call(ToolNames.SetParameters, "{\"param\":\"Mark\",\"value\":null}"),
            }, ms: false);
            Assert.All(recs, r => { Assert.True(r.Derived); Assert.Equal("", r.Value); });
            Assert.Equal(new[] { "Type Comments", "URL", "Mark" }, recs.Select(r => r.Key));
        }

        [Fact]
        public void Records_TableRowsRunCodeAndTheCap()
        {
            var table = WritePreviewText.Records(new[] { Call(ToolNames.SetParameters, "{\"param\":\"Mark\",\"table_rows\":[1,2,3]}") }, false);
            Assert.Equal(new PreviewRecord("Mark", "3 values from the table", false), Assert.Single(table));

            var code = WritePreviewText.Records(new[] { Call(ToolNames.RunRevitCode, "{}") }, false);
            Assert.Equal("Revit code", Assert.Single(code).Key);

            var many = Enumerable.Range(0, 11).Select(i => Call(ToolNames.SetParameters, "{\"param\":\"P" + i + "\",\"value\":\"v\"}")).ToList();
            var capped = WritePreviewText.Records(many, false);
            Assert.Equal(9, capped.Count);
            Assert.Equal(new PreviewRecord("…", "+3 more", false), capped[8]);
        }

        [Fact]
        public void Records_LongValuesAreCut_AndOtherToolsAreIgnored()
        {
            var long80 = new string('x', 200);
            var recs = WritePreviewText.Records(new[]
            {
                Call(ToolNames.SetParameters, "{\"param\":\"Desc\",\"value\":\"" + long80 + "\"}"),
                Call(ToolNames.FindElements, "{}"),
            }, false);
            var one = Assert.Single(recs);
            Assert.Equal(80, one.Value.Length);
            Assert.EndsWith("…", one.Value);
            Assert.Empty(WritePreviewText.Records(null, false));
        }

        [Fact]
        public void PreviewRecords_ComeFromTheLatestWriteStep_AndOpenUntilItIsDone()
        {
            var s = Step("write:r", WritePreviewText.Phase, "What I will write", StepState.Running);
            s.Preview = new[] { new PreviewRecord("Model", "A", false) };
            Assert.Single(RunningCard.PreviewRecords(new[] { s }));
            Assert.True(RunningCard.PreviewOpen(new[] { s }));
            s.State = StepState.Done;
            Assert.False(RunningCard.PreviewOpen(new[] { s }));
            Assert.Empty(RunningCard.PreviewRecords(new[] { Step("call:1", "tool", "x") }));
        }

        // ---- the composer's scope chip and strings -------------------------------------------

        [Theory]
        [InlineData("3D", 52, "{3D} · 52 selected")]
        [InlineData("Level 1", 0, "{Level 1}")]
        [InlineData("  ", 4, "")]
        [InlineData(null, 4, "")]
        public void ScopeText_ViewThenSelection(string? view, int selected, string expected) =>
            Assert.Equal(expected, RunningText.ScopeText(view, selected));

        [Fact]
        public void TheRunningComposerStrings_FollowTheBoard()
        {
            Assert.Equal("Type the next step. It runs when this one finishes.", RunningText.QueuePlaceholder);
            Assert.Equal("Queues after this run", RunningText.QueueNote);
        }

        // ---- the view reads only tokens that exist ---------------------------------------------

        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }

        [Theory]
        [InlineData("UI/Copilot/Controls/RunningCardView.cs")]
        [InlineData("UI/Copilot/Controls/PromptBar.xaml.cs")]
        public void EveryRunTokenTheViewsUse_IsInTheTokenTable(string file)
        {
            var text = File.ReadAllText(Path.Combine(Root(), file));
            var used = Regex.Matches(text, "\"(Cp\\.Run\\.[A-Za-z0-9.]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.NotEmpty(used);
            var missing = used.Where(k => !ThemeTokens.Solid.ContainsKey(k) && k != "Cp.Run.Font" && k != "Cp.Run.FontMono").ToList();
            Assert.True(missing.Count == 0, "Not in ThemeTokens: " + string.Join(", ", missing));
        }

        [Fact]
        public void TheRunningFontsAreShipped_AndTheLicenceTravelsWithThem()
        {
            var fonts = Path.Combine(Root(), "Fonts");
            foreach (var f in new[] { "Geist-Regular.ttf", "Geist-Medium.ttf", "Geist-SemiBold.ttf", "GeistMono-Regular.ttf", "GeistMono-Medium.ttf", "Geist-OFL.txt" })
                Assert.True(File.Exists(Path.Combine(fonts, f)), f);
            Assert.Contains("SIL OPEN FONT LICENSE", File.ReadAllText(Path.Combine(fonts, "Geist-OFL.txt")).ToUpperInvariant());
        }

        [Fact]
        public void TheLiveRowUsesTheRunningCard_AndTheStatusStripStepsAsideWhileRunning()
        {
            var chat = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/Screens/ChatView.xaml.cs"));
            Assert.Contains("col.Children.Add(RunningPanel(m))", chat);
            Assert.Contains("CopilotLabel()", chat);
            Assert.Contains("emptyThread || running ? Visibility.Collapsed : Visibility.Visible", chat);
        }

        [Fact]
        public void TheScopeChip_ShrinksWithAnEllipsis_SoTheQueueNoteStaysWhole()
        {
            var s = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/Controls/PromptBar.xaml.cs"));
            Assert.Contains("var inner = new Grid();", s);
            Assert.Contains("MaxWidth = 128", s);
            Assert.Contains("TextTrimming = TextTrimming.CharacterEllipsis", s);
            Assert.Contains("_queueNote = new TextBlock { Text = RunningText.QueueNote, FontSize = 11, TextWrapping = TextWrapping.NoWrap", s);
        }
    }
}
