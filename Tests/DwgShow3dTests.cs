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

        [Fact]
        public void Asks_are_recognised_and_other_text_is_not()
        {
            Assert.True(DwgShow3d.IsAsk("tunjuk 3D"));
            Assert.True(DwgShow3d.IsAsk("Tunjuk 3d pelan 2"));
            Assert.True(DwgShow3d.IsAsk("show 3D 1"));
            Assert.False(DwgShow3d.IsAsk("apa itu 3D?"));
            Assert.False(DwgShow3d.IsAsk(""));
        }

        [Fact]
        public void Picks_by_number_by_title_or_the_only_plan()
        {
            var sheet = Sheet(Plan(1, "PELAN ARAS 1", 5), Plan(2, "PELAN BUMBUNG", 6), Plan(3, null, 2));
            Assert.Equal(2, DwgShow3d.Pick(sheet, "tunjuk 3D 2", out _).N);
            Assert.Equal(2, DwgShow3d.Pick(sheet, "tunjuk 3D bumbung", out _).N);
            Assert.Null(DwgShow3d.Pick(sheet, "tunjuk 3D", out string which));
            Assert.Contains("tunjuk 3D 1", which);
            Assert.Null(DwgShow3d.Pick(sheet, "tunjuk 3D 3", out _));   // a fragment is not listed
            Assert.Equal(1, DwgShow3d.Pick(Sheet(Plan(1, "PELAN ARAS 1", 5)), "tunjuk 3D", out _).N);
            Assert.Null(DwgShow3d.Pick(null, "tunjuk 3D", out string none));
            Assert.Contains("DWG", none);
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
