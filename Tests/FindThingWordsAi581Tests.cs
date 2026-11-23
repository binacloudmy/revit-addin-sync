#nullable enable
// AI-581 (live 23 Nov, MRSM 3 and 4): "Cari semua injap" searched Pipe Accessories and
// Plumbing Fixtures, counted 14 hand spray bidets as valves and never searched Pipe Fittings,
// where the 18 jkrME_pip-ft_stopvalve fittings are. "Set the mounting height of all light
// switches" searched Electrical Fixtures for "switch", matched all 270 SSO sockets and never
// searched Lighting Devices. A thing word now decides the categories whatever category the
// model gives: injap/valve = Pipe Accessories and Pipe Fittings whose own name says valve;
// suis/switch = every Lighting Device, and Electrical Fixtures whose own name says switch but
// not socket or outlet. Family names below come from the ticket where it quotes them
// (jkrME_pip-ft_stopvalve, jkrME_pip-ac_gate valve_50-300 mm, SSO_); the rest are constructed.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FindThingWordsAi581Tests
    {
        private const string PA = "OST_PipeAccessory", PF = "OST_PipeFitting", LD = "OST_LightingDevices", EF = "OST_ElectricalFixtures";

        private static FindQuery Q(string query)
        {
            var args = JsonDocument.Parse("{\"queries\":[" + query + "]}").RootElement.Clone();
            Assert.True(QueryParser.TryParse(args, out var plan, out var error), error);
            return plan.Queries[0];
        }

        private static bool Hit(FindQuery q, string category, params string?[] fields) =>
            q.CategoryOsts.Contains(category) && TextMatch.Matches(q.Terms, fields, null, category);

        // ── injap / valve ─────────────────────────────────────────────────

        [Theory]
        [InlineData("{\"text\":\"injap\"}")]
        [InlineData("{\"text\":\"semua injap\"}")]
        [InlineData("{\"category\":\"Pipe Accessories\",\"text\":\"injap\"}")]   // live query 1
        [InlineData("{\"category\":\"Pipe Accessories\",\"text\":\"valve\"}")]   // live query 2
        [InlineData("{\"category\":\"Plumbing Fixtures\",\"text\":\"valve\"}")]  // live query 3
        [InlineData("{\"category\":\"valves\"}")]
        public void Valve_SearchesPipeAccessoriesAndPipeFittingsOnly(string query)
        {
            var q = Q(query);
            Assert.Equal(new[] { PA, PF }, q.CategoryOsts);
            Assert.True(Hit(q, PF, "jkrME_pip-ft_stopvalve", "Standard"));
            Assert.True(Hit(q, PA, "jkrME_pip-ac_gate valve_50-300 mm", "65 mm"));
            Assert.False(Hit(q, PF, "jkrME_pip-ft_elbow", "Standard"));               // a fitting not named valve
            Assert.False(Hit(q, "OST_PlumbingFixtures", "Hand Spray Bidet", "Hand Spray Bidet c/w valve"));   // never searched
        }

        // ── suis / switch ─────────────────────────────────────────────────

        [Theory]
        [InlineData("{\"text\":\"light switch\"}")]
        [InlineData("{\"text\":\"suis\"}")]
        [InlineData("{\"category\":\"Electrical Fixtures\",\"text\":\"switch\"}")]   // live query
        [InlineData("{\"category\":\"Electrical Fixtures\",\"text\":\"light switches\"}")]
        public void Switch_SearchesEveryLightingDeviceAndElectricalFixturesNamedSwitch(string query)
        {
            var q = Q(query);
            Assert.Equal(new[] { LD, EF }, q.CategoryOsts);
            Assert.True(Hit(q, LD, "jkrEL_lit-dv_1G1W_wall_6t_N", "Standard"));       // a lighting device need not say switch
            Assert.True(Hit(q, EF, "jkrEL_ele-fx_isolator switch_20A_wall_N", "20A")); // an electrical fixture named switch
            Assert.False(Hit(q, EF, "SSO_13A Switched Socket Outlet", "13A Twin"));   // a switched socket outlet is a socket
            Assert.False(Hit(q, EF, "jkrEL_ele-fx_SSO_13A_twin_wall_N", "Twin switched")); // SSO in the name
            Assert.False(Hit(q, EF, "jkrEL_ele-fx_ceiling rose_5A_N", "Standard"));    // not named switch
        }

        [Fact]
        public void Switch_WordsInsideOtherPhrasesKeepTheirOwnEntry()
        {
            Assert.Equal("OST_ElectricalEquipment", Synonyms.CategoryFor("papan suis"));
            Assert.Equal("electrical room", Synonyms.EntryFor("bilik suis")!.En);
            Assert.Equal("electrical room", Synonyms.EntryFor("switch room")!.En);
            Assert.Equal("OST_ElectricalEquipment", Synonyms.CategoryFor("switchboard"));
            Assert.Equal(new[] { "OST_ElectricalEquipment" }, Q("{\"text\":\"papan suis\"}").CategoryOsts);
            Assert.Equal(new[] { "OST_LightingFixtures" }, Q("{\"text\":\"lampu\"}").CategoryOsts);
        }

        // Owner decision: a thing word replaces only the model's category; categories the text itself
        // names are searched too, and their elements need only the thing word ("fire alarm switches").
        [Theory]
        [InlineData("{\"text\":\"fire alarm switches\"}")]
        [InlineData("{\"category\":\"Electrical Fixtures\",\"text\":\"fire alarm switch\"}")]
        [InlineData("{\"text\":\"suis penggera kebakaran\"}")]
        public void CategoriesTheTextNamesAreKeptBesideAThingWord(string query)
        {
            var q = Q(query);
            Assert.Equal(new[] { LD, EF, "OST_FireAlarmDevices" }, q.CategoryOsts);
            Assert.True(Hit(q, "OST_FireAlarmDevices", "jkrEL_fir-al_break glass switch_surface_24V"));   // constructed
            Assert.False(Hit(q, "OST_FireAlarmDevices", "jkrEL_fir-al_smoke detector_2 wire_24V"));
            Assert.True(Hit(q, LD, "jkrEL_lit-dv_1G1W_wall_6t_N"));
            Assert.False(Hit(q, EF, "SSO_13A Switched Socket Outlet"));
        }

        [Fact]
        public void ValveBesideAPipeFittingWordStaysTheValveSearch()
        {
            var q = Q("{\"text\":\"kelengkapan paip injap\"}");
            Assert.Equal(new[] { PA, PF }, q.CategoryOsts);
            Assert.True(Hit(q, PF, "jkrME_pip-ft_stopvalve"));
            Assert.False(Hit(q, PF, "jkrME_pip-ft_elbow"));
        }

        // Verifier: a category the model gives outside the thing's own set is kept, with the word as its
        // condition; only the thing's known wrong pick (Plumbing Fixtures for a valve) is replaced.
        [Fact]
        public void TheModelsCategoryIsKeptUnlessItIsTheKnownWrongPick()
        {
            var me = Q("{\"category\":\"Mechanical Equipment\",\"text\":\"valve\"}");   // no synonym: resolved in Revit
            Assert.Equal(new[] { PA, PF }, me.CategoryOsts);
            Assert.True(me.KeepCategoryText);
            var d = FindScope.Decide(me, false);
            Assert.True(d.ResolveCategoryText);
            Assert.Equal(new[] { PA, PF }, d.Categories);
            Assert.True(TextMatch.Matches(me.Terms, new[] { "jkrME_mec-eq_pressure reducing valve station" }, null, "OST_MechanicalEquipment"));
            Assert.False(TextMatch.Matches(me.Terms, new[] { "jkrME_mec-eq_booster pump set" }, null, "OST_MechanicalEquipment"));

            var fa = Q("{\"category\":\"Fire Alarm Devices\",\"text\":\"switch\"}");   // synonym "fire alarm devices"? kept either way
            Assert.True(fa.CategoryOsts.Contains("OST_FireAlarmDevices") || fa.KeepCategoryText);
            Assert.Contains(LD, fa.CategoryOsts);
            Assert.True(TextMatch.Matches(fa.Terms, new[] { "jkrEL_fir-al_break glass switch_surface_24V" }, null, "OST_FireAlarmDevices"));
            Assert.False(TextMatch.Matches(fa.Terms, new[] { "jkrEL_fir-al_smoke detector_2 wire_24V" }, null, "OST_FireAlarmDevices"));

            var pf = Q("{\"category\":\"Plumbing Fixtures\",\"text\":\"valve\"}");
            Assert.Equal(new[] { PA, PF }, pf.CategoryOsts);
            Assert.False(pf.KeepCategoryText);
        }

        // Verifier: JKR's SSO is a "13A switch socket outlet": naming the socket takes the switch word's
        // socket exclusion away, so the sockets are found.
        [Theory]
        [InlineData("{\"text\":\"13A switch socket outlet\"}")]
        [InlineData("{\"text\":\"switch socket outlet\"}")]
        [InlineData("{\"text\":\"suis soket\"}")]
        [InlineData("{\"category\":\"Electrical Fixtures\",\"text\":\"13A switch socket outlet\"}")]
        public void ASwitchSocketOutletIsFound(string query)
        {
            var q = Q(query);
            var sso = new[] { "jkrEL_ele-fx_SSO_2G_flush_13A_N", "13A Switch Socket Outlet" };
            Assert.True((q.CategoryOsts.Count == 0 || q.CategoryOsts.Contains(EF)) && TextMatch.Matches(q.Terms, sso, null, EF));
        }

        [Fact]
        public void OtherQueriesKeepTheirCategory()
        {
            Assert.Equal(new[] { "OST_PlumbingFixtures" }, Q("{\"text\":\"sinki\"}").CategoryOsts);
            Assert.Empty(Q("{\"category\":\"Pipe Accessories\",\"text\":\"strainer\"}").CategoryOsts);   // resolved from the model's category
            Assert.True(TextMatch.Matches(Q("{\"text\":\"pintu kalis api\"}").Terms, new[] { "Door - Fire Rated 60 min" }));
        }
    }
}
