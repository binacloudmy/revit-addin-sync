#nullable enable
// AI-296: each written value carries its own source end to end. set_parameters takes an optional
// source, the change row is typed (scope, names, raw value and unit, source), and the result card
// shows each row's own source instead of one attachment name for the whole card.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CopilotPerValueSourceTests
    {
        private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

        // ---- args ----------------------------------------------------------------------------

        private static SetParametersArgs Parse(string source) =>
            SetParametersArgs.Parse(J("{\"target\":{\"ids\":[7]},\"param\":\"URL\",\"value\":\"www.pcolite.com\"" + (source == "" ? "" : ",\"source\":" + source) + "}"));

        [Fact]
        public void Args_WithoutASourceHaveNone() => Assert.Null(Parse("").Source);

        [Theory]
        [InlineData("{\"kind\":\"prompt\"}", "prompt", null, null)]
        [InlineData("{\"kind\":\"calculated\"}", "calculated", null, null)]
        [InlineData("{\"kind\":\"rule\"}", "rule", null, null)]
        [InlineData("{\"kind\":\"document\",\"attachment\":\"b.pdf\",\"page\":8,\"quote\":\"www.pcolite.com\"}", "document", 8, null)]
        [InlineData("{\"kind\":\"excel\",\"attachment\":\"d.xlsx\",\"row\":3}", "excel", null, 3)]
        public void Args_ReadAValidSource(string source, string kind, int? page, int? row)
        {
            var s = Parse(source).Source!;
            Assert.Equal(kind, s.Kind);
            Assert.Equal(page, s.Page);
            Assert.Equal(row, s.Row);
        }

        [Theory]
        [InlineData("{\"kind\":\"guess\"}")]
        [InlineData("{\"kind\":\"document\",\"page\":8}")]
        [InlineData("{\"kind\":\"document\",\"attachment\":\"b.pdf\",\"page\":0}")]
        [InlineData("{\"kind\":\"excel\",\"attachment\":\"d.xlsx\"}")]
        [InlineData("{\"kind\":\"excel\",\"row\":2}")]
        [InlineData("{}")]
        public void Args_RefuseABrokenSourceAsInvalidArgs(string source)
        {
            var ex = Assert.Throws<WriteRefusal>(() => Parse(source));
            Assert.Equal(ErrorCodes.InvalidArgs, ex.Code);
        }

        [Fact]
        public void Args_SourceNeverChangesTheRestOfTheParse()
        {
            var plain = Parse("");
            var with = Parse("{\"kind\":\"prompt\"}");
            Assert.Equal(plain.Param, with.Param);
            Assert.Equal(plain.Value!.Value.GetRawText(), with.Value!.Value.GetRawText());
            Assert.Equal(plain.Target!.Ids, with.Target!.Ids);
        }

        // ---- typed rows ----------------------------------------------------------------------

        [Theory]
        [InlineData("2700 mm", true, "2700", "mm")]
        [InlineData("-1,5 m", true, "-1.5", "m")]
        [InlineData("1,500 mm", true, "1500", "mm")]
        [InlineData("1,234,567 mm", true, "1234567", "mm")]
        [InlineData("1,500.25 mm", true, "1500.25", "mm")]
        [InlineData("-2,000 mm", true, "-2000", "mm")]
        [InlineData("12,5", true, "12.5", null)]
        [InlineData("0,25 m", true, "0.25", "m")]
        [InlineData("10 W", true, "10", "W")]
        [InlineData("60", true, "60", null)]
        [InlineData("0.5 m²", true, "0.5", "m²")]
        public void Split_NumbersAndTheirUnit(string display, bool numeric, string raw, string? unit)
        {
            var (r, u) = FieldChangeTyping.Split(display, numeric);
            Assert.Equal(JsonValueKind.Number, r!.Value.ValueKind);
            Assert.Equal(double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture), r.Value.GetDouble());
            Assert.Equal(unit, u);
        }

        [Theory]
        [InlineData("007", false)]
        [InlineData("www.pcolite.com", true)]
        [InlineData("4000K (840), Ra 80", true)]
        [InlineData("1X13W", true)]
        [InlineData("2026-11-10", true)]
        [InlineData("LED Recessed Downlight", true)]
        public void Split_TextStaysTextAndAMarkKeepsItsZeros(string display, bool numeric)
        {
            var (r, u) = FieldChangeTyping.Split(display, numeric);
            Assert.Equal(JsonValueKind.String, r!.Value.ValueKind);
            Assert.Equal(display, r.Value.GetString());
            Assert.Null(u);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void Split_EmptyIsNothing(string? display)
        {
            var (r, u) = FieldChangeTyping.Split(display);
            Assert.Null(r);
            Assert.Null(u);
        }

        [Fact]
        public void Type_FillsAnInstanceRow()
        {
            var src = new ResultSourceDto { Kind = ResultSourceKinds.Prompt };
            var row = FieldChangeTyping.Type(new FieldChangeDto { Id = 5, Field = "Height", Before = "2400 mm", After = "2700 mm" }, false, " Ceiling 5 ", "CL-01", src);
            Assert.Equal(ResultTargetScopes.Instance, row.Scope);
            Assert.Equal("Ceiling 5", row.Element);
            Assert.Equal("CL-01", row.TypeName);
            Assert.Equal(2400, row.BeforeRaw!.Value.GetDouble());
            Assert.Equal(2700, row.AfterRaw!.Value.GetDouble());
            Assert.Equal("mm", row.Unit);
            Assert.Same(src, row.Source);
        }

        [Fact]
        public void Type_ATypeRowNamesTheTypeAndNotItsOwnType()
        {
            var row = FieldChangeTyping.Type(new FieldChangeDto { Id = 9, Field = "URL", After = "www.pcolite.com" }, true, "Downlight 1x13W", "ignored", null, numeric: false);
            Assert.Equal(ResultTargetScopes.Type, row.Scope);
            Assert.Equal("Downlight 1x13W", row.Element);
            Assert.Null(row.TypeName);
            Assert.Null(row.BeforeRaw);
            Assert.Equal("www.pcolite.com", row.AfterRaw!.Value.GetString());
            Assert.Null(row.Unit);
            Assert.Null(row.Source);
        }

        [Fact]
        public void TypedRow_RoundTripsThroughTheWire()
        {
            var row = FieldChangeTyping.Type(new FieldChangeDto { Id = 5, Field = "Height", Before = "2400 mm", After = "2700 mm" }, false, "Ceiling 5", "CL-01",
                new ResultSourceDto { Kind = ResultSourceKinds.Document, Attachment = "b.pdf", Page = 8, Quote = "2700" });
            var again = CopilotJson.Deserialize<FieldChangeDto>(CopilotJson.Serialize(row))!;
            Assert.Equal("instance", again.Scope);
            Assert.Equal(8, again.Source!.Page);
            Assert.Equal("mm", again.Unit);
            // An old row (no typed fields) still reads, and writes no typed keys.
            var old = CopilotJson.Deserialize<FieldChangeDto>("{\"id\":1,\"field\":\"Mark\",\"after\":\"D1\"}")!;
            Assert.Null(old.Scope);
            Assert.Equal("{\"id\":1,\"field\":\"Mark\",\"after\":\"D1\"}", CopilotJson.Serialize(old));
        }

        [Fact]
        public void SchemaFieldChange_EveryKeyHasAProperty()
        {
            // A schema key without a property would be dropped on a round trip.
            var dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "contracts", "copilot", "envelope.schema.json");
            var props = JsonDocument.Parse(System.IO.File.ReadAllText(dir)).RootElement.GetProperty("$defs").GetProperty("field_change").GetProperty("properties")
                .EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray();
            var names = typeof(FieldChangeDto).GetProperties()
                .Select(p => ((System.Text.Json.Serialization.JsonPropertyNameAttribute?)System.Attribute.GetCustomAttribute(p, typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute)))?.Name)
                .Where(n => n != null).Select(n => n!).OrderBy(x => x).ToArray();
            Assert.Equal(props, names);
        }

        // ---- the result card -------------------------------------------------------------------

        private static ToolCallDto Call() =>
            new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = ToolNames.SetParameters, Args = J("{}") };

        private static ToolRequestDto Request() =>
            new ToolRequestDto { RequestId = "r", Kind = ToolRequestKinds.Write, Calls = new List<ToolCallDto> { Call() } };

        private static Envelope TypeCommit(string field, string after, ResultSourceDto? source) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set " + field, Count = 1,
            Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new List<FieldChangeDto>
            {
                new FieldChangeDto { Id = 101, Field = field, Before = "", After = after, Scope = "type", Element = "Downlight", Source = source },
            } } },
            Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 1, Mismatches = new List<MismatchDto>() },
            Data = J("{\"param\":\"" + field + "\",\"type_param\":true,\"types\":[{\"id\":101,\"name\":\"Downlight\",\"instances_in_model\":38}]}"),
        };

        [Fact]
        public void Card_EachRowShowsItsOwnSource_NotOneAttachmentForAll()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(), new[] { TypeCommit("URL", "www.pcolite.com", new ResultSourceDto { Kind = ResultSourceKinds.Document, Attachment = "brochure.pdf", Page = 8 }) });
            ev.Add(Request(), new[] { TypeCommit("Description", "LED Recessed Downlight", new ResultSourceDto { Kind = ResultSourceKinds.Prompt }) });
            ev.Add(Request(), new[] { TypeCommit("Type Comments", "4000K", new ResultSourceDto { Kind = ResultSourceKinds.Calculated }) });
            ev.Add(Request(), new[] { TypeCommit("Mark", "D-02", new ResultSourceDto { Kind = ResultSourceKinds.Excel, Attachment = "doors.xlsx", Row = 2 }) });
            ev.Add(Request(), new[] { TypeCommit("Model", "Adrino", null) });

            var rows = ResultCardModel.From(ev, ReceiptLanguage.En, "brochure.pdf").Groups.Single().Rows.ToDictionary(r => r.Param);

            Assert.Equal("brochure.pdf, page 8", rows["URL"].Source);
            Assert.Equal("Your prompt", rows["Description"].Source);
            Assert.Equal("Copilot's choice", rows["Type Comments"].Source);
            Assert.Equal("doors.xlsx, row 2", rows["Mark"].Source);
            Assert.Equal("brochure.pdf", rows["Model"].Source);   // no source of its own: the old attachment name stands
            Assert.Equal(ResultSourceKinds.Document, rows["URL"].SourceInfo!.Kind);
            Assert.Null(rows["Model"].SourceInfo);
        }

        [Fact]
        public void Card_MalayLabels()
        {
            Assert.Equal("Prompt anda", SourceLabels.Of(new ResultSourceDto { Kind = ResultSourceKinds.Prompt }, "", ReceiptLanguage.Ms));
            Assert.Equal("Pilihan Copilot", SourceLabels.Of(new ResultSourceDto { Kind = ResultSourceKinds.Calculated }, "", ReceiptLanguage.Ms));
            Assert.Equal("b.pdf, halaman 3", SourceLabels.Of(new ResultSourceDto { Kind = ResultSourceKinds.Document, Attachment = "b.pdf", Page = 3 }, "", ReceiptLanguage.Ms));
            Assert.Equal("d.xlsx, baris 4", SourceLabels.Of(new ResultSourceDto { Kind = ResultSourceKinds.Excel, Attachment = "d.xlsx", Row = 4 }, "", ReceiptLanguage.Ms));
            Assert.Equal("b.pdf", SourceLabels.Of(new ResultSourceDto { Kind = ResultSourceKinds.Document, Attachment = "b.pdf" }, "x", ReceiptLanguage.En));
            Assert.Equal("fallback", SourceLabels.Of(null, "fallback", ReceiptLanguage.En));
            Assert.Equal("fallback", SourceLabels.Of(new ResultSourceDto { Kind = "web" }, "fallback", ReceiptLanguage.En));
        }

        [Fact]
        public void Card_InstanceRowKeepsOneSourceOnlyWhenEveryElementHasTheSame()
        {
            Envelope Inst(ResultSourceDto? a, ResultSourceDto? b) => new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set Mark", Count = 2,
                Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new List<FieldChangeDto>
                {
                    new FieldChangeDto { Id = 1, Field = "Mark", After = "D1", Source = a },
                    new FieldChangeDto { Id = 2, Field = "Mark", After = "D2", Source = b },
                } } },
                Data = J("{\"param\":\"Mark\",\"type_param\":false,\"instances_category\":\"Doors\"}"),
            };
            var prompt = new ResultSourceDto { Kind = ResultSourceKinds.Prompt };
            var same = new TurnEvidence();
            same.Add(Request(), new[] { Inst(prompt, new ResultSourceDto { Kind = ResultSourceKinds.Prompt }) });
            Assert.Equal("Your prompt", ResultCardModel.From(same, ReceiptLanguage.En, "list.xlsx").Groups.Single().Rows.Single().Source);

            var mixed = new TurnEvidence();
            mixed.Add(Request(), new[] { Inst(prompt, new ResultSourceDto { Kind = ResultSourceKinds.Calculated }) });
            Assert.Equal("list.xlsx", ResultCardModel.From(mixed, ReceiptLanguage.En, "list.xlsx").Groups.Single().Rows.Single().Source);
        }
    }
}