#nullable enable
// Live test round 9: the progress strip (ReceiptTemplates.Headline over TurnEvidence.Receipt) miscounted.
//   attempt 1, D-t01  "50/50 done" for 58 tags: the code write's touched list is capped at 50 rows;
//                     the real total (58) is in data.changes and verified.checked.
//   F-t04             "10/20 done · 10 unchanged": two set_parameters calls reached the same 10 types; the
//                     first changed them, the second found them already set. The 10 were counted twice.
//   E-c07             "0/2 done · 2 failed (timed out)" with three failed calls.
// Envelopes are shaped like the live ones, with invented names.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ReceiptCountTests
    {
        private static int _call;

        private static void Add(TurnEvidence ev, string tool, string json, string kind = ToolRequestKinds.Write)
        {
            var env = CopilotJson.Deserialize<Envelope>(json)!;
            var id = "c" + (++_call);
            ev.Add(new ToolRequestDto
            {
                RequestId = "r" + _call, Kind = kind,
                Calls = new List<ToolCallDto> { new ToolCallDto { CallId = id, IdempotencyKey = "k" + id, Tool = tool } },
            }, new[] { env });
        }

        private static string Headline(TurnEvidence ev) => ReceiptTemplates.Headline(ev.Receipt, ReceiptLanguage.En);

        private static string Ids(IEnumerable<long> ids) => string.Join(",", ids.Select(i => "{\"id\":" + i + "}"));

        [Fact]
        public void A_code_write_counts_every_element_it_made_not_the_capped_list()
        {
            var ev = new TurnEvidence();
            // 58 tags made; the envelope lists the first 50 and says 58 in data.changes and verified.checked.
            Add(ev, ToolNames.RunRevitCode,
                "{\"ok\":true,\"status\":\"done\",\"phase\":\"commit\",\"summary\":\"MADE 58 Door tags\",\"handle\":\"h_1\","
                + "\"data\":{\"mode\":\"write\",\"output\":\"MADE 58 Door tags\",\"changes\":{\"added\":58,\"deleted\":0}},"
                + "\"changes\":{\"touched\":{\"added\":[" + Ids(Enumerable.Range(1, 50).Select(i => (long)i)) + "],\"modified\":[],\"deleted\":[]}},"
                + "\"verified\":{\"method\":\"exists\",\"checked\":58,\"mismatches\":[]}}");

            Assert.Equal("58/58 done", Headline(ev));
        }

        private static string TypeWrite(string status, string before) =>
            "{\"ok\":true,\"status\":\"" + status + "\",\"phase\":\"commit\",\"summary\":\"Set Rated Current = 0.04 A on 3 types.\",\"count\":3,"
            + "\"data\":{\"param\":\"Rated Current\",\"type_param\":true},"
            + "\"changes\":{\"touched\":{\"added\":[],\"modified\":["
            + string.Join(",", new[] { 11, 12, 13 }.Select(i => "{\"id\":" + i + ",\"field\":\"Rated Current\",\"before\":\"" + before + "\",\"after\":\"0.04 A\"}"))
            + "],\"deleted\":[]}}}";

        [Fact]
        public void Two_writes_to_the_same_types_count_each_type_once()
        {
            var ev = new TurnEvidence();
            Add(ev, ToolNames.SetParameters, TypeWrite("done", "0 A"));
            Add(ev, ToolNames.SetParameters, TypeWrite("noop", "0.04 A"));   // the same 3 types, already set by the first

            Assert.Equal("3/3 done", Headline(ev));
        }

        [Fact]
        public void An_unchanged_write_to_other_elements_still_counts_as_unchanged()
        {
            var ev = new TurnEvidence();
            Add(ev, ToolNames.SetParameters, TypeWrite("done", "0 A"));
            Add(ev, ToolNames.SetParameters,
                "{\"ok\":true,\"status\":\"noop\",\"phase\":\"commit\",\"summary\":\"Nothing to change.\",\"count\":2,"
                + "\"changes\":{\"touched\":{\"added\":[],\"modified\":[{\"id\":21,\"field\":\"Rated Current\",\"before\":\"0.04 A\",\"after\":\"0.04 A\"},"
                + "{\"id\":22,\"field\":\"Rated Current\",\"before\":\"0.04 A\",\"after\":\"0.04 A\"}],\"deleted\":[]}}}");

            Assert.Equal("3/5 done · 2 unchanged", Headline(ev));
        }

        private const string Timeout =
            "{\"ok\":false,\"status\":\"timeout\",\"phase\":\"preview\",\"summary\":\"Stopped at line 9 after 31.0 s: the code passed the time limit of 30 s.\","
            + "\"data\":{\"limit\":\"time\",\"steps\":100,\"seconds\":31.0,\"lines\":[9]},"
            + "\"error\":{\"code\":\"timeout\",\"message\":\"The code was stopped.\"},\"next\":\"Retry smaller.\"}";

        [Fact]
        public void Three_timed_out_code_runs_after_a_scripts_question_read_three_failed()
        {
            var ev = new TurnEvidence();
            // The stored script stops to ask first (not a write the strip counts), then three runs time out.
            Add(ev, ToolNames.RunRevitCode,
                "{\"ok\":true,\"status\":\"noop\",\"phase\":\"preview\",\"summary\":\"The code ran but changed nothing.\","
                + "\"data\":{\"mode\":\"write\",\"output\":\"NOTHING MADE: ask each ASK, then rerun\\nASK which scope? Whole model (12 walls)\",\"changes\":{\"added\":0,\"deleted\":0}}}");
            Add(ev, ToolNames.RunRevitCode, Timeout);
            Add(ev, ToolNames.RunRevitCode, Timeout);
            Add(ev, ToolNames.RunRevitCode, Timeout);

            Assert.Equal("0/3 done · 3 failed (timed out)", Headline(ev));
        }
    }
}
