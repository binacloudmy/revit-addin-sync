#nullable enable
// Result pattern Task 3: the result card is built from envelopes, never from prose.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ResultCardModelTests
    {
        private static ToolCallDto Call(string tool, string args = "{}") =>
            new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = tool, Args = JsonDocument.Parse(args).RootElement.Clone() };

        private static ToolRequestDto Request(string kind, params ToolCallDto[] calls) =>
            new ToolRequestDto { RequestId = "r", Kind = kind, Calls = calls.ToList() };

        private static Envelope TypeCommit(string field, long typeA, long typeB, int mismatches = 0) => new Envelope
        {
            Ok = true, Status = mismatches > 0 ? EnvelopeStatus.Partial : EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set " + field, Count = 2,
            Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new List<FieldChangeDto>
            {
                new FieldChangeDto { Id = typeA, Field = field, Before = "", After = "Adrino LED 601 / 10 / 840" },
                new FieldChangeDto { Id = typeB, Field = field, Before = "", After = "Adrino LED 601 / 10 / 840" },
            } } },
            Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 2,
                Mismatches = Enumerable.Range(0, mismatches).Select(i => new MismatchDto { Id = typeA, Field = field, Expected = "x", Actual = "y" }).ToList() },
            Data = JsonDocument.Parse("{\"param\":\"" + field + "\",\"type_param\":true,\"types\":[{\"id\":" + typeA + ",\"name\":\"jkrEL_lit-fx_LED_c_dl_1X13W\",\"instances_in_model\":38},{\"id\":" + typeB + ",\"name\":\"jkrEL_lit-fx_LED_c_dl_1X8W\",\"instances_in_model\":14}],\"instances_affected\":52}").RootElement.Clone(),
        };

        [Fact]
        public void TypeWrites_GroupByType_WithOneRowPerParameter()
        {
            var ev = new TurnEvidence();
            foreach (var f in new[] { "Model", "Manufacturer", "Type Comments", "Description", "URL" })
                ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { TypeCommit(f, 101, 202) });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En, "PCO_broucher_Downlight_2026.pdf");

            Assert.Equal(ResultKind.Changes, m.Kind);
            Assert.Equal("10 changes applied", m.Headline);
            Assert.Equal("2 types · 52 elements · read back from Revit", m.Detail);
            Assert.Equal(2, m.Groups.Count);
            Assert.All(m.Groups, g => Assert.True(g.IsType));
            Assert.Equal("jkrEL_lit-fx_LED_c_dl_1X13W", m.Groups[0].TypeName);
            Assert.Equal(38, m.Groups[0].Instances);
            Assert.Equal(5, m.Groups[0].Rows.Count);
            Assert.Equal("PCO_broucher_Downlight_2026.pdf", m.Groups[0].Rows[0].Source);
            Assert.Equal("Adrino LED 601 / 10 / 840", m.Groups[0].Rows[0].After);
        }

        [Fact]
        public void Mismatches_ShowInTheDetail()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { TypeCommit("Model", 101, 202, mismatches: 1) });
            var m = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Contains("1 mismatch", m.Detail);
        }

        [Fact]
        public void InstanceWrites_OneGroup_OneRowPerField_ManyValuesCollapse()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set Mark", Count = 3,
                Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new List<FieldChangeDto>
                {
                    new FieldChangeDto { Id = 1, Field = "Mark", Before = "", After = "D1" },
                    new FieldChangeDto { Id = 2, Field = "Mark", Before = "", After = "D2" },
                    new FieldChangeDto { Id = 3, Field = "Mark", Before = "", After = "D3" },
                } } },
                Data = JsonDocument.Parse("{\"param\":\"Mark\",\"type_param\":false,\"instances_category\":\"Doors\"}").RootElement.Clone(),
            };
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { env });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(ResultKind.Changes, m.Kind);
            var g = Assert.Single(m.Groups);
            Assert.Equal("Doors", g.TypeName);
            Assert.False(g.IsType);
            Assert.Equal(3, g.Instances);
            var row = Assert.Single(g.Rows);
            Assert.Equal("3 values", row.After);
            Assert.Equal("1 change applied", m.Headline);
            Assert.Equal("3 elements · read back from Revit", m.Detail);
        }

        [Fact]
        public void AddedOnly_IsCreated()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Created 4 sheets", Count = 4,
                Changes = new ChangeReportDto { Touched = new ChangesDto { Added = new List<ElementRef> { new(5001, null), new(5002, null), new(5003, null), new(5004, null) } } },
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
            };
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.RunRevitCode)), new[] { env });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(ResultKind.Created, m.Kind);
            Assert.Equal("4 created", m.Headline);
            Assert.Equal(4, m.Created.Count);
            Assert.Empty(m.Warnings);
            Assert.Equal("Undo removes them in one step", m.Detail);
        }

        private static WarningDto Overlap(params long[] ids) => new WarningDto
        {
            Text = "Highlighted walls overlap. One of them may be ignored when Revit finds room boundaries.",
            Severity = WarningSeverity.Warning, DefinitionId = "00000000-0000-0000-0000-00000000a332", Ids = ids.ToList(),
        };

        private static Envelope AddedCommit(string phase, long id, params WarningDto[] warnings) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = phase, Summary = "Created a wall", Count = 1,
            Changes = new ChangeReportDto { Touched = new ChangesDto { Added = new List<ElementRef> { new(id, null) } } },
            Warnings = warnings.ToList(),
            Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
        };

        [Fact]
        public void Created_OnTopOfAnExistingElement_KeepsRevitsWarning()
        {
            // AI-332: the new wall (9001) overlaps an existing one (42). Revit's warning was deleted
            // to keep the commit dialog-free, but the card must still say so.
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.RunRevitCode)), new[] { AddedCommit(EnvelopePhase.Commit, 9001, Overlap(42, 9001)) });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(ResultKind.Created, m.Kind);
            var w = Assert.Single(m.Warnings);
            Assert.StartsWith("Highlighted walls overlap", w.Title);
            Assert.Equal(new long[] { 42, 9001 }, w.Ids);
            Assert.False(w.Fixable);
            Assert.Equal("Undo removes them in one step · 1 warning", m.Detail);
            Assert.Empty(m.Findings);
        }

        [Fact]
        public void CommitWarnings_AreDeduped_AndPreviewWarningsAreNotShown()
        {
            var ev = new TurnEvidence();
            // The preview's elements were rolled back: its warning is not the result.
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.RunRevitCode)), new[] { AddedCommit(EnvelopePhase.Preview, 8001, Overlap(42, 8001)) });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.RunRevitCode)), new[] { AddedCommit(EnvelopePhase.Commit, 9001, Overlap(42, 9001)) });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.RunRevitCode)), new[] { AddedCommit(EnvelopePhase.Commit, 9002, Overlap(42, 9001),
                new WarningDto { Text = "There are identical instances in the same place. This will result in double counting in schedules.", Severity = WarningSeverity.Warning, Ids = new List<long> { 7, 9002 } }) });

            var m = ResultCardModel.From(ev, ReceiptLanguage.Ms);

            Assert.Equal(ResultKind.Created, m.Kind);
            Assert.Equal(2, m.Warnings.Count);
            Assert.DoesNotContain(m.Warnings, w => w.Ids.Contains(8001));
            Assert.EndsWith(" · 2 amaran", m.Detail);
        }

        [Fact]
        public void Changes_WithACommitWarning_SayItInTheDetail()
        {
            var env = TypeCommit("Model", 101, 202);
            env.Warnings = new List<WarningDto> { Overlap(101, 303) };
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { env });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(ResultKind.Changes, m.Kind);
            Assert.Single(m.Warnings);
            Assert.Equal("2 types · 52 elements · read back from Revit · 1 warning", m.Detail);
            Assert.Equal("2 types · 52 elements", m.Counts);
        }

        [Fact]
        public void EmptyTouched_IsNone()
        {
            var env = new Envelope { Ok = true, Status = EnvelopeStatus.Noop, Phase = EnvelopePhase.Commit, Summary = "Nothing to change.", Count = 0 };
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { env });
            Assert.Equal(ResultKind.None, ResultCardModel.From(ev, ReceiptLanguage.En).Kind);
            Assert.Equal(ResultKind.None, ResultCardModel.From(new TurnEvidence(), ReceiptLanguage.En).Kind);
        }

        [Fact]
        public void Warnings_WithoutACommit_AreFindings()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "Checked 235 fixtures", Count = 235,
                Warnings = new List<WarningDto>
                {
                    new WarningDto { Text = "No electrical circuit", Severity = WarningSeverity.Error, Ids = new List<long> { 1, 2, 3, 4, 5, 6 } },
                    new WarningDto { Text = "Outside any room", Severity = WarningSeverity.Warning, Ids = new List<long> { 7, 8 } },
                },
            };
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.FindElements)), new[] { env });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(ResultKind.Findings, m.Kind);
            Assert.Equal("8 elements fail", m.Headline);
            Assert.Equal("2 kinds of issue · 6 fixable automatically", m.Detail);
            Assert.True(m.Findings[0].Fixable);
            Assert.False(m.Findings[1].Fixable);
        }

        [Fact]
        public void ReadWithSelection_IsSelection()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "54 found", Count = 54,
                Highlight = new HighlightDto { Selected = 54, VisibleInView = 54 },
            };
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.FindElements)), new[] { env });

            var m = ResultCardModel.From(ev, ReceiptLanguage.Ms);

            Assert.Equal(ResultKind.Selection, m.Kind);
            Assert.Equal("54 dipilih", m.Headline);
            Assert.Equal("54 kelihatan dalam view", m.Detail);
        }
    }
}
