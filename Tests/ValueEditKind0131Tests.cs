#nullable enable
// Hand test 17 Nov (add-in 0.0.130): "set Comments to 230V" was written, then Change on the Comments row and ROUND2-EDIT showed the red line "Type a
// number, e.g. 12 V." and was refused. The edit checked units because the SHOWN value "230V" looks like a quantity; Comments is a text
// parameter. The write's typed rows say which it is (after_raw: a string for text, a number for a quantity), and the card now takes that.
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ValueEditKind0131Tests
    {
        private static FieldChangeDto Typed(string? before, string after, bool numeric) =>
            FieldChangeTyping.Type(new FieldChangeDto { Id = 1, Field = "x", Before = before, After = after }, false, "Door", "T", null, numeric);

        [Fact]
        public void TheLiveShape_CommentsHolding230V_TakesRound2Edit()
        {
            var kind = ResultCardModel.KindOf(new[] { Typed(null, "230V", numeric: false) });   // set_parameters on a text parameter: numeric false
            Assert.Equal(RowValueKind.Text, kind);
            Assert.Equal("ROUND2-EDIT", ValueInput.Normalize("230V", "ROUND2-EDIT", out var error, kind));
            Assert.Equal("", error);
            Assert.Equal("0.04A-ish", ValueInput.Normalize("0.04A", "0.04A-ish", out _, RowValueKind.Text));
        }

        [Fact]
        public void ARealQuantity_StillChecksTheNumberAndTheUnit()
        {
            var kind = ResultCardModel.KindOf(new[] { Typed("800 mm", "900 mm", numeric: true) });
            Assert.Equal(RowValueKind.Quantity, kind);
            Assert.Null(ValueInput.Normalize("900 mm", "abc", out var error, kind));
            Assert.Equal("Type a number, e.g. 12 mm.", error);
            Assert.Null(ValueInput.Normalize("900 mm", "5 m", out var unitError, kind));
            Assert.Equal("This value is in mm.", unitError);
            Assert.Equal("1000 mm", ValueInput.Normalize("900 mm", "1000", out _, kind));
        }

        [Fact]
        public void TheCallersWithNoKind_KeepTheOldGuessFromTheShownValue()
        {
            Assert.Null(ValueInput.Normalize("900 mm", "abc", out var error));
            Assert.Equal("Type a number, e.g. 12 mm.", error);
            Assert.Equal("12.00 W", ValueInput.Normalize("10.00 W", "12", out _));
        }

        [Fact]
        public void AnUnknownRow_TakesText_ButStillNormalisesANumber()
        {
            Assert.Equal("ROUND2-EDIT", ValueInput.Normalize("230V", "ROUND2-EDIT", out _, RowValueKind.Unknown));
            Assert.Equal("5 A", ValueInput.Normalize("230 V", "5 A", out _, RowValueKind.Unknown));
            Assert.Equal("12.00 W", ValueInput.Normalize("10.00 W", "12", out _, RowValueKind.Unknown));
        }

        [Fact]
        public void KindOf_NoRawOrAMix_IsUnknown()
        {
            Assert.Equal(RowValueKind.Unknown, ResultCardModel.KindOf(new List<FieldChangeDto> { new FieldChangeDto { Id = 1, Field = "x", After = "5" } }));
            Assert.Equal(RowValueKind.Unknown, ResultCardModel.KindOf(new[] { Typed(null, "230V", false), Typed("1 mm", "2 mm", true) }));
            Assert.Equal(RowValueKind.Unknown, ResultCardModel.KindOf(new List<FieldChangeDto>()));
        }

        [Fact]
        public void TheCardEdit_PassesTheRowsKind()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var body = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "UI/Copilot/Controls/ChangesBody.cs"));
            Assert.Contains("ValueInput.Normalize(row.After, box.Text, out var e, row.ValueKind)", body);
            Assert.Contains("ValueKind = KindOf(rows)", System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "UI/Copilot/Model/ResultCardModel.cs")));
        }
    }
}
