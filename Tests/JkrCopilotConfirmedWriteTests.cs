using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-490: the confirmed_write field of the Fix via Copilot turn (bina-ai PR #402): built per family, capped, attached only
    /// after Ya and only to the launcher's own prompt turns.</summary>
    [Collection("ForcedConfirmStatic")]
    public class JkrCopilotConfirmedWriteTests
    {
        private static CopilotPromptFacts Facts(string check, int n, string cat = "Doors")
        {
            var ids = Enumerable.Range(1, n).Select(i => 1000L + i).ToList();
            return new CopilotPromptFacts
            {
                CheckId = check, Category = cat, ElementIds = ids, ElementLabels = ids.ToDictionary(i => i, i => cat + " · T" + i),
            };
        }

        [Theory]
        [InlineData("Db.2", "Material")]
        [InlineData("A5.2", "Name")]
        public void An_instance_family_sends_its_ids_and_the_declared_param_and_no_type_fields(string id, string param)
        {
            var d = JkrCopilotConfirmedWrite.ForPart(Facts(id, 3), 0);
            Assert.Equal("jkr_fix", d.Source);
            Assert.Equal("set_parameters", d.Tool);
            Assert.Equal(id, d.CheckId);
            Assert.Equal(new long[] { 1001, 1002, 1003 }, d.Ids);
            Assert.Equal(new[] { param }, d.Params);
            Assert.Null(d.TypeLevel);
            Assert.Null(d.Instances);
        }

        [Fact]
        public void The_sheet_family_declares_its_two_named_params()
        {
            Assert.Equal(new[] { "Sheet Name", "Drawn By" }, JkrCopilotConfirmedWrite.ForPart(Facts("C4.2.2", 2, "Sheets"), 0).Params);
        }

        [Theory]
        [InlineData("Da.1")]
        [InlineData("Dc.1")]
        public void A_type_family_sends_one_instance_id_per_type_type_level_true_and_the_instance_total(string id)
        {
            // 4 instances of 2 types: ids 1001,1002 are type "A", 1003,1004 are type "B"
            var f = new CopilotPromptFacts
            {
                CheckId = id, Category = "Doors", ElementIds = new long[] { 1001, 1002, 1003, 1004 },
                ElementLabels = new Dictionary<long, string> { [1001] = "Doors · A", [1002] = "Doors · A", [1003] = "Doors · B", [1004] = "Doors · B" },
            };
            var d = JkrCopilotConfirmedWrite.ForPart(f, 0);
            Assert.Equal(new long[] { 1001, 1003 }, d.Ids);   // the ids the prompt lists
            Assert.Equal(true, d.TypeLevel);
            Assert.Equal(4, d.Instances);
            Assert.Equal(new[] { "Type Name" }, d.Params);
            Assert.Null(JkrCopilotConfirmedWrite.ForPart(f, 1));   // a type family has one part only
        }

        [Fact]
        public void Families_whose_params_are_known_only_at_run_time_send_nothing_so_the_server_asks_as_before()
        {
            Assert.Null(JkrCopilotConfirmedWrite.ForPart(Facts("Db.1", 3), 0));
            Assert.Null(JkrCopilotConfirmedWrite.ForPart(Facts("C1.6.3", 3, "Rooms"), 0));
            Assert.Null(JkrCopilotConfirmedWrite.ForPart(Facts("Zz.9", 3), 0));   // generic wording: no declared param
            Assert.Null(JkrCopilotConfirmedWrite.ForPart(Facts("Db.2", 0), 0));    // nothing to confirm
        }

        [Fact]
        public void Each_part_confirms_only_its_own_ids_and_the_parts_cover_the_row_exactly_once()
        {
            var f = Facts("Db.2", 120);
            var parts = Enumerable.Range(0, 3).Select(i => JkrCopilotConfirmedWrite.ForPart(f, i)).ToList();
            Assert.Equal(new[] { 50, 50, 20 }, parts.Select(p => p.Ids.Count).ToArray());
            Assert.Equal(f.ElementIds.OrderBy(x => x), parts.SelectMany(p => p.Ids).OrderBy(x => x));
            Assert.All(parts, p => Assert.InRange(p.Ids.Count, 1, ConfirmedWriteDto.MaxIds));
            // and they are the ids each prompt lists
            var prompts = JkrCopilotPrompt.Build(f, false);
            for (var i = 0; i < 3; i++)
                foreach (var id in parts[i].Ids) Assert.Contains("- " + id + " ", prompts[i]);
        }

        [Fact]
        public void The_wire_json_matches_the_server_contract_and_omits_unset_fields()
        {
            var json = CopilotJson.Serialize(JkrCopilotConfirmedWrite.ForPart(Facts("Db.2", 2), 0));
            using var doc = JsonDocument.Parse(json);
            var names = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "check_id", "ids", "params", "source", "tool" }, names);
            var typeJson = CopilotJson.Serialize(JkrCopilotConfirmedWrite.ForPart(Facts("Da.1", 2), 0));
            using var t = JsonDocument.Parse(typeJson);
            Assert.True(t.RootElement.GetProperty("type_level").GetBoolean());
            Assert.Equal(2, t.RootElement.GetProperty("instances").GetInt32());
        }

        [Fact]
        public void Every_declared_param_is_named_in_the_prompt_how_text_in_both_languages()
        {
            foreach (var id in JkrCopilotFix.Allowed)
            {
                var v = JkrCopilotPrompt.VariantFor(id);
                foreach (var p in v.Params)
                {
                    Assert.Contains(p, v.HowMs);
                    Assert.Contains(p, v.HowEn);
                }
            }
        }

        [Fact]
        public void The_prompt_tells_copilot_to_target_ids_never_a_filter_and_a_type_family_to_read_its_ids_first()
        {
            foreach (var bm in new[] { false, true })
            {
                var inst = JkrCopilotPrompt.Build(Facts("Db.2", 3), bm)[0];
                Assert.Contains(bm ? "sasaran id sahaja" : "ids only as the target", inst);
                Assert.DoesNotContain(bm ? "baca dahulu id ini" : "Read these ids first", inst);
                var type = JkrCopilotPrompt.Build(Facts("Da.1", 3), bm)[0];
                Assert.Contains(bm ? "sasaran id sahaja" : "ids only as the target", type);
                Assert.Contains(bm ? "baca dahulu id ini" : "Read these ids first", type);
                Assert.Contains(bm ? "kumpulan mengikut type" : "grouped by type", type);
                Assert.Contains(bm ? "dengan inspect DAN jalankan find_elements" : "with inspect AND run find_elements", type);
                Assert.DoesNotContain(" or find_elements", type);
                Assert.DoesNotContain(", atau find_elements", type);
            }
        }

        [Fact]
        public void A_type_at_the_reader_cap_shows_2000_plus_in_the_dialog_and_still_sends_the_capped_number()
        {
            var ids = Enumerable.Range(1, 2000).Select(i => 5000L + i).ToList();
            var f = new CopilotPromptFacts { CheckId = "Da.1", Category = "Doors", ElementIds = ids, ElementLabels = ids.ToDictionary(i => i, i => "Doors · Big") };
            Assert.True(JkrCopilotConfirmedWrite.InstanceCountCapped(f));
            var en = JkrCopilotConfirm.Build(f, false);
            Assert.Contains("(2000+ instances)", en.Heading);
            Assert.Contains("at least 2000+ instances", en.Body);
            var bm = JkrCopilotConfirm.Build(f, true);
            Assert.Contains("(2000+ instance)", bm.Heading);
            Assert.Contains("sekurang-kurangnya 2000+ instance", bm.Body);
            Assert.Equal(2000, JkrCopilotConfirmedWrite.ForPart(f, 0).Instances);
            // two types of 1500 each are not capped (per type, not per row)
            var two = Enumerable.Range(1, 3000).Select(i => 9000L + i).ToList();
            var g = new CopilotPromptFacts { CheckId = "Da.1", Category = "Doors", ElementIds = two, ElementLabels = two.ToDictionary(i => i, i => i % 2 == 0 ? "Doors · A" : "Doors · B") };
            Assert.False(JkrCopilotConfirmedWrite.InstanceCountCapped(g));
            Assert.Contains("(3000 instances)", JkrCopilotConfirm.Build(g, false).Heading);
        }

        [Fact]
        public void The_wire_body_has_no_types_field()
        {
            Assert.DoesNotContain("\"types\"", CopilotJson.Serialize(JkrCopilotConfirmedWrite.ForPart(Facts("Da.1", 2), 0)));
        }

        // ---- the launcher: only after Ya, only the launcher's own prompt, cleared at the end ----

        private sealed class SeeingSession : ICopilotSession
        {
            public readonly List<(string Text, string Check)> Seen = new List<(string, string)>();
            private int _sends;
            public Task<string> OpenPaneAsync() => Task.FromResult<string>(null);
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn { get; set; } = true;
            public bool IsPaneVisible => true;
            public bool IsSending => false;
            public CopilotCardState Cards => _sends == 0 ? CopilotCardState.None : CopilotCardState.Approved;
            public void Send(string text)
            {
                _sends++;
                var taken = ConfirmedWriteSlot.TakeFor(text);   // what the router would take for this message
                Seen.Add((text, taken?.CheckId));
            }
        }

        private static readonly CopilotFixRequest Req = new CopilotFixRequest("i1", "Db.2", new long[] { 1, 2 }, "T");

        private static JkrCopilotFixLauncher Launcher(ICopilotSession s, Func<CopilotFixRequest, bool> confirm, Func<CopilotFixRequest, int, ConfirmedWriteDto> cw, params string[] parts) =>
            new JkrCopilotFixLauncher(s, _ => parts, _ => { }, () => false, _ => Task.CompletedTask, indexingTimeout: TimeSpan.FromSeconds(1),
                turnTimeout: TimeSpan.FromSeconds(2), startGrace: TimeSpan.FromSeconds(1), settle: TimeSpan.FromSeconds(1), confirm: confirm, confirmedWrite: cw);

        private static ConfirmedWriteDto Dto(int part) => new ConfirmedWriteDto { CheckId = "part" + part, Ids = new List<long> { part + 1 }, Params = new List<string> { "Material" } };

        [Fact]
        public async Task After_Ya_each_part_goes_out_with_its_own_field_and_nothing_stays_armed()
        {
            ConfirmedWriteSlot.Clear();
            var s = new SeeingSession();
            var outcome = await Launcher(s, r => true, (r, i) => Dto(i), "A", "B").LaunchAsync(Req);
            Assert.Equal(CopilotLaunchOutcome.Sent, outcome);
            Assert.Equal(new[] { ("A", "part0"), ("B", "part1") }, s.Seen.ToArray());
            Assert.False(ConfirmedWriteSlot.IsArmed);
        }

        [Fact]
        public async Task A_No_sends_nothing_and_arms_nothing()
        {
            ConfirmedWriteSlot.Clear();
            var s = new SeeingSession();
            Assert.Equal(CopilotLaunchOutcome.NotConfirmed, await Launcher(s, r => false, (r, i) => Dto(i), "A").LaunchAsync(Req));
            Assert.Empty(s.Seen);
            Assert.False(ConfirmedWriteSlot.IsArmed);
        }

        [Fact]
        public async Task Without_a_confirm_step_no_field_is_attached_even_if_a_builder_is_given()
        {
            ConfirmedWriteSlot.Clear();
            var s = new SeeingSession();
            await Launcher(s, null, (r, i) => Dto(i), "A").LaunchAsync(Req);
            Assert.Equal(new (string, string)[] { ("A", null) }, s.Seen.ToArray());
        }

        [Fact]
        public async Task A_builder_that_throws_or_returns_null_still_sends_the_prompt_without_the_field()
        {
            ConfirmedWriteSlot.Clear();
            var s = new SeeingSession();
            await Launcher(s, r => true, (r, i) => throw new InvalidOperationException("boom"), "A").LaunchAsync(Req);
            var s2 = new SeeingSession();
            await Launcher(s2, r => true, (r, i) => null, "A").LaunchAsync(Req);
            Assert.Equal(new (string, string)[] { ("A", null) }, s.Seen.ToArray());
            Assert.Equal(new (string, string)[] { ("A", null) }, s2.Seen.ToArray());
        }

        [Fact]
        public async Task The_sign_in_park_sends_no_field_and_leaves_nothing_armed_for_the_later_chat()
        {
            ConfirmedWriteSlot.Clear();
            var s = new SeeingSession { IsSignedIn = false };
            Assert.Equal(CopilotLaunchOutcome.SignInNeeded, await Launcher(s, r => true, (r, i) => Dto(i), "A").LaunchAsync(Req));
            Assert.Equal(new (string, string)[] { ("A", null) }, s.Seen.ToArray());
            Assert.Null(ConfirmedWriteSlot.TakeFor("A"));
        }

        [Fact]
        public async Task A_send_that_throws_clears_the_slot()
        {
            ConfirmedWriteSlot.Clear();
            var s = new ThrowingSession();
            Assert.Equal(CopilotLaunchOutcome.SendFailed, await Launcher(s, r => true, (r, i) => Dto(i), "A").LaunchAsync(Req));
            Assert.False(ConfirmedWriteSlot.IsArmed);
        }

        private sealed class ThrowingSession : ICopilotSession
        {
            public Task<string> OpenPaneAsync() => Task.FromResult<string>(null);
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsPaneVisible => true;
            public bool IsSending => false;
            public CopilotCardState Cards => CopilotCardState.None;
            public void Send(string text) => throw new InvalidOperationException("x");
        }

        // ---- the slot ----

        [Fact]
        public void The_slot_is_one_shot_matches_only_its_own_prompt_and_honours_the_compat_switch()
        {
            ConfirmedWriteSlot.Clear();
            try
            {
                ConfirmedWriteSlot.Arm("  prompt  ", Dto(0));
                Assert.Null(ConfirmedWriteSlot.TakeFor("something else"));
                Assert.True(ConfirmedWriteSlot.IsArmed);
                Assert.NotNull(ConfirmedWriteSlot.TakeFor("prompt"));
                Assert.Null(ConfirmedWriteSlot.TakeFor("prompt"));   // consumed

                ConfirmedWriteSlot.Enabled = false;
                ConfirmedWriteSlot.Arm("prompt", Dto(0));
                Assert.False(ConfirmedWriteSlot.IsArmed);
                Assert.Null(ConfirmedWriteSlot.TakeFor("prompt"));
            }
            finally { ConfirmedWriteSlot.ResetEnabled(); ConfirmedWriteSlot.Clear(); }
        }
    }
}
