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

            req.Calls = new[] { Call("set_parameters", new { param = "Height Offset From Level", value = "2700 mm" }) };
            var card = ConfirmCardModel.From(req, ReceiptLanguage.En);

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

            var card = ConfirmCardModel.From(req, ReceiptLanguage.Ms);

            Assert.True(card.HardWarning);
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.SideEffect && r.Text.Contains("Rooms 6"));
            Assert.StartsWith("Ubah 2", card.Headline);
            Assert.Equal("Penerangan pembantu", card.AssistantLabel);
        }

        [Fact]
        public void PlainEditIsNotAHardWarning_AndOffersBuildOnlyWhenAsked()
        {
            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { Preview(modified: 34) } }, ReceiptLanguage.En);
            Assert.False(card.HardWarning);
            Assert.False(card.OfferApproveThisBuild);

            var build = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { Preview(added: 12) }, OfferApproveThisBuild = true }, ReceiptLanguage.En);
            Assert.True(build.OfferApproveThisBuild);
            Assert.StartsWith("Add 12", build.Headline);
        }

        [Fact]
        public void CodeIsShownInFull_IntentGoesOnTheAssistantLine()
        {
            var code = string.Join("\n", Enumerable.Range(1, 80).Select(i => "Print(" + i + ");"));
            var card = ConfirmCardModel.From(
                new ConfirmRequest
                {
                    Preview = new[] { Preview(added: 1) },
                    Calls = new[] { Call("run_revit_code", new { code, intent = "Create sheet A-101" }) },
                },
                ReceiptLanguage.En);

            Assert.Equal(code, card.Code);
            Assert.Contains("Create sheet A-101", card.AssistantText);
            Assert.Equal("Run Revit code", card.ToolLabel);
            // Ruling R42: the model's intent never becomes the headline or a label.
            Assert.DoesNotContain("A-101", card.Headline);
            Assert.DoesNotContain("A-101", card.ToolLabel);
            Assert.All(card.Rows, r => Assert.DoesNotContain("A-101", r.Text));
        }

        [Fact]
        public void UncategorisedChanges_AreOneOtherSettingsRow_AndCountTowardTheHardWarning()
        {
            // Ruling R43: W3 reports elements with no category under their class
            // name ("class:<ClassName>", or an empty key); the card folds them into
            // one "Other settings: N" side-effect row.
            var preview = Preview(modified: 2, sideModified: new Dictionary<string, int>
            {
                ["Rooms"] = 1,
                ["class:ParameterFilterElement"] = 2,
                ["class:FillPatternElement"] = 1,
                [""] = 1,
            });

            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { preview } }, ReceiptLanguage.En);

            var other = Assert.Single(card.Rows, r => r.Text.StartsWith("Other settings: "));
            Assert.Equal(ConfirmRowKind.SideEffect, other.Kind);
            // L16: friendly names; an unlisted class (or none) is an internal setting.
            Assert.Equal("Other settings: 4 — Fill patterns 1, View filters 2, 1 other (an internal Revit setting)", other.Text);
            Assert.DoesNotContain("ParameterFilterElement", other.Text);
            var changed = Assert.Single(card.Rows, r => r.Text.StartsWith("Also changed: "));
            Assert.Equal("Also changed: Rooms 1 (room boundaries recompute)", changed.Text);   // classes are not categories
            Assert.True(card.HardWarning);                                  // 5 side effects > 2 touched

            var ms = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { preview } }, ReceiptLanguage.Ms);
            Assert.Contains(ms.Rows, r => r.Text.StartsWith("Tetapan lain: 4"));
        }

        [Fact]
        public void UncategorisedDeletions_AreDeletions_InRed()
        {
            var card = ConfirmCardModel.From(new ConfirmRequest
            {
                Preview = new[] { Preview(modified: 3, sideDeleted: new Dictionary<string, int> { ["class:ParameterFilterElement"] = 1 }) },
            }, ReceiptLanguage.En);

            var other = Assert.Single(card.Rows, r => r.Text.StartsWith("Other settings: "));
            Assert.True(other.Danger);
            Assert.True(card.HardWarning);
            Assert.StartsWith("Delete 1", card.Headline);
            Assert.DoesNotContain(card.Rows, r => r.Text.StartsWith("Also deleted: "));
        }

        [Fact]
        public void PreviewFailure_IsAnAddinTemplate_RawTextOnlyUnderCodeOutput()
        {
            // Review I2: the exception text the model's code threw is model-shaped
            // text; the card body says what failed in the add-in's own words.
            var failed = EnvelopeFactory.Failed(ErrorCodes.RuntimeError, "NullReferenceException: Object reference not set (line 4)");
            var owned = EnvelopeFactory.Failed(ErrorCodes.NotOwned, "4 ceilings are borrowed by aisyah.");
            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { failed, owned } }, ReceiptLanguage.En);

            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Failure && r.Text == "Preview failed: error in Revit");
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Failure && r.Text == "Preview failed: not owned");
            Assert.All(card.Rows, r => Assert.DoesNotContain("NullReference", r.Text));
            Assert.All(card.Rows, r => Assert.DoesNotContain("aisyah", r.Text));
            Assert.Equal("Code output", card.CodeOutputLabel);
            Assert.Contains("NullReferenceException", card.CodeOutput);
            Assert.True(card.EstimateOnly);

            var ms = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { failed } }, ReceiptLanguage.Ms);
            Assert.Contains(ms.Rows, r => r.Text == "Pratonton gagal: ralat dalam Revit");
            Assert.Equal("Output kod", ms.CodeOutputLabel);
        }

        [Fact]
        public void NotAligned_DropsPerCallAttribution_HeadlineFromPreviewTotals()
        {
            // Review I1: [read-mode run_revit_code, set_parameters] -> W3 previews only
            // the write, so Calls and Preview differ in length. Nothing is attributed
            // to a call; the headline still reports the true total (400, not the
            // 50-row cap).
            var preview = Preview(modified: 50);
            preview.Count = 400;
            var card = ConfirmCardModel.From(new ConfirmRequest
            {
                Preview = new[] { preview },
                Calls = new[]
                {
                    Call("run_revit_code", new { code = "Print(doc.Title);", intent = "Read the title" }),
                    Call("set_parameters", new { param = "Height Offset From Level", value = "2700 mm" }),
                },
            }, ReceiptLanguage.En);

            Assert.Equal("Change 400 elements", card.Headline);
            Assert.Null(card.Code);
            Assert.True(card.AssistantText == null || !card.AssistantText.Contains("Read the title"));
            Assert.Equal("Revit changes", card.ToolLabel);
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Modify && r.Text.Contains("… and 350 more"));
        }

        [Fact]
        public void Aligned_UsesOnlyTheBatchCalls_AndTheTrueTargetTotal()
        {
            var preview = Preview(modified: 50);
            preview.Count = 400;
            var card = ConfirmCardModel.From(new ConfirmRequest
            {
                Preview = new[] { preview, Preview(added: 2) },
                Calls = new[]
                {
                    Call("set_parameters", new { param = "Height Offset From Level", value = "2700 mm" }),
                    Call("run_revit_code", new { code = "/* make 2 */", intent = "Add two tags" }),
                },
            }, ReceiptLanguage.En);

            Assert.Equal("Add 2 · Change 400 elements", card.Headline);
            Assert.Equal("/* make 2 */", card.Code);
            Assert.Contains("Add two tags", card.AssistantText);
            Assert.Equal("Set parameters + Run Revit code", card.ToolLabel);
        }

        [Fact]
        public void PreviewImages_AreOnTheCard()
        {
            var preview = Preview(added: 3);
            preview.Image = new ImageDto { Base64 = "iVBORw0KGgo=", Width = 64, Height = 32, Caption = "3 new walls, Level 2" };

            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { preview } }, ReceiptLanguage.En);

            var img = Assert.Single(card.Images);
            Assert.Equal("iVBORw0KGgo=", img.Base64);
            Assert.Equal("3 new walls, Level 2", img.Caption);
        }

        /// <summary>The final review's probe 2: what RevitWriteEngine.Decorate builds for
        /// code that edits 34 existing elements and deletes 7 (touched modifications
        /// carry no field), with or without R78's touched_by_category.</summary>
        private static Envelope CodeEdit(SideEffectsDto? byCategory) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview, Summary = "Preview: tidy sheets",
            Changes = new ChangeReportDto
            {
                Touched = new ChangesDto
                {
                    Modified = Enumerable.Range(1, 34).Select(i => new FieldChangeDto { Id = i, Field = "" }).ToList(),
                    Deleted = Enumerable.Range(100, 7).Select(i => new ElementRef(i, null)).ToList(),
                },
                SideEffects = new SideEffectsDto(),
                TouchedByCategory = byCategory,
            },
        };

        private static ConfirmCardModel CodeCard(Envelope env, ReceiptLanguage lang = ReceiptLanguage.En) =>
            ConfirmCardModel.From(new ConfirmRequest
            {
                Preview = new[] { env },
                Calls = new[] { Call(ToolNames.RunRevitCode, new { code = "Print(1);", intent = "tidy sheets" }) },
            }, lang);

        [Fact]
        public void CodeEdits_AreCountedByCategory_DeletionsFirst_NoEmptyFieldRow()
        {
            var card = CodeCard(CodeEdit(new SideEffectsDto
            {
                DeletedByCategory = new Dictionary<string, int> { ["Views"] = 5, ["Sheets"] = 2 },
                ModifiedByCategory = new Dictionary<string, int> { ["Walls"] = 34 },
            }));

            Assert.Equal("Delete 7 · Change 34 elements", card.Headline);
            Assert.Equal(ConfirmRowKind.Delete, card.Rows[0].Kind);
            Assert.True(card.Rows[0].Danger);
            Assert.Equal("Delete 2 Sheets, 5 Views", card.Rows[0].Text);
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Modify && r.Text == "Change 34 Walls");
            Assert.DoesNotContain(card.Rows, r => r.Text.Contains("(empty)") || r.Text.StartsWith(": "));
            Assert.DoesNotContain(card.Rows, r => r.Text.Contains("more"));
            Assert.True(card.HardWarning);                                   // deletions

            var ms = CodeCard(CodeEdit(new SideEffectsDto { ModifiedByCategory = new Dictionary<string, int> { ["Walls"] = 34 } }), ReceiptLanguage.Ms);
            Assert.Contains(ms.Rows, r => r.Text == "Ubah 34 Walls");
        }

        [Fact]
        public void Additions_AndUncategorisedTouched_ReadByCategory()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto { Added = Enumerable.Range(1, 13).Select(i => new ElementRef(i, null)).ToList() },
                    TouchedByCategory = new SideEffectsDto
                    {
                        AddedByCategory = new Dictionary<string, int> { ["Door Tags"] = 12, ["class:ReferencePlane"] = 1 },
                    },
                },
            };

            var card = CodeCard(env);

            Assert.Equal("Add 13 elements", card.Headline);
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Add && r.Text == "Add 12 Door Tags, 1 other (an internal Revit setting)");
            Assert.False(card.HardWarning);
        }

        [Fact]
        public void WithoutTouchedByCategory_FallsBackToElementCounts_NeverAnEmptyFieldRow()
        {
            var card = CodeCard(CodeEdit(null));                             // probe 2 exactly

            Assert.Equal("Delete 7 · Change 34 elements", card.Headline);
            Assert.Equal("Delete 7 elements", card.Rows[0].Text);
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Modify && r.Text == "Change 34 elements");
            Assert.DoesNotContain(card.Rows, r => r.Text.Contains("(empty)") || r.Text.StartsWith(": "));
        }

        [Fact]
        public void TypedWrite_WithCategories_KeepsItsBeforeAfterRows()
        {
            var env = Preview(modified: 34);
            env.Changes!.TouchedByCategory = new SideEffectsDto { ModifiedByCategory = new Dictionary<string, int> { ["Ceilings"] = 34 } };

            var card = ConfirmCardModel.From(new ConfirmRequest
            {
                Preview = new[] { env },
                Calls = new[] { Call(ToolNames.SetParameters, new { param = "Height Offset From Level", value = "2700 mm" }) },
            }, ReceiptLanguage.En);

            Assert.Equal("Change 34 elements", card.Headline);
            Assert.Contains(card.Rows, r => r.Text == "Change 34 Ceilings");
            Assert.Contains(card.Rows, r => r.Text == "Height Offset From Level: 2400 mm → 2700 mm (34)");
            Assert.DoesNotContain(card.Rows, r => r.Text.Contains("more"));
        }

        [Fact]
        public void TypedWrite_On120Doors_ValueRowsSayTheyCover50Of120()
        {
            // The re-review's N-3 shape: set_parameters on 120 doors, the read-back
            // capped at 50 rows, R78 Doors = 120.
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview, Count = 120,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto
                    {
                        Modified = Enumerable.Range(1, 50).Select(i => new FieldChangeDto { Id = i, Field = "Fire Rating", Before = "30", After = "60" }).ToList(),
                    },
                    SideEffects = new SideEffectsDto(),
                    TouchedByCategory = new SideEffectsDto { ModifiedByCategory = new Dictionary<string, int> { ["Doors"] = 120 } },
                },
            };

            var card = ConfirmCardModel.From(new ConfirmRequest
            {
                Preview = new[] { env },
                Calls = new[] { Call(ToolNames.SetParameters, new { param = "Fire Rating", value = "60" }) },
            }, ReceiptLanguage.En);

            Assert.Equal("Change 120 elements", card.Headline);
            var rows = card.Rows.Where(r => r.Kind == ConfirmRowKind.Modify).Select(r => r.Text).ToList();
            Assert.Equal(new[] { "Change 120 Doors", "Fire Rating: 30 → 60 (50)", "… and 70 more" }, rows);

            var ms = ConfirmCardModel.From(new ConfirmRequest
            {
                Preview = new[] { env },
                Calls = new[] { Call(ToolNames.SetParameters, new { param = "Fire Rating", value = "60" }) },
            }, ReceiptLanguage.Ms);
            Assert.Contains(ms.Rows, r => r.Text == "… dan 70 lagi");
        }

        private static Envelope PreviewWith(object data, ChangeReportDto changes) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview,
            Data = JsonSerializer.SerializeToElement(data), Changes = changes,
        };

        [Fact]
        public void CodePreview_ShowsItsValueRows_AndWhatItCreatesByName()
        {
            // L15: W3 fills touched.modified field rows and data.created for code previews.
            var renumber = PreviewWith(new
            {
                mode = "write",
                created = new[]
                {
                    new { category = "Views", name = "Interior Elevation 1 - a" },
                    new { category = "Views", name = "Interior Elevation 1 - b" },
                    new { category = "Elevation Marks", name = "Elevation 1" },
                },
            }, new ChangeReportDto
            {
                Touched = new ChangesDto
                {
                    Modified = new List<FieldChangeDto>
                    {
                        new FieldChangeDto { Id = 1, Field = "Mark", Before = "101", After = "D-01-01" },
                        new FieldChangeDto { Id = 2, Field = "Mark", Before = "102", After = "D-01-02" },
                    },
                    Added = Enumerable.Range(10, 3).Select(i => new ElementRef(i, null)).ToList(),
                },
                SideEffects = new SideEffectsDto(),
                TouchedByCategory = new SideEffectsDto
                {
                    ModifiedByCategory = new Dictionary<string, int> { ["Doors"] = 2 },
                    AddedByCategory = new Dictionary<string, int> { ["Views"] = 2, ["Elevation Marks"] = 1 },
                },
            });

            var card = CodeCard(renumber);

            Assert.Contains(card.Rows, r => r.Text == "Mark: 101 → D-01-01 (1)");
            Assert.Contains(card.Rows, r => r.Text == "Mark: 102 → D-01-02 (1)");
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Add && r.Text == "Views: Interior Elevation 1 - a, Interior Elevation 1 - b");
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Add && r.Text == "Elevation Marks: Elevation 1");
        }

        [Fact]
        public void TypeTarget_SaysHowManyInstancesItAffects()
        {
            // L17: W3's set_parameters data on a type parameter.
            var env = PreviewWith(new
            {
                param = "Width", type_param = true, matched = 1, changed = 1,
                types = new[] { new { id = 5001, name = "PTr680a", instances_in_model = 8 } },
                instances_affected = 8,
            }, new ChangeReportDto
            {
                Touched = new ChangesDto { Modified = new List<FieldChangeDto> { new FieldChangeDto { Id = 5001, Field = "Width", Before = "900 mm", After = "1000 mm" } } },
                SideEffects = new SideEffectsDto(),
                TouchedByCategory = new SideEffectsDto { ModifiedByCategory = new Dictionary<string, int> { ["Doors"] = 1 } },
            });
            var req = new ConfirmRequest { Preview = new[] { env }, Calls = new[] { Call(ToolNames.SetParameters, new { param = "Width", value = "1000 mm", type_param = true }) } };

            var card = ConfirmCardModel.From(req, ReceiptLanguage.En);
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Modify && r.Text == "Type PTr680a — affects 8 doors");
            Assert.Contains(card.Rows, r => r.Text == "Width: 900 mm → 1000 mm (1)");

            var ms = ConfirmCardModel.From(req, ReceiptLanguage.Ms);
            Assert.Contains(ms.Rows, r => r.Text == "Jenis PTr680a — melibatkan 8 doors");
        }

        [Fact]
        public void ValueRows_NeverShowNoChange_AndGroupByFieldAndValueAfter()
        {
            // L18: J-06 showed "SALI->ZA (2), ZA->ZA (2)".
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto
                    {
                        Modified = new List<FieldChangeDto>
                        {
                            new FieldChangeDto { Id = 1, Field = "Zone", Before = "SALI", After = "ZA" },
                            new FieldChangeDto { Id = 2, Field = "Zone", Before = "SALI", After = "ZA" },
                            new FieldChangeDto { Id = 3, Field = "Zone", Before = "ZA", After = "ZA" },
                            new FieldChangeDto { Id = 4, Field = "Zone", Before = "ZA", After = "ZA" },
                            new FieldChangeDto { Id = 5, Field = "Zone", Before = "ZB", After = "ZC" },
                            new FieldChangeDto { Id = 6, Field = "Zone", Before = "ZD", After = "ZC" },
                        },
                    },
                    SideEffects = new SideEffectsDto(),
                },
            };

            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { env } }, ReceiptLanguage.En);
            var rows = card.Rows.Where(r => r.Text.StartsWith("Zone")).Select(r => r.Text).ToList();

            Assert.Equal(new[] { "Zone: SALI → ZA (2)", "Zone: various → ZC (2)" }, rows);
            Assert.DoesNotContain(card.Rows, r => r.Text.Contains("ZA → ZA"));
        }

        [Fact]
        public void AutomaticSideEffects_SayWhy_AndListedClassesReadByName()
        {
            // L16: M-02 ("filter = 'other'", Sun Path, Center Lines).
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto { Added = new List<ElementRef> { new ElementRef(1, null), new ElementRef(2, null) } },
                    SideEffects = new SideEffectsDto
                    {
                        ModifiedByCategory = new Dictionary<string, int> { ["Center Line"] = 119, ["class:SunAndShadowSettings"] = 1 },
                    },
                    TouchedByCategory = new SideEffectsDto { AddedByCategory = new Dictionary<string, int> { ["class:ParameterFilterElement"] = 2 } },
                },
            };

            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { env } }, ReceiptLanguage.En);

            Assert.Contains(card.Rows, r => r.Text == "Add 2 View filters");
            Assert.Contains(card.Rows, r => r.Text == "Also changed: Center Line 119 (pipe/duct centre lines regenerate)");
            Assert.Contains(card.Rows, r => r.Text == "Other settings: 1 — Sun settings 1");
        }

        [Fact]
        public async Task DecidesExactlyOnce()
        {
            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { Preview(modified: 1) } }, ReceiptLanguage.En);

            Assert.True(card.TryDecide(new ConfirmDecision { Approved = true }));
            Assert.False(card.TryDecide(new ConfirmDecision { Approved = false }));

            var d = await card.Decision;
            Assert.True(d.Approved);
            Assert.True(card.Resolved);
        }

        [Fact]
        public async Task CancellationDeclines()
        {
            var card = ConfirmCardModel.From(new ConfirmRequest { Preview = new[] { Preview(modified: 1) } }, ReceiptLanguage.En);
            using var cts = new CancellationTokenSource();
            card.BindCancellation(cts.Token);

            cts.Cancel();
            var d = await card.Decision;

            Assert.False(d.Approved);
            Assert.False(card.TryDecide(new ConfirmDecision { Approved = true }));
        }

        // ── AI-269 item 2: readable approval cards ────────────────────────────

        private static ConfirmRequest CodeRequest(string code, string intent, string? description = null, params Envelope[] previews) => new ConfirmRequest
        {
            BatchId = "b2",
            Preview = previews.Length > 0 ? previews : new[] { Preview(added: 1) },
            PlanHash = "sha256:24e39f414fb880038fa382b6deb6d8702dd902117c003f1006412df64e4a0035",
            AssistantDescription = description,
            Calls = new[] { Call(ToolNames.RunRevitCode, new { code, intent }) },
        };

        [Fact]
        public void TheIntentIsTheCardsPlainWordsLine_AndTheCodeStaysAvailableForTheToggle()
        {
            var card = ConfirmCardModel.From(CodeRequest("var s = ViewSchedule.CreateSchedule(doc, id);", "Create schedule 'Door Schedule QS', 8 columns, 62 rows"), ReceiptLanguage.En);
            Assert.Equal("Create schedule 'Door Schedule QS', 8 columns, 62 rows", card.AssistantText);
            Assert.True(card.HasCode);
            Assert.Contains("ViewSchedule", card.Code);
            // The code is never part of the plain words, the headline or a row.
            Assert.DoesNotContain("ViewSchedule", card.AssistantText);
            Assert.DoesNotContain("ViewSchedule", card.Headline);
            Assert.All(card.Rows, r => Assert.DoesNotContain("ViewSchedule", r.Text));
        }

        [Fact]
        public void ATypedWriteHasNoCodeToToggle()
        {
            var req = new ConfirmRequest
            {
                BatchId = "b3", Preview = new[] { Preview(modified: 2) }, PlanHash = "sha256:abc",
                Calls = new[] { Call(ToolNames.SetParameters, new { param = "Mark", value = "D1" }) },
            };
            Assert.False(ConfirmCardModel.From(req, ReceiptLanguage.En).HasCode);
        }

        [Fact]
        public void GuidsHashesAndHandlesAreTakenOutOfTheAssistantsWords()
        {
            var card = ConfirmCardModel.From(CodeRequest("x();",
                "Rename sheets 7f3a1c2e-9b4d-4e1a-8c55-0123456789ab (h_1a2b3c4d) sha256:24e39f414fb880038fa382b6deb6d8702dd902117c003f1006412df64e4a0035",
                description: "Done for handle h_7f3a91c2."), ReceiptLanguage.En);
            Assert.Equal("Done for handle.\nRename sheets", card.AssistantText);
        }

        [Fact]
        public void OnlyRealHandlesGo_AndOnlyTheirOwnBrackets()
        {
            Assert.Equal("Type h_facade", ConfirmCardModel.Plain("Type h_facade"));            // six hex letters: a word, not a handle
            Assert.Equal("see h_decade and h_accede", ConfirmCardModel.Plain("see h_decade and h_accede"));
            Assert.Equal("level (L2)", ConfirmCardModel.Plain("level (L2 h_7f3a91c2)"));          // the bracket of the text stays
            Assert.Equal("(see).", ConfirmCardModel.Plain("(see h_deadbeef)."));
            Assert.Equal("Sheets, done", ConfirmCardModel.Plain("Sheets (h_1a2b3c4d), done"));     // the id's own brackets go with it
            Assert.Equal("Sheets [x] done", ConfirmCardModel.Plain("Sheets [x] [h_1a2b3c4d] done"));
            Assert.Equal("is done", ConfirmCardModel.Plain("h_1a2b3c4d is done"));
        }

        [Fact]
        public void TextWithNoIdIsReturnedExactlyAsItWas()
        {
            foreach (var words in new[] { "Delete sheet 'A101 : Plan'", "Door  A", "  padded  ", "Create schedule 'Door Schedule QS' (8 columns, 62 rows); level 02, Tower A." })
                Assert.Equal(words, ConfirmCardModel.Plain(words));
            Assert.Null(ConfirmCardModel.Plain(null));
            Assert.Equal("   ", ConfirmCardModel.Plain("   "));
        }

        [Fact]
        public void MeasuredRowsAreNeverRewritten()
        {
            // R42: the rows are the add-in's own facts; a drafter approves from them. Even a GUID value or an odd
            // sheet name stays exactly as measured.
            var env = Preview(modified: 1);
            env.Changes!.Touched!.Modified![0] = new FieldChangeDto { Id = 1, Field = "Mark", Before = "D1", After = "7f3a1c2e-9b4d-4e1a-8c55-0123456789ab" };
            env.Warnings = new List<WarningDto> { new WarningDto { Text = "Sheet 7f3a1c2e-9b4d-4e1a-8c55-0123456789ab  has no title block.", Severity = "warning", Ids = new List<long> { 1 } } };
            var card = ConfirmCardModel.From(CodeRequest("x();", "Set marks", null, env), ReceiptLanguage.En);
            Assert.Contains(card.Rows, r => r.Text == "Mark: D1 → 7f3a1c2e-9b4d-4e1a-8c55-0123456789ab (1)");
            Assert.Contains(card.Rows, r => r.Kind == ConfirmRowKind.Warning && r.Text == "Sheet 7f3a1c2e-9b4d-4e1a-8c55-0123456789ab  has no title block.");
        }

        [Fact]
        public void TheCodeToggleReadsInTheDraftersLanguage_ClosedByDefault()
        {
            Assert.Equal("Show code", ConfirmCardModel.CodeToggleLabel(ms: false, open: false));
            Assert.Equal("Hide code", ConfirmCardModel.CodeToggleLabel(ms: false, open: true));
            Assert.Equal("Tunjuk kod", ConfirmCardModel.CodeToggleLabel(ms: true, open: false));
            Assert.Equal("Sembunyikan kod", ConfirmCardModel.CodeToggleLabel(ms: true, open: true));
        }
    }
}
