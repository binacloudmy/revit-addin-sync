// RenameCandidates.Build — the single rename planner.
//
// The contract: dry_run and the transaction loop consume ONE plan. They used to
// derive names independently (Contains/Replace copy-pasted between the preview
// block and the apply loop), which is how a preview comes to promise 40 renames
// that land as 12. Every assertion here is about the plan being complete and
// honest BEFORE anything is written.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace Tests
{
    public class RenameCandidatesTests
    {
        private static RenameTarget T(long id, string name, string scope = "family",
                                     string number = "", string kind = "type")
            => new RenameTarget
            {
                Id = id, Kind = kind, CurrentName = name,
                CurrentNumber = number, UniquenessScope = scope,
            };

        [Fact]
        public void Literal_replace_produces_one_candidate()
        {
            var plan = RenameCandidates.Build(
                new[] { T(1, "jkrAR18_door") }, "jkrAR18", "jkrAR25",
                RenameField.Name, RenameMode.Literal);

            Assert.Null(plan.Error);
            Assert.Equal(1, plan.WouldRename);
            Assert.Equal("jkrAR18_door", plan.Candidates[0].From);
            Assert.Equal("jkrAR25_door", plan.Candidates[0].To);
            Assert.Equal("name", plan.Candidates[0].Field);
        }

        [Fact]
        public void Non_matching_targets_are_skipped_entirely()
        {
            var plan = RenameCandidates.Build(
                new[] { T(1, "Basic Wall") }, "jkrAR18", "jkrAR25",
                RenameField.Name, RenameMode.Literal);

            Assert.Empty(plan.Candidates);
            Assert.Equal(0, plan.WouldRename);
        }

        [Fact]
        public void A_replace_that_changes_nothing_is_not_a_candidate()
        {
            // find == replace. Reporting this as a rename inflates the count the
            // drafter approves.
            var plan = RenameCandidates.Build(
                new[] { T(1, "jkrAR18_door") }, "jkrAR18", "jkrAR18",
                RenameField.Name, RenameMode.Literal);

            Assert.Empty(plan.Candidates);
        }

        [Fact]
        public void A_replace_producing_a_blank_name_is_refused()
        {
            // Revit rejects an empty name; emitting it as renamable guarantees a
            // skip at apply time.
            var plan = RenameCandidates.Build(
                new[] { T(1, "jkrAR18") }, "jkrAR18", "",
                RenameField.Name, RenameMode.Literal);

            Assert.Empty(plan.Candidates);
        }

        [Fact]
        public void Literal_matching_is_case_sensitive()
        {
            var plan = RenameCandidates.Build(
                new[] { T(1, "JKRAR18_door") }, "jkrAR18", "jkrAR25",
                RenameField.Name, RenameMode.Literal);

            Assert.Empty(plan.Candidates);
        }

        [Fact]
        public void Regex_mode_anchors_a_version_bump_safely()
        {
            // THE motivating case: a bare "18" -> "25" would maul the project
            // code. An anchored pattern cannot.
            var plan = RenameCandidates.Build(
                new[] { T(1, "jkrAR18_3a_(BE18sr18_p18-018)") },
                @"^jkrAR18", "jkrAR25",
                RenameField.Name, RenameMode.Regex);

            Assert.Equal("jkrAR25_3a_(BE18sr18_p18-018)", plan.Candidates[0].To);
        }

        [Fact]
        public void An_invalid_regex_is_a_plan_error_not_a_crash()
        {
            var plan = RenameCandidates.Build(
                new[] { T(1, "anything") }, "[unclosed", "x",
                RenameField.Name, RenameMode.Regex);

            Assert.NotNull(plan.Error);
            Assert.Empty(plan.Candidates);
        }

        [Fact]
        public void Field_number_reads_and_writes_the_number()
        {
            var plan = RenameCandidates.Build(
                new[] { T(1, "Ground Floor Plan", "sheet_number", "jkrAR18-A-101", "sheet") },
                "jkrAR18", "jkrAR25", RenameField.Number, RenameMode.Literal);

            Assert.Single(plan.Candidates);
            Assert.Equal("number", plan.Candidates[0].Field);
            Assert.Equal("jkrAR18-A-101", plan.Candidates[0].From);
            Assert.Equal("jkrAR25-A-101", plan.Candidates[0].To);
        }

        [Fact]
        public void Field_both_emits_a_candidate_per_field()
        {
            var plan = RenameCandidates.Build(
                new[] { T(1, "jkrAR18 Ground Floor", "sheet_number", "jkrAR18-A-101", "sheet") },
                "jkrAR18", "jkrAR25", RenameField.Both, RenameMode.Literal);

            Assert.Equal(2, plan.Candidates.Count);
            Assert.Contains(plan.Candidates, c => c.Field == "name");
            Assert.Contains(plan.Candidates, c => c.Field == "number");
        }

        [Fact]
        public void Field_both_on_a_target_with_no_number_emits_only_the_name()
        {
            var plan = RenameCandidates.Build(
                new[] { T(1, "jkrAR18_door") }, "jkrAR18", "jkrAR25",
                RenameField.Both, RenameMode.Literal);

            Assert.Single(plan.Candidates);
            Assert.Equal("name", plan.Candidates[0].Field);
        }

        [Fact]
        public void A_collision_with_an_existing_name_is_flagged_not_dropped()
        {
            // jkrAR25_door already exists. Silently skipping this at apply time
            // is how "renamed 3 of 40" happens after a preview promised 40.
            var plan = RenameCandidates.Build(
                new[] { T(1, "jkrAR18_door"), T(2, "jkrAR25_door") },
                "jkrAR18", "jkrAR25", RenameField.Name, RenameMode.Literal);

            var c = Assert.Single(plan.Candidates);
            Assert.True(c.Collides);
            Assert.Equal(2, c.CollidesWith);
            Assert.Equal(0, plan.WouldRename);
            Assert.Equal(1, plan.WouldCollide);
        }

        [Fact]
        public void Two_renames_colliding_with_each_other_are_flagged()
        {
            // Both land on the same new name. The second is a collision even
            // though nothing pre-existing occupies it.
            var plan = RenameCandidates.Build(
                new[] { T(1, "jkrAR18_door_a"), T(2, "jkrAR18_door_b") },
                @"jkrAR18_door_\w", "jkrAR25_door", RenameField.Name, RenameMode.Regex);

            Assert.Equal(2, plan.Candidates.Count);
            Assert.Equal(1, plan.WouldRename);
            Assert.Equal(1, plan.WouldCollide);
        }

        [Fact]
        public void Collisions_are_scoped_not_global()
        {
            // Same target name in two different families is legal in Revit.
            var plan = RenameCandidates.Build(
                new[]
                {
                    T(1, "jkrAR18_leaf", "type:DoorA"),
                    T(2, "jkrAR25_leaf", "type:DoorB"),
                },
                "jkrAR18", "jkrAR25", RenameField.Name, RenameMode.Literal);

            var c = Assert.Single(plan.Candidates);
            Assert.False(c.Collides);
            Assert.Equal(1, plan.WouldRename);
        }

        [Fact]
        public void A_renamed_away_name_frees_its_slot()
        {
            // A is renamed to B's CURRENT name while B is ALSO renamed away in
            // the same sweep (both match "1" -> "12"; B's current "door_12"
            // itself contains "1", so it moves on to "door_122"). Without the
            // pass-1 vacate step, B's pre-sweep value would still be marked
            // occupied when A claims it, reporting a phantom collision and
            // blocking a legal sweep. With the vacate step, neither collides.
            var plan = RenameCandidates.Build(
                new[] { T(1, "door_1"), T(2, "door_12") },
                "1", "12", RenameField.Name, RenameMode.Literal);

            Assert.Equal(2, plan.Candidates.Count);
            Assert.DoesNotContain(plan.Candidates, c => c.Collides);
            Assert.Equal(2, plan.WouldRename);
            Assert.Equal(0, plan.WouldCollide);

            var a = plan.Candidates.Single(c => c.Id == 1);
            Assert.Equal("door_1", a.From);
            Assert.Equal("door_12", a.To);

            var b = plan.Candidates.Single(c => c.Id == 2);
            Assert.Equal("door_12", b.From);
            Assert.Equal("door_122", b.To);
        }

        [Fact]
        public void Missing_find_is_a_plan_error()
        {
            var plan = RenameCandidates.Build(
                new[] { T(1, "x") }, "", "y", RenameField.Name, RenameMode.Literal);

            Assert.NotNull(plan.Error);
        }
    }
}
