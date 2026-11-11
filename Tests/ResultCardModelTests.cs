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

        // ---- Board 7: a value written and then put back is "Changed back", not dropped ----

        private static Envelope Put(string field, long type, string before, string after, int instances = 22) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set " + field, Count = 1,
            Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new List<FieldChangeDto>
            {
                new FieldChangeDto { Id = type, Field = field, Before = before, After = after },
            } } },
            Data = JsonDocument.Parse("{\"param\":\"" + field + "\",\"type_param\":true,\"types\":[{\"id\":" + type + ",\"name\":\"Wall light 1x28W\",\"instances_in_model\":" + instances + "}],\"instances_affected\":" + instances + "}").RootElement.Clone(),
        };

        [Fact]
        public void WrittenThenPutBack_IsAChangedBackRow_WithWhatWasIntended()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { TypeCommit("Model", 101, 202) });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { Put("Wattage Comments", 303, "T5", "10 W") });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { Put("Wattage Comments", 303, "10 W", "T5") });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En, "brochure.pdf");

            Assert.Equal(ResultKind.Changes, m.Kind);
            var back = Assert.Single(m.Groups, g => g.ChangedBack);
            Assert.Equal("Wall light 1x28W", back.TypeName);
            var row = Assert.Single(back.Rows);
            Assert.True(row.ChangedBack);
            Assert.Equal("T5", row.After);          // what the type holds now
            Assert.Equal("10 W", row.Intended);     // what was written
            Assert.Equal(1, m.ChangedBackTypes);
            Assert.Equal(2, m.ChangeCount);          // the two types that kept their values: one row each
            Assert.Contains("1 changed back", m.Detail);
            Assert.Contains("1 changed back", m.Counts);
            Assert.False(row.NeedsCheck);
        }

        [Fact]
        public void EverythingPutBack_IsNoNetChange_NotAnEmptyCard()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { Put("Wattage Comments", 303, "T5", "10 W") });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { Put("Wattage Comments", 303, "10 W", "T5") });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(ResultKind.Changes, m.Kind);
            Assert.Equal("No net change", m.Headline);
            Assert.Contains("written, then put back", m.Detail);
            Assert.Equal(0, m.ChangeCount);
            Assert.Equal(0, m.InstanceCount);
            Assert.True(Assert.Single(m.Groups).ChangedBack);
        }

        [Fact]
        public void AWriteThatChangedNothing_IsNotChangedBack()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { Put("Wattage Comments", 303, "T5", "T5") });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(0, m.ChangedBackRows);
            Assert.DoesNotContain(m.Groups, g => g.ChangedBack);
        }

        [Fact]
        public void ChangedBack_ReadsInMalay()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { TypeCommit("Model", 101, 202) });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { Put("Wattage Comments", 303, "T5", "10 W") });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { Put("Wattage Comments", 303, "10 W", "T5") });

            Assert.Contains("1 dipulihkan balik", ResultCardModel.From(ev, ReceiptLanguage.Ms).Detail);
        }
    }
}
