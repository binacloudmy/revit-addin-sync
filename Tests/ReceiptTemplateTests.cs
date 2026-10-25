#nullable enable
// W5 Task 3: receipts, evidence and the caution line are rendered by the pane
// from envelopes (spec §7.11), never from the reply text, in the drafter's
// language (EN/BM). Revit-free.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ReceiptTemplateTests
    {
        private static ToolRequestDto WriteReq(params string[] tools) => new ToolRequestDto
        {
            RequestId = "req_w",
            Kind = ToolRequestKinds.Write,
            Calls = tools.Select((t, i) => new ToolCallDto { CallId = "c" + i, Tool = t, Args = JsonSerializer.SerializeToElement(new { }) }).ToList(),
        };

        private static ToolRequestDto ReadReq(params string[] tools) => new ToolRequestDto
        {
            RequestId = "req_r",
            Kind = ToolRequestKinds.Read,
            Calls = tools.Select((t, i) => new ToolCallDto { CallId = "r" + i, Tool = t, Args = JsonSerializer.SerializeToElement(new { }) }).ToList(),
        };

        /// <summary>34 targets, 4 skipped because another user owns them.</summary>
        private static Envelope PartialNotOwned() => new Envelope
        {
            Ok = false,
            Status = EnvelopeStatus.Partial,
            Phase = EnvelopePhase.Commit,
            Summary = "Set Height Offset From Level on 30 of 34 ceilings; 4 are owned by another user.",
            Count = 34,
            Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 30 },
            Error = new ErrorDto { Code = ErrorCodes.NotOwned, Message = "4 ceilings are borrowed by aisyah." },
        };

        private static Envelope Done(int n) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "ok", Count = n,
            Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = n },
        };

        [Fact]
        public void RendersFractionsInBothLanguages()
        {
            var ev = new TurnEvidence();
            ev.Add(WriteReq("set_parameters"), new[] { PartialNotOwned() });

            var facts = ev.Receipt;
            Assert.Equal(30, facts.Done);
            Assert.Equal(34, facts.Total);

            Assert.Equal("30/34 done · 4 failed (not owned)", ReceiptTemplates.Headline(facts, ReceiptLanguage.En));
            Assert.Equal("30/34 siap · 4 gagal (milik pengguna lain)", ReceiptTemplates.Headline(facts, ReceiptLanguage.Ms));
            Assert.True(ev.Caution);
            Assert.NotEmpty(ReceiptTemplates.Caution(facts, ReceiptLanguage.En));
            Assert.NotEmpty(ReceiptTemplates.Caution(facts, ReceiptLanguage.Ms));
        }

        [Fact]
        public void SumsBatchesAndKeepsEveryOutcomeVisible()
        {
            var ev = new TurnEvidence();
            ev.Add(WriteReq("set_parameters"), new[] { Done(12) });
            ev.Add(WriteReq("set_parameters", "run_revit_code"), new[]
            {
                new Envelope { Ok = true, Status = EnvelopeStatus.Rejected, Phase = EnvelopePhase.Preview, Summary = "Declined.", Count = 5 },
                new Envelope { Ok = true, Status = EnvelopeStatus.Noop, Phase = EnvelopePhase.Commit, Summary = "Already set.", Count = 3,
                               Verified = new VerifiedDto { Method = VerifyMethods.None } },
            });
            ev.Add(WriteReq("set_parameters"), new[]
            {
                new Envelope { Ok = false, Status = EnvelopeStatus.Partial, Phase = EnvelopePhase.Commit, Summary = "2 did not read back.", Count = 6,
                               Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 6,
                                   Mismatches = new List<MismatchDto> { new MismatchDto { Id = 1, Field = "Mark" }, new MismatchDto { Id = 2, Field = "Mark" } } } },
            });

            var f = ev.Receipt;

            Assert.Equal(16, f.Done);            // 12 + (6 - 2)
            Assert.Equal(26, f.Total);           // 12 + 5 + 3 + 6
            Assert.Equal("16/26 done · 3 unchanged · 2 failed (read-back mismatch) · 5 not applied (declined)",
                ReceiptTemplates.Headline(f, ReceiptLanguage.En));
            Assert.Equal("16/26 siap · 3 tiada perubahan · 2 gagal (bacaan semula tidak sepadan) · 5 tidak dilaksanakan (ditolak)",
                ReceiptTemplates.Headline(f, ReceiptLanguage.Ms));
        }

        [Fact]
        public void AllDone_NoCaution()
        {
            var ev = new TurnEvidence();
            ev.Add(WriteReq("set_parameters"), new[] { Done(34) });

            Assert.False(ev.Caution);
            Assert.Equal("34/34 done", ReceiptTemplates.Headline(ev.Receipt, ReceiptLanguage.En));
            Assert.Equal("34/34 siap", ReceiptTemplates.Headline(ev.Receipt, ReceiptLanguage.Ms));
        }

        [Fact]
        public void FailedWriteWithoutCount_CountsAsOne()
        {
            var ev = new TurnEvidence();
            ev.Add(WriteReq("run_revit_code"), new[] { EnvelopeFactory.Failed(ErrorCodes.CompileError, "Line 3: ; expected") });

            Assert.True(ev.Caution);
            Assert.Equal("0/1 done · 1 failed (code error)", ReceiptTemplates.Headline(ev.Receipt, ReceiptLanguage.En));
        }

        [Fact]
        public void ReadOnlyTurn_HasNoReceiptButHasEvidence()
        {
            var ev = new TurnEvidence();
            ev.Add(ReadReq("find_elements"), new[]
            {
                new Envelope
                {
                    Ok = true, Status = "done", Phase = "read", Summary = "34 ceilings", Count = 34,
                    Searched = new SearchedDto
                    {
                        Sources = new List<string> { "host" }, Categories = new List<string> { "Ceilings" },
                        Filter = "Level = 'Level 2'", NotSearched = new List<string> { "link: STR_v3 (unloaded)" },
                    },
                    Highlight = new HighlightDto { Selected = 34, VisibleInView = 30 },
                },
            });

            Assert.False(ev.HasWrites);
            Assert.False(ev.Caution);
            var item = Assert.Single(ev.Evidence);
            Assert.Equal("34 found · searched host · Ceilings · Level = 'Level 2' · 34 selected, 30 visible in view",
                ReceiptTemplates.EvidenceLine(item, ReceiptLanguage.En));
            Assert.Equal("34 dijumpai · dicari host · Ceilings · Level = 'Level 2' · 34 dipilih, 30 kelihatan dalam pandangan",
                ReceiptTemplates.EvidenceLine(item, ReceiptLanguage.Ms));
            Assert.Equal("Not searched: link: STR_v3 (unloaded)", ReceiptTemplates.NotSearchedLine(item, ReceiptLanguage.En));
            Assert.Equal("Tidak dicari: link: STR_v3 (unloaded)", ReceiptTemplates.NotSearchedLine(item, ReceiptLanguage.Ms));
        }

        [Fact]
        public void ReadClassifiedCodeInAWriteRequest_IsEvidenceNotAReceipt()
        {
            // Ruling R13: run_revit_code always arrives in a write request; code
            // the add-in classified as read runs with no card and returns phase read.
            var ev = new TurnEvidence();
            ev.Add(WriteReq("run_revit_code"), new[] { new Envelope { Ok = true, Status = "done", Phase = "read", Summary = "12 sheets", Count = 12 } });

            Assert.False(ev.HasWrites);
            Assert.False(ev.Caution);
        }

        [Theory]
        [InlineData("Naikkan siling Aras 2 ke 2700 mm", ReceiptLanguage.Ms)]
        [InlineData("tukar semua pintu di aras 3", ReceiptLanguage.Ms)]
        [InlineData("berapa banyak bilik yang tiada nombor?", ReceiptLanguage.Ms)]
        [InlineData("Raise the Level 2 ceilings to 2700 mm", ReceiptLanguage.En)]
        [InlineData("how many doors are on level 3", ReceiptLanguage.En)]
        [InlineData("", ReceiptLanguage.En)]
        public void DetectsTheDraftersLanguage(string text, ReceiptLanguage expected)
        {
            Assert.Equal(expected, ReceiptTemplates.Detect(text));
        }
    }
}
