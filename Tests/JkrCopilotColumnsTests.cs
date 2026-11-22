using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-531 (WP-9): C1.6.3 "Fix via Copilot" adds the missing columns to a room schedule with the stored playbook edit-schedule, and fills empty
    /// room values with set_parameters. The columns come from the evaluator's own note, never from the model. Revit- and WPF-free.</summary>
    public class JkrCopilotColumnsTests
    {
        private static readonly string[] Cols = { "Occupancy", "Department", "Kod_Ruang_AR_jkr_stt" };

        private static CopilotPromptFacts Mixed() => new CopilotPromptFacts
        {
            CheckId = "C1.6.3", Category = "Schedule, Rooms",
            ElementIds = new List<long> { 900, 101, 102 },
            ElementLabels = new Dictionary<long, string> { [900] = "Schedule · Room Finishes", [101] = "Rooms · Pejabat", [102] = "Rooms · Stor" },
            Columns = new Dictionary<long, IReadOnlyList<string>> { [900] = Cols },
        };

        [Fact]
        public void The_note_the_evaluator_writes_is_read_back_into_the_column_names()
        {
            var note = SpecBC.LacksNote("Room Finishes", 200, Cols);
            Assert.Equal("Schedule 'Room Finishes' lacks the columns mandatory at LOi 200 (PB p.37): Occupancy, Department, Kod_Ruang_AR_jkr_stt", note);
            Assert.True(SpecBC.TryLacksColumns("Model: 3 columns missing\n" + note + "\nRequired: x", out var got));
            Assert.Equal(Cols, got);
            Assert.False(SpecBC.TryLacksColumns("Pejabat | Occupancy, Department", out _));
            Assert.False(SpecBC.TryLacksColumns("", out _));
            Assert.False(SpecBC.TryLacksColumns(null, out _));
        }

        [Fact]
        public void A_schedule_line_names_the_schedule_and_the_columns_and_the_prompt_runs_edit_schedule()
        {
            var p = Assert.Single(JkrCopilotPrompt.Build(Mixed()));
            Assert.StartsWith("Update the schedules and rooms below: add a field to each schedule for every mandatory column it lacks", p);
            Assert.Contains("- 900 — schedule \"Room Finishes\" — add columns: Occupancy, Department, Kod_Ruang_AR_jkr_stt", p);
            Assert.Contains("- 101 — Rooms · Pejabat", p);
            Assert.Contains("playbook \"edit-schedule\"", p);
            Assert.Contains("inputs target = that schedule name and add = the columns on its line", p);
            Assert.Contains("Never add a schedule", p);
            // the backend vetoes the edit-schedule suggestion on "make", "new" or "create" beside "schedule" (its not_for list): none in the words this family adds
            var ours = JkrCopilotPrompt.VariantFor("C1.6.3").ScheduleHow + p.Substring(0, p.IndexOf('\n'));
            Assert.DoesNotMatch(new Regex(@"\b(make|made|new|create|creating)\b", RegexOptions.IgnoreCase), ours);
            Assert.Contains("never bind or add a parameter yourself", p);
            Assert.Contains("set_parameters", p);                       // the rooms keep the set_parameters path
            Assert.Contains("ask me once with ask_user for all of them together", p);
            Assert.DoesNotContain("Baiki", p);                          // English only (AI-513)
            Assert.DoesNotMatch(new Regex(@"\b(Yes/No|Yes or No)\b", RegexOptions.IgnoreCase), p);
        }

        [Fact]
        public void Only_schedules_leave_out_the_room_text_and_only_rooms_leave_out_the_schedule_text()
        {
            var only = Mixed(); only.ElementIds = new List<long> { 900 };
            var ps = Assert.Single(JkrCopilotPrompt.Build(only));
            Assert.StartsWith("Update the schedules below: add a field to each schedule for every mandatory column it lacks. Change the schedules only.", ps);
            Assert.Contains("playbook \"edit-schedule\"", ps);
            Assert.DoesNotContain("set_parameters", ps);
            var rooms = Mixed(); rooms.ElementIds = new List<long> { 101, 102 }; rooms.Columns = new Dictionary<long, IReadOnlyList<string>>();
            var pr = Assert.Single(JkrCopilotPrompt.Build(rooms));
            Assert.StartsWith(JkrCopilotPrompt.VariantFor("C1.6.3").Task, pr);   // rooms only: the old wording
            Assert.DoesNotContain("edit-schedule", pr);
            Assert.Contains("set_parameters", pr);
        }

        [Fact]
        public void A_schedule_row_with_no_named_columns_gets_no_line_and_no_write()
        {
            var f = Mixed(); f.Columns = new Dictionary<long, IReadOnlyList<string>>();
            Assert.DoesNotContain(900, JkrCopilotPrompt.TargetIds(f));
            Assert.Equal(new long[] { 101, 102 }, JkrCopilotPrompt.TargetIds(f));
            var none = Mixed(); none.ElementIds = new List<long> { 900 }; none.Columns = new Dictionary<long, IReadOnlyList<string>>();
            Assert.Empty(JkrCopilotPrompt.Build(none));
        }

        [Fact]
        public void The_confirm_says_what_is_added_where_and_that_nothing_is_made_or_deleted()
        {
            var c = JkrCopilotConfirm.Build(Mixed());
            Assert.Contains("Add the mandatory columns to the 1 room schedule listed below with the stored playbook edit-schedule", c.Body);
            Assert.Contains("fill empty values on the 2 rooms", c.Body);
            Assert.Contains("no schedule is made, filtered or deleted", c.Body);
            Assert.Contains("you can Undo", c.Body);
            Assert.Contains("900 — schedule \"Room Finishes\" — add columns: Occupancy, Department, Kod_Ruang_AR_jkr_stt", c.Details);
            Assert.Contains("101 — Rooms · Pejabat", c.Details);
        }

        [Fact]
        public void Rooms_only_keep_the_old_confirm_and_no_confirmed_write_field()
        {
            var rooms = Mixed(); rooms.ElementIds = new List<long> { 101, 102 }; rooms.Columns = new Dictionary<long, IReadOnlyList<string>>();
            Assert.Contains("works out the new values", JkrCopilotConfirm.Build(rooms).Body);
            Assert.Null(JkrCopilotConfirmedWrite.ForPart(Mixed(), 0));
            Assert.Null(JkrCopilotConfirmedWrite.ForPart(rooms, 0));
        }

        [Fact]
        public void Merged_rows_keep_the_columns_of_each_schedule()
        {
            var a = Mixed(); a.ElementIds = new List<long> { 900 };
            var b = new CopilotPromptFacts { CheckId = "C1.6.3", Category = "Rooms", ElementIds = new List<long> { 101 }, ElementLabels = new Dictionary<long, string> { [101] = "Rooms · Pejabat" } };
            var m = JkrCopilotPrompt.MergeFacts(new[] { a, b });
            Assert.Equal(Cols, JkrCopilotPrompt.ColumnsOf(m, 900));
            Assert.Empty(JkrCopilotPrompt.ColumnsOf(m, 101));
            Assert.Equal(new long[] { 900, 101 }, JkrCopilotPrompt.TargetIds(m));
        }
    }
}
