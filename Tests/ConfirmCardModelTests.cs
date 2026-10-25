#nullable enable
// W5 Task 3: the confirm card (spec §7.5) is built only from what the add-in
// measured: deletions first and in red, before -> after from the preview,
// side effects, warnings, the code in full, add-in-owned labels. The cloud's
// words sit on their own labelled line. One decision per card. Revit-free.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ConfirmCardModelTests
    {
        private static Envelope Preview(int modified = 0, int added = 0, int deleted = 0,
            Dictionary<string, int>? sideModified = null, Dictionary<string, int>? sideDeleted = null) => new Envelope
        {
            Ok = true,
            Status = EnvelopeStatus.Done,
            Phase = EnvelopePhase.Preview,
            Summary = "Preview.",
            Changes = new ChangeReportDto
            {
                Touched = new ChangesDto
                {
                    Modified = Enumerable.Range(1, modified).Select(i => new FieldChangeDto
                        { Id = i, Field = "Height Offset From Level", Before = "2400 mm", After = "2700 mm" }).ToList(),
                    Added = Enumerable.Range(100, added).Select(i => new ElementRef(i, null)).ToList(),
                    Deleted = Enumerable.Range(200, deleted).Select(i => new ElementRef(i, null)).ToList(),
                },
                SideEffects = new SideEffectsDto
                {
                    ModifiedByCategory = sideModified ?? new Dictionary<string, int>(),
                    DeletedByCategory = sideDeleted ?? new Dictionary<string, int>(),
                },
            },
            PlanHash = "sha256:24e39f414fb880038fa382b6deb6d8702dd902117c003f1006412df64e4a0035",
            Warnings = new List<WarningDto> { new WarningDto { Text = "Ceiling is above the level above it.", Severity = "warning", Ids = new List<long> { 1 } } },
        };

        private static ToolCallDto Call(string tool, object args) =>
            new ToolCallDto { CallId = "c", Tool = tool, Args = JsonSerializer.SerializeToElement(args) };

        [Fact]
        public void DeletionsComeFirstInRedWithAHardWarning()
        {
            var req = new ConfirmRequest
            {
                BatchId = "b1",
                Preview = new[] { Preview(modified: 34, deleted: 3, sideDeleted: new Dictionary<string, int> { ["Tags"] = 4 }) },
                PlanHash = "sha256:abc",
                AssistantDescription = "Naikkan 34 siling di Aras 2 ke 2700 mm.",
            };

            var card = ConfirmCardModel.From(req, new[] { Call("set_parameters", new { param = "Height Offset From Level", value = "2700 mm" }) }, ReceiptLanguage.En);

            Assert.Equal(ConfirmRowKind.Delete, card.Rows[0].Kind);
            Assert.True(card.Rows[0].Danger);
            Assert.Contains("3", card.Rows[0].Text);
            Assert.True(card.HardWarning);
            Assert.Equal("Delete 7 · Change 34 elements", card.Headline);   // touched 3 + side effects 4
            Assert.Equal(ConfirmRowKind.Delete, card.Rows[1].Kind);
            Assert.Contains("Tags 4", card.Rows[1].Text);
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Modify && r.Text.Contains("2400 mm → 2700 mm") && r.Text.Contains("34"));
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Warning && r.Text.Contains("above the level"));
            // The cloud's words never reach the headline or the rows.
            Assert.DoesNotContain("Naikkan", card.Headline);
            Assert.All(card.Rows, r => Assert.DoesNotContain("Naikkan", r.Text));
            Assert.Equal("Naikkan 34 siling di Aras 2 ke 2700 mm.", card.AssistantText);
            Assert.Equal("Assistant's description", card.AssistantLabel);
        }

        [Fact]
        public void SideEffectsLargerThanTheTouchedSet_AreAHardWarning()
        {
            var req = new ConfirmRequest
            {
                Preview = new[] { Preview(modified: 2, sideModified: new Dictionary<string, int> { ["Rooms"] = 6, ["Schedules"] = 2 }) },
            };

            var card = ConfirmCardModel.From(req, null, ReceiptLanguage.Ms);

            Assert.True(card.HardWarning);
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.SideEffect && r.Text.Contains("Rooms 6"));
            Assert.StartsWith("Ubah 2", card.Headline);
            Assert.Equal("Penerangan pembantu", card.AssistantLabel);
        }

        [Fact]
        public void PlainEditIsNotAHardWarning_AndOffersBuildOnlyWhenAsked()
        {
            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { Preview(modified: 34) } }, null, ReceiptLanguage.En);
            Assert.False(card.HardWarning);
            Assert.False(card.OfferApproveThisBuild);

            var build = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { Preview(added: 12) }, OfferApproveThisBuild = true }, null, ReceiptLanguage.En);
            Assert.True(build.OfferApproveThisBuild);
            Assert.StartsWith("Add 12", build.Headline);
        }

        [Fact]
        public void CodeIsShownInFull_IntentGoesOnTheAssistantLine()
        {
            var code = string.Join("\n", Enumerable.Range(1, 80).Select(i => "Print(" + i + ");"));
            var card = ConfirmCardModel.From(
                new ConfirmRequest { Preview = new[] { Preview(added: 1) } },
                new[] { Call("run_revit_code", new { code, intent = "Create sheet A-101" }) },
                ReceiptLanguage.En);

            Assert.Equal(code, card.Code);
            Assert.Contains("Create sheet A-101", card.AssistantText);
            Assert.Equal("Run Revit code", card.ToolLabel);
        }

        [Fact]
        public void PreviewThatCouldNotRun_SaysSoPlainly()
        {
            var failed = EnvelopeFactory.Failed(ErrorCodes.NotOwned, "4 ceilings are borrowed by aisyah.");
            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { failed } }, null, ReceiptLanguage.En);

            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Failure && r.Text.Contains("borrowed"));
            Assert.True(card.EstimateOnly);
        }

        [Fact]
        public async Task DecidesExactlyOnce()
        {
            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { Preview(modified: 1) } }, null, ReceiptLanguage.En);

            Assert.True(card.TryDecide(new ConfirmDecision { Approved = true }));
            Assert.False(card.TryDecide(new ConfirmDecision { Approved = false }));

            var d = await card.Decision;
            Assert.True(d.Approved);
            Assert.True(card.Resolved);
        }

        [Fact]
        public async Task CancellationDeclines()
        {
            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { Preview(modified: 1) } }, null, ReceiptLanguage.En);
            using var cts = new CancellationTokenSource();
            card.BindCancellation(cts.Token);

            cts.Cancel();
            var d = await card.Decision;

            Assert.False(d.Approved);
            Assert.False(card.TryDecide(new ConfirmDecision { Approved = true }));
        }
    }
}
