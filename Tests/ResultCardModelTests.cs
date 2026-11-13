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

        // ---- Board 5: an isolate result says what Revit selected and offers "Reset isolate" ----

        private static Envelope Found(int selected, int visible) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = selected + " found", Count = selected,
            Highlight = new HighlightDto { Selected = selected, VisibleInView = visible },
        };

        private static ResultCardModel IsolateCard(string intent, string code = "")
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.FindElements)), new[] { Found(52, 52) });
            var args = JsonSerializer.Serialize(new { intent, code });
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.RunRevitCode, args)),
                new[] { new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "ran" } });
            return ResultCardModel.From(ev, ReceiptLanguage.En);
        }

        [Theory]
        [InlineData("Isolate the 52 downlights in the view", "")]
        [InlineData("show them", "view.IsolateElementsTemporary(ids);")]
        [InlineData("show them", "view.EnableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);")]
        public void ACodeCallThatIsolates_MarksTheCard(string intent, string code)
        {
            var m = IsolateCard(intent, code);

            Assert.Equal(ResultKind.Selection, m.Kind);
            Assert.True(m.Isolated);
        }

        [Fact]
        public void APlainSelection_IsNotIsolated()
        {
            var m = IsolateCard("Select the downlights");

            Assert.False(m.Isolated);
            Assert.DoesNotContain(SelectionLines.For(m, ReceiptLanguage.En), l => l.Contains("isolated"));
        }

        [Fact]
        public void AFailedIsolateCall_DoesNotMarkTheCard()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.FindElements)), new[] { Found(52, 52) });
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.RunRevitCode, "{\"intent\":\"Isolate them\"}")),
                new[] { new Envelope { Ok = false, Status = EnvelopeStatus.Failed, Phase = EnvelopePhase.Read, Summary = "failed" } });

            Assert.False(ResultCardModel.From(ev, ReceiptLanguage.En).Isolated);
        }

        [Fact]
        public void SelectionLines_SayWhatRevitSelected_WhatIsHidden_AndThatTheViewIsIsolated()
        {
            var m = new ResultCardModel { Kind = ResultKind.Selection, Selected = 52, VisibleInView = 50, Isolated = true };

            var en = SelectionLines.For(m, ReceiptLanguage.En);
            Assert.Equal("Selected in Revit: 52", en[0]);
            Assert.Equal("2 of the selected elements are not visible in this view.", en[1]);
            Assert.StartsWith("This view is isolated", en[2]);

            var ms = SelectionLines.For(m, ReceiptLanguage.Ms);
            Assert.Equal("Dipilih dalam Revit: 52", ms[0]);
            Assert.Equal("Tetapkan semula isolate", SelectionLines.ResetLabel(ReceiptLanguage.Ms));
            Assert.Equal("Reset isolate", SelectionLines.ResetLabel(ReceiptLanguage.En));
        }

        [Fact]
        public void SelectionLines_AreEmptyForOtherKinds()
        {
            Assert.Empty(SelectionLines.For(new ResultCardModel { Kind = ResultKind.Changes }, ReceiptLanguage.En));
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

        // ---- Review findings on "Changed back" ----

        private static Envelope PutMany(string field, long[] ids, string[] afters, string category = "Lighting Fixtures") => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set " + field, Count = ids.Length,
            Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = ids.Select((id, i) => new FieldChangeDto { Id = id, Field = field, Before = "old" + i, After = afters[i] }).ToList() } },
            Data = JsonDocument.Parse("{\"param\":\"" + field + "\",\"type_param\":false,\"instances_category\":\"" + category + "\"}").RootElement.Clone(),
        };

        private static Envelope BackMany(string field, long[] ids, string category = "Lighting Fixtures") => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set " + field, Count = ids.Length,
            Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = ids.Select((id, i) => new FieldChangeDto { Id = id, Field = field, Before = "x", After = "old" + i }).ToList() } },
            Data = JsonDocument.Parse("{\"param\":\"" + field + "\",\"type_param\":false,\"instances_category\":\"" + category + "\"}").RootElement.Clone(),
        };

        [Fact]
        public void PutBackValuesThatDiffered_AreALabel_NeverAValueToWrite()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { PutMany("Mark", new long[] { 1, 2 }, new[] { "A", "B" }) });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { BackMany("Mark", new long[] { 1, 2 }) });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            var row = Assert.Single(Assert.Single(m.Groups).Rows);
            Assert.True(row.ChangedBack);
            Assert.Null(row.Intended);                       // "2 values" is not something to write
            Assert.Equal("2 values", row.IntendedLabel);
            Assert.Equal("2 values", row.IntendedText);
            Assert.False(m.Groups[0].CanWriteAgain);
        }

        [Fact]
        public void PutBackValuesThatWereTheSame_CanBeWrittenAgain()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { PutMany("Mark", new long[] { 1, 2 }, new[] { "A", "A" }) });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { BackMany("Mark", new long[] { 1, 2 }) });

            var g = Assert.Single(ResultCardModel.From(ev, ReceiptLanguage.En).Groups);

            Assert.Equal("A", Assert.Single(g.Rows).Intended);
            Assert.True(g.CanWriteAgain);
        }

        [Fact]
        public void AGroupWithLiveAndPutBackRows_IsMixed_AndCountsOnlyTheElementsThatKeepAChange()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { PutMany("Mark", new long[] { 1, 2, 3 }, new[] { "A", "A", "A" }) });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { PutMany("Comments", new long[] { 10, 11 }, new[] { "C", "C" }) });
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { BackMany("Comments", new long[] { 10, 11 }) });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            var g = Assert.Single(m.Groups);
            Assert.False(g.ChangedBack);                    // not every row
            Assert.True(g.AnyChangedBack);                  // but the card still says so
            Assert.Equal(3, g.Instances);                   // the 2 put-back elements are not counted
            Assert.Equal(1, m.ChangeCount);
        }
    }
}
