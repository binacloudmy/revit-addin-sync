using System.Linq;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-435: the AI-fixer row — suggestion (or "no suggestion yet") and a manual
    /// input together, the manual value validated against the rule before any write.</summary>
    public class JkrFixerRowTests
    {
        // ── what a manual value becomes ──

        [Theory]
        [InlineData("Kod_Jenis_jkr_stt value valid", "Kod_Jenis_jkr_stt", "type")]
        [InlineData("Bahan_Utama_jkr_stt material code valid", "Bahan_Utama_jkr_stt", "type")]
        [InlineData("Kaedah_Pemasangan_jkr_sit", "Kaedah_Pemasangan_jkr_sit", "instance")]
        public void A_rule_that_names_a_parameter_becomes_set_parameter(string rule, string param, string target)
        {
            var p = JkrManualFix.Plan(rule, "Walls", "(kt) Konkrit");
            Assert.True(p.Ok, p.Error);
            Assert.Equal("set_parameter", p.Action);
            Assert.Equal(param, p.ParameterName);
            Assert.Equal(target, p.Target);
        }

        [Fact]
        public void A_name_rule_becomes_rename_type()
        {
            var p = JkrManualFix.Plan("Material code in name is valid", "Walls", "jkrAR_wll-b_(bb02) Batu Bata");
            Assert.True(p.Ok, p.Error);
            Assert.Equal("rename_type", p.Action);
            Assert.Equal("jkrAR_wll-b_(bb02) Batu Bata", p.Value);
        }

        // ── validation before write ──

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_is_refused(string v) => Assert.False(JkrManualFix.Plan("Naming convention", "Walls", v).Ok);

        [Fact]
        public void A_name_must_carry_the_jkr_prefix()
        {
            Assert.False(JkrManualFix.Plan("Naming convention", "Walls", "Basic Wall").Ok);
            Assert.True(JkrManualFix.Plan("Naming convention", "Walls", "jkrAR_wll-b_(bb02) Batu Bata").Ok);
        }

        [Fact]
        public void A_material_parameter_needs_a_code_in_brackets()
        {
            Assert.False(JkrManualFix.Plan("Bahan_Utama_jkr_stt material code valid", "Walls", "Konkrit").Ok);
            Assert.True(JkrManualFix.Plan("Bahan_Utama_jkr_stt material code valid", "Walls", "(kt) Konkrit").Ok);
        }

        [Fact]
        public void Levels_and_grids_use_their_own_pattern()
        {
            Assert.True(JkrManualFix.Plan("Naming convention", "Levels", "L02 +4.500").Ok);
            Assert.False(JkrManualFix.Plan("Naming convention", "Levels", "Level 2").Ok);
            Assert.True(JkrManualFix.Plan("Naming convention", "Grids", "B2").Ok);
            Assert.False(JkrManualFix.Plan("Naming convention", "Grids", "I").Ok);
        }

        [Fact]
        public void A_reference_set_constrains_the_value_and_returns_its_spelling()
        {
            var set = new[] { "(kt) Konkrit", "(bb02) Batu Bata" };
            var ok = JkrManualFix.Plan("Bahan_Utama_jkr_stt material code valid", "Walls", " (KT) konkrit ", set);
            Assert.True(ok.Ok, ok.Error);
            Assert.Equal("(kt) Konkrit", ok.Value);
            Assert.False(JkrManualFix.Plan("Bahan_Utama_jkr_stt material code valid", "Walls", "(zz) Lain", set).Ok);
        }

        [Fact]
        public void Errors_follow_the_language()
        {
            Assert.Equal("Enter a value.", JkrManualFix.Plan("x", "", "", null).Error);
        }

        // ── the row ──

        private static JkrFixerRow Row(FixerAiState s, string value = "", string reason = "", string[] options = null) =>
            new JkrFixerRow("1", "Naming convention", "Walls", s, value, reason, options);

        [Fact]
        public void Not_asked_says_no_suggestion_yet_and_offers_ask_AI_and_the_manual_input()
        {
            var r = Row(FixerAiState.NotAsked);
            Assert.Equal("No suggestion yet.", r.AiLine);
            Assert.True(r.CanAsk);
            Assert.False(r.CanAccept);
            Assert.True(r.IsFreeText);          // the manual input is there with no suggestion
        }

        [Fact]
        public void Asked_with_nothing_back_says_so()
        {
            var r = Row(FixerAiState.NoSuggestion);
            Assert.Equal("The AI fixer has no suggestion for this item.", r.AiLine);
            Assert.True(r.CanAsk);
        }

        [Fact]
        public void A_suggestion_shows_with_its_reason_and_the_manual_input_stays()
        {
            var r = Row(FixerAiState.Suggested, "jkrAR_wll-b_(bb02) Batu Bata", "category mismatch");
            Assert.True(r.CanAccept);
            Assert.False(r.CanAsk);
            Assert.Equal("category mismatch", r.AiReason);
            Assert.True(r.HasAiReason);
            Assert.Equal("", r.AiLine);
            Assert.False(string.IsNullOrEmpty(r.ManualLabel));
            r.ManualValue = "jkrAR_wll-b_(kt) Konkrit";
            Assert.True(r.CanWriteManual);      // a person may override a suggestion
        }

        [Fact]
        public void A_reason_without_a_suggestion_is_not_shown()
        {
            Assert.False(Row(FixerAiState.NoSuggestion, "x", "stale").HasAiReason);
        }

        [Fact]
        public void Typing_validates_inline_and_gates_write()
        {
            var r = Row(FixerAiState.NotAsked);
            Assert.False(r.CanWriteManual);
            Assert.Equal("", r.ManualError);    // an empty box is not an error
            r.ManualValue = "Basic Wall";
            Assert.True(r.HasManualError);
            Assert.False(r.CanWriteManual);
            r.ManualValue = "jkrAR_wll-b_(bb02) Batu Bata";
            Assert.False(r.HasManualError);
            Assert.True(r.CanWriteManual);
        }

        [Fact]
        public void A_reference_set_turns_the_input_into_a_dropdown_of_that_set()
        {
            var r = Row(FixerAiState.NotAsked, options: new[] { "(kt) Konkrit", "(bb02) Batu Bata", "" });
            Assert.True(r.HasOptions);
            Assert.Equal(new[] { "(kt) Konkrit", "(bb02) Batu Bata" }, r.Options.ToArray());
            r.ManualValue = "(zz) Lain";
            Assert.False(r.CanWriteManual);
            r.ManualValue = "(kt) Konkrit";
            Assert.True(r.CanWriteManual);
        }
    }
}
