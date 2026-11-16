#nullable enable
// Live 13 Nov (F-t04): one type parameter written on two types that share a type name ("Standard" here) in two
// families, 38 and 14 instances. The card keyed types by their name, so the two collapsed into one row of 38, the
// read-back chip said "38 of 38", and the two "Written as asked" rows did not say which type each was for.
// A type is its id (family + name), never its name alone. Invented names only.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TypeIdentityCardTests
    {
        private const string Param = "Rated_Current";

        private static ToolCallDto Call() =>
            new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = ToolNames.SetParameters, Args = JsonDocument.Parse("{}").RootElement.Clone() };

        private static ToolRequestDto Request() =>
            new ToolRequestDto { RequestId = "r", Kind = ToolRequestKinds.Write, Calls = new List<ToolCallDto> { Call() } };

        /// <summary>A type-parameter commit as the add-in sends it: one entry in data.types per type, one modified row each.</summary>
        private static Envelope TypeCommit(params (long Id, string Name, string? Family, int Instances)[] types)
        {
            var data = new
            {
                param = Param, type_param = true, matched = types.Sum(t => t.Instances), changed = types.Length, unchanged = 0,
                types = types.Select(t => new { id = t.Id, name = t.Name, family = t.Family, category = "Lighting Fixtures", instances_in_model = t.Instances }).ToArray(),
                instances_affected = types.Sum(t => t.Instances), instances_category = "Lighting Fixtures",
            };
            return new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Count = types.Length,
                Summary = "Set " + Param + " = 0.04 A on " + types.Length + " type(s)",
                Data = JsonDocument.Parse(JsonSerializer.Serialize(data)).RootElement.Clone(),
                Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = types.Select(t => new FieldChangeDto
                {
                    Id = t.Id, Field = Param, Before = "0 A", After = "0.04 A", Scope = ResultTargetScopes.Type, Element = t.Name,
                    Source = new ResultSourceDto { Kind = ResultSourceKinds.Document, Attachment = "catalogue.pdf", Page = 2 },
                }).ToList() } },
                Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = types.Length, Mismatches = new List<MismatchDto>() },
            };
        }

        private static TurnEvidence TwoFamiliesOneTypeName()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(), new[] { TypeCommit((5001, "Standard", "Alpha Downlight", 38)) });
            ev.Add(Request(), new[] { TypeCommit((5002, "Standard", "Beta Downlight", 14)) });
            return ev;
        }

        [Fact]
        public void TwoFamiliesSharingATypeName_AreTwoTypes_EachWithItsFamilyAndCount()
        {
            var m = ResultCardModel.From(TwoFamiliesOneTypeName(), ReceiptLanguage.En, "catalogue.pdf");

            Assert.Equal(ResultKind.Changes, m.Kind);
            Assert.Equal(2, m.Groups.Count);
            Assert.All(m.Groups, g => Assert.True(g.IsType));
            Assert.Equal(new[] { "Alpha Downlight: Standard", "Beta Downlight: Standard" }, m.Groups.Select(g => g.Title));
            Assert.Equal(new[] { "Standard", "Standard" }, m.Groups.Select(g => g.TypeName));
            Assert.Equal(new[] { 38, 14 }, m.Groups.Select(g => g.Instances));
            Assert.Equal(new[] { 5001L }, m.Groups[0].Ids);
            Assert.Equal(new[] { 5002L }, m.Groups[1].Ids);
            Assert.Equal(52, m.InstanceCount);
            Assert.Equal("2 types · 52 elements · read back from Revit", m.Detail);
            // The tile's own title is the whole label, not cut back to the shared type name.
            Assert.Equal("Alpha Downlight: Standard", ChangesBodyText.TileLabels(m.Groups[0].Title).Title);
        }

        [Fact]
        public void TheReadBackChip_CountsTypes_AndNamesWhatTheyAffectOnce()
        {
            var m = ResultCardModel.From(TwoFamiliesOneTypeName(), ReceiptLanguage.En);
            Assert.Equal((2, 2), m.ReadBack());
            Assert.Equal("Read back from Revit: 2 of 2 types match · affects 52 Lighting Fixtures", m.ReadBackText(ReceiptLanguage.En));
            Assert.Equal("Dibaca balik dari Revit: 2 daripada 2 jenis sepadan · melibatkan 52 Lighting Fixtures", m.ReadBackText(ReceiptLanguage.Ms));
        }

        [Fact]
        public void EachWrittenRow_SaysWhichTypeItIsFor()
        {
            var m = ResultCardModel.From(TwoFamiliesOneTypeName(), ReceiptLanguage.En, "catalogue.pdf");
            Assert.All(m.Groups, g => Assert.Single(g.Rows));
            Assert.Equal("For Alpha Downlight: Standard", ChangesBodyText.WrittenFor(m.Groups[0].Title, ReceiptLanguage.En));
            var table = ChangeTable.Rows(m, ReceiptLanguage.En);
            Assert.Equal(2, table.Count);
            Assert.Equal(new[] { "Alpha Downlight: Standard", "Beta Downlight: Standard" }, table.Select(r => r[0]));
            Assert.All(table, r => Assert.Equal("0.04 A", r[3]));
        }

        [Fact]
        public void TheModelMarks_AreOneGroupPerType_WithTheirOwnCounts()
        {
            var ev = TwoFamiliesOneTypeName();
            var m = ResultCardModel.From(ev, ReceiptLanguage.En);
            var set = HighlightSet.From(m, ev.Calls.Select(c => c.Env).ToList());

            var types = set.Kinds.Where(k => k.Kind == MarkKind.TypeInstances).ToList();
            Assert.Equal(new[] { 38, 14 }, types.Select(t => t.Count));
            Assert.Equal(new[] { "Changed type Alpha Downlight: Standard", "Changed type Beta Downlight: Standard" }, types.Select(MarkKey.LineLabel));
            Assert.Equal("52 elements changed", MarkKey.Title(set.Kinds));
            Assert.Equal(new[] { "Alpha Downlight: Standard", "Beta Downlight: Standard" },
                set.Rows.Where(r => r.Kind == MarkKind.TypeInstances).Select(r => r.Title));
        }

        [Fact]
        public void TheProgressStrip_CountsTheTwoTypeWritesOnce()
        {
            var f = TwoFamiliesOneTypeName().Receipt;
            Assert.Equal("2/2 done", ReceiptTemplates.Headline(f, ReceiptLanguage.En));
        }

        [Fact]
        public void OneFamilyWithTwoTypes_IsTwoRows_LabelledWithTheFamily()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(), new[] { TypeCommit((6001, "Standard", "Alpha Downlight", 20), (6002, "Emergency", "Alpha Downlight", 6)) });
            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(new[] { "Alpha Downlight: Standard", "Alpha Downlight: Emergency" }, m.Groups.Select(g => g.Title));
            Assert.Equal(new[] { 20, 6 }, m.Groups.Select(g => g.Instances));
            Assert.Equal(26, m.InstanceCount);
            Assert.Equal("Read back from Revit: 2 of 2 types match · affects 26 Lighting Fixtures", m.ReadBackText(ReceiptLanguage.En));
        }

        [Fact]
        public void TwoTypesWithOneName_AndNoFamily_AreStillTwoRows_ToldApartByTheirIds()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(), new[] { TypeCommit((7001, "Standard", null, 3)) });
            ev.Add(Request(), new[] { TypeCommit((7002, "Standard", null, 4)) });
            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(2, m.Groups.Count);
            Assert.Equal(2, m.Groups.Select(g => g.Title).Distinct().Count());
            Assert.All(m.Groups, g => Assert.StartsWith("Standard", g.Title));
            Assert.Equal(7, m.InstanceCount);
        }

        [Fact]
        public void OneTypeWrittenTwice_StaysOneRowPerParameterOfThatType()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(), new[] { TypeCommit((8001, "Standard", "Alpha Downlight", 9)) });
            var second = TypeCommit((8001, "Standard", "Alpha Downlight", 9));
            second.Changes!.Touched!.Modified![0].Field = "Rated_Power";
            ev.Add(Request(), new[] { second });
            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            var g = Assert.Single(m.Groups);
            Assert.Equal(2, g.Rows.Count);
            Assert.Equal(9, m.InstanceCount);
            Assert.Equal("Read back from Revit: 1 of 1 type matches · affects 9 Lighting Fixtures", m.ReadBackText(ReceiptLanguage.En));
        }

        [Fact]
        public void AnInstanceWrite_ReadsAsBefore()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set Mark", Count = 3,
                Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new List<FieldChangeDto>
                {
                    new FieldChangeDto { Id = 1, Field = "Mark", Before = "", After = "D1" },
                    new FieldChangeDto { Id = 2, Field = "Mark", Before = "", After = "D2" },
                    new FieldChangeDto { Id = 3, Field = "Mark", Before = "", After = "D3" },
                } } },
                Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 3, Mismatches = new List<MismatchDto>() },
                Data = JsonDocument.Parse("{\"param\":\"Mark\",\"type_param\":false,\"instances_category\":\"Doors\"}").RootElement.Clone(),
            };
            var ev = new TurnEvidence();
            ev.Add(Request(), new[] { env });
            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            var g = Assert.Single(m.Groups);
            Assert.False(g.IsType);
            Assert.Equal("Doors", g.Title);
            Assert.Equal(3, g.Instances);
            Assert.Equal((3, 3), m.ReadBack());
            Assert.Equal("Read back from Revit: 3 of 3 match", m.ReadBackText(ReceiptLanguage.En));
            Assert.Equal("Dibaca balik dari Revit: 3 daripada 3 sepadan", m.ReadBackText(ReceiptLanguage.Ms));
            Assert.Equal("Doors", ChangeTable.Rows(m, ReceiptLanguage.En)[0][0]);
        }
    }
}
