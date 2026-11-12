#nullable enable
// A long prose answer: the first-sentence headline, the word-count thresholds, the steps summary, and the server's prose result.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ProseAnswerTests
    {
        private static string Words(int n) => string.Join(" ", Enumerable.Range(1, n).Select(i => "word" + i));

        // ---- thresholds ------------------------------------------------------------------------

        [Theory]
        [InlineData(24, false)]
        [InlineData(25, true)]
        [InlineData(80, true)]
        public void Applies_from_25_words(int n, bool expected) => Assert.Equal(expected, ProseSplit.Applies(Words(n) + "."));

        [Theory]
        [InlineData(60, false)]
        [InlineData(61, true)]
        public void Collapses_above_60_words(int n, bool expected) => Assert.Equal(expected, ProseSplit.CollapseRest(Words(n)));

        [Fact]
        public void Punctuation_alone_is_not_a_word() => Assert.Equal(3, ProseSplit.WordCount("a - b — c ..."));

        [Fact]
        public void Structured_answers_are_left_alone()
        {
            Assert.False(ProseSplit.Applies("# Result\n" + Words(40)));
            Assert.False(ProseSplit.Applies("- first item\n" + Words(40)));
            Assert.False(ProseSplit.Applies("1. first item\n" + Words(40)));
            Assert.False(ProseSplit.Applies(Words(40) + "\n| a | b |\n|---|---|\n| 1 | 2 |"));
            Assert.False(ProseSplit.Applies(Words(40) + "\n```\ncode\n```"));
            Assert.False(ProseSplit.Applies(null));
        }

        // ---- first sentence --------------------------------------------------------------------

        [Fact]
        public void First_sentence_is_the_headline_and_the_rest_follows()
        {
            var (h, rest) = ProseSplit.Split("Most elements have 3 neighbours within 1 m. The top 5 are listed below, with their ids. Others have none.");
            Assert.Equal("Most elements have 3 neighbours within 1 m.", h);
            Assert.Equal("The top 5 are listed below, with their ids. Others have none.", rest);
        }

        [Fact]
        public void A_decimal_does_not_end_the_sentence()
        {
            var (h, rest) = ProseSplit.Split("The average gap is 1.5 m between the walls, which is fine. Next sentence here.");
            Assert.Equal("The average gap is 1.5 m between the walls, which is fine.", h);
            Assert.Equal("Next sentence here.", rest);
        }

        [Fact]
        public void An_abbreviation_does_not_end_the_sentence()
        {
            var (h, _) = ProseSplit.Split("Several walls, e.g. the ones on level 2, are too thin. Check them.");
            Assert.Equal("Several walls, e.g. the ones on level 2, are too thin.", h);
        }

        [Fact]
        public void Emphasis_is_removed_from_the_headline_and_numbers_stay()
        {
            var (h, rest) = ProseSplit.Split("**1,284 elements** have a neighbour within 1 m. More below.");
            Assert.Equal("1,284 elements have a neighbour within 1 m.", h);
            Assert.Equal("More below.", rest);
        }

        [Fact]
        public void A_very_long_first_sentence_is_cut_and_the_whole_text_is_kept_as_detail()
        {
            var text = Words(80) + ". Second sentence.";
            var (h, rest) = ProseSplit.Split(text);
            Assert.True(h.Length <= ProseSplit.MaxHeadlineChars + 1);
            Assert.Equal(text, rest);
        }

        [Fact]
        public void One_sentence_has_no_rest()
        {
            var (h, rest) = ProseSplit.Split("Done, every wall on level 1 now has the new fire rating.");
            Assert.Equal("Done, every wall on level 1 now has the new fire rating.", h);
            Assert.Equal("", rest);
        }

        [Fact]
        public void Empty_text_splits_to_nothing() => Assert.Equal(("", ""), ProseSplit.Split(null));

        // ---- steps summary ---------------------------------------------------------------------

        private static ProgressStep Step(string id, string label, StepState state, string outcome = "") =>
            new ProgressStep { StepId = id, Phase = "tool", Label = label, State = state, Outcome = outcome };

        [Fact]
        public void Failed_steps_collapse_into_one_line_and_the_worked_ones_are_rows()
        {
            var steps = new[]
            {
                Step("1", "Read elements", StepState.Error),
                Step("2", "Read elements", StepState.Error),
                Step("3", "Read elements", StepState.Done, "62 elements"),
            };
            Assert.Equal(2, StepsSummary.Failed(steps));
            Assert.Equal(1, StepsSummary.WorkedRows(steps).Count);
            Assert.Equal("2 earlier attempts failed, then it worked", StepsSummary.FailedLine(2, true, ReceiptLanguage.En));
            Assert.Equal("1 earlier attempt failed, then it worked", StepsSummary.FailedLine(1, true, ReceiptLanguage.En));
            Assert.Equal("2 attempts failed", StepsSummary.FailedLine(2, false, ReceiptLanguage.En));
            Assert.Equal("", StepsSummary.FailedLine(0, true, ReceiptLanguage.En));
        }

        [Fact]
        public void Failed_reads_known_only_from_the_evidence_still_count() =>
            Assert.Equal(3, StepsSummary.Failed(new[] { Step("1", "Read elements", StepState.Done) }, failedEvidence: 3));

        [Fact]
        public void More_detail_label_has_both_languages()
        {
            Assert.Equal("More detail ▾", StepsSummary.MoreDetailLabel(false, ReceiptLanguage.En));
            Assert.Equal("Less detail ▴", StepsSummary.MoreDetailLabel(true, ReceiptLanguage.En));
            Assert.Equal("Lagi butiran ▾", StepsSummary.MoreDetailLabel(false, ReceiptLanguage.Ms));
        }

        // ---- the server's prose result ---------------------------------------------------------

        private static TurnResultDto Prose(ResultCountsDto? counts = null, List<ResultCheckDto>? checks = null, List<ResultItemDto>? items = null) =>
            new TurnResultDto { Reply = "Most elements have a neighbour within 1 m.", Kind = TurnResultKinds.Selection, Counts = counts, Checks = checks, Items = items };

        [Fact]
        public void Selection_without_counts_is_a_prose_result()
        {
            Assert.True(ResultCardModel.IsProseResult(Prose()));
            Assert.True(ResultCardModel.IsProseResult(Prose(new ResultCountsDto())));
        }

        [Fact]
        public void Selection_with_counts_or_another_kind_is_not()
        {
            Assert.False(ResultCardModel.IsProseResult(Prose(new ResultCountsDto { Elements = 12, Types = 3 })));
            Assert.False(ResultCardModel.IsProseResult(new TurnResultDto { Reply = "x", Kind = TurnResultKinds.Changes }));
            Assert.False(ResultCardModel.IsProseResult(null));
        }

        [Fact]
        public void The_info_check_becomes_a_note_not_a_check_card()
        {
            var note = "3 earlier attempts failed (2 compile error, 1 time-out); the answer is from the run that worked.";
            var checks = new List<ResultCheckDto>
            {
                new ResultCheckDto { Severity = RuleSeverity.Info, Text = note },
                new ResultCheckDto { Severity = RuleSeverity.Warning, Text = "Two rooms are not enclosed." },
            };
            var card = new ResultCardModel().WithServerResult(Prose(checks: checks));
            Assert.True(card.ProseResult);
            Assert.Equal(new[] { note }, card.Notes);
            Assert.Single(card.Checks);
            Assert.Equal("Two rooms are not enclosed.", card.Checks[0].Text);
        }

        [Fact]
        public void A_prose_result_without_rows_draws_no_card()
        {
            var card = new ResultCardModel().WithServerResult(Prose(checks: new List<ResultCheckDto> { new ResultCheckDto { Severity = RuleSeverity.Info, Text = "1 earlier attempt failed." } }));
            Assert.Equal(ResultKind.None, card.Kind);
            Assert.True(card.ProseResult);
            Assert.Equal("Most elements have a neighbour within 1 m.", card.ServerReply);
        }

        [Fact]
        public void A_prose_result_with_rows_is_a_tally_card_under_the_server_headline()
        {
            var items = new List<ResultItemDto>
            {
                new ResultItemDto { Kind = ResultItemKinds.Row, Title = "Walls", Count = 40 },
                new ResultItemDto { Kind = ResultItemKinds.Row, Title = "Doors", Count = 12 },
            };
            var card = new ResultCardModel().WithServerResult(Prose(items: items));
            Assert.True(card.ProseResult);
            Assert.NotEqual(ResultKind.None, card.Kind);
            Assert.Equal("Most elements have a neighbour within 1 m.", card.Headline);
            Assert.True(card.ReplacesProse);
        }

        // ---- wiring (source text; WPF is checked by the headless repro on the PC) ---------------

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UI", "Copilot", "Model", "ProseSplit.cs"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void Steps_toggle_is_the_quiet_ghost_and_has_no_lime_outline_on_mouse_focus()
        {
            var kit = Src("UI/Copilot/Controls/CardKit.cs");
            Assert.DoesNotContain("IsKeyboardFocused", kit);
            Assert.Contains("FocusVisualStyle", kit);
            var evidence = Src("UI/Copilot/Controls/TurnEvidenceView.cs");
            Assert.Contains("HorizontalAlignment.Left", evidence);
            Assert.DoesNotContain("Read failed", evidence);
        }

        [Fact]
        public void Chat_view_lays_out_prose_answers_with_the_headline_and_hosts_steps_in_the_evidence()
        {
            var chat = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("ProseAnswer.Build", chat);
            Assert.Contains("ProseAnswer.MoreDetail", chat);
            Assert.Contains("stepsInEvidence", chat);
        }
    }
}
