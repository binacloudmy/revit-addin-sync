#nullable enable
// r13 (0.0.148), live fb713fc2 on 0.0.147 (frame R7-18): the server sent {kind:"view", ids:[3431003]} for the made section box view. The row
// was the button (a click opened the view) but drew as plain text under "Copy list". A view or sheet row Revit vouched for now shows
// "Open ›" at its right end; the row stays the one way in (no second button, no "Open view" button label).
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ViewRowOpen0148Tests
    {
        private static ItemRow MadeView()
        {
            var result = new TurnResultDto
            {
                Items = new List<ResultItemDto>
                {
                    new ResultItemDto { Kind = ResultItemKinds.View, Title = "Section Box - PTBR_Aras 01", Ids = new List<JsonElement> { JsonDocument.Parse("3431003").RootElement.Clone() } },
                },
            };
            return ResultItems.From(result)!.Sections[0].Rows[0];
        }

        [Fact]
        public void TheMadeViewRow_OpensTheView_AndShowsTheCue()
        {
            var row = MadeView();
            Assert.Equal(ItemButton.OpenView, row.Button);
            Assert.Equal(new long[] { 3431003 }, ResultItems.SampleIds(row));
            Assert.True(ResultItems.ShowButton(row, new Dictionary<long, ItemClass> { [3431003] = ItemClass.View }));
            Assert.False(ResultItems.ShowButton(row, new Dictionary<long, ItemClass> { [3431003] = ItemClass.Template }));
            Assert.False(ResultItems.ShowButton(row, new Dictionary<long, ItemClass>()));
            Assert.Equal("Open ›", ItemText.OpenCue(row.Button, ReceiptLanguage.En));
            Assert.Equal("Buka ›", ItemText.OpenCue(row.Button, ReceiptLanguage.Ms));
        }

        [Fact]
        public void OnlyViewAndSheetRows_GetTheCue()
        {
            Assert.Equal("Open ›", ItemText.OpenCue(ItemButton.OpenSheet, ReceiptLanguage.En));
            Assert.Equal("", ItemText.OpenCue(ItemButton.Select, ReceiptLanguage.En));
            Assert.Equal("", ItemText.OpenCue(ItemButton.None, ReceiptLanguage.En));
        }

        [Fact]
        public void TheCue_IsDrawnOnlyOnceRevitVouches_AndTheRowStaysTheButton()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var src = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/ItemsCard.cs")).Replace("\r\n", "\n");
            var open = src.Substring(src.IndexOf("private static void OpenRow(", System.StringComparison.Ordinal));
            open = open.Substring(0, open.IndexOf("private static void OpenCue(", System.StringComparison.Ordinal));
            Assert.Contains("OpenCue(line, ItemText.OpenCue(row.Button, lang));", open);
            Assert.Contains("var why = await actions.OpenView(id, sheet);", open);   // the click opens it
            Assert.Contains("actions.Rows?.Button(line,", open);
            Assert.Contains("if (map == null || ResultItems.ShowButton(row, map)) OpenRow(row, line, actions, lang);", src);
            Assert.DoesNotContain("ItemText.ButtonLabel(", src);
        }
    }
}
