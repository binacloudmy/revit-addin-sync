#nullable enable
// UX review Q3 (18 Nov), the delete gate (F09_gate.png): the button read "Delete 1 elements" and was the same lime as every safe confirm. Text and colour
// only: the labels' order, the answers and Cancel are untouched.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class DeleteGate0138Tests
    {
        private static WideWriteModel Gate(string allLabel)
        {
            var opts = new List<AskOptionModel>
            {
                new AskOptionModel { Id = WideWriteModel.WriteAllId, Label = allLabel },
                new AskOptionModel { Id = WideWriteModel.WriteCancelId, Label = "Cancel, write nothing" },
            };
            return WideWriteModel.FromAsk("Before I run code that deletes elements; it declares 1.\nDelete door with Mark 349", opts, ReceiptLanguage.En)!;
        }

        [Fact]
        public void OneWithAPluralNoun_ReadsInTheSingular()
        {
            Assert.Equal("Delete 1 element", WideWriteModel.OneNoun("Delete 1 elements"));
            Assert.Equal("Write to the 1 type", WideWriteModel.OneNoun("Write to the 1 types"));
            Assert.Equal("Delete 12 elements", WideWriteModel.OneNoun("Delete 12 elements"));
            Assert.Equal("Delete 11 elements", WideWriteModel.OneNoun("Delete 11 elements"));
            Assert.Equal("Cancel, write nothing", WideWriteModel.OneNoun("Cancel, write nothing"));
            Assert.Equal("", WideWriteModel.OneNoun(null));
        }

        [Fact]
        public void TheDeleteGate_HasTheSingularLabel_TheWarningFlag_AndTheSameOrderAndAnswers()
        {
            var m = Gate("Delete 1 elements");
            Assert.Equal(new[] { "Delete 1 element", "Cancel, write nothing" }, m.Buttons.Select(b => b.Label));
            Assert.True(m.Buttons[0].Destructive);
            Assert.False(m.Buttons[1].Destructive);
            Assert.Equal(WideRole.Primary, m.Buttons[0].Role);   // role, order and answer ids unchanged
            Assert.Equal(WideWriteModel.WriteAllId, m.Buttons[0].Id);
            Assert.Equal(WideWriteModel.WriteCancelId, m.Buttons[1].Id);
        }

        [Fact]
        public void AWriteGate_IsNotMarkedDestructive()
        {
            Assert.False(Gate("Write to all 12 types").Buttons[0].Destructive);
            Assert.True(WideWriteModel.IsDeleteLabel("Padam 3 elemen"));
            Assert.False(WideWriteModel.IsDeleteLabel("Undelete the thing"));
        }

        [Fact]
        public void TheCard_DrawsTheDestructiveButtonInRed_NotTheKitPrimary()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var src = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/WideWriteCard.cs"));
            Assert.Contains("spec.Role == WideRole.Primary && !spec.Destructive ? CardKit.Primary", src);
            Assert.Contains("\"Cp.Red\"", src);
        }
    }
}
