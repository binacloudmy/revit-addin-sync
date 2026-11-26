// AI-605 demo: "tunjuk 3D" picks one plan of the sheet read_dwg_plan read and asks the #272 builder for it, in place.
using System.Linq;
using Cad2Bim;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class DwgShow3dTests
    {
        private static Wall W(double y) =>
            new Wall(new Segment(new Point(0, y + 50), new Point(5000, y + 50)), new Segment(new Point(0, y - 50), new Point(5000, y - 50)));

        private static DwgPlan Plan(int n, string title, int walls)
        {
            var p = new DwgPlan { N = n, Title = title, Cluster = new PlanCluster() };
            for (int i = 0; i < walls; i++) p.Cluster.Add(W(i * 1000 + n * 100000));
            return p;
        }

        private static DwgPlanSheet Sheet(params DwgPlan[] plans)
        {
            var s = new DwgPlanSheet();
            s.Plans.AddRange(plans);
            return s;
        }

        private static DwgPlan Pick(DwgPlanSheet sheet, string text)
        {
            Assert.True(DwgShow3d.Intercepts(sheet, text, out DwgPlan plan, out _), text);
            return plan;
        }

        [Fact]
        public void Only_a_bare_ask_a_number_or_a_plan_title_is_answered_locally()
        {
            var sheet = Sheet(Plan(1, "PELAN ARAS 1", 5), Plan(2, "PELAN BUMBUNG", 6), Plan(3, null, 2));
            Assert.False(DwgShow3d.Intercepts(sheet, "show 3D view of the roof", out _, out _));
            Assert.False(DwgShow3d.Intercepts(sheet, "apa itu 3D?", out _, out _));
            Assert.False(DwgShow3d.Intercepts(sheet, "", out _, out _));
            // No sheet read yet: every "tunjuk 3D" goes to the engine.
            Assert.False(DwgShow3d.Intercepts(null, "tunjuk 3D", out _, out _));
            Assert.False(DwgShow3d.Intercepts(null, "show me 3d", out _, out _));
            Assert.False(DwgShow3d.Intercepts(Sheet(Plan(3, null, 2)), "tunjuk 3D", out _, out _));
        }

        [Fact]
        public void Picks_by_title_first_then_a_whole_number_then_the_only_plan()
        {
            var sheet = Sheet(Plan(1, "ARAS 2", 5), Plan(2, "ARAS 1", 6), Plan(3, "PELAN BUMBUNG", 7), Plan(4, null, 2));
            Assert.Equal(2, Pick(sheet, "tunjuk 3D aras 1").N);   // the title, not plan n=1
            Assert.Equal(3, Pick(sheet, "Tunjuk 3d bumbung").N);
            Assert.Equal(1, Pick(sheet, "tunjuk 3D 1").N);
            Assert.Equal(3, Pick(sheet, "show 3D pelan 3").N);
            Assert.Null(Pick(sheet, "tunjuk 3D 4"));                // a fragment is not listed: asks which
            Assert.True(DwgShow3d.Intercepts(sheet, "tunjuk 3D", out DwgPlan none, out string which));
            Assert.Null(none);
            Assert.Contains("tunjuk 3D 1", which);
            Assert.Equal(1, Pick(Sheet(Plan(1, "PELAN ARAS 1", 5)), "tunjuk 3D").N);
        }

        [Fact]
        public void Request_is_one_plan_in_place_on_the_lowest_level()
        {
            DwgPlan p = Plan(2, "PELAN ARAS 1", 5);
            BuildRequest r = DwgShow3d.Request("C:/a.dwg", p, new CadToBimSettings());
            Assert.Equal(BuildTarget.AddToProject, r.Target);
            Assert.Equal(StoreyMode.KeepPosition, r.Storeys);
            Assert.Null(r.LevelId);
            Assert.Equal(p.Cluster.Walls, r.Walls);
            Assert.Contains("5 dinding", DwgShow3d.Summary(p));
        }

        [Fact]
        public void Ya_Tidak_on_the_gate_ids_survive_the_approval_filter()
        {
            var ask = new BinaVibe.Copilot.Contracts.AskDto
            {
                RequestId = "local-dwg3d", Question = "Bina 3D: X",
                Options = new System.Collections.Generic.List<BinaVibe.Copilot.Contracts.AskOptionDto>
                {
                    new() { Id = RevitWebAppSync.UI.Copilot.Model.WideWriteModel.WriteAllId, Label = "Ya" },
                    new() { Id = RevitWebAppSync.UI.Copilot.Model.WideWriteModel.WriteCancelId, Label = "Tidak" },
                },
            };
            var card = RevitWebAppSync.UI.Copilot.Model.AskCardModel.From(ask, RevitWebAppSync.UI.Copilot.Model.ReceiptLanguage.Ms);
            Assert.NotNull(card.Wide);
            Assert.Equal(new[] { "Ya", "Tidak" }, card.Wide.Buttons.Select(b => b.Label));
            Assert.True(card.AnswerForOptions(new[] { RevitWebAppSync.UI.Copilot.Model.WideWriteModel.WriteAllId }).Values.ContainsKey("write_all"));
        }
    }
}
