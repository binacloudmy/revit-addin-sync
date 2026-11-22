#nullable enable
using System.Collections.Generic;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>C4.2.2: a sheet's own name and number are instance properties of the ViewSheet, never a type rename. The pure rules;
    /// the Revit-bound lookup (SHEET_NAME fallback) is exercised live.</summary>
    public class SheetParamsTests
    {
        [Theory]
        [InlineData("Sheet Name", SheetParamKind.Name)]
        [InlineData("sheet name", SheetParamKind.Name)]
        [InlineData("Name", SheetParamKind.Name)]
        [InlineData(" Sheet Title ", SheetParamKind.Name)]
        [InlineData("Sheet Number", SheetParamKind.Number)]
        [InlineData("Number", SheetParamKind.Number)]
        [InlineData("Drawn By", SheetParamKind.None)]
        [InlineData("Type Name", SheetParamKind.None)]
        [InlineData("", SheetParamKind.None)]
        [InlineData(null, SheetParamKind.None)]
        public void Sheet_param_names_are_classified(string? param, SheetParamKind expected) => Assert.Equal(expected, SheetParams.Classify(param));

        [Theory]
        [InlineData("A-101 Floor Plan")]
        [InlineData("  Pelan Aras 1 ")]
        [InlineData("Unnamed")]
        public void A_sheet_name_is_accepted_and_names_may_repeat(string name)
        {
            Assert.Null(SheetParams.Check(SheetParamKind.Name, name));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void An_empty_sheet_name_or_number_is_refused(string? v)
        {
            Assert.Contains("empty", SheetParams.Check(SheetParamKind.Name, v));
            Assert.Contains("empty", SheetParams.Check(SheetParamKind.Number, v));
        }

        [Theory]
        [InlineData("A:B")] [InlineData("A{B}")] [InlineData("A[1]")] [InlineData("A|B")] [InlineData("A;B")] [InlineData("A<B>")] [InlineData("A?")] [InlineData("A\\B")]
        public void A_sheet_number_with_a_character_revit_refuses_is_refused_before_the_write(string v) =>
            Assert.Contains("cannot contain", SheetParams.Check(SheetParamKind.Number, v));

        [Theory]
        [InlineData("Plan: Level 1")] [InlineData("A{B}")] [InlineData("A|B")]
        public void A_sheet_NAME_with_those_characters_is_not_refused_by_us_until_verified_live(string v) =>
            Assert.Null(SheetParams.Check(SheetParamKind.Name, v));

        [Fact]
        public void A_sheet_name_or_number_over_255_characters_is_refused()
        {
            Assert.Contains("at most 255", SheetParams.Check(SheetParamKind.Name, new string('a', 256)));
            Assert.Contains("at most 255", SheetParams.Check(SheetParamKind.Number, new string('a', 256)));
        }

        [Fact]
        public void A_sheet_number_another_sheet_holds_is_a_duplicate_even_with_other_case()
        {
            var ledger = new SheetParams.NumberLedger(new[] { new KeyValuePair<long, string>(1, "A-100"), new KeyValuePair<long, string>(2, "A-101") });
            var v = ledger.Claim(3, "a-101");
            Assert.NotNull(v);
            Assert.Contains("already has the number", v);
            Assert.Null(ledger.Claim(3, "A-102"));
        }

        [Fact]
        public void A_sheet_keeping_its_own_number_is_not_a_duplicate()
        {
            var ledger = new SheetParams.NumberLedger(new[] { new KeyValuePair<long, string>(1, "A-100") });
            Assert.Null(ledger.Claim(1, "A-100"));
        }

        [Fact]
        public void Two_sheets_of_one_call_cannot_take_the_same_number_but_the_first_is_kept()
        {
            // the planner skips the refused sheet with this reason; the other sheets still change (Partial)
            var ledger = new SheetParams.NumberLedger(new[] { new KeyValuePair<long, string>(1, "A-100"), new KeyValuePair<long, string>(2, "A-101"), new KeyValuePair<long, string>(3, "A-102") });
            Assert.Null(ledger.Claim(1, "B-1"));
            Assert.NotNull(ledger.Claim(2, "B-1"));
            Assert.Null(ledger.Claim(3, "B-3"));
        }

        [Fact]
        public void Type_param_true_on_a_sheet_is_refused_with_the_way_out()
        {
            var msg = SheetParams.TypeParamRefusal("Sheet Name");
            Assert.Contains("type_param false", msg);
            Assert.Contains("nothing was changed", msg);
        }

        [Fact]
        public void Plain_name_with_type_param_stays_refused_on_sheets_and_grids_and_Type_Name_is_the_only_type_rename()
        {
            foreach (var c in new[] { "Sheets", "Grids", "Levels", "Views" })
                Assert.True(TypeRename.NameIsInstanceProperty(c));
            Assert.False(TypeRename.IsTypeNameParam("Name"));
            Assert.False(TypeRename.IsTypeNameParam("Sheet Name"));
            Assert.Equal(SheetParamKind.None, SheetParams.Classify("Type Name"));   // never swallowed by the sheet path
            Assert.Contains("type_param false", TypeRename.InstanceNameRefusal("Grids"));
        }

        [Fact]
        public void The_c422_prompt_names_the_params_the_writer_accepts_and_forbids_type_param()
        {
            var v = JkrCopilotPrompt.VariantFor("C4.2.2");
            Assert.Equal(new[] { "Sheet Name", "Drawn By" }, v.Params);
            Assert.All(v.Params, p => Assert.Contains("'" + p + "'", v.How));
            Assert.Contains("type_param false", v.How);
            Assert.Contains("ask_user", v.How);
        }
    }
}
