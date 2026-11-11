#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CreatedNamingTests
    {
        [Fact] public void Sheet_NumberAndName() => Assert.Equal("A-101 - Level 1", CreatedNaming.Display(true, "A-101", "Level 1"));
        [Fact] public void Sheet_NameOnly() => Assert.Equal("Level 1", CreatedNaming.Display(true, "", "Level 1"));
        [Fact] public void Sheet_NumberOnly() => Assert.Equal("A-101", CreatedNaming.Display(true, "A-101", " "));
        [Fact] public void View_Name() => Assert.Equal("Plan 1", CreatedNaming.Display(false, null, "Plan 1"));
        [Fact] public void Named_Element() => Assert.Equal("Grid A", CreatedNaming.Display(false, "ignored", "Grid A"));
        [Fact] public void Unnamed_IsNull() { Assert.Null(CreatedNaming.Display(false, null, "  ")); Assert.Null(CreatedNaming.Display(true, null, null)); }

        private static Envelope With(string? data) => new Envelope { Data = data == null ? null : JsonDocument.Parse(data).RootElement.Clone() };

        [Fact] public void Label_NamePresent() =>
            Assert.Equal("A-101 - Level 1", ResultCardModel.CreatedLabel(With("{\"created\":[{\"id\":7,\"category\":\"Sheets\",\"name\":\"A-101 - Level 1\"}]}"), 7));
        [Fact] public void Label_IdAbsent() =>
            Assert.Equal("Element 8", ResultCardModel.CreatedLabel(With("{\"created\":[{\"id\":7,\"category\":\"Sheets\",\"name\":\"X\"}]}"), 8));
        [Fact] public void Label_NoCreated() { Assert.Equal("Element 8", ResultCardModel.CreatedLabel(With("{\"mode\":\"write\"}"), 8)); Assert.Equal("Element 8", ResultCardModel.CreatedLabel(With(null), 8)); }
    }
}
