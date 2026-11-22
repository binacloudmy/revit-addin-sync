#nullable enable
// Boards 8-10 against the contract's own examples (contracts/copilot/examples/turn-result.json):
// the items and mapping read, grouped, and given the buttons their kind and ids allow.
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ResultItemsTests
    {
        private static string ContractsDir([CallerFilePath] string thisFile = "")
        {
            var copied = Path.Combine(AppContext.BaseDirectory, "contracts", "copilot");
            if (File.Exists(Path.Combine(copied, "tools.json"))) return copied;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "contracts", "copilot");
                if (File.Exists(Path.Combine(candidate, "tools.json"))) return candidate;
            }
            var fromSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "..", "contracts", "copilot"));
            if (File.Exists(Path.Combine(fromSource, "tools.json"))) return fromSource;
            throw new DirectoryNotFoundException("contracts/copilot not found");
        }

        private static TurnResultDto Result(string section, string name)
        {
            var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir(), "examples", "turn-result.json"))).RootElement;
            // valid cases are the turn_end itself; invalid ones wrap it in { why, add_in, data }
            var c = doc.GetProperty(section).GetProperty(name);
            var data = section == "valid" ? c : c.GetProperty("data");
            return CopilotJson.Deserialize<TurnEndDto>(data.GetRawText())!.ReadResult()!;
        }

        // ---- board 8: sheets ----

        [Fact]
        public void Sheets_AreGroupedByTheirGroup_EachWithNumberScaleAndFill_AndAnOpenSheetButton()
        {
            var items = ResultItems.From(Result("valid", "created_sheets_en"))!;

            Assert.Equal(new[] { "Lighting plans", "Elevations" }, items.Sections.Select(s => s.Name).ToArray());
            var row = items.Sections[0].Rows[0];
            Assert.Equal("Level 1 Lighting Plan", row.Title);
            Assert.Equal("EL-101", row.Cell("number"));
            Assert.Equal("EL-101 · 1:100 · fills 72%", ItemText.FactsLine(row, ReceiptLanguage.En));
            Assert.Equal("1:100 · fills 72%", ItemText.FactsLine(row, ReceiptLanguage.En, withNumber: false));
            Assert.Equal("EL-101 · 1:100 · memenuhi 72%", ItemText.FactsLine(row, ReceiptLanguage.Ms));
            Assert.Equal(new long[] { 412001 }, row.Ids.ToArray());          // a sheet item has exactly one id: the sheet
            Assert.Equal(ItemButton.OpenSheet, row.Button);
            Assert.Equal("Open sheet in Revit", ItemText.ButtonLabel(row.Button, ReceiptLanguage.En));
            Assert.Equal("Buka sheet dalam Revit", ItemText.ButtonLabel(row.Button, ReceiptLanguage.Ms));
            Assert.Equal("Lighting plans · 2", ItemText.SectionTitle(items.Sections[0]));      // the sheet and its view
        }

        [Fact]
        public void AViewIsItsOwnRow_AndGetsTheOpenViewButton()
        {
            var items = ResultItems.From(Result("valid", "created_sheets_en"))!;

            var view = items.Sections[0].Rows.Single(r => r.Kind == "view");
            Assert.Equal("Level 1 - Lighting", view.Title);
            Assert.Equal(new long[] { 301001 }, view.Ids.ToArray());
            Assert.Equal(ItemButton.OpenView, view.Button);
            Assert.Equal("", ItemText.FactsLine(view, ReceiptLanguage.En));
        }

        [Fact]
        public void TheSheetResult_AlsoKeepsItsChecksAndNextSteps()
        {
            var card = new ResultCardModel { Kind = ResultKind.Created }.WithServerResult(Result("valid", "created_sheets_en"));

            Assert.Equal(new[] { "Tag lights on these plans", "Add a sheet list schedule" }, card.Next.ToArray());
            Assert.Equal("Keep 1:150", Assert.Single(Assert.Single(card.Checks).Actions).Label);
            Assert.Equal(4, card.Items!.ItemCount);
        }

        // ---- board 9: sheets from Excel ----

        [Fact]
        public void TheExcelMapping_ReadsAsColumnToParameter()
        {
            var items = ResultItems.From(Result("valid", "created_sheets_from_excel"))!;

            Assert.Equal(new[] { "Sheet No → Sheet Number", "Sheet Title → Sheet Name", "Size → Title block" },
                items.Mapping.Select(ItemText.MappingLine).ToArray());
        }

        [Fact]
        public void ExcelRows_ShowTheirRowNumberAndState_AndOnlyTheOnesWithIdsGetAButton()
        {
            var items = ResultItems.From(Result("valid", "created_sheets_from_excel"))!;
            var rows = items.Sections.SelectMany(s => s.Rows).ToList();

            var sheet = rows.Single(r => r.Kind == "sheet");
            Assert.Equal("EL-000 · row 2", ItemText.FactsLine(sheet, ReceiptLanguage.En));
            Assert.Equal(ItemButton.OpenSheet, sheet.Button);

            var notDone = rows.Single(r => r.Kind == "row");
            Assert.Equal("EL-005 · row 7 · Already in model", ItemText.FactsLine(notDone, ReceiptLanguage.En));
            Assert.Equal("EL-005 · baris 7 · Already in model", ItemText.FactsLine(notDone, ReceiptLanguage.Ms));
            Assert.Equal(ItemButton.None, notDone.Button);
            Assert.Equal("", ItemText.ButtonLabel(ItemButton.None, ReceiptLanguage.En));
        }

        // ---- board 10: smart tags ----

        [Fact]
        public void TaggedViews_ListTheViewWithItsTagCount_ThenTagTypesAndTaggedElementsUnderTheirView()
        {
            var items = ResultItems.From(Result("valid", "tagged_views"))!;

            Assert.Equal(new[] { "", "Level 1 - Lighting", "Level 2 - Lighting" }, items.Sections.Select(s => s.Name).ToArray());
            var view = items.Sections[0].Rows.Single();
            Assert.Equal(("view", 82), (view.Kind, view.Count));
            Assert.Equal(ItemButton.OpenView, view.Button);
            Assert.Equal("Open view", ItemText.ButtonLabel(view.Button, ReceiptLanguage.En));
            Assert.Equal("82", ItemText.SectionTitle(items.Sections[0]));

            var tag = items.Sections[1].Rows.Single();
            Assert.Equal(("Downlight 1x13W", 38), (tag.Title, tag.Count));
            Assert.Equal(ItemButton.Select, tag.Button);
            Assert.Equal("Level 1 - Lighting · 38", ItemText.SectionTitle(items.Sections[1]));

            var element = items.Sections[2].Rows.Single();
            Assert.Equal("No Type Mark", element.Title);
            Assert.Equal("Select them in Revit", ItemText.ButtonLabel(element.Button, ReceiptLanguage.En));
            Assert.Equal("Pilih dalam Revit", ItemText.ButtonLabel(element.Button, ReceiptLanguage.Ms));
        }

        // ---- shared ----

        [Fact]
        public void AResultWithNoItemsOrMapping_HasNoItems()
        {
            Assert.Null(ResultItems.From(Result("valid", "brochure_fill_ms")));
            Assert.Null(ResultItems.From(null));
        }

        [Fact]
        public void ARowWithoutIds_HasNoButton_WhateverItsKind()
        {
            var r = new ItemRow { Kind = "sheet", Title = "x" };
            Assert.Equal(ItemButton.None, r.Button);
            r.Ids.Add(5);
            Assert.Equal(ItemButton.OpenSheet, r.Button);
        }

        [Fact]
        public void AnItemWithoutATitle_IsLeftOut_AndAnIdThatIsNotAnInteger_IsNeverAnId()
        {
            var noTitle = Result("invalid", "item_without_title");
            Assert.Null(ResultItems.From(noTitle));

            var badId = Result("invalid", "item_id_not_an_integer");
            var items = ResultItems.From(badId)!;
            Assert.Empty(items.Sections.Single().Rows.Single().Ids);
            Assert.Contains(TurnResultInvariants.Violations(badId), v => v.Contains("ids[0] must be an integer"));
        }

        [Fact]
        public void TheFourNewInvalidCases_AreEachReportedAsViolations()
        {
            foreach (var name in new[] { "item_without_title", "item_kind_unknown", "item_id_not_an_integer", "mapping_without_param" })
                Assert.NotEmpty(TurnResultInvariants.Violations(Result("invalid", name)));
            Assert.Contains(TurnResultInvariants.Violations(Result("invalid", "item_kind_unknown")), v => v.Contains("kind 'level'"));
            Assert.Contains(TurnResultInvariants.Violations(Result("invalid", "mapping_without_param")), v => v.Contains("mapping[0].param"));
        }

        [Fact]
        public void ExtraCells_FollowTheKnownOnes_AsGiven()
        {
            var r = new TurnResultDto
            {
                Reply = "x", Kind = "created",
                Items = new System.Collections.Generic.List<ResultItemDto>
                {
                    new ResultItemDto { Title = "t", Cells = new System.Collections.Generic.Dictionary<string, string> { ["zone"] = "North", ["row"] = "3", ["number"] = "A1" } },
                },
            };

            var row = ResultItems.From(r)!.Sections[0].Rows[0];
            Assert.Equal("A1 · row 3 · North", ItemText.FactsLine(row, ReceiptLanguage.En));
        }

        [Fact]
        public void ASectionWithoutAGroupName_IsHeadedByWhatItsRowsAre()
        {
            var items = ResultItems.From(Result("valid", "tagged_views"))!;

            Assert.Equal("Views · 82", ItemText.SectionHeader(items.Sections[0], ReceiptLanguage.En));
            Assert.Equal("View · 82", ItemText.SectionHeader(items.Sections[0], ReceiptLanguage.Ms));
            Assert.Equal("Level 1 - Lighting · 38", ItemText.SectionHeader(items.Sections[1], ReceiptLanguage.En));
            Assert.Equal("Sheets", ItemText.KindLabel("sheet", ReceiptLanguage.En));
            Assert.Equal("Items", ItemText.KindLabel(null, ReceiptLanguage.En));
        }
    }
}
