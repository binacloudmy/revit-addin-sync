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

        private static ImageDto Png(string caption) => new ImageDto { Base64 = "iVBORw0KGgo=", Width = 512, Height = 384, Caption = caption };

        [Fact]
        public void LookImages_ReachTheDrafter_PreviewImagesStayOnTheCard()
        {
            var ev = new TurnEvidence();
            ev.Add(ReadReq(ToolNames.Look), new[] { new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "Level 2 plan", Image = Png("Level 2 - plan") } });
            ev.Add(WriteReq(ToolNames.SetParameters), new[] { new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview, Image = Png("preview") } });

            var img = Assert.Single(ev.Images);                      // the read's, not the write preview's
            Assert.Equal("Level 2 - plan", img.Caption);
            Assert.Equal("iVBORw0KGgo=", img.Base64);

            for (var i = 0; i < 12; i++)
                ev.Add(ReadReq(ToolNames.Look), new[] { new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Image = Png("v" + i) } });
            Assert.Equal(TurnEvidence.MaxImages, ev.Images.Count);    // what one turn can show the model
        }

        /// <summary>check's data as W6's CheckReport builds it (RuleSet.cs CheckResult).</summary>
        private static Envelope CheckEnvelope() => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Partial, Phase = EnvelopePhase.Read, Summary = "JKR LOD 300: 2 rules", Count = 163,
            Data = JsonSerializer.SerializeToElement(new
            {
                rule_set_id = "jkr-lod-300", version = "2026.1",
                totals = new { @checked = 163, pass = 120, fail = 40, not_verified = 3 },
                rules = new object[]
                {
                    new { rule_id = "L3-12", cite = "JKR LOD 4.2", severity = "error", pass = 80, fail = 40, not_verified = 0,
                          top = new object[0], fail_handle = "h_1a2b3c4d" },
                    new { rule_id = "L3-14", cite = "JKR LOD 4.4", severity = "warning", pass = 40, fail = 0, not_verified = 3,
                          top = new object[0], not_verified_reason = "link STR unloaded" },
                },
            }),
        };

        [Fact]
        public void CheckRules_BecomeATable_WithAHandlePerFailingRule()
        {
            var ev = new TurnEvidence();
            ev.Add(ReadReq(ToolNames.Check), new[] { CheckEnvelope() });

            var t = Assert.Single(ev.Tables);
            Assert.Equal(ResultTable.CheckKind, t.Kind);
            Assert.Equal("Check jkr-lod-300 2026.1", t.Title);
            Assert.Equal(new[] { "Rule", "Cite", "Severity", "Pass", "Fail", "Not verified", "Why not verified" }, t.Columns);
            Assert.Equal(new[] { "L3-12", "JKR LOD 4.2", "error", "80", "40", "0", "" }, t.Rows[0]);
            Assert.Equal(new[] { "L3-14", "JKR LOD 4.4", "warning", "40", "0", "3", "link STR unloaded" }, t.Rows[1]);
            Assert.Equal("h_1a2b3c4d", t.HandleOf(0));
            Assert.Null(t.HandleOf(1));                                // nothing failed: nothing to highlight
            Assert.Null(t.HandleOf(9));

            Assert.Equal("L3-12 · JKR LOD 4.2 — 40 fail · 80 pass", ReceiptTemplates.CheckRuleLine(t, 0, ReceiptLanguage.En));
            Assert.Equal("L3-14 · JKR LOD 4.4 — 0 gagal · 40 lulus · 3 tidak disahkan (link STR unloaded)",
                ReceiptTemplates.CheckRuleLine(t, 1, ReceiptLanguage.Ms));

            // Export: the same table goes to the pane's Excel/CSV writer.
            var csv = TableAttachment.ToCsv(t);
            Assert.StartsWith("Rule,Cite,Severity,Pass,Fail,Not verified,Why not verified", csv);
            Assert.Contains("L3-14,JKR LOD 4.4,warning,40,0,3,link STR unloaded", csv);
        }

        [Fact]
        public void HistoryKeepsTheModelsWordsApartFromTheAddins()
        {
            var ev = new TurnEvidence();
            ev.Add(WriteReq(ToolNames.SetParameters), new[] { Done(34) });

            var empty = ReceiptTemplates.HistoryOf("", null, ev, ReceiptLanguage.En);
            Assert.Equal("", empty.Reply);                              // never "Done"
            Assert.Equal("Receipt: 34/34 done", empty.Note);

            var cancelled = ReceiptTemplates.HistoryOf("Saya mula…", "Dibatalkan.", ev, ReceiptLanguage.Ms);
            Assert.Equal("Saya mula…", cancelled.Reply);
            Assert.Equal("Dibatalkan. · Resit: 34/34 siap", cancelled.Note);

            var read = ReceiptTemplates.HistoryOf("12 pintu.", null, new TurnEvidence(), ReceiptLanguage.Ms);
            Assert.Equal("12 pintu.", read.Reply);
            Assert.Null(read.Note);

            Assert.True(ReceiptTemplates.IsLegacyAddinDone("Done"));
            Assert.False(ReceiptTemplates.IsLegacyAddinDone("Done. 4 doors renamed."));
        }

        [Fact]
        public void HistoryAndExport_ShowTheAddinsLine_NeverAnEmptyModelBubble()
        {
            // A write-only turn: no reply text, the receipt as the add-in's note.
            var writeOnly = ReceiptTemplates.BotLines("", "Receipt: 34/34 done");
            Assert.Null(writeOnly.Reply);
            Assert.Equal("BINA add-in: Receipt: 34/34 done", writeOnly.AddinLine);
            Assert.Equal("BINA add-in: Receipt: 34/34 done", ReceiptTemplates.BotTranscript("", "Receipt: 34/34 done"));

            // A cancelled turn after some text: both, model first.
            Assert.Equal("Saya mula…\nBINA add-in: Dibatalkan.", ReceiptTemplates.BotTranscript("Saya mula…", "Dibatalkan."));

            // A plain answer: the model's words only.
            var answer = ReceiptTemplates.BotLines("12 pintu.", null);
            Assert.Equal("12 pintu.", answer.Reply);
            Assert.Null(answer.AddinLine);

            // History from before the split: its "Done" was the add-in's.
            var legacy = ReceiptTemplates.BotLines("Done", null);
            Assert.Null(legacy.Reply);
            Assert.Equal("BINA add-in: Done", legacy.AddinLine);

            // Nothing at all: nothing to draw or export.
            Assert.Equal("", ReceiptTemplates.BotTranscript(null, null));
        }
    }
}
